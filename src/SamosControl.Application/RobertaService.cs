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
        RobertaAction.AskCurrentERP => "Pergunte qual sistema (ERP/PDV) a empresa usa hoje para vender e controlar estoque. Faça UMA única pergunta. Se o cliente acabou de dizer algo, pode comentar em meia frase antes.",
        RobertaAction.AskStoreCount => "Pergunte quantas lojas/unidades a empresa tem. Faça UMA única pergunta; pode comentar brevemente o que o cliente disse antes, sem repetir a pergunta anterior.",
        RobertaAction.AskPainPoint => "Pergunte, de forma aberta, o que mais toma tempo ou gera retrabalho na operação hoje. NÃO sugira uma área específica. UMA única pergunta.",
        RobertaAction.DeepenPain => "O cliente declarou uma dor. (1) Reconheça a dor com naturalidade, mostrando que entendeu o problema; (2) se houver ÂNGULO DO PRODUTO, conecte em uma frase curta; (3) faça UMA pergunta de aprofundamento, escolhida entre as PERGUNTAS POSSÍVEIS, a mais adequada ao contexto. NÃO ofereça demonstração, NÃO transfira, NÃO despeje funcionalidades.",
        RobertaAction.PitchFeature => "Apresente um mini-insight comercial conectando a dor do cliente ao ÂNGULO DO PRODUTO indicado, usando SOMENTE esse ângulo. Gere curiosidade e valor percebido, sem listar funcionalidades e sem repetir ângulos anteriores. A mensagem pode terminar sem pergunta, ou com no máximo UMA pergunta leve.",
        RobertaAction.RespectBusy => "O cliente está sem tempo. Seja gentil e muito breve (1 ou 2 frases), diga que pode retomar depois no horário que for melhor, SEM perguntas e sem insistir.",
        RobertaAction.ExplainFeature => "Responda a dúvida do cliente usando SOMENTE a BASE DE CONHECIMENTO. No máximo uma pergunta leve ao final, opcional.",
        RobertaAction.ExplainStockControl => "Explique o controle de estoque usando SOMENTE a BASE DE CONHECIMENTO e os fatos. No máximo uma pergunta leve ao final, opcional.",
        RobertaAction.ExplainMultistore => "Explique a gestão multiloja usando SOMENTE a BASE DE CONHECIMENTO e os fatos. No máximo uma pergunta leve ao final, opcional.",
        RobertaAction.ExplainPricing => "Informe preço SOMENTE se estiver na BASE DE CONHECIMENTO; não invente valores nem descontos. Não faça perguntas.",
        RobertaAction.OfferDemo => "Ofereça uma demonstração rápida, sem compromisso, ligada à dor do cliente quando houver, com UMA pergunta fechada (sim/não). Se o cliente está satisfeito, reconheça isso e NÃO pergunte sobre dores.",
        RobertaAction.ScheduleDemo => "Combine a demonstração; peça apenas um melhor horário. UMA pergunta.",
        RobertaAction.HandleObjection => "Reconheça a objeção com respeito, sem insistir nem repetir argumentos.",
        RobertaAction.FollowUp => "Retome o contato de forma leve, em poucas frases, citando a dor ou o assunto que o cliente trouxe antes e oferecendo um ponto de valor. Sem 'conseguiu ver minha mensagem?' e sem repetir mensagens anteriores.",
        _ => "Encerre com cordialidade, sem perguntas."
    };

    private static string FormatInstruction(ReplyFormat f) => f switch
    {
        ReplyFormat.Audio => "FORMATO: ÁUDIO. Escreva um roteiro falado natural (15 a 45 segundos, no máximo ~100 palavras), SEM emojis, SEM números, SEM links e SEM listas, como se estivesse falando.",
        ReplyFormat.AudioPlusText => $"FORMATO: ÁUDIO + TEXTO. Escreva primeiro o roteiro falado (15 a 45 segundos, sem emojis, números ou links), depois a linha '{FormatAdvisor.Separator}' e então uma mensagem de texto curta (1 a 2 frases) com o que precisa ficar registrado.",
        _ => "FORMATO: TEXTO curto de WhatsApp."
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
        Fact("dores citadas", s.PainPoints, "resposta do lead"); Fact("dor principal", s.MainPain, "classificação das respostas do lead");
        if (s.SatisfiedWithCurrent) sb.AppendLine("FATO: o cliente disse estar satisfeito com a solução atual (fonte: resposta do lead)");
        if (s.InterestLevel > 0) sb.AppendLine($"INFERÊNCIA: nível de interesse {s.InterestLevel}/3 (não afirmar ao cliente)");
        sb.AppendLine($"INFERÊNCIA: temperatura comercial {s.Temperature}/100, intenção de compra {s.BuyingIntent}, engajamento {s.Engagement} (uso interno, nunca dizer ao cliente)");
        sb.AppendLine("PERFIL DE COMUNICAÇÃO: " + FeelingRules.StyleGuidance(s));
        if (!string.IsNullOrWhiteSpace(s.FeaturesPresented)) sb.AppendLine("JÁ APRESENTADO (não repetir): " + s.FeaturesPresented);
        if (!string.IsNullOrWhiteSpace(s.LastQuestion)) sb.AppendLine("ÚLTIMA PERGUNTA FEITA: " + s.LastQuestion);
        var unknown = new List<string>();
        if (s.CurrentErp == null) unknown.Add("sistema atual"); if (s.StoreCount == null) unknown.Add("número de lojas"); if (string.IsNullOrWhiteSpace(s.PainPoints)) unknown.Add("dores");
        if (unknown.Count > 0) sb.AppendLine("NÃO SABEMOS: " + string.Join(", ", unknown));
        return sb.ToString();
    }

    private static string ProductBlock(Decision d, ConversationState s)
    {
        if (d.Action is not (RobertaAction.DeepenPain or RobertaAction.PitchFeature or RobertaAction.OfferDemo or RobertaAction.FollowUp)) return "";
        var angle = ProductCatalog.Next(s.MainPain, s.FeaturesPresented);
        var sb = new StringBuilder("\n\nÂNGULO DO PRODUTO (única base permitida para falar do Axis nesta mensagem): " + angle.Text);
        if (d.Action == RobertaAction.DeepenPain) sb.Append("\nPERGUNTAS POSSÍVEIS (escolha UMA, adapte ao contexto): " + string.Join(" | ", ProductCatalog.ProbesFor(s.MainPain)));
        return sb.ToString();
    }

    private static string GroundedText(Lead lead, ConversationState s, string kb) =>
        $"{lead.Name} {lead.CompanyName} {lead.City} {lead.State} {lead.Segment} {s.CurrentErp} {s.StoreCount} {s.PainPoints} {kb} {ProductCatalog.AllText(s.MainPain)}";

    private async Task<string> GenerateAsync(PromptVersion p, Decision d, Lead lead, ConversationState s, string kbPrompt, List<AiChatMessage> hist, string text, string? fixes, ReplyFormat format, IReadOnlyList<string> recent, CancellationToken ct)
    {
        var asked = ConversationStateRules.Set(s.QuestionsAsked);
        var openers = recent.Select(ResponseValidator.Opener).Where(x => x.Length > 0).Distinct().ToList();
        var sys = p.Content + "\n\nAÇÃO: " + Instruction(d.Action) + "\n" + FormatInstruction(format) + "\n\nFATOS:\n" + FactsBlock(lead, s) +
                  "\nPERGUNTAS JÁ FEITAS (não repetir): " + (asked.Count == 0 ? "nenhuma" : string.Join(", ", asked)) +
                  (openers.Count == 0 ? "" : "\nABERTURAS JÁ USADAS (comece de outro jeito): " + string.Join(", ", openers)) + ProductBlock(d, s) +
                  "\n\nBASE DE CONHECIMENTO:\n" + kbPrompt + (fixes == null ? "" : "\n\nA TENTATIVA ANTERIOR FOI REPROVADA PELOS MOTIVOS: " + fixes + ". Corrija.");
        var msgs = new List<AiChatMessage> { new("system", sys) };
        msgs.AddRange(hist.TakeLast(8));
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

    public async Task<RobertaProposal> ProposeAsync(SdrContact contact, Lead lead, string text, List<AiChatMessage> hist, bool viaAudio = false, TimeSpan? replyDelay = null, bool voiceAvailable = false, bool lastOutboundWasAudio = false, CancellationToken ct = default)
    {
        var state = await StateAsync(lead, ct);
        var sig = InboundClassifier.Classify(text);
        ConversationStateRules.Apply(state, sig, text, viaAudio, replyDelay);

        var kb = await _kb.RelevantAsync(text);
        var kbText = KnowledgeService.AsPrompt(kb);
        var kbPricing = kb.Any(k => InboundClassifier.Normalize(k.Question + " " + k.Topic).Contains("preco") || InboundClassifier.Normalize(k.Question + k.Topic).Contains("valor") || InboundClassifier.Normalize(k.Question + k.Topic).Contains("plano"));
        var d = CommercialDecisionEngine.Decide(state, sig, kb.Count > 0, kbPricing);
        if (d.Action == RobertaAction.HandoffToSeller && d.Reason.StartsWith("Pergunta fora da base")) await _kb.RegisterGapAsync(text, lead.Id);

        var prompt = await _prompts.ActiveAsync(ct);
        string? reply = null; ValidationResult v; var escalated = false;
        var recent = hist.Where(m => m.Role == "assistant").Select(m => m.Content).TakeLast(5).ToList();
        var format = FormatAdvisor.Choose(state, sig, d.Action, voiceAvailable, state.ClientSentAudio && viaAudio, lastOutboundWasAudio);
        var asked = ConversationStateRules.Set(state.QuestionsAsked);

        if (d.Action == RobertaAction.DoNotContact) { reply = GoodbyeOptOut; v = new(true, new(), ResponseValidator.Validate(reply, new(d.Action, recent, asked, GroundedText(lead, state, kbText), text)).Scores); format = ReplyFormat.Text; }
        else if (d.Action == RobertaAction.CloseConversation) { reply = GoodbyeClose; v = new(true, new(), ResponseValidator.Validate(reply, new(d.Action, recent, asked, GroundedText(lead, state, kbText), text)).Scores); format = ReplyFormat.Text; }
        else if (!d.NeedsLlm) { v = new(true, new(), new()); format = ReplyFormat.Text; }
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
                        reply = await GenerateAsync(prompt, d, lead, state, kbText, hist, text, fixes, format, recent, ct);
                        if (format != ReplyFormat.Text && FormatAdvisor.Finalize(format, reply) == ReplyFormat.Text) { format = ReplyFormat.Text; reply = FormatAdvisor.Plain(reply); }
                        else if (format == ReplyFormat.AudioPlusText && FormatAdvisor.Finalize(format, reply) == ReplyFormat.Audio) format = ReplyFormat.Audio;
                        var ctx = new ValidationContext(d.Action, recent, asked, GroundedText(lead, state, kbText), text, format);
                        v = ResponseValidator.Validate(reply, ctx);
                        if (v.Ok && await JudgeAsync(prompt, FormatAdvisor.Plain(reply), d, lead, state, kbText, ct) is { } j) v = new(false, new() { j }, v.Scores);
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Roberta: falha ao gerar resposta"); v = new(false, new() { "llm_error" }, new()); reply = null; break; }
                    if (v.Ok) break;
                    fixes = string.Join(", ", v.Issues);
                }
            }
            if (!v.Ok)
            {
                escalated = true;
                d = new Decision(RobertaAction.HandoffToSeller, "Resposta reprovada pelo validador ou IA indisponível: " + string.Join(", ", v.Issues), false, null, "outro");
                reply = null; format = ReplyFormat.Text;
            }
        }

        ConversationStateRules.RecordAction(state, d.Action, reply, FormatAdvisor.Code(format), d.HandoffKind);
        state.NextAction = null;
        state.Summary = Summarize(state);

        var sug = new SdrSuggestion
        {
            ContactId = contact.Id, LeadId = lead.Id, InboundText = text.Truncate(1900), Action = d.Action.ToString(), Reply = reply?.Truncate(1900), Format = FormatAdvisor.Code(format),
            ValidatorOk = v.Ok, ValidatorIssues = string.Join(", ", v.Issues).Truncate(900), PromptVersion = prompt.Version,
            ScoresJson = JsonSerializer.Serialize(v.Scores),
            ContextJson = JsonSerializer.Serialize(new
            {
                reason = d.Reason, stage = state.Stage.ToString(), state.CurrentErp, state.StoreCount, state.PainPoints, state.QuestionsAsked, state.QuestionsAnswered,
                state.SatisfiedWithCurrent, state.NegativeStreak, state.TurnCount, state.InterestLevel, kb = kb.Select(k => k.Question).Take(6),
                feeling = new
                {
                    temperatura_lead = state.Temperature, intencao_compra = state.BuyingIntent, dor_principal = state.MainPain, engajamento = state.Engagement,
                    objetivo_atual = state.CurrentGoal, acao_recomendada = d.Action.ToString(), formato_recomendado = FormatAdvisor.Code(format),
                    deve_transferir = d.Action == RobertaAction.HandoffToSeller, estilo = state.ClientStyle
                }
            })
        };
        _db.SdrSuggestions.Add(sug);
        await _db.SaveChangesAsync(ct);
        return new RobertaProposal(d, reply, v, state, sug, escalated);
    }

    /// <summary>Contextual follow-up for a lead that stopped answering: resumes from the pain already voiced, never a generic nudge.
    /// Returns null when there is no state to build on (callers fall back to the campaign templates).</summary>
    public async Task<string?> FollowUpTextAsync(Lead lead, List<AiChatMessage> hist, CancellationToken ct = default)
    {
        var s = await _db.ConversationStates.AsNoTracking().FirstOrDefaultAsync(x => x.LeadId == lead.Id, ct);
        if (s == null || s.OptOut || s.Stage is ConvStage.Encerrada or ConvStage.Handoff || string.IsNullOrWhiteSpace(s.PainPoints) && s.MainPain == null || !_llm.IsConfigured) return null;
        var prompt = await _prompts.ActiveAsync(ct);
        var recent = hist.Where(m => m.Role == "assistant").Select(m => m.Content).TakeLast(5).ToList();
        var d = new Decision(RobertaAction.FollowUp, "Follow-up contextual.", true);
        string? fixes = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var reply = await GenerateAsync(prompt, d, lead, s, "", hist, "(o cliente parou de responder; escreva o follow-up)", fixes, ReplyFormat.Text, recent, ct);
                var v = ResponseValidator.Validate(reply, new(RobertaAction.FollowUp, recent, ConversationStateRules.Set(s.QuestionsAsked), GroundedText(lead, s, ""), ""));
                if (v.Ok) return reply;
                fixes = string.Join(", ", v.Issues);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Roberta: falha no follow-up contextual"); return null; }
        }
        return null;
    }

    private static string Summarize(ConversationState s)
    {
        var parts = new List<string>();
        if (s.CurrentErp != null) parts.Add("sistema: " + s.CurrentErp);
        if (s.StoreCount != null) parts.Add("lojas: " + s.StoreCount);
        if (!string.IsNullOrWhiteSpace(s.PainPoints)) parts.Add("dores: " + s.PainPoints);
        if (s.MainPain != null) parts.Add("dor principal: " + s.MainPain);
        parts.Add($"temperatura {s.Temperature}/100 ({s.BuyingIntent})");
        if (s.SatisfiedWithCurrent) parts.Add("satisfeito com a solução atual");
        if (s.PriceDiscussed) parts.Add("perguntou preço");
        if (s.DemoAccepted) parts.Add("aceitou demo");
        parts.Add($"turnos: {s.TurnCount}, interesse {s.InterestLevel}/3");
        return string.Join(" · ", parts).Truncate(1400);
    }
}
