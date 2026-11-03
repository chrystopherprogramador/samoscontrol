using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SamosControl.Domain;

namespace SamosControl.Application;

/// <summary>Actions the commercial engine may allow. The LLM only phrases the chosen action; it never picks it.</summary>
public enum RobertaAction
{
    AskCurrentERP, AskStoreCount, AskPainPoint, ExplainFeature, ExplainStockControl, ExplainMultistore, ExplainPricing,
    OfferDemo, ScheduleDemo, HandleObjection, FollowUp, HandoffToSeller, CloseConversation, DoNotContact
}

/// <summary>What the inbound message means, extracted with deterministic rules (no LLM).</summary>
public record InboundSignals
{
    public string Normalized { get; init; } = "";
    public bool OptOut { get; init; }
    public bool Refusal { get; init; }
    public bool HumanRequest { get; init; }
    public bool Price { get; init; }
    public bool Discount { get; init; }
    public bool DemoRequest { get; init; }
    public bool Yes { get; init; }
    public bool No { get; init; }
    public bool Maybe { get; init; }
    public bool Satisfied { get; init; }
    public bool Angry { get; init; }
    public bool OutOfIcp { get; init; }
    public bool AsksQuestion { get; init; }
    public bool Identity { get; init; }
    public bool Short { get; init; }
    public bool NoErp { get; init; }
    public string? ErpMention { get; init; }
    public int? StoreCount { get; init; }
    public bool TopicFiscal { get; init; }
    public bool TopicStock { get; init; }
    public bool TopicMultistore { get; init; }
    public bool TopicImplementation { get; init; }
}

