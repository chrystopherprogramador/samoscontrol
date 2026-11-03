using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record RobertaProposal(Decision Decision, string? Reply, ValidationResult Validation, ConversationState State, SdrSuggestion Suggestion, bool Escalated);

/// <summary>Roberta 2.0: state → deterministic decision → LLM phrases only → validator → (regenerate once | escalate).</summary>
public class RobertaService
{
    public const string GoodbyeOptOut = "Tudo bem, não vamos mais te chamar. Obrigado pelo retorno!";
    public const string GoodbyeClose = "Entendido, obrigada pela atenção! Se um dia quiser conversar sobre a gestão da sua loja, é só chamar. 😊";

    private readonly AppDbContext _db; private readonly ILlmService _llm; private readonly PromptService _prompts; private readonly KnowledgeService _kb; private readonly ILogger<RobertaService> _log;
    public RobertaService(AppDbContext db, ILlmService llm, PromptService prompts, KnowledgeService kb, ILogger<RobertaService> log)
    { _db = db; _llm = llm; _prompts = prompts; _kb = kb; _log = log; }

    public static string Instruction(RobertaAction a) => a switch
    {
        RobertaAction.AskCurrentERP => "Pergunte qual sistema (ERP/PDV) a empresa usa hoje para vender e controlar estoque. Faça UMA única pergunta, nada além dela.",
        RobertaAction.AskStoreCount => "Pergunte quantas lojas/unidades a empresa tem. Faça UMA única pergunta.",
        RobertaAction.AskPainPoint => "Pergunte, de forma aberta, o que mais toma tempo ou gera retrabalho na operação hoje. NÃO sugira uma área específica. UMA única pergunta.",
        RobertaAction.ExplainFeature => "Responda a dúvida do cliente usando SOMENTE a BASE DE CONHECIMENTO. Não faça perguntas.",
        RobertaAction.ExplainStockControl => "Explique o controle de estoque usando SOMENTE a BASE DE CONHECIMENTO e os fatos. Não faça perguntas.",
        RobertaAction.ExplainMultistore => "Explique a gestão multiloja usando SOMENTE a BASE DE CONHECIMENTO e os fatos. Não faça perguntas.",
        RobertaAction.ExplainPricing => "Informe preço SOMENTE se estiver na BASE DE CONHECIMENTO; não invente valores nem descontos. Não faça perguntas.",
        RobertaAction.OfferDemo => "Ofereça uma demonstração rápida, sem compromisso, com UMA pergunta fechada (sim/não). Se o cliente está satisfeito, reconheça isso e NÃO pergunte sobre dores.",
        RobertaAction.ScheduleDemo => "Combine a demonstração; peça apenas um melhor horário. UMA pergunta.",
        RobertaAction.HandleObjection => "Reconheça a objeção com respeito, sem insistir nem repetir argumentos.",
        RobertaAction.FollowUp => "Retome o contato de forma leve, em uma frase, sem repetir mensagens anteriores.",
        _ => "Encerre com cordialidade, sem perguntas."
    };

    public async Task<ConversationState> StateAsync(Lead lead, CancellationToken ct = default)
    {
        var s = await _db.ConversationStates.FirstOrDefaultAsync(x => x.LeadId == lead.Id, ct);
        if (s != null) return s;
        s = new ConversationState { LeadId = lead.Id, Segment = lead.Segment };
        _db.ConversationStates.Add(s);
        return s;
    }

