using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Application;

/// <summary>Stable objection codes (persisted in <see cref="ConversationState.ObjectionLog"/>).</summary>
public static class Obj
{
    public const string JaTenhoSistema = "ja_tenho_sistema", Caro = "caro", NaoTrocar = "nao_trocar", Material = "material", VouPensar = "vou_pensar",
        FazTudo = "faz_tudo", Prova = "prova_resultado", Desconfianca = "desconfianca", NecessidadeNaoAtendida = "necessidade_nao_atendida";
}

/// <summary>Deterministic reading of what the lead said beyond pains/intents: objections, identity questions, channel and time preferences.</summary>
public static class Roberta4Rules
{
    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RProva = R(@"\b(prova|provas|comprovar|comprovacao|cases?|depoimentos?|referencias?|resultados? (concretos?|reais|comprovados?)|quem (ja )?usa|que (empresas?|lojas?) (ja )?usam?|tem (algum )?(cliente|exemplo)|mostra (resultado|numero)s?|numeros? de (retorno|resultado)|quanto (vou |a gente )?(economiz\w+|ganh\w+|redu\w+)|qual (o )?retorno|roi)\b");
    private static readonly Regex RDesconfia = R(@"\b(golpe|fraude|nao confio|desconfi\w+|e confiavel|isso e serio)\b");
    private static readonly Regex RFazTudo = R(@"\b(ja )?(faz|tem|atende|resolve) tudo( isso)?\b|\bja (temos|tenho|tem) (todas|tudo)\b|\btodas (essas|as) (funcionalidades|funcoes)\b.{0,40}\bja\b");
    private static readonly Regex RNaoTrocar = R(@"\bnao (quero|vou|vamos|pretendo|pretendemos|queremos) (trocar|mudar|migrar)\b|\b(trocar|mudar|migrar) (de )?(sistema|erp)? ?(da|e|seria) (muito )?(trabalho|complicad\w+|dor de cabeca)\b|\bdor de cabeca (de|pra|para) (trocar|migrar|mudar)\b");
    private static readonly Regex RCaro = R(@"\b(muito caro|ta caro|esta caro|e caro|achei caro|caro demais|salgado|fora do (meu )?orcamento|nao (cabe|tenho) (no )?orcamento|orcamento (apertado|curto)|mais barato)\b");
    private static readonly Regex RMaterial = R(@"\b(me )?(manda|mande|envia|envie|passa|passe|pode (mandar|enviar|passar))\w* (uma |um |o |a |algum |alguma )?(apresentacao|material|catalogo|folder|pdf|proposta|informacoes|video|slides)\b|\b(tem|teria) (algum )?(material|apresentacao|catalogo|folder)\b");
    private static readonly Regex RPensar = R(@"\b(vou pensar|deixa eu pensar|preciso pensar|vou analisar|vou conversar com (meu|o|a) (socio|socia|marido|esposa|dono|chefe)|depois eu (vejo|te falo|retorno)|te (aviso|retorno))\b");
    private static readonly Regex RUnsupported = R(@"\b(preciso|precisamos|queria|queremos|procuro|procuramos|voces (tem|fazem|possuem)|tem)\b.{0,50}\b(ordem de producao|producao industrial|ordem de servico|agendamento|agenda de (clientes|consultas)|comanda|cardapio|ponto eletronico|folha de pagamento|prontuario|contabilidade|gestao de frota)\b");
    private static readonly Regex RJaTenho = R(@"\bja (tenho|temos|uso|usamos|trabalhamos com) (um |o |outro )?(sistema|erp|programa)\b");
    private static readonly Regex RJaTenhoSatisfied = R(@"\b(funciona|atende|resolve|satisfeit\w+|tudo bem|esta bom|ta bom|de boa)\b");