public static class InboundClassifier
{
    public static readonly string[] KnownErps =
    {
        "bling", "tiny", "olist", "omie", "totvs", "protheus", "linx", "microvix", "vhsys", "sankhya", "conta azul", "contaazul",
        "nibo", "mercos", "sige", "millennium", "winthor", "sap", "bimer", "zeus", "saipos", "ideal soft", "ecosis", "tray", "ideris"
    };

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString().ToLowerInvariant(), @"\s+", " ").Trim();
    }

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReOptOut = R(@"\b(descadastr\w*|remov\w+ (meu|o meu|esse) (numero|contato)|nao quero (mais )?(receber|mensagem|contato)|nao (me )?(chame|envie|mande|escreva)\w*( mais)?|para de (me )?(mandar|enviar|chamar)|me (tire|exclua|remova)\w*|spam|bloquear|vou (te )?denunciar)\b");
    private static readonly Regex ReOptOutStart = R(@"^(sair|cancelar|remover|pare|parar|stop)\b");
    private static readonly Regex ReIdentity = R(@"\b(quem (e|fala|esta falando|sao voces|e voce)|de onde (voce|voces|e)|qual (a )?empresa|do que se trata|que (empresa|numero) (e|e esse)|como (conseguiu|conseguiram) (meu|o meu) (numero|contato))\b|^(oi|ola|opa|bom dia|boa tarde|boa noite|e ai)\b[^a-z0-9]*(tudo bem|td bem|tudo certo)?[^a-z0-9]*$");
    private static readonly Regex ReRefusal = R(@"\b(nao tenho interesse|sem interesse|nao me interessa|nao tem interesse|nao quero|nao preciso|nao precisamos|nao e pra mim|nao vamos precisar|nao pretendo|nao pretendemos|dispenso)\b");
    private static readonly Regex ReHuman = R(@"\b(falar com (alguem|uma pessoa|um humano|o dono|um vendedor|um atendente|voces)|quero (um |o )?(humano|atendente|vendedor|especialista)|atendente|humano|pessoa de verdade|e (um )?robo|voce e (uma )?(ia|robo|maquina))\b");
    private static readonly Regex RePrice = R(@"\b(preco|precos|quanto (custa|fica|sai|cobram|e o|e a)|qual (o |e o )?(valor|preco)|mensalidade|orcamento|investimento|tabela de preco|planos?)\b");
    private static readonly Regex ReDiscount = R(@"\b(desconto|promocao|mais barato|abaixar|baixar o preco|negociar|condicao especial)\b");
    private static readonly Regex ReDemo = R(@"\b(demonstracao|demo|apresentacao|ver funcionando|ver o sistema|conhecer o sistema|teste gratis|reuniao|agendar|marcar (uma )?(conversa|reuniao|call)|videochamada)\b");
    private static readonly Regex ReYes = R(@"^(sim|claro|pode|pode sim|ok|okay|quero|tenho|com certeza|bora|vamos|isso|isso mesmo|positivo|uhum|aham|beleza|fechado|tenho sim|gostaria|quero sim|pode ser sim|interessante|me interessa)\b");
    private static readonly Regex ReNo = R(@"^(nao|nao tenho|nao temos|nunca|nenhum|nenhuma|nada|n|nope|negativo|nao obrigad\w*|nao precisa)\b");
    private static readonly Regex ReMaybe = R(@"\b(talvez|depende|nao sei|quem sabe|vou ver|vou pensar|pode ser|mais pra frente|mais tarde|me chama depois|outra hora)\b");
    private static readonly Regex ReSatisfied = R(@"\b(tudo bem|esta tudo (bem|certo|ok)|ta tudo (bem|certo|ok)|estou satisfeit\w+|estamos satisfeit\w+|satisfeit\w+|sem problemas?|nao tenho (problema|dor|dificuldade)s?|nao temos (problema|dificuldade)s?|funciona bem|atende (bem|direitinho|certinho|nossa|a gente|o que)|estamos bem|to de boa|de boa|nada a reclamar|ja resolve|ja atende|ta funcionando|esta funcionando|ja tenho (um )?sistema|ja usamos|ja uso)\b");
    private static readonly Regex ReAngry = R(@"\b(absurdo|ridiculo|palhacada|irritad\w+|me incomod\w+|estou (puto|bravo|com raiva)|pessimo|lixo|vergonha|porra|caralho|merda|idiota|burro|procon|denunci\w+|processar|me deixa em paz|chat[oa]s?|chatice|cansad\w+ (disso|de voces))\b");
    private static readonly Regex ReOutOfIcp = R(@"\b(nao (somos|sou|e|temos) (uma )?(loja|comercio)|nao vendemos (roupa|calcado|moda)|nao trabalh\w+ com (moda|roupa|calcado|varejo)|nao e do (meu|nosso) ramo|somos (uma )?(industria|clinica|escola|oficina|restaurante|escritorio)|sou (dentista|medic\w|advogad\w|contador|autonom\w))\b");
    private static readonly Regex ReQuestion = R(@"(\?|^(como|quanto|qual|quais|quando|onde|voces|vcs|tem|faz|fazem|funciona|integra|da pra|da para|pode|existe|o que|que tipo|precisa|e possivel)\b)");
    private static readonly Regex ReNoErp = R(@"\b(nao (uso|usamos|tenho|temos) (nenhum )?(sistema|erp|programa)|sem sistema|nenhum sistema|uso (uma )?planilha|usamos (uma )?planilha|caderno|controle (manual|no papel)|na mao|no papel|excel)\b");
    private static readonly Regex ReStoresNum = R(@"\b(\d{1,3})\s*(lojas?|filiais?|unidades?|pontos? de venda|pdvs?)\b");
    private static readonly Regex ReStoresWord = R(@"\b(uma|duas|dois|tres|quatro|cinco|seis|sete|oito|nove|dez)\s*(lojas?|filiais?|unidades?)\b");
    private static readonly Regex ReOneStore = R(@"\b(so uma|apenas uma|somente uma|uma unica|loja unica)\b|^uma( loja)?$");
    private static readonly Regex ReFiscal = R(@"\b(fiscal|nota fiscal|nfe|nf-e|nfce|nfc-e|sped|imposto|tributa\w+|emissao de nota|emitir nota)\b");
    private static readonly Regex ReStock = R(@"\b(estoque|inventario|grade|tamanho|cor|sku|reposicao|rfid|etiqueta|contagem)\b");
    private static readonly Regex ReMulti = R(@"\b(multiloja|multi-loja|varias lojas|filiais?|rede de lojas|centralizar|todas as lojas|matriz)\b");
    private static readonly Regex ReImpl = R(@"\b(implanta\w+|migra\w+|prazo|treinamento|quanto tempo|como funciona (a )?(implantacao|instalacao)|instala\w+|importar (os )?dados)\b");

    private static readonly Dictionary<string, int> Words = new()
    { ["uma"] = 1, ["duas"] = 2, ["dois"] = 2, ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["sete"] = 7, ["oito"] = 8, ["nove"] = 9, ["dez"] = 10 };

    public static InboundSignals Classify(string? raw)
    {
        var t = Normalize(raw);
        var wordCount = t.Length == 0 ? 0 : t.Split(' ').Length;
        var optOut = ReOptOut.IsMatch(t) || ReOptOutStart.IsMatch(t);
        var refusal = !optOut && ReRefusal.IsMatch(t);
        var no = ReNo.IsMatch(t);
        var yes = ReYes.IsMatch(t) && !no;

        int? stores = null;
        var m = ReStoresNum.Match(t);
        if (m.Success) stores = int.Parse(m.Groups[1].Value);
        else if ((m = ReStoresWord.Match(t)).Success) stores = Words[m.Groups[1].Value];
        else if (ReOneStore.IsMatch(t)) stores = 1;

        string? erp = null;
        foreach (var k in KnownErps)
            if (Regex.IsMatch(t, @"\b" + Regex.Escape(k) + @"\b")) { erp = k; break; }

        return new InboundSignals
        {
            Normalized = t,
            OptOut = optOut,
            Refusal = refusal,
            HumanRequest = ReHuman.IsMatch(t),
            Price = RePrice.IsMatch(t),
            Discount = ReDiscount.IsMatch(t),
            DemoRequest = ReDemo.IsMatch(t),
            Yes = yes,
            No = no,
            Maybe = ReMaybe.IsMatch(t),
            Satisfied = ReSatisfied.IsMatch(Regex.Replace(t, @"\btudo bem\s*\?", "")) && !ReNoErp.IsMatch(t),
            Angry = ReAngry.IsMatch(t),
            OutOfIcp = ReOutOfIcp.IsMatch(t),
            AsksQuestion = ReQuestion.IsMatch(t) && !ReIdentity.IsMatch(t),
            Identity = ReIdentity.IsMatch(t),
            Short = wordCount <= 3,
            NoErp = ReNoErp.IsMatch(t),
            ErpMention = erp,
            StoreCount = stores,
            TopicFiscal = ReFiscal.IsMatch(t),
            TopicStock = ReStock.IsMatch(t),
            TopicMultistore = ReMulti.IsMatch(t),
            TopicImplementation = ReImpl.IsMatch(t)
        };
    }
}

