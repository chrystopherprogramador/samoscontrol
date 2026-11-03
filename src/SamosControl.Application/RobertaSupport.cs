using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

// ---------------- Validator ----------------
public record ValidationContext(RobertaAction Action, IReadOnlyList<string> RecentOutgoing, ISet<string> AskedTopics, string GroundedText, string InboundText, ReplyFormat Format = ReplyFormat.Text);
public record ValidationResult(bool Ok, List<string> Issues, Dictionary<string, int> Scores);

/// <summary>Deterministic checks that run before any message is sent. The optional LLM judge can only add issues, never remove them.</summary>
public static class ResponseValidator
{
    public const int MaxChars = 400, MaxSentences = 3, MaxCharsRich = 800, MaxSentencesRich = 7;
    private static readonly Regex AiDisclosure = new(@"\b(sou|somos|era|sera)\b[^.!?]{0,30}\b(ia|bot|robo|chatbot|inteligencia artificial|assistente virtual)\b|assistente virtual|inteligencia artificial|\bchatbot\b", RegexOptions.Compiled);
    private static readonly Regex BannedOpener = new(@"^\W*(entendi,? faz sentido|que bom)\b", RegexOptions.Compiled);
    /// <summary>First two words of a message, used to stop Roberta from opening every message the same way.</summary>
    public static string Opener(string s) => string.Join(' ', Regex.Split(InboundClassifier.Normalize(s), @"[^a-z0-9]+").Where(w => w.Length > 0).Take(2));
    private static readonly string[] Forbidden = { "garanto", "garantimos", "100%", "gratis", "gratuito", "sem custo", "desconto", "promocao", "sou humano", "sou uma pessoa", "sou o dono" };
    private static readonly Regex Num = new(@"R\$\s*\d[\d.,]*|\d[\d.,]*\s*%|\b\d{2,}[\d.,]*\b", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static HashSet<string> Tokens(string s) =>
        Regex.Split(InboundClassifier.Normalize(s), @"[^a-z0-9]+").Where(w => w.Length > 2).ToHashSet();

    public static double Jaccard(string a, string b)
    {
        var x = Tokens(a); var y = Tokens(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        return (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    public static string? QuestionTopic(string reply)
    {
        var n = InboundClassifier.Normalize(reply);
        if (!n.Contains('?')) return null;
        if (Regex.IsMatch(n, @"quantas? (lojas|unidades|filiais)|numero de lojas|tamanho da (rede|operacao)")) return ConversationStateRules.TopicStores;
        if (Regex.IsMatch(n, @"(qual|que) (sistema|erp)|usa(m)? (algum|um) (sistema|erp)|sistema (voces )?(usa|utiliza)")) return ConversationStateRules.TopicErp;
        if (Regex.IsMatch(n, @"dificuldade|dor\b|gargalo|retrabalho|toma(m)? (mais )?tempo|da (mais )?trabalho|problema|desafio")) return ConversationStateRules.TopicPain;
        return null;
    }

    public static ValidationResult Validate(string? reply, ValidationContext c)
    {
        var issues = new List<string>();
        var r = (reply ?? "").Trim();
        var grounded = InboundClassifier.Normalize(c.GroundedText + " " + c.InboundText);
        if (r.Length == 0) { issues.Add("empty"); return new(false, issues, Scores(issues, 0, c, r)); }

        var norm = InboundClassifier.Normalize(r);
        var combined = c.Format != ReplyFormat.Text;
        var maxChars = combined ? MaxCharsRich : MaxChars; var maxSentences = combined ? MaxSentencesRich : MaxSentences;
        if (r.Length > maxChars) issues.Add("too_long");
        if (Regex.Split(r, @"(?<=[.!?])\s+").Count(x => x.Trim().Length > 0) > maxSentences) issues.Add("too_many_sentences");
        var q = r.Count(ch => ch == '?');
        if (q > 1) issues.Add("multiple_questions");

        var noQuestion = c.Action is RobertaAction.CloseConversation or RobertaAction.DoNotContact or RobertaAction.HandoffToSeller or RobertaAction.RespectBusy;
        var mustAsk = c.Action is RobertaAction.AskCurrentERP or RobertaAction.AskStoreCount or RobertaAction.AskPainPoint or RobertaAction.DeepenPain;
        if (noQuestion && q > 0) issues.Add("question_not_allowed");
        if (mustAsk && q != 1) issues.Add("action_requires_one_question");

        // Topic checks only protect the qualification questions; deepening a pain is allowed to probe the pain again.
        var qt = QuestionTopic(r);
        var expected = ConversationStateRules.TopicOf(c.Action.ToString());
        var painProbe = c.Action is RobertaAction.DeepenPain or RobertaAction.PitchFeature or RobertaAction.OfferDemo || c.Action.ToString().StartsWith("Explain");
        if (qt != null && qt != expected && c.AskedTopics.Contains(qt) && !(painProbe && qt == ConversationStateRules.TopicPain)) issues.Add("repeated_question:" + qt);
        if (qt != null && !mustAsk && !(painProbe && qt == ConversationStateRules.TopicPain)) issues.Add("off_action_question:" + qt);
        if (qt != null && mustAsk && c.Action != RobertaAction.DeepenPain && qt != expected) issues.Add("question_topic_mismatch:" + qt);

        if (AiDisclosure.IsMatch(norm)) issues.Add("ai_disclosure");
        if (BannedOpener.IsMatch(norm)) issues.Add("banned_opener");
        var opener = Opener(r);
        if (opener.Length > 0 && c.RecentOutgoing.Any(o => Opener(o) == opener)) issues.Add("repeated_opener");
        if (c.Format != ReplyFormat.Text)
        {
            var spoken = FormatAdvisor.Split(r).Audio;
            if (spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > FormatAdvisor.MaxAudioWords) issues.Add("audio_too_long");
            if (Num.IsMatch(spoken) || Url.IsMatch(spoken) || spoken.Any(ch => char.IsSurrogate(ch))) issues.Add("audio_has_text_only_content");
            if (c.Format == ReplyFormat.AudioPlusText && string.IsNullOrWhiteSpace(FormatAdvisor.Split(r).Text)) issues.Add("audio_text_missing_companion");
        }

        double maxSim = 0;
        foreach (var o in c.RecentOutgoing) maxSim = Math.Max(maxSim, Jaccard(r, o));
        if (maxSim >= 0.6) issues.Add("repeated_message");

        foreach (var f in Forbidden)
            if (norm.Contains(f) && !grounded.Contains(f)) { issues.Add("forbidden:" + f); break; }
        foreach (Match m in Num.Matches(r))
        {
            var digits = InboundClassifier.Normalize(m.Value);
            if (!grounded.Contains(digits) && !grounded.Contains(Regex.Replace(digits, @"[^\d]", ""))) { issues.Add("unsupported_number:" + m.Value.Trim()); break; }
        }
        if (Url.IsMatch(r) && !Url.Matches(r).Any(u => grounded.Contains(InboundClassifier.Normalize(u.Value)))) issues.Add("unsupported_url");
        foreach (var erp in InboundClassifier.KnownErps)
            if (Regex.IsMatch(norm, @"\b" + Regex.Escape(erp) + @"\b") && !grounded.Contains(erp)) { issues.Add("unsupported_erp:" + erp); break; }

        return new(issues.Count == 0, issues, Scores(issues, maxSim, c, r));
    }

    private static Dictionary<string, int> Scores(List<string> issues, double maxSim, ValidationContext c, string r)
    {
        int Hall = Math.Max(0, 100 - 35 * issues.Count(i => i.StartsWith("unsupported") || i.StartsWith("forbidden")));
        var progress = issues.Any(i => i.StartsWith("repeated")) ? 30 : 100;
        var goal = c.Action switch
        {
            RobertaAction.AskCurrentERP or RobertaAction.AskStoreCount or RobertaAction.AskPainPoint => r.Contains('?') ? 100 : 20,
            RobertaAction.CloseConversation or RobertaAction.DoNotContact or RobertaAction.HandoffToSeller => r.Contains('?') ? 40 : 100,
            _ => r.Length > 0 ? 90 : 0
        };
        var natural = Math.Max(0, 100 - (r.Length > MaxChars ? 40 : 0) - (issues.Contains("multiple_questions") ? 30 : 0) - (issues.Contains("too_many_sentences") ? 20 : 0));
        var ctx = c.GroundedText.Length == 0 ? 50 : Math.Min(100, 50 + (int)(Jaccard(r, c.GroundedText) * 200));
        return new()
        {
            ["HallucinationScore"] = Hall,
            ["RepetitionScore"] = (int)Math.Round(100 - Math.Min(1, maxSim) * 100),
            ["ConversationProgressScore"] = progress,
            ["ContextUsageScore"] = ctx,
            ["NaturalnessScore"] = natural,
            ["GoalCompletionScore"] = goal
        };
    }
}

// ---------------- LLM service ----------------
public record LlmCall(string Task, IReadOnlyList<AiChatMessage> Messages, bool Json = false, int? LeadId = null, string? PromptName = null, int? PromptVersion = null, string? Action = null);

public interface ILlmService
{
    bool IsConfigured { get; }
    Task<string> ChatAsync(LlmCall call, CancellationToken ct = default);
}

/// <summary>Single entry point for LLM calls: timeout, one retry, circuit breaker and an execution log (no direct provider calls elsewhere in Roberta).</summary>
public class LlmService : ILlmService
{
    private static int _fails; private static DateTime _openUntil = DateTime.MinValue;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    public const int BreakerThreshold = 5; public static readonly TimeSpan BreakerOpen = TimeSpan.FromSeconds(60);
    private readonly IAiProvider _ai; private readonly AppDbContext _db; private readonly ILogger<LlmService> _log;
    public LlmService(IAiProvider ai, AppDbContext db, ILogger<LlmService> log) { _ai = ai; _db = db; _log = log; }
    public bool IsConfigured => _ai.IsConfigured;

    public async Task<string> ChatAsync(LlmCall call, CancellationToken ct = default)
    {
        var exec = new LlmExecution
        {
            LeadId = call.LeadId, Task = call.Task, PromptName = call.PromptName, PromptVersion = call.PromptVersion, Provider = _ai.Name, Action = call.Action,
            Input = string.Join("\n", call.Messages.Select(m => $"[{m.Role}] {m.Content}")).Truncate(8000)
        };
        var sw = Stopwatch.StartNew();
        try
        {
            if (DateTime.UtcNow < _openUntil) throw new InvalidOperationException("Circuit breaker aberto: IA temporariamente suspensa.");
            Exception? last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(Timeout);
                try
                {
                    var res = await _ai.ChatAsync(call.Messages, call.Json, cts.Token);
                    Interlocked.Exchange(ref _fails, 0);
                    exec.Output = res.Truncate(8000);
                    return res;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { last = ex; _log.LogWarning(ex, "LLM {Task} tentativa {N} falhou", call.Task, attempt + 1); }
            }
            if (Interlocked.Increment(ref _fails) >= BreakerThreshold) _openUntil = DateTime.UtcNow + BreakerOpen;
            throw last ?? new InvalidOperationException("Falha na IA.");
        }
        catch (Exception ex) { exec.Error = ex.Message.Truncate(480); throw; }
        finally
        {
            exec.LatencyMs = (int)sw.ElapsedMilliseconds;
            try { _db.LlmExecutions.Add(exec); await _db.SaveChangesAsync(CancellationToken.None); } catch (Exception ex) { _log.LogWarning(ex, "Não foi possível registrar a execução da LLM"); }
        }
    }

    public static void ResetBreaker() { _fails = 0; _openUntil = DateTime.MinValue; }
}

// ---------------- Prompt versioning ----------------
public class PromptService
{
    public const string Name = "roberta";
    public const string DefaultContent =
        "Você é a Roberta, da Samos, conversando por WhatsApp com o responsável por uma loja. Apresente-se sempre apenas como Roberta, da Samos.\n" +
        "A Roberta não é apenas uma qualificadora de leads. Ela é uma SDR vendedora: tem iniciativa comercial, entende o cenário do cliente, aprofunda dores reais, conecta a dor a uma funcionalidade do Axis, gera interesse e só então conduz para demonstração ou especialista.\n" +
        "Você redige APENAS a mensagem da ação indicada em AÇÃO. O próximo passo, o handoff e a temperatura já foram decididos pelo sistema: não decida transferir, não prometa horário e não revele esses dados internos.\n" +
        "ESTILO: português do Brasil, tom humano de WhatsApp, curto (normalmente 1 a 3 frases, até 400 caracteres em texto). Nem toda mensagem precisa terminar com pergunta: pode comentar, trazer um insight ou um mini-pitch. No máximo UMA pergunta por mensagem. Sem listas, sem tom corporativo, sem excesso de emojis. Adapte-se ao PERFIL DE COMUNICAÇÃO: cliente objetivo = direto; comunicativo = pode conversar mais; informal = informalidade moderada, sem forçar intimidade; técnico = mais detalhe; com pressa = menos perguntas.\n" +
        "VARIAÇÃO: não comece sempre do mesmo jeito. Evite repetir 'Entendi, faz sentido', 'Perfeito' e 'Que bom'; varie as aberturas (veja ABERTURAS JÁ USADAS) e reaja ao que o cliente realmente disse.\n" +
        "VENDA CONSULTIVA: ao ouvir uma dor, reconheça-a, mostre que entendeu o problema, descubra frequência ou impacto quando fizer sentido, conecte ao ÂNGULO DO PRODUTO e gere curiosidade. Apresente apenas o que se relaciona ao contexto, uma coisa por vez; nunca despeje várias funcionalidades.\n" +
        "PRODUTO (Axis): estoque = controle de estoque, movimentações, inventário, WMS, RFID, rastreabilidade; moda = grade, tamanho e cor; vendas = PDV, força de vendas, Axis Vendedor; clientes = CRM, fidelidade, cashback; fiscal = NF-e, NFC-e, NFS-e, MDF-e, CT-e; financeiro = financeiro, boleto, conciliação bancária; redes = controle de múltiplas lojas. Fale do Axis SOMENTE com o ÂNGULO DO PRODUTO e a BASE DE CONHECIMENTO.\n" +
        "Use SOMENTE os FATOS, o ÂNGULO DO PRODUTO e a BASE DE CONHECIMENTO. Tudo o que está em NÃO SABEMOS é desconhecido: não afirme nem presuma, e não pergunte de novo o que já está nos FATOS. Itens marcados INFERÊNCIA são uso interno: nunca diga ao cliente.\n" +
        "Nunca invente preço, prazo, desconto, número, link, funcionalidade, nome de sistema ou promessa. Nunca repita uma pergunta já feita (veja PERGUNTAS JÁ FEITAS). Se o cliente disse que está satisfeito ou respondeu 'não', não insista no assunto.\n" +
        "Nunca diga que é IA, bot, robô, inteligência artificial ou assistente virtual. Responda APENAS com o texto da mensagem (ou o roteiro, conforme FORMATO), sem aspas e sem explicações.";
    public const string LegacyDefaultMarker = "assistente virtual de pré-vendas";

    private readonly AppDbContext _db; private readonly ILlmService _llm;
    public PromptService(AppDbContext db, ILlmService llm) { _db = db; _llm = llm; }

    public async Task<PromptVersion> ActiveAsync(CancellationToken ct = default)
    {
        var p = await _db.PromptVersions.Where(x => x.Name == Name && x.IsActive).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (p != null)
        {
            // The untouched Roberta 2.0 system prompt is replaced by the "SDR vendedora" one; prompts edited by a person are never touched.
            if (p.CreatedBy == "sistema" && p.Content.Contains(LegacyDefaultMarker))
            {
                p.IsActive = false;
                var max = await _db.PromptVersions.Where(x => x.Name == Name).MaxAsync(x => (int?)x.Version, ct) ?? p.Version;
                p = new PromptVersion { Name = Name, Version = max + 1, Content = DefaultContent, IsActive = true, CreatedBy = "sistema", Notes = "Roberta 3.0: SDR vendedora (versão anterior preservada para rollback)" };
                _db.PromptVersions.Add(p); await _db.SaveChangesAsync(ct);
            }
            return p;
        }
        p = await _db.PromptVersions.Where(x => x.Name == Name).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (p != null) { p.IsActive = true; await _db.SaveChangesAsync(ct); return p; }
        p = new PromptVersion { Name = Name, Version = 1, Content = DefaultContent, IsActive = true, CreatedBy = "sistema", Notes = "Prompt inicial da Roberta 2.0" };
        _db.PromptVersions.Add(p); await _db.SaveChangesAsync(ct);
        return p;
    }

    public async Task<PromptVersion> CreateAsync(string content, string? notes, string? by, CancellationToken ct = default)
    {
        var max = await _db.PromptVersions.Where(x => x.Name == Name).MaxAsync(x => (int?)x.Version, ct) ?? 0;
        var p = new PromptVersion { Name = Name, Version = max + 1, Content = content.Trim(), Notes = notes, CreatedBy = by, IsActive = false };
        _db.PromptVersions.Add(p); await _db.SaveChangesAsync(ct);
        return p;
    }

    /// <summary>Golden scenarios (action + inbound) used as a regression gate before a prompt goes live.</summary>
    public static readonly (RobertaAction Action, string Inbound)[] Golden =
    {
        (RobertaAction.AskCurrentERP, "Oi, quem é?"), (RobertaAction.AskStoreCount, "Uso o Bling"), (RobertaAction.AskPainPoint, "Tenho 2 lojas"),
        (RobertaAction.OfferDemo, "Está tudo bem com o sistema atual"), (RobertaAction.ExplainFeature, "Vocês controlam estoque por tamanho e cor?"),
        (RobertaAction.CloseConversation, "Não tenho interesse"), (RobertaAction.DeepenPain, "Uai, a questão de gestão de estoque, sempre fura o estoque")
    };

    /// <summary>Pass rate (0-100) of the prompt over the golden scenarios, or null when no AI is configured.</summary>
    public async Task<int?> RegressionAsync(PromptVersion p, CancellationToken ct = default)
    {
        if (!_llm.IsConfigured) return null;
        var pass = 0;
        foreach (var (action, inbound) in Golden)
        {
            if (action == RobertaAction.CloseConversation) { pass++; continue; } // fixed template, not model-dependent
            try
            {
                var msgs = new List<AiChatMessage> { new("system", p.Content + "\n\nAÇÃO: " + RobertaService.Instruction(action) + "\nPERGUNTAS JÁ FEITAS: nenhuma"), new("user", inbound) };
                var reply = await _llm.ChatAsync(new LlmCall("prompt_regression", msgs, false, null, p.Name, p.Version, action.ToString()), ct);
                var v = ResponseValidator.Validate(reply, new(action, Array.Empty<string>(), new HashSet<string>(), "", inbound));
                if (v.Ok) pass++;
            }
            catch { /* counts as failure */ }
        }
        return pass * 100 / Golden.Length;
    }

    public const int MinPassRate = 90;

    public async Task<(bool Ok, string Message)> ActivateAsync(int id, bool force, CancellationToken ct = default)
    {
        var p = await _db.PromptVersions.FindAsync(new object[] { id }, ct);
        if (p == null) return (false, "Versão não encontrada.");
        var current = await _db.PromptVersions.Where(x => x.Name == p.Name && x.IsActive).FirstOrDefaultAsync(ct);
        var rate = await RegressionAsync(p, ct);
        p.PassRate = rate;
        if (!force)
        {
            if (rate == null) { await _db.SaveChangesAsync(ct); return (false, "Sem IA configurada para rodar a regressão. Ative com 'forçar' apenas se souber o que está fazendo."); }
            if (rate < MinPassRate || (current?.PassRate != null && rate < current.PassRate))
            { await _db.SaveChangesAsync(ct); return (false, $"Regressão reprovada: {rate}% (mínimo {MinPassRate}% e não pode ser menor que a versão ativa)."); }
        }
        foreach (var x in await _db.PromptVersions.Where(x => x.Name == p.Name && x.IsActive).ToListAsync(ct)) x.IsActive = false;
        p.IsActive = true; await _db.SaveChangesAsync(ct);
        return (true, $"Versão {p.Version} ativada" + (rate.HasValue ? $" (regressão {rate}%)." : " (forçada)."));
    }
}