    /// <summary>Objection code for the (normalized) message, or null. The order matters: the most specific objection wins.</summary>
    public static string? Objection(string t)
    {
        if (RProva.IsMatch(t)) return Obj.Prova;
        if (RDesconfia.IsMatch(t)) return Obj.Desconfianca;
        if (RFazTudo.IsMatch(t)) return Obj.FazTudo;
        if (RNaoTrocar.IsMatch(t)) return Obj.NaoTrocar;
        if (RCaro.IsMatch(t)) return Obj.Caro;
        if (RMaterial.IsMatch(t)) return Obj.Material;
        if (RPensar.IsMatch(t)) return Obj.VouPensar;
        if (RUnsupported.IsMatch(t)) return Obj.NecessidadeNaoAtendida;
        if (RJaTenho.IsMatch(t) && !RJaTenhoSatisfied.IsMatch(t)) return Obj.JaTenhoSistema;
        return null;
    }

    private static readonly Regex RAi = R(@"\b(voce|vc|isso|ai|ta|esta|estou|to) (e|eh|esta|ta|falando com) (uma |um )?(ia|robo|bot|chatbot|maquina|inteligencia artificial|assistente virtual|humano|humana|pessoa|gente)\b|\b(e|eh|sao) (um |uma )?(robo|bot|ia|chatbot|inteligencia artificial|maquina|assistente virtual)\b|\bfalando com (um |uma )?(robo|bot|ia|maquina|humano|humana|pessoa)\b|\bpessoa de verdade\b|\b(voce|vc) e (real|de verdade)\b|\bautomatic\w+\?");
    /// <summary>The lead asks whether they are talking to an AI/bot/person. Always answered transparently.</summary>
    public static bool AskedIfAi(string t) => RAi.IsMatch(t);

    private static readonly Regex RTextOnly = R(@"\b(so (por )?texto|somente (por )?texto|apenas (por )?texto|nao (manda|mande|envie|envia|precisa( de)?|gosto( de)?) audio|sem audio|prefiro (texto|escrever|ler)|nao consigo ouvir|nao posso ouvir)\b");
    private static readonly Regex RAudioPref = R(@"\b(prefiro audio|pode (mandar|enviar) audio|manda audio|mande audio|mais facil (por )?audio|melhor (por )?audio)\b");
    public static bool TextOnly(string t) => RTextOnly.IsMatch(t);
    public static bool PrefersAudio(string t) => RAudioPref.IsMatch(t) && !RTextOnly.IsMatch(t);

    private static readonly Regex RTime = R(@"\b((amanha|hoje|segunda|terca|quarta|quinta|sexta|sabado)\b( (de manha|a tarde|a noite|depois do almoco))?|de manha|a tarde|a noite|depois do almoco|(depois|apos) das? \d{1,2}( ?h(oras)?)?|as \d{1,2}( ?h(oras)?|:\d\d)|(na )?proxima semana)\b");
    public static string? PreferredTime(string t)
    {
        var m = RTime.Match(t);
        return m.Success ? m.Value.Trim() : null;
    }

    private static readonly Regex ROff = R(@"\b(futebol|jogo do|flamengo|corinthians|palmeiras|sao paulo fc|copa do mundo|chuva|calor|frio demais|feriado|fim de semana|ferias|natal|politica|eleicao|novela|viu o jogo|transito|churrasco)\b|^(e (voce|vc)|tudo bem e (voce|vc))\b");
    public static bool OffTopic(string t) => ROff.IsMatch(t);

    private static readonly Regex RInsist = R(@"\b(ja (falei|disse|avisei)|para de insistir|pare de insistir|insist\w+|voces nao (desistem|entendem)|toda hora (me )?(chama|manda|mandam|chamam)|de novo (voces|essa mensagem))\b");
    public static bool Insistence(string t) => RInsist.IsMatch(t);

    private static readonly Regex RDemoNoApres = R(@"\b(demonstracao|demo|ver funcionando|ver o sistema|conhecer o sistema|teste gratis|reuniao|agendar|marcar (uma )?(conversa|reuniao|call)|videochamada)\b");
    public static bool DemoCore(string t) => RDemoNoApres.IsMatch(t);