public record Decision(RobertaAction Action, string Reason, bool NeedsLlm, string? Topic = null);

public static class ConversationStateRules
{
    public const string TopicErp = "erp", TopicStores = "stores", TopicPain = "pain";
    public static HashSet<string> Set(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    public static string Join(IEnumerable<string> s) => string.Join(",", s.OrderBy(x => x));

    public static string? TopicOf(string? action) => action switch
    {
        nameof(RobertaAction.AskCurrentERP) => TopicErp,
        nameof(RobertaAction.AskStoreCount) => TopicStores,
        nameof(RobertaAction.AskPainPoint) => TopicPain,
        _ => null
    };

    /// <summary>Updates the persisted state with what the inbound message revealed. Mutates and returns the same instance.</summary>
    public static ConversationState Apply(ConversationState s, InboundSignals sig, string rawText)
    {
        var asked = Set(s.QuestionsAsked); var answered = Set(s.QuestionsAnswered);
        s.TurnCount++; s.LastContactAt = DateTime.UtcNow;

        if (sig.OptOut) { s.OptOut = true; s.Stage = ConvStage.Encerrada; }
        if (sig.HumanRequest) s.HumanRequested = true;
        if (sig.Price) s.PriceDiscussed = true;

        if (sig.ErpMention != null && s.CurrentErp == null) { s.CurrentErp = sig.ErpMention; answered.Add(TopicErp); }
        if (sig.NoErp) { s.CurrentErp ??= "nenhum (planilha/manual)"; answered.Add(TopicErp); }
        if (sig.StoreCount.HasValue) { s.StoreCount = sig.StoreCount; answered.Add(TopicStores); }

        // The reply to the question we asked counts as an answer, even a negative one: it must never be asked again.
        var lastTopic = TopicOf(s.LastAction);
        if (lastTopic != null && !sig.OptOut) answered.Add(lastTopic);
        if (lastTopic == TopicPain && !sig.Yes && !sig.No && !sig.Satisfied && !sig.Maybe && !sig.Refusal && rawText.Trim().Length >= 5)
            s.PainPoints = Append(s.PainPoints, rawText.Trim(), 900);

        if (s.LastAction == nameof(RobertaAction.OfferDemo))
        {
            if (sig.Yes || sig.DemoRequest) s.DemoAccepted = true;
            else if (sig.No || sig.Refusal) s.Objections = Append(s.Objections, "recusou demonstração", 900);
        }
        if (sig.DemoRequest) s.DemoAccepted = true;

        var negative = sig.No || sig.Satisfied || sig.Refusal;
        if (sig.Satisfied) { s.SatisfiedWithCurrent = true; s.Objections = Append(s.Objections, "satisfeito com a solução atual", 900); }
        if (negative && !(sig.StoreCount.HasValue || sig.ErpMention != null || sig.NoErp)) s.NegativeStreak++;
        else if (!sig.Maybe && !sig.Short) s.NegativeStreak = 0;

        if (sig.Refusal) s.Objections = Append(s.Objections, "recusa explícita", 900);
        s.InterestLevel = sig.Refusal || sig.OptOut || sig.Angry ? 1
            : sig.DemoRequest || sig.HumanRequest || sig.Discount ? 3
            : sig.Price || (sig.Yes && s.LastAction != null) ? Math.Max(s.InterestLevel, 2)
            : s.InterestLevel;

        s.QuestionsAsked = Join(asked); s.QuestionsAnswered = Join(answered);
        return s;
    }

    /// <summary>Registers an action as sent/proposed so the engine never repeats it.</summary>
    public static void RecordAction(ConversationState s, RobertaAction a)
    {
        s.LastAction = a.ToString();
        var asked = Set(s.QuestionsAsked);
        var t = TopicOf(a.ToString());
        if (t != null) asked.Add(t);
        if (a == RobertaAction.OfferDemo) s.DemoOffered = true;
        if (a == RobertaAction.HandoffToSeller) s.Stage = ConvStage.Handoff;
        else if (a is RobertaAction.CloseConversation or RobertaAction.DoNotContact) s.Stage = ConvStage.Encerrada;
        else if (a == RobertaAction.OfferDemo) s.Stage = ConvStage.Demo;
        else if (a is RobertaAction.ExplainFeature or RobertaAction.ExplainMultistore or RobertaAction.ExplainStockControl or RobertaAction.ExplainPricing) s.Stage = ConvStage.Apresentacao;
        else if (t != null) s.Stage = ConvStage.Qualificacao;
        s.QuestionsAsked = Join(asked);
    }

    private static string Append(string? cur, string add, int max)
    {
        var r = string.IsNullOrWhiteSpace(cur) ? add : cur + "; " + add;
        return r.Length > max ? r[^max..] : r;
    }
}

public static class CommercialDecisionEngine
{
    public const int MaxTurns = 8;
    public const int BigNetwork = 10;