    private static string FactsBlock(Lead lead, ConversationState s)
    {
        var sb = new StringBuilder();
        void Fact(string k, string? v, string src) { if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"FATO: {k} = {v} (fonte: {src})"); }
        Fact("empresa", lead.CompanyName ?? lead.Name, "CRM"); Fact("cidade/UF", string.IsNullOrWhiteSpace(lead.City) ? null : $"{lead.City}/{lead.State}", "CRM");
        Fact("segmento", lead.Segment, "CRM"); Fact("sistema atual", s.CurrentErp, "resposta do lead"); Fact("número de lojas", s.StoreCount?.ToString(), "resposta do lead");
        Fact("dores citadas", s.PainPoints, "resposta do lead");
        if (s.SatisfiedWithCurrent) sb.AppendLine("FATO: o cliente disse estar satisfeito com a solução atual (fonte: resposta do lead)");
        if (s.InterestLevel > 0) sb.AppendLine($"INFERÊNCIA: nível de interesse {s.InterestLevel}/3 (não afirmar ao cliente)");
        var unknown = new List<string>();
        if (s.CurrentErp == null) unknown.Add("sistema atual"); if (s.StoreCount == null) unknown.Add("número de lojas"); if (string.IsNullOrWhiteSpace(s.PainPoints)) unknown.Add("dores");
        if (unknown.Count > 0) sb.AppendLine("NÃO SABEMOS: " + string.Join(", ", unknown));
        return sb.ToString();
    }

    private static string GroundedText(Lead lead, ConversationState s, string kb) =>
        $"{lead.Name} {lead.CompanyName} {lead.City} {lead.State} {lead.Segment} {s.CurrentErp} {s.StoreCount} {s.PainPoints} {kb}";

    private async Task<string> GenerateAsync(PromptVersion p, Decision d, Lead lead, ConversationState s, string kbPrompt, List<AiChatMessage> hist, string text, string? fixes, CancellationToken ct)
    {
        var asked = ConversationStateRules.Set(s.QuestionsAsked);
        var sys = p.Content + "\n\nAÇÃO: " + Instruction(d.Action) + "\n\nFATOS:\n" + FactsBlock(lead, s) +
                  "\nPERGUNTAS JÁ FEITAS (não repetir): " + (asked.Count == 0 ? "nenhuma" : string.Join(", ", asked)) +
                  "\n\nBASE DE CONHECIMENTO:\n" + kbPrompt + (fixes == null ? "" : "\n\nA TENTATIVA ANTERIOR FOI REPROVADA PELOS MOTIVOS: " + fixes + ". Corrija.");
        var msgs = new List<AiChatMessage> { new("system", sys) };
        msgs.AddRange(hist.TakeLast(6));
        if (hist.Count == 0 || hist[^1].Role != "user" || hist[^1].Content != text) msgs.Add(new("user", text));
        var raw = await _llm.ChatAsync(new LlmCall("roberta_reply", msgs, false, lead.Id, p.Name, p.Version, d.Action.ToString()), ct);
        return raw.Trim().Trim('"');
    }

    private async Task<string?> JudgeAsync(PromptVersion p, string reply, Decision d, Lead lead, ConversationState s, string kbPrompt, CancellationToken ct)
    {
        try
        {
            var sys = "Você é um auditor. Verifique se a MENSAGEM só afirma o que está nos FATOS/BASE, não repete perguntas já feitas e cumpre a AÇÃO. Responda APENAS JSON: {\"ok\":true,\"issues\":\"\"}.\n" +
                      "AÇÃO: " + Instruction(d.Action) + "\nFATOS:\n" + FactsBlock(lead, s) + "\nPERGUNTAS JÁ FEITAS: " + s.QuestionsAsked + "\nBASE:\n" + kbPrompt;
            var raw = await _llm.ChatAsync(new LlmCall("roberta_judge", new[] { new AiChatMessage("system", sys), new AiChatMessage("user", "MENSAGEM: " + reply) }, true, lead.Id, p.Name, p.Version, d.Action.ToString()), ct);
            var a = raw.IndexOf('{'); var b = raw.LastIndexOf('}'); if (a < 0 || b <= a) return null;
            using var doc = JsonDocument.Parse(raw[a..(b + 1)]);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return "judge:" + (doc.RootElement.TryGetProperty("issues", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : "reprovado");
        }
        catch (Exception ex) { _log.LogDebug(ex, "Juiz semântico indisponível (ignorado)"); }
        return null;
    }

    public async Task<RobertaProposal> ProposeAsync(SdrContact contact, Lead lead, string text, List<AiChatMessage> hist, CancellationToken ct = default)
    {
        var state = await StateAsync(lead, ct);
        var sig = InboundClassifier.Classify(text);
        ConversationStateRules.Apply(state, sig, text);

        var kb = await _kb.RelevantAsync(text);
        var kbText = KnowledgeService.AsPrompt(kb);
        var kbPricing = kb.Any(k => InboundClassifier.Normalize(k.Question + " " + k.Topic).Contains("preco") || InboundClassifier.Normalize(k.Question + k.Topic).Contains("valor") || InboundClassifier.Normalize(k.Question + k.Topic).Contains("plano"));
        var d = CommercialDecisionEngine.Decide(state, sig, kb.Count > 0, kbPricing);
        if (d.Action == RobertaAction.HandoffToSeller && d.Reason.StartsWith("Pergunta fora da base")) await _kb.RegisterGapAsync(text, lead.Id);

        var prompt = await _prompts.ActiveAsync(ct);
        string? reply = null; ValidationResult v; var escalated = false;
        var recent = hist.Where(m => m.Role == "assistant").Select(m => m.Content).TakeLast(5).ToList();
        var ctx = new ValidationContext(d.Action, recent, ConversationStateRules.Set(state.QuestionsAsked), GroundedText(lead, state, kbText), text);

        if (d.Action == RobertaAction.DoNotContact) { reply = GoodbyeOptOut; v = new(true, new(), ResponseValidator.Validate(reply, ctx).Scores); }
        else if (d.Action == RobertaAction.CloseConversation) { reply = GoodbyeClose; v = new(true, new(), ResponseValidator.Validate(reply, ctx).Scores); }
        else if (!d.NeedsLlm) { v = new(true, new(), new()); }
        else
        {
            v = new(false, new() { "llm_unavailable" }, new());
            if (_llm.IsConfigured)
            {
                string? fixes = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        reply = await GenerateAsync(prompt, d, lead, state, kbText, hist, text, fixes, ct);
                        v = ResponseValidator.Validate(reply, ctx);
                        if (v.Ok && await JudgeAsync(prompt, reply, d, lead, state, kbText, ct) is { } j) v = new(false, new() { j }, v.Scores);
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Roberta: falha ao gerar resposta"); v = new(false, new() { "llm_error" }, new()); reply = null; break; }
                    if (v.Ok) break;
                    fixes = string.Join(", ", v.Issues);
                }
            }
            if (!v.Ok)
            {
                escalated = true;
                d = new Decision(RobertaAction.HandoffToSeller, "Resposta reprovada pelo validador ou IA indisponível: " + string.Join(", ", v.Issues), false);
                reply = null;
            }
        }

        ConversationStateRules.RecordAction(state, d.Action);
        state.NextAction = null;
        state.Summary = Summarize(state);

        var sug = new SdrSuggestion
        {
            ContactId = contact.Id, LeadId = lead.Id, InboundText = text.Truncate(1900), Action = d.Action.ToString(), Reply = reply?.Truncate(1900),
            ValidatorOk = v.Ok, ValidatorIssues = string.Join(", ", v.Issues).Truncate(900), PromptVersion = prompt.Version,
            ScoresJson = JsonSerializer.Serialize(v.Scores),
            ContextJson = JsonSerializer.Serialize(new
            {
                reason = d.Reason, stage = state.Stage.ToString(), state.CurrentErp, state.StoreCount, state.PainPoints, state.QuestionsAsked, state.QuestionsAnswered,
                state.SatisfiedWithCurrent, state.NegativeStreak, state.TurnCount, state.InterestLevel, kb = kb.Select(k => k.Question).Take(6)
            })
        };
        _db.SdrSuggestions.Add(sug);
        await _db.SaveChangesAsync(ct);
        return new RobertaProposal(d, reply, v, state, sug, escalated);
    }

    private static string Summarize(ConversationState s)
    {
        var parts = new List<string>();
        if (s.CurrentErp != null) parts.Add("sistema: " + s.CurrentErp);
        if (s.StoreCount != null) parts.Add("lojas: " + s.StoreCount);
        if (!string.IsNullOrWhiteSpace(s.PainPoints)) parts.Add("dores: " + s.PainPoints);
        if (s.SatisfiedWithCurrent) parts.Add("satisfeito com a solução atual");
        if (s.PriceDiscussed) parts.Add("perguntou preço");
        if (s.DemoAccepted) parts.Add("aceitou demo");
        parts.Add($"turnos: {s.TurnCount}, interesse {s.InterestLevel}/3");
        return string.Join(" · ", parts).Truncate(1400);
    }
}