    /// <summary>Hypotheses the engine may keep about a pain. They are never stated to the lead as fact; they only guide probing questions.</summary>
    public static string? HypothesisFor(string? pain) => pain switch
    {
        Pain.StockDivergence => "pode haver movimentações de estoque sem registro ou várias pessoas mexendo no estoque (não confirmado)",
        Pain.Stock => "pode faltar rastreabilidade das movimentações (não confirmado)",
        Pain.Fiscal => "pode haver retrabalho na emissão ou conferência de notas (não confirmado)",
        Pain.Financial => "pode haver conciliação ou cobrança feitas manualmente (não confirmado)",
        Pain.Sales => "pode haver lentidão no PDV em horário de pico (não confirmado)",
        Pain.Ecommerce => "estoque da loja física e do online pode estar separado (não confirmado)",
        Pain.Multistore => "as lojas podem ter controles separados (não confirmado)",
        Pain.Customers => "pode não haver histórico de compras dos clientes (não confirmado)",
        Pain.System => "o sistema atual pode ter limitações que ainda não conhecemos (não confirmado)",
        _ => null
    };

    public static string PainFamily(string? pain) => pain is Pain.Stock or Pain.StockDivergence ? Pain.Stock : pain ?? "";
}

/// <summary>Auditable commercial decision for one turn. It is produced by the deterministic engine and validated; an LLM planner (if ever enabled) must fit the same contract.</summary>
public record CommercialPlan
{
    [JsonPropertyName("commercial_goal")] public string CommercialGoal { get; init; } = "";
    [JsonPropertyName("main_goal")] public string MainGoal { get; init; } = "qualified_demo";
    [JsonPropertyName("conversation_stage")] public string ConversationStage { get; init; } = "descoberta";
    [JsonPropertyName("customer_engagement")] public string CustomerEngagement { get; init; } = "baixo";
    [JsonPropertyName("customer_sentiment")] public string CustomerSentiment { get; init; } = "neutral";
    [JsonPropertyName("buying_intent")] public string BuyingIntent { get; init; } = "baixa";
    [JsonPropertyName("selected_strategy")] public string SelectedStrategy { get; init; } = "";
    [JsonPropertyName("strategy_reason")] public string StrategyReason { get; init; } = "";
    [JsonPropertyName("next_action")] public string NextAction { get; init; } = "";
    [JsonPropertyName("response_modality")] public string ResponseModality { get; init; } = "text";
    [JsonPropertyName("human_handoff")] public bool HumanHandoff { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Parses a plan from an untrusted source (e.g. an LLM). Returns null when it is malformed; validity is checked by <see cref="PlanValidator"/>.</summary>
    public static CommercialPlan? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var a = json.IndexOf('{'); var b = json.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonSerializer.Deserialize<CommercialPlan>(json[a..(b + 1)]); } catch (JsonException) { return null; }
    }
}

public static class PlanBuilder
{
    public static string Strategy(RobertaAction a) => a switch
    {
        RobertaAction.AskCurrentERP or RobertaAction.AskStoreCount or RobertaAction.AskPainPoint => "discover_need",
        RobertaAction.HandoffToSeller => "transfer_human",
        RobertaAction.CloseConversation => "close_politely",
        RobertaAction.ExplainFeature or RobertaAction.ExplainStockControl or RobertaAction.ExplainMultistore => "send_useful_information",
        RobertaAction.RespectBusy => "pause_conversation",
        _ => Regex.Replace(a.ToString(), "(?<=[a-z])(?=[A-Z])", "_").ToLowerInvariant()
    };

    public static readonly IReadOnlySet<string> KnownStrategies = Enum.GetValues<RobertaAction>().Select(Strategy).ToHashSet();