    /// <summary>Deterministic next step. <paramref name="kbMatch"/> = the knowledge base has something relevant to the message; <paramref name="kbPricing"/> = it has pricing info.</summary>
    public static Decision Decide(ConversationState s, InboundSignals sig, bool kbMatch, bool kbPricing)
    {
        if (s.OptOut || sig.OptOut) return new(RobertaAction.DoNotContact, "Lead pediu para não receber mais contato.", false);
        if (sig.Refusal && !sig.AsksQuestion || sig.Refusal && sig.No) return new(RobertaAction.CloseConversation, "Recusa clara.", false);
        if (sig.OutOfIcp) return new(RobertaAction.CloseConversation, "Lead fora do perfil (ICP).", false);
        if (sig.Angry) return new(RobertaAction.HandoffToSeller, "Lead irritado/reclamação: atendimento humano.", false);
        if (sig.HumanRequest || s.HumanRequested) return new(RobertaAction.HandoffToSeller, "Lead pediu atendimento humano.", false);
        if (sig.Discount) return new(RobertaAction.HandoffToSeller, "Negociação/desconto: humano.", false);
        if (sig.DemoRequest || s.DemoAccepted) return new(RobertaAction.HandoffToSeller, "Lead quer/aceitou demonstração.", false);
        if (s.StoreCount >= BigNetwork) return new(RobertaAction.HandoffToSeller, $"Rede com {s.StoreCount} lojas: prioridade para vendedor.", false);
        if (sig.Price)
            return kbPricing && !s.PriceDiscussedBefore() ? new(RobertaAction.ExplainPricing, "Preço pedido e há informação na base.", true)
                : new(RobertaAction.HandoffToSeller, "Preço/proposta: humano.", false);
        if (s.TurnCount > MaxTurns) return new(RobertaAction.HandoffToSeller, "Limite de turnos automáticos atingido.", false);

        if (sig.AsksQuestion && !sig.Yes && !sig.No)
        {
            if (!kbMatch) return new(RobertaAction.HandoffToSeller, "Pergunta fora da base de conhecimento.", false);
            var a = sig.TopicStock ? RobertaAction.ExplainStockControl : sig.TopicMultistore ? RobertaAction.ExplainMultistore : RobertaAction.ExplainFeature;
            return new(a, "Lead fez uma pergunta; responder só com a base.", true);
        }

        // Satisfied / repeated negatives: change strategy and stop inventing pain points.
        if (s.SatisfiedWithCurrent || s.NegativeStreak >= 2)
            return s.DemoOffered ? new(RobertaAction.CloseConversation, "Cliente satisfeito e demo já oferecida: encerrar com elegância.", false)
                : new(RobertaAction.OfferDemo, "Cliente satisfeito: oferta leve, sem perguntar dores.", true);

        var asked = ConversationStateRules.Set(s.QuestionsAsked); var answered = ConversationStateRules.Set(s.QuestionsAnswered);
        bool Done(string t) => asked.Contains(t) || answered.Contains(t);
        if (!Done(ConversationStateRules.TopicErp) && s.CurrentErp == null) return new(RobertaAction.AskCurrentERP, "Descobrir sistema atual.", true, ConversationStateRules.TopicErp);
        if (!Done(ConversationStateRules.TopicStores) && s.StoreCount == null) return new(RobertaAction.AskStoreCount, "Descobrir porte (lojas).", true, ConversationStateRules.TopicStores);
        if (!Done(ConversationStateRules.TopicPain) && string.IsNullOrWhiteSpace(s.PainPoints)) return new(RobertaAction.AskPainPoint, "Entender a principal dor.", true, ConversationStateRules.TopicPain);

        if (!s.DemoOffered) return new(RobertaAction.OfferDemo, "Qualificação concluída: oferecer demonstração.", true);
        if (s.InterestLevel >= 2 || sig.Yes) return new(RobertaAction.HandoffToSeller, "Interesse confirmado após demo oferecida.", false);
        return new(RobertaAction.CloseConversation, "Sem avanço após qualificação e demo oferecida.", false);
    }

    private static bool PriceDiscussedBefore(this ConversationState s) => s.LastAction == nameof(RobertaAction.ExplainPricing);
}