    private static string Goal(RobertaAction a) => a switch
    {
        RobertaAction.AskCurrentERP => "identify_current_system", RobertaAction.AskStoreCount => "identify_company_size", RobertaAction.AskPainPoint => "identify_pain",
        RobertaAction.DeepenPain => "understand_pain", RobertaAction.ExploreImpact => "understand_impact", RobertaAction.ShareInsight => "build_authority",
        RobertaAction.PitchFeature => "connect_pain_to_solution", RobertaAction.TellUseCase => "illustrate_usage", RobertaAction.CreateCuriosity => "create_interest",
        RobertaAction.ConfirmUnderstanding => "confirm_understanding", RobertaAction.DifferentiateCompetitor => "find_unmet_need", RobertaAction.HandleObjection => "resolve_objection",
        RobertaAction.OfferDemo => "schedule_qualified_demo", RobertaAction.AskForMeeting => "schedule_qualified_demo", RobertaAction.HandoffToSeller => "handoff_to_human",
        RobertaAction.BuildRapport => "build_rapport", RobertaAction.SendUsefulInformation => "send_approved_information", RobertaAction.PauseConversation or RobertaAction.RespectBusy => "respect_lead_time",
        RobertaAction.CloseConversation or RobertaAction.DoNotContact => "end_respectfully", RobertaAction.AnswerIdentity => "be_transparent", RobertaAction.FollowUp => "resume_conversation",
        _ => "answer_question"
    };

    private static string Next(RobertaAction a) => a switch
    {
        RobertaAction.AskCurrentERP or RobertaAction.AskStoreCount or RobertaAction.AskPainPoint or RobertaAction.DeepenPain or RobertaAction.ExploreImpact
            or RobertaAction.DifferentiateCompetitor or RobertaAction.ConfirmUnderstanding or RobertaAction.OfferDemo or RobertaAction.AskForMeeting or RobertaAction.HandleObjection => "ask_contextual_question",
        RobertaAction.PitchFeature or RobertaAction.ShareInsight or RobertaAction.TellUseCase or RobertaAction.CreateCuriosity or RobertaAction.SendUsefulInformation
            or RobertaAction.ExplainFeature or RobertaAction.ExplainStockControl or RobertaAction.ExplainMultistore or RobertaAction.ExplainPricing => "send_value_message",
        RobertaAction.PauseConversation or RobertaAction.RespectBusy => "wait_for_lead",
        RobertaAction.CloseConversation or RobertaAction.DoNotContact => "end_conversation",
        RobertaAction.HandoffToSeller => "handoff_to_human",
        RobertaAction.AnswerIdentity => "disclose_identity",
        RobertaAction.BuildRapport => "bridge_back_to_topic",
        _ => "resume_conversation"
    };

    public static string Sentiment(InboundSignals sig) =>
        sig.Angry || sig.Refusal || sig.OptOut ? "negative"
        : sig.Dissatisfied || sig.No || sig.PainCategory != null || sig.Busy ? "concerned"
        : sig.Yes || sig.Curious || sig.DemoRequest ? "positive" : "neutral";

    public static CommercialPlan Build(ConversationState s, InboundSignals sig, Decision d, ReplyFormat format)
    {
        var hard = d.Score == 0;
        return new CommercialPlan
        {
            CommercialGoal = Goal(d.Action), ConversationStage = s.OpportunityStage, CustomerEngagement = s.Engagement, CustomerSentiment = Sentiment(sig),
            BuyingIntent = s.BuyingIntent, SelectedStrategy = Strategy(d.Action), StrategyReason = d.Reason, NextAction = Next(d.Action),
            ResponseModality = FormatAdvisor.Code(format), HumanHandoff = d.Action == RobertaAction.HandoffToSeller,
            Confidence = hard ? 0.95 : Math.Round(Math.Clamp(d.Score / 100.0, 0.5, 0.95), 2)
        };
    }
}

/// <summary>Never trusts a plan blindly: checks it against the hard rules (consent, identity, handoff, modality) and the lead's explicit preferences.</summary>
public static class PlanValidator
{
    private static readonly HashSet<string> Modalities = new() { "text", "audio", "audio_text" };

    public static List<string> Validate(CommercialPlan p, ConversationState s, InboundSignals sig, bool voiceAvailable)
    {
        var issues = new List<string>();
        if (!PlanBuilder.KnownStrategies.Contains(p.SelectedStrategy)) issues.Add("unknown_strategy");
        if (p.Confidence is < 0 or > 1 || double.IsNaN(p.Confidence)) issues.Add("confidence_out_of_range");
        if (!Modalities.Contains(p.ResponseModality)) issues.Add("unknown_modality");
        else if (p.ResponseModality != "text" && !voiceAvailable) issues.Add("audio_without_tts");
        else if (p.ResponseModality != "text" && s.PreferredChannel == "texto") issues.Add("audio_against_preference");
        if (sig.OptOut && p.SelectedStrategy != "do_not_contact") issues.Add("opt_out_ignored");
        if (sig.AskedIfAi && p.SelectedStrategy != "answer_identity" && !sig.OptOut) issues.Add("identity_not_disclosed");
        if (sig.HumanRequest && !sig.AskedIfAi && !p.HumanHandoff && !sig.OptOut) issues.Add("handoff_required");
        if (p.HumanHandoff != (p.SelectedStrategy == "transfer_human")) issues.Add("handoff_inconsistent");
        var hist = (s.ActionHistory ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (hist.Length >= 3 && hist[^1] == hist[^2] && hist[^2] == hist[^3] && Enum.TryParse<RobertaAction>(hist[^1], out var rep) && PlanBuilder.Strategy(rep) == p.SelectedStrategy
            && p.SelectedStrategy is not ("close_politely" or "do_not_contact" or "transfer_human"))
            issues.Add("action_repeated");
        return issues;
    }
}

public record FollowUpCheck(bool Allowed, string Reason, bool Close = false);

/// <summary>Whether a follow-up may be sent now. Pure function so it can be tested without WhatsApp or database.</summary>
public static class FollowUpPolicy
{
    public const int HardMaxFollowUps = 3;
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(20);

    public static FollowUpCheck Evaluate(ConversationState? s, int attempts, int campaignMax, bool leadReplied, DateTime? lastSentAt, DateTime nowUtc, int localHour, bool sunday)
    {
        if (s != null && (s.OptOut || s.Stage == ConvStage.Encerrada || s.OpportunityStage is "encerrado" or "transferido")) return new(false, "Lead encerrado, em transferência ou pediu para não ser contatado.", true);
        if (leadReplied) return new(false, "O lead já respondeu: follow-up de silêncio não se aplica.", true);
        var followUps = Math.Max(0, attempts - 1);
        if (attempts > campaignMax || followUps >= HardMaxFollowUps) return new(false, "Limite de tentativas atingido.", true);
        if (s != null && s.OpportunityStage == "pausado") return new(false, "Conversa pausada por baixo engajamento.", true);
        if (lastSentAt is { } l && nowUtc - l < MinInterval) return new(false, "Intervalo mínimo entre contatos ainda não passou.");
        if (sunday || localHour < 8 || localHour >= 20) return new(false, "Fora do horário comercial de contato.");
        var hasReason = s != null && (!string.IsNullOrWhiteSpace(s.PainPoints) || s.MainPain != null || !string.IsNullOrWhiteSpace(s.Commitments));
        if (attempts >= 2 && !hasReason) return new(false, "Sem motivo útil para retomar (sem dor, compromisso ou informação nova).", true);
        return new(true, hasReason ? "Retomada contextual justificada." : "Primeiro lembrete após o contato inicial sem resposta.");
    }
}

public record RobertaMetricsReport(int Sent, int Replied, int Qualified, int DemoOffered, int DemoAccepted, int Handoffs, int UsefulHandoffs, int Abandoned, int Suggestions, int CommunicationErrors,
    IReadOnlyDictionary<string, int> Objections, IReadOnlyDictionary<string, int> Strategies, IReadOnlyList<PromptMetrics> ByPrompt)
{
    static double Pct(int a, int b) => b == 0 ? 0 : Math.Round(100.0 * a / b, 1);
    public double ResponseRate => Pct(Replied, Sent);
    public double QualificationRate => Pct(Qualified, Replied);
    public double DemoAcceptedRate => Pct(DemoAccepted, DemoOffered);
    public double UsefulHandoffRate => Pct(UsefulHandoffs, Handoffs);
    public double AbandonmentRate => Pct(Abandoned, Replied);
    public double ErrorRate => Pct(CommunicationErrors, Suggestions);
}

public record PromptMetrics(int Version, int Conversations, int Handoffs, int Demos, double HandoffRate);

/// <summary>Outcome metrics for continuous improvement. Read-only: Roberta never edits her own production prompt; changes go through approved prompt versions.</summary>
public static class RobertaMetrics
{
    private static readonly HashSet<string> UsefulKinds = new() { "demo", "maduro", "rede", "preco", "negociacao", "implantacao", "prova", "material" };

    public static async Task<RobertaMetricsReport> ComputeAsync(AppDbContext db, DateTime sinceUtc, bool includeShadow = false, CancellationToken ct = default)
    {
        var contacts = await db.SdrContacts.AsNoTracking().Where(c => c.CreatedAt >= sinceUtc || c.LastSentAt >= sinceUtc).Select(c => new { c.Id, c.LeadId, c.LastSentAt, c.RepliedAt }).ToListAsync(ct);
        var leadIds = contacts.Select(c => c.LeadId).ToHashSet();
        var states = (await db.ConversationStates.AsNoTracking().ToListAsync(ct)).Where(s => leadIds.Contains(s.LeadId)).ToList();
        var sugs = await db.SdrSuggestions.AsNoTracking().Where(x => x.CreatedAt >= sinceUtc && (includeShadow || x.Status != SuggestionStatus.Shadow))
            .Select(x => new { x.ContactId, x.Action, x.Strategy, x.ValidatorOk, x.PromptVersion }).ToListAsync(ct);

        var sent = contacts.Count(c => c.LastSentAt != null);
        var replied = contacts.Count(c => c.RepliedAt != null);
        var qualified = states.Count(s => (s.CurrentErp != null && s.StoreCount != null) || s.MainPain != null);
        var handoffs = states.Where(s => s.Stage == ConvStage.Handoff).ToList();
        var objections = new Dictionary<string, int>();
        foreach (var s in states)
            foreach (var o in (s.ObjectionLog ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) objections[o] = objections.GetValueOrDefault(o) + 1;
        var strategies = sugs.Where(x => x.Strategy != null).GroupBy(x => x.Strategy!).ToDictionary(g => g.Key, g => g.Count());
        var byPrompt = sugs.Where(x => x.PromptVersion != null).GroupBy(x => x.PromptVersion!.Value).OrderBy(g => g.Key).Select(g =>
        {
            var conv = g.Select(x => x.ContactId).Distinct().Count();
            var h = g.Where(x => x.Action == nameof(RobertaAction.HandoffToSeller)).Select(x => x.ContactId).Distinct().Count();
            var demo = g.Where(x => x.Action is nameof(RobertaAction.OfferDemo) or nameof(RobertaAction.AskForMeeting)).Select(x => x.ContactId).Distinct().Count();
            return new PromptMetrics(g.Key, conv, h, demo, conv == 0 ? 0 : Math.Round(100.0 * h / conv, 1));
        }).ToList();

        return new RobertaMetricsReport(sent, replied, qualified, states.Count(s => s.DemoOffered), states.Count(s => s.DemoAccepted), handoffs.Count,
            handoffs.Count(s => s.HandoffKind != null && UsefulKinds.Contains(s.HandoffKind)),
            states.Count(s => s.OpportunityStage == "pausado" || (s.Stage == ConvStage.Encerrada && !s.DemoAccepted && !s.OptOut) || s.LossSignals >= 3),
            sugs.Count, sugs.Count(x => !x.ValidatorOk), objections, strategies, byPrompt);
    }
}
