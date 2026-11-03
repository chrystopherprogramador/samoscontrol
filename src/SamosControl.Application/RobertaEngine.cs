using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SamosControl.Domain;

namespace SamosControl.Application;

/// <summary>Actions the commercial engine may allow. The LLM only phrases the chosen action; it never picks it.</summary>
public enum RobertaAction
{
    AskCurrentERP, AskStoreCount, AskPainPoint, ExplainFeature, ExplainStockControl, ExplainMultistore, ExplainPricing,
    OfferDemo, ScheduleDemo, HandleObjection, FollowUp, HandoffToSeller, CloseConversation, DoNotContact,
    /// <summary>Acknowledge a pain, connect it briefly to the product and ask ONE probing question.</summary>
    DeepenPain,
    /// <summary>Connect the pain to a product angle (insight / mini-pitch); a question is optional.</summary>
    PitchFeature,
    /// <summary>The lead is busy: short, respectful, no questions.</summary>
    RespectBusy,

    // ---- Roberta 4.0 ----
    // Canonical names kept from earlier versions: DiscoverNeed = AskCurrentERP/AskStoreCount/AskPainPoint, ClosePolitely = CloseConversation,
    // TransferHuman = HandoffToSeller, SendAudio is a response modality (see ReplyFormat), not an action.
    BuildRapport, ExploreImpact, ShareInsight, TellUseCase, DifferentiateCompetitor, CreateCuriosity, ConfirmUnderstanding,
    AskForMeeting, SendUsefulInformation, PauseConversation,
    /// <summary>The lead asked whether they are talking to an AI: fixed transparent answer plus a human option (no LLM).</summary>
    AnswerIdentity
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
    /// <summary>Pain category code (see <see cref="Pain"/>) when the message states a pain; null otherwise.</summary>
    public string? PainCategory { get; init; }
    /// <summary>0 none, 1 mentioned, 2 strong ("sempre", "todo dia"...).</summary>
    public int PainIntensity { get; init; }
    public bool Dissatisfied { get; init; }
    public bool Busy { get; init; }
    public bool Curious { get; init; }
    public bool Informal { get; init; }
    public bool Technical { get; init; }
    public int WordCount { get; init; }
    /// <summary>Objection code (see <see cref="Obj"/>) or null.</summary>
    public string? Objection { get; init; }
    public bool AskedIfAi { get; init; }
    public bool TextOnlyRequest { get; init; }
    public bool PrefersAudio { get; init; }
    public string? PreferredTime { get; init; }
    public bool OffTopic { get; init; }
    public bool Insistence { get; init; }
}

/// <summary>Pain categories (stable codes persisted in <see cref="ConversationState.MainPain"/>).</summary>
public static class Pain
{
    public const string StockDivergence = "estoque_divergencia", Stock = "estoque", Fiscal = "fiscal", Financial = "financeiro",
        Sales = "vendas_pdv", System = "sistema_atual", Customers = "clientes", Multistore = "multiloja", Ecommerce = "ecommerce", Other = "outra";
}

public static class InboundClassifier
{
    public static readonly string[] KnownErps =
    {
        "bling", "tiny", "olist", "omie", "totvs", "protheus", "linx", "microvix", "vhsys", "sankhya", "conta azul", "contaazul",
        "nibo", "mercos", "sige", "millennium", "winthor", "sap", "bimer", "zeus", "saipos", "ideal soft", "ecosis", "tray", "ideris",
        "datasystem", "hiper", "gdoor", "kmm"
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
    private static readonly Regex ReAngry = R(@"\b(absurdo|ridiculo|palhacada|irritad\w+|me incomod\w+|estou (puto|bravo|com raiva)|pessimo|lixo|vergonha|porra|caralho|merda|idiota|burro|procon|denunci\w+|processar|me deixa em paz|cansad\w+ (disso|de voces)|(voces|vcs|voce|sao|estao|mensagens?|ligacoes) (\w+ )?chat[oa]s?|voce e (\w+ )?chat[oa]s?|chatice)\b");
    private static readonly Regex ReOutOfIcp = R(@"\b(nao (somos|sou|e|temos) (uma )?(loja|comercio)|nao vendemos (roupa|calcado|moda)|nao trabalh\w+ com (moda|roupa|calcado|varejo)|nao e do (meu|nosso) ramo|somos (uma )?(industria|clinica|escola|oficina|restaurante|escritorio)|sou (dentista|medic\w|advogad\w|contador|autonom\w))\b");
    // "Oi, tudo bem?" is a greeting, not a product question that would need the knowledge base.
    private static readonly Regex ReSmallTalk = R(@"\b(tudo (bem|bom|certo|joia)|td bem|como vai|como (voce|vc) esta|beleza)\b\s*\??");
    private static readonly Regex ReQuestion = R(@"(\?|^(como|quanto|qual|quais|quando|onde|voces|vcs|tem|faz|fazem|funciona|integra|da pra|da para|pode|existe|o que|que tipo|precisa|e possivel)\b)");
    private static readonly Regex ReNoErp = R(@"\b(nao (uso|usamos|tenho|temos) (nenhum )?(sistema|erp|programa)|sem sistema|nenhum sistema|uso (uma )?planilha|usamos (uma )?planilha|caderno|controle (manual|no papel)|na mao|no papel|excel)\b");
    private static readonly Regex ReStoresNum = R(@"\b(\d{1,3})\s*(lojas?|filiais?|unidades?|pontos? de venda|pdvs?)\b");
    private static readonly Regex ReStoresWord = R(@"\b(uma|duas|dois|tres|quatro|cinco|seis|sete|oito|nove|dez)\s*(lojas?|filiais?|unidades?)\b");
    private static readonly Regex ReOneStore = R(@"\b(so uma|apenas uma|somente uma|uma unica|loja unica)\b|^uma( loja)?$");
    private static readonly Regex ReFiscal = R(@"\b(fiscal|nota fiscal|nfe|nf-e|nfce|nfc-e|sped|imposto|tributa\w+|emissao de nota|emitir nota)\b");
    private static readonly Regex ReStock = R(@"\b(estoque|inventario|grade|tamanho|cor|sku|reposicao|rfid|etiqueta|contagem)\b");
    private static readonly Regex ReMulti = R(@"\b(multiloja|multi-loja|varias lojas|filiais?|rede de lojas|centralizar|todas as lojas|matriz)\b");
    private static readonly Regex ReImpl = R(@"\b(implanta\w+|migra\w+|prazo|treinamento|quanto tempo|como funciona (a )?(implantacao|instalacao)|instala\w+|importar (os )?dados)\b");

    private const string Problem = @"(problema\w*|dificuldade\w*|dor de cabeca|complicad\w+|trabalho(so)?|retrabalho|bagunc\w+|desorganiz\w+|demor\w+|manual|na mao|erro\w*|errad\w+|falh\w+|trav\w+|lent\w+|perco|perdemos|prejuizo|atras\w+|confus\w+)";
    private static readonly Regex RePainStockDiv = R(@"\b(fura\w*|furo\w*|divergen\w+|nao (bate|confere|fecha)|estoque (errado|desatualizado|negativo)|(sumi\w*|perd\w+) (de )?(produto|mercadoria|item|peca)s?|diferenca (de|no|do) estoque)\b");
    private static readonly Regex RePainStock = R(@"\b(estoque|inventario|reposicao|contagem|grade)\b.{0,60}\b" + Problem + @"\b|\b" + Problem + @"\b.{0,60}\b(estoque|inventario|reposicao|contagem)\b|\bgestao de estoque\b");
    private static readonly Regex RePainFiscal = R(@"\b(fiscal|nota fiscal|notas|nfe|nfce|nf-e|nfc-e|sped|imposto\w*|tributa\w+|emissao)\b.{0,60}\b(" + Problem + @"|rejeit\w+|multa\w*)\b|\brejeicao\b");
    private static readonly Regex RePainFinancial = R(@"\b(financeiro|fluxo de caixa|conciliacao|boletos?|inadimpl\w+|contas a (pagar|receber)|fechamento de caixa)\b.{0,60}\b" + Problem + @"\b");
    private static readonly Regex RePainSales = R(@"\b(pdv|caixa|fila|checkout|venda|vendas)\b.{0,60}\b(lent\w+|trav\w+|cai(u|mos)?|fora do ar|demor\w+|erro\w*|falh\w+)\b");
    private static readonly Regex RePainEcom = R(@"\b(e-?commerce|loja virtual|site|marketplace|mercado livre|shopee|nuvemshop|shopify|vtex)\b.{0,60}\b(integra\w+|sincroni\w+|manual|divergen\w+|desatualiz\w+|" + Problem + @")\b");
    private static readonly Regex RePainMulti = R(@"\b(varias lojas|filiais?|multiloja|todas as lojas|cada loja|matriz)\b.{0,60}\b(dificil|complicad\w+|bagunc\w+|nao consigo|nao conseguimos|trabalh\w+|descentraliz\w+|" + Problem + @")\b");
    private static readonly Regex RePainCustomers = R(@"\b(clientes?|fidelidade|recompra)\b.{0,50}\b(some\w*|nao volta\w*|nao sei|nao (temos|tenho) (controle|cadastro)|sem controle|perd\w+)\b|\bnao sei quem (sao|e) (meus |os )?clientes\b");
    private static readonly Regex RePainSystem = R(@"\b(sistema|programa|erp|datasystem|sistema atual)\b.{0,50}\b(ruim|lent\w+|limitad\w+|antig\w+|trav\w+|caro|complicad\w+|burocratic\w+|fraco|instavel|cai|falha\w*|deixa a desejar|nao atende|nao integra)\b|\bsuporte (ruim|demora\w*|nao responde|pessimo|fraco)\b|\bnao (integra|emite|gera|controla)\b|\b(trocar|mudar) (de )?sistema\b|\bfalta (de )?(integracao|relatorio\w*|recurso\w*)\b");
    private static readonly Regex RePainGeneric = R(@"\b(dor de cabeca|dificuldade\w*|trabalhoso|retrabalho|perco tempo|perdemos tempo|toma (muito )?tempo|da trabalho|bagunc\w+|complicad\w+|prejuizo|problemas?|reclam\w+)\b");
    private static readonly Regex RePainStrong = R(@"\b(sempre|toda hora|todo dia|toda semana|todo mes|muito|demais|absurd\w+|prejuizo|perdendo|perdemos|nunca bate|vive|vivo|constante\w*)\b");
    private static readonly Regex ReBusy = R(@"\b(sem tempo|estou (muito )?(corrido|ocupad\w+|atendendo|na correria|em reuniao|dirigindo)|to (corrido|ocupad\w+|na correria|atendendo)|agora nao (da|posso)|nao (posso|consigo) (falar|conversar) agora|nao e uma boa hora|momento (ruim|corrido)|me (chama|procura|liga|fala) (depois|mais tarde|amanha)|mais tarde|outro (momento|dia)|amanha)\b");
    private static readonly Regex ReCurious = R(@"\b(como (funciona|e que funciona|fica)|me (explica|conta|fala)|conta mais|queria saber mais|gostaria de saber|pode explicar|quero entender|interessante|me interessa)\b");
    private static readonly Regex ReInformal = R(@"\b(vc|vcs|blz|tb|tbm|pq|kk+|rs+|uai|opa|valeu|brother|mano|cara|nossa|ne|sussa|bora|slk|tmj)\b|😂|😅|🙏|👍|😉");
    private static readonly Regex ReTechnical = R(@"\b(api|integracao|sped|wms|rfid|sku|erp|nfe|nf-e|nfce|cest|ncm|cfop|tef|webhook|sincroniz\w+|lote|serie|xml)\b");

    private static readonly Dictionary<string, int> Words = new()
    { ["uma"] = 1, ["duas"] = 2, ["dois"] = 2, ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["sete"] = 7, ["oito"] = 8, ["nove"] = 9, ["dez"] = 10 };

    public static InboundSignals Classify(string? raw)
    {
        var t = Normalize(raw);
        var wordCount = t.Length == 0 ? 0 : t.Split(' ').Length;
        var optOut = ReOptOut.IsMatch(t) || ReOptOutStart.IsMatch(t);
        var objection = Roberta4Rules.Objection(t);
        var refusal = !optOut && ReRefusal.IsMatch(t) && objection != Obj.NaoTrocar;
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

        var satisfiedRaw = ReSatisfied.IsMatch(Regex.Replace(t, @"\btudo bem\s*\?", "")) && !ReNoErp.IsMatch(t);
        string? pain = RePainStockDiv.IsMatch(t) ? Pain.StockDivergence
            : RePainFiscal.IsMatch(t) ? Pain.Fiscal
            : RePainFinancial.IsMatch(t) ? Pain.Financial
            : RePainSales.IsMatch(t) ? Pain.Sales
            : RePainEcom.IsMatch(t) ? Pain.Ecommerce
            : RePainMulti.IsMatch(t) ? Pain.Multistore
            : RePainCustomers.IsMatch(t) ? Pain.Customers
            : RePainStock.IsMatch(t) ? Pain.Stock
            : RePainSystem.IsMatch(t) ? Pain.System
            : !satisfiedRaw && !refusal && !optOut && RePainGeneric.IsMatch(t) ? Pain.Other : null;
        var painStrong = pain != null && RePainStrong.IsMatch(t);
        // "Não quero trocar de sistema" is an objection, not a complaint about the system.
        if (objection == Obj.NaoTrocar) painStrong = false;
        if (objection == Obj.NaoTrocar && pain == Pain.System) pain = null;
        var aiQuestion = Roberta4Rules.AskedIfAi(t);
        var demoReq = objection == Obj.Material ? Roberta4Rules.DemoCore(t) : ReDemo.IsMatch(t);

        return new InboundSignals
        {
            Normalized = t,
            OptOut = optOut,
            Refusal = refusal,
            HumanRequest = ReHuman.IsMatch(t),
            Price = RePrice.IsMatch(t),
            Discount = ReDiscount.IsMatch(t),
            DemoRequest = demoReq,
            Yes = yes,
            No = no,
            Maybe = ReMaybe.IsMatch(t),
            Satisfied = satisfiedRaw && (pain == null || pain == Pain.Other),
            Angry = ReAngry.IsMatch(t),
            OutOfIcp = ReOutOfIcp.IsMatch(t),
            AsksQuestion = ReQuestion.IsMatch(ReSmallTalk.Replace(t, " ").Trim()) && !ReIdentity.IsMatch(t),
            Identity = ReIdentity.IsMatch(t),
            Short = wordCount <= 3,
            NoErp = ReNoErp.IsMatch(t),
            ErpMention = erp,
            StoreCount = stores,
            TopicFiscal = ReFiscal.IsMatch(t),
            TopicStock = ReStock.IsMatch(t),
            TopicMultistore = ReMulti.IsMatch(t),
            TopicImplementation = ReImpl.IsMatch(t),
            PainCategory = pain,
            PainIntensity = pain == null ? 0 : painStrong ? 2 : 1,
            Dissatisfied = RePainSystem.IsMatch(t) && objection != Obj.NaoTrocar,
            Busy = ReBusy.IsMatch(t),
            Curious = ReCurious.IsMatch(t),
            Informal = ReInformal.IsMatch(raw ?? "") || ReInformal.IsMatch(t),
            Technical = ReTechnical.IsMatch(t),
            WordCount = wordCount,
            Objection = objection,
            AskedIfAi = aiQuestion,
            TextOnlyRequest = Roberta4Rules.TextOnly(t),
            PrefersAudio = Roberta4Rules.PrefersAudio(t),
            PreferredTime = Roberta4Rules.PreferredTime(t),
            OffTopic = Roberta4Rules.OffTopic(t),
            Insistence = Roberta4Rules.Insistence(t)
        };
    }
}

public record Decision(RobertaAction Action, string Reason, bool NeedsLlm, string? Topic = null, string? HandoffKind = null, int Score = 0);

public static class ConversationStateRules
{
    public const string TopicErp = "erp", TopicStores = "stores", TopicPain = "pain";
    /// <summary>Silence after which a new message counts as the lead coming back (the conversation is resumed, not restarted).</summary>
    public static readonly TimeSpan ReturnAfter = TimeSpan.FromHours(48);
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
    public static ConversationState Apply(ConversationState s, InboundSignals sig, string rawText, bool viaAudio = false, TimeSpan? replyDelay = null)
    {
        var asked = Set(s.QuestionsAsked); var answered = Set(s.QuestionsAnswered);
        var erpBefore = s.CurrentErp;
        s.Returned = s.TurnCount > 0 && s.LastInboundAt is { } prev && DateTime.UtcNow - prev > ReturnAfter;
        if (s.Returned) { s.LossSignals = 0; if (s.OpportunityStage == "pausado") s.OpportunityStage = "aprofundamento"; }
        s.TurnCount++; s.LastContactAt = DateTime.UtcNow; s.LastInboundAt = s.LastContactAt;
        s.StateVersion++;
        s.InboundWords += sig.WordCount;
        if (viaAudio) s.ClientSentAudio = true;
        var lastTopic = TopicOf(s.LastAction);
        var storeChanged = sig.StoreCount.HasValue && sig.StoreCount != s.StoreCount;
        var spontaneous = (sig.ErpMention != null && lastTopic != TopicErp) || (sig.StoreCount.HasValue && lastTopic != TopicStores) || (sig.PainCategory != null && lastTopic != TopicPain);

        if (sig.OptOut) { s.OptOut = true; s.Stage = ConvStage.Encerrada; }
        if (sig.HumanRequest && !sig.AskedIfAi) s.HumanRequested = true;
        if (sig.Price) s.PriceDiscussed = true;
        if (sig.AskedIfAi) s.AiDisclosed = true;
        if (sig.TextOnlyRequest) s.PreferredChannel = "texto";
        else if (sig.PrefersAudio) s.PreferredChannel = "audio";
        if (sig.PreferredTime != null) s.PreferredTime = sig.PreferredTime;
        if (sig.Objection != null)
        {
            s.ObjectionLog = AppendCsv(s.ObjectionLog, sig.Objection, 300);
            s.Objections = Append(s.Objections, "objeção: " + sig.Objection, 900);
        }

        if (sig.ErpMention != null && s.CurrentErp == null) { s.CurrentErp = sig.ErpMention; answered.Add(TopicErp); }
        else if (lastTopic == TopicErp && s.CurrentErp == null && CleanErp(rawText, sig) is { } erpName) s.CurrentErp = erpName;
        if (sig.NoErp) { s.CurrentErp ??= "nenhum (planilha/manual)"; answered.Add(TopicErp); }
        if (sig.StoreCount.HasValue) { s.StoreCount = sig.StoreCount; answered.Add(TopicStores); }

        // The reply to the question we asked counts as an answer, even a negative one: it must never be asked again.
        if (lastTopic != null && !sig.OptOut) answered.Add(lastTopic);
        var detail = !sig.Yes && !sig.No && !sig.Satisfied && !sig.Maybe && !sig.Refusal && rawText.Trim().Length >= 5;
        if (sig.PainCategory != null)
        {
            answered.Add(TopicPain);
            var prevMain = s.MainPain;
            if (s.MainPain == null || sig.PainCategory != Pain.Other) s.MainPain = s.MainPain != null && s.MainPain != Pain.Other && sig.PainCategory == Pain.Other ? s.MainPain : sig.PainCategory;
            // A second, different pain restarts the consultative path for that pain (the first one stays recorded).
            if (prevMain != null && prevMain != Pain.Other && s.MainPain != Pain.Other && Roberta4Rules.PainFamily(prevMain) != Roberta4Rules.PainFamily(s.MainPain))
            { s.PainDepth = 0; s.PitchCount = 0; s.ImpactKnown = false; }
            if (sig.PainCategory != Pain.Other) s.PainCategories = AppendCsv(s.PainCategories, sig.PainCategory, 120, true);
            if (Roberta4Rules.HypothesisFor(s.MainPain) is { } hyp && !(s.Hypotheses ?? "").Contains(hyp, StringComparison.Ordinal)) s.Hypotheses = Append(s.Hypotheses, hyp, 590);
            s.PainIntensity = Math.Max(s.PainIntensity, sig.PainIntensity);
            if (!(s.PainPoints ?? "").Contains(rawText.Trim(), StringComparison.OrdinalIgnoreCase)) s.PainPoints = Append(s.PainPoints, rawText.Trim(), 900);
            // A pain voiced after "everything is fine" cancels the satisfied state.
            s.SatisfiedWithCurrent = false; s.NegativeStreak = 0;
        }
        else if ((lastTopic == TopicPain || s.LastAction == nameof(RobertaAction.DeepenPain)) && detail)
        {
            s.PainPoints = Append(s.PainPoints, rawText.Trim(), 900);
            if (lastTopic == TopicPain) { s.MainPain ??= Pain.Other; s.PainIntensity = Math.Max(s.PainIntensity, 1); }
        }

        if (s.LastAction is nameof(RobertaAction.OfferDemo) or nameof(RobertaAction.AskForMeeting))
        {
            if (sig.Yes || sig.DemoRequest) s.DemoAccepted = true;
            else if (sig.No || sig.Refusal) s.Objections = Append(s.Objections, "recusou demonstração", 900);
        }
        if (sig.DemoRequest) s.DemoAccepted = true;
        if (s.LastAction == nameof(RobertaAction.ExploreImpact) && !s.ImpactKnown && sig.WordCount >= 3 && !sig.No && !sig.Refusal && !sig.AsksQuestion)
        { s.ImpactKnown = true; s.ImpactNotes = Append(s.ImpactNotes, rawText.Trim(), 480); }

        // Satisfied only counts as "no opportunity" when it is not merely "já tenho sistema" (that is an objection to explore).
        var satisfied = sig.Satisfied && sig.Objection != Obj.JaTenhoSistema;
        var negative = sig.No || satisfied || sig.Refusal;
        if (satisfied) { s.SatisfiedWithCurrent = true; s.Objections = Append(s.Objections, "satisfeito com a solução atual", 900); }
        if (negative && !(sig.StoreCount.HasValue || sig.ErpMention != null || sig.NoErp)) s.NegativeStreak++;
        else if (!sig.Maybe && !sig.Short) s.NegativeStreak = 0;

        if (sig.Refusal) s.Objections = Append(s.Objections, "recusa explícita", 900);
        if (sig.Busy && !(s.Objections ?? "").Contains("sem tempo")) s.Objections = Append(s.Objections, "sem tempo agora", 900);
        s.InterestLevel = sig.Refusal || sig.OptOut || sig.Angry ? 1
            : sig.DemoRequest || sig.HumanRequest || sig.Discount ? 3
            : sig.Price || (sig.Yes && s.LastAction != null) ? Math.Max(s.InterestLevel, 2)
            : s.InterestLevel;

        FeelingRules.Update(s, sig, replyDelay, spontaneous, storeChanged);
        if (!(sig.Refusal || sig.OptOut || sig.Angry)) s.InterestLevel = Math.Max(s.InterestLevel, s.Temperature >= 75 ? 3 : s.Temperature >= 45 ? 2 : 0);

        s.QuestionsAsked = Join(asked); s.QuestionsAnswered = Join(answered);

        // Disengagement: one-word replies that bring no information. Longer or informative replies reset it.
        var informative = sig.ErpMention != null || sig.StoreCount.HasValue || sig.PainCategory != null || sig.NoErp || sig.DemoRequest || sig.Curious || sig.AsksQuestion
            || (erpBefore == null && s.CurrentErp != null) || sig.Objection != null;
        if (s.TurnCount >= 2 && sig.WordCount <= 1 && !informative) s.LossSignals++;
        else if (sig.WordCount >= 5 || informative) s.LossSignals = 0;
        return s;
    }

    private static string AppendCsv(string? cur, string add, int max, bool unique = false)
    {
        var items = (cur ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!unique || !items.Contains(add)) items.Add(add);
        var r = string.Join(",", items);
        while (r.Length > max && items.Count > 1) { items.RemoveAt(0); r = string.Join(",", items); }
        return r.Length > max ? r[^max..] : r;
    }

    private static readonly Regex ReErpLead = new(@"^(o|a|um|uma|uso|usamos|usa|e|eh|aqui|sistema|o sistema|nosso|e o|e a|usando|temos|tenho)\s+", RegexOptions.Compiled);
    /// <summary>Name of the system when the lead answered the ERP question with an unknown name (e.g. "O Datasystem").</summary>
    private static string? CleanErp(string raw, InboundSignals sig)
    {
        if (sig.Yes || sig.No || sig.Maybe || sig.Satisfied || sig.Refusal || sig.AsksQuestion || sig.Identity || sig.WordCount is 0 or > 4) return null;
        var t = sig.Normalized;
        for (var i = 0; i < 3; i++) t = ReErpLead.Replace(t, "");
        t = Regex.Replace(t, @"[^a-z0-9 ]", "").Trim();
        return t.Length is >= 2 and <= 40 ? t : null;
    }

    /// <summary>Registers an action as sent/proposed so the engine never repeats it.</summary>
    public static void RecordAction(ConversationState s, RobertaAction a, string? reply = null, string? format = null, string? handoffKind = null)
    {
        s.LastAction = a.ToString();
        s.ActionHistory = AppendCsv(s.ActionHistory, a.ToString(), 300);
        var asked = Set(s.QuestionsAsked);
        var t = TopicOf(a.ToString());
        if (t != null) asked.Add(t);
        if (a == RobertaAction.OfferDemo || a == RobertaAction.AskForMeeting) s.DemoOffered = true;
        if (a == RobertaAction.DeepenPain) s.PainDepth++;
        if (a == RobertaAction.AnswerIdentity) s.AiDisclosed = true;
        if (a is RobertaAction.PitchFeature or RobertaAction.ShareInsight or RobertaAction.TellUseCase or RobertaAction.CreateCuriosity or RobertaAction.DifferentiateCompetitor)
        {
            if (a == RobertaAction.PitchFeature) s.PitchCount++;
            var angle = ProductCatalog.Next(s.MainPain, s.FeaturesPresented);
            s.FeaturesPresented = Join(Set(s.FeaturesPresented).Append(angle.Id));
        }
        if (a == RobertaAction.RespectBusy || a == RobertaAction.PauseConversation) s.Commitments = CommitOnce(s.Commitments, s.PreferredTime != null ? "retomar " + s.PreferredTime : "retomar quando o lead puder");
        if (a == RobertaAction.SendUsefulInformation) s.Commitments = CommitOnce(s.Commitments, "informação/material combinado");
        if (a == RobertaAction.HandoffToSeller) { s.Stage = ConvStage.Handoff; s.HandoffKind = handoffKind ?? s.HandoffKind ?? "outro"; }
        else if (a is RobertaAction.CloseConversation or RobertaAction.DoNotContact) s.Stage = ConvStage.Encerrada;
        else if (a is RobertaAction.OfferDemo or RobertaAction.AskForMeeting) s.Stage = ConvStage.Demo;
        else if (a is RobertaAction.ExplainFeature or RobertaAction.ExplainMultistore or RobertaAction.ExplainStockControl or RobertaAction.ExplainPricing or RobertaAction.PitchFeature
            or RobertaAction.ShareInsight or RobertaAction.TellUseCase or RobertaAction.CreateCuriosity or RobertaAction.DifferentiateCompetitor) s.Stage = ConvStage.Apresentacao;
        else if (t != null || a is RobertaAction.DeepenPain or RobertaAction.ExploreImpact) s.Stage = ConvStage.Qualificacao;
        s.OpportunityStage = a switch
        {
            RobertaAction.CloseConversation or RobertaAction.DoNotContact => "encerrado",
            RobertaAction.HandoffToSeller => "transferido",
            RobertaAction.PauseConversation => "pausado",
            RobertaAction.OfferDemo or RobertaAction.AskForMeeting => "demo",
            RobertaAction.PitchFeature or RobertaAction.ShareInsight or RobertaAction.TellUseCase or RobertaAction.CreateCuriosity or RobertaAction.DifferentiateCompetitor => "apresentacao",
            RobertaAction.DeepenPain or RobertaAction.ExploreImpact or RobertaAction.ConfirmUnderstanding or RobertaAction.HandleObjection => "aprofundamento",
            _ => s.OpportunityStage
        };
        s.Strategy = PlanBuilder.Strategy(a);
        s.CurrentGoal = a switch
        {
            RobertaAction.AskCurrentERP => "descobrir_sistema", RobertaAction.AskStoreCount => "descobrir_porte", RobertaAction.AskPainPoint => "descobrir_dor",
            RobertaAction.DeepenPain => "aprofundar_dor", RobertaAction.PitchFeature => "conectar_solucao", RobertaAction.OfferDemo or RobertaAction.AskForMeeting => "gerar_demo",
            RobertaAction.ExploreImpact => "entender_impacto", RobertaAction.ConfirmUnderstanding => "confirmar_entendimento", RobertaAction.HandleObjection => "tratar_objecao",
            RobertaAction.DifferentiateCompetitor => "achar_necessidade_nao_atendida", RobertaAction.BuildRapport => "criar_rapport",
            RobertaAction.HandoffToSeller => "transferir", RobertaAction.RespectBusy or RobertaAction.PauseConversation => "respeitar_tempo", RobertaAction.FollowUp => "retomar",
            RobertaAction.CloseConversation or RobertaAction.DoNotContact => "encerrar", _ => "responder_duvida"
        };
        if (format != null)
        {
            s.LastFormat = format;
            if (format != "text") s.AudioSentCount++;
        }
        if (!string.IsNullOrWhiteSpace(reply))
        {
            var q = Regex.Split(reply, @"(?<=[.!?])\s+").LastOrDefault(x => x.Contains('?'));
            s.LastQuestion = q?.Trim().Truncate(290);
        }
        s.QuestionsAsked = Join(asked);
    }

    private static string CommitOnce(string? cur, string add) =>
        (cur ?? "").Contains(add, StringComparison.OrdinalIgnoreCase) ? cur! : Append(cur, add, 480);

    private static string Append(string? cur, string add, int max)
    {
        var r = string.IsNullOrWhiteSpace(cur) ? add : cur + "; " + add;
        return r.Length > max ? r[^max..] : r;
    }
}

public static class CommercialDecisionEngine
{
    public const int MaxTurns = 14;
    public const int BigNetwork = 10;

    private static Decision Handoff(string reason, string kind) => new(RobertaAction.HandoffToSeller, reason, false, null, kind);

    /// <summary>Deterministic next step. <paramref name="kbMatch"/> = the knowledge base has something relevant to the message; <paramref name="kbPricing"/> = it has pricing info.</summary>
    public static Decision Decide(ConversationState s, InboundSignals sig, bool kbMatch, bool kbPricing)
    {
        if (s.OptOut || sig.OptOut) return new(RobertaAction.DoNotContact, "Lead pediu para não receber mais contato.", false);
        if (sig.Refusal && !sig.AsksQuestion || sig.Refusal && sig.No) return new(RobertaAction.CloseConversation, "Recusa clara.", false);
        if (sig.OutOfIcp) return new(RobertaAction.CloseConversation, "Lead fora do perfil (ICP).", false);
        if (sig.Insistence && !sig.AskedIfAi) return new(RobertaAction.CloseConversation, "Lead incomodado com insistência: encerrar com respeito, sem nova tentativa.", false);
        if (sig.Angry) return Handoff("Lead irritado/reclamação: atendimento humano.", "irritado");
        if (sig.AskedIfAi) return new(RobertaAction.AnswerIdentity, "Lead perguntou se fala com uma IA: responder com transparência e oferecer atendimento humano.", false);
        if (sig.HumanRequest || s.HumanRequested) return Handoff("Lead pediu atendimento humano.", "humano");
        if (sig.Discount) return Handoff("Negociação/desconto: humano.", "negociacao");
        if (sig.DemoRequest || s.DemoAccepted) return Handoff("Lead quer/aceitou demonstração.", "demo");
        if (s.StoreCount >= BigNetwork) return Handoff($"Rede com {s.StoreCount} lojas: prioridade para vendedor.", "rede");
        if (sig.Objection is { } ob) return Objection(s, sig, ob, kbMatch);
        if (sig.Price)
            return kbPricing && !s.PriceDiscussedBefore() ? new(RobertaAction.ExplainPricing, "Preço pedido e há informação na base.", true)
                : Handoff("Preço/proposta: humano.", "preco");
        if (s.TurnCount > MaxTurns) return Handoff("Limite de turnos automáticos atingido.", s.MainPain != null ? "maduro" : "outro");

        if (sig.Busy)
            return s.LastAction == nameof(RobertaAction.RespectBusy) ? new(RobertaAction.CloseConversation, "Segue sem tempo: encerrar sem pressão.", false)
                : new(RobertaAction.RespectBusy, "Lead sem tempo agora: resposta curta, sem perguntas.", true);

        // Small talk is answered briefly and bridged back, never ignored (and never repeated twice in a row).
        if (sig.OffTopic && sig.PainCategory == null && sig.ErpMention == null && !sig.StoreCount.HasValue && !sig.TopicStock && !sig.TopicFiscal && !kbMatch
            && s.LastAction != nameof(RobertaAction.BuildRapport))
            return new(RobertaAction.BuildRapport, "Lead mudou de assunto: acolher em uma frase e retomar com naturalidade.", true);

        if (sig.AsksQuestion && !sig.Yes && !sig.No)
        {
            if (!kbMatch) return Handoff("Pergunta fora da base de conhecimento.", "duvida");
            var a = sig.TopicStock ? RobertaAction.ExplainStockControl : sig.TopicMultistore ? RobertaAction.ExplainMultistore : RobertaAction.ExplainFeature;
            return new(a, "Lead fez uma pergunta; responder só com a base.", true);
        }

        // Satisfied / repeated negatives: change strategy and stop inventing pain points.
        if (s.SatisfiedWithCurrent || s.NegativeStreak >= 2)
            return s.DemoOffered ? new(RobertaAction.CloseConversation, "Cliente satisfeito e demo já oferecida: encerrar com elegância.", false)
                : new(RobertaAction.OfferDemo, "Cliente satisfeito: oferta leve, sem perguntar dores.", true);

        // Disengagement: pause (and then stop) instead of pushing a lead who only sends monosyllables.
        if (s.LossSignals >= 3)
            return s.LastAction == nameof(RobertaAction.PauseConversation) ? new(RobertaAction.CloseConversation, "Sem engajamento mesmo após pausa: encerrar com elegância.", false)
                : new(RobertaAction.PauseConversation, "Respostas mínimas repetidas (perda de interesse): recuar sem pressionar.", true);

        var asked = ConversationStateRules.Set(s.QuestionsAsked); var answered = ConversationStateRules.Set(s.QuestionsAnswered);
        bool Done(string t) => asked.Contains(t) || answered.Contains(t);

        // Consultative selling: a stated pain is acknowledged, probed and connected to the product before anything else.
        // After that the next step is chosen among several candidates, weighing the history so what did not work is not repeated.
        var painLed = s.MainPain != null && !s.DemoOffered;
        if (painLed && s.PainDepth == 0) return new(RobertaAction.DeepenPain, $"Dor declarada ({s.MainPain}): reconhecer e aprofundar antes de avançar.", true);
        if (painLed && s.PitchCount == 0 && s.Temperature < 70 && !s.Returned) return new(RobertaAction.PitchFeature, $"Dor aprofundada ({s.MainPain}): conectar a dor à solução.", true, null, null, 60);
        var chosen = painLed ? Choose(s, sig) : null;
        if (chosen != null && chosen.Score >= 60) return chosen;

        if (!Done(ConversationStateRules.TopicErp) && s.CurrentErp == null) return new(RobertaAction.AskCurrentERP, "Descobrir sistema atual.", true, ConversationStateRules.TopicErp);
        if (!Done(ConversationStateRules.TopicStores) && s.StoreCount == null) return new(RobertaAction.AskStoreCount, "Descobrir porte (lojas).", true, ConversationStateRules.TopicStores);

        if (chosen != null) return chosen;
        if (!Done(ConversationStateRules.TopicPain) && string.IsNullOrWhiteSpace(s.PainPoints)) return new(RobertaAction.AskPainPoint, "Entender a principal dor.", true, ConversationStateRules.TopicPain);

        if (!s.DemoOffered) return new(RobertaAction.OfferDemo, "Qualificação concluída: oferecer demonstração.", true);
        if (s.LastAction == nameof(RobertaAction.OfferDemo) && s.Temperature >= 55 && !sig.No && !(s.Objections ?? "").Contains("recusou demonstração"))
            return new(RobertaAction.AskForMeeting, "Demo oferecida, lead morno/quente e sem aceite: propor um horário sem pressão.", true, null, null, 70);
        if ((s.InterestLevel >= 2 || sig.Yes) && !sig.No && !(s.Objections ?? "").Contains("recusou demonstração")) return Handoff("Interesse confirmado após demo oferecida.", "maduro");
        return new(RobertaAction.CloseConversation, "Sem avanço após qualificação e demo oferecida.", false);
    }

    private static int Occurrences(string? csv, string item) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Count(x => x == item);

    /// <summary>How an objection is handled. The first occurrence is worked on; the same objection again is respected (no insistence).</summary>
    private static Decision Objection(ConversationState s, InboundSignals sig, string ob, bool kbMatch)
    {
        var again = Occurrences(s.ObjectionLog, ob) >= 2;
        Decision Handle(string why) => new(RobertaAction.HandleObjection, why, true, ob);
        switch (ob)
        {
            case Obj.Prova:
                return Handoff("Pedido de prova de resultados: nunca inventar cases ou números; encaminhar a quem pode apresentar referências reais.", "prova");
            case Obj.Material:
                return kbMatch ? new(RobertaAction.SendUsefulInformation, "Pedido de material: enviar só o que existe na base aprovada e combinar próximo passo opcional.", true, ob)
                    : Handoff("Pedido de material sem conteúdo aprovado na base: humano envia.", "material");
            case Obj.Caro:
                return again ? Handoff("Preço continua sendo barreira: condições comerciais com humano.", "negociacao")
                    : Handle("Objeção de preço: entender o critério de comparação antes de qualquer argumento.");
            case Obj.Desconfianca:
                return again ? Handoff("Desconfiança persiste: humano.", "humano") : Handle("Desconfiança: resposta honesta, só fatos, oferecendo parar o contato.");
            case Obj.FazTudo:
                return again ? new(RobertaAction.CloseConversation, "Lead reafirma que o sistema atual atende: reconhecer e encerrar sem insistir.", false)
                    : new(RobertaAction.DifferentiateCompetitor, "Sistema atual 'faz tudo': buscar diferença real e verificável ou reconhecer que talvez não haja oportunidade.", true, ob);
            case Obj.VouPensar:
                return again ? new(RobertaAction.PauseConversation, "Segue em 'vou pensar': pausar sem pressionar.", true) : Handle("'Vou pensar': investigar com delicadeza se falta alguma informação.");
            default: // já tenho sistema, não quero trocar, necessidade não atendida
                if (!again && s.DemoOffered && s.SatisfiedWithCurrent && ob is Obj.JaTenhoSistema or Obj.NaoTrocar) again = true; // already said it and a demo was offered: stop
                return again ? new(RobertaAction.CloseConversation, "Objeção repetida: respeitar e encerrar sem insistir.", false)
                    : Handle(ob == Obj.JaTenhoSistema ? "Já tem sistema: descobrir necessidade não atendida antes de vender."
                        : ob == Obj.NaoTrocar ? "Não quer trocar: reconhecer o custo da mudança, sem insistir."
                        : "Necessidade fora do que o Axis cobre: ser honesta, sem prometer.");
        }
    }

    /// <summary>Scored candidates after a pain is known. The score is a preference, not a script: history, style and temperature change the winner.</summary>
    public static Decision Choose(ConversationState s, InboundSignals sig)
    {
        var hist = (s.ActionHistory ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).TakeLast(4).ToList();
        var style = ConversationStateRules.Set(s.ClientStyle);
        var rushed = style.Contains("objetivo") || style.Contains("apressado");
        var cands = new List<Decision>();
        void Add(RobertaAction a, int score, string why)
        {
            score -= 25 * hist.Count(h => h == a.ToString());
            if (s.LastAction == a.ToString() && (sig.Short || sig.No || s.LossSignals > 0)) score -= 20; // it did not work last time
            if (score > 0) cands.Add(new(a, why, true, null, null, score));
        }
        var depth = s.PainDepth; var pitched = s.PitchCount;
        if (s.Returned && depth >= 1) Add(RobertaAction.ConfirmUnderstanding, 95, "Lead voltou após dias: retomar do ponto em que a conversa parou, confirmando o que foi entendido.");
        if (sig.Dissatisfied && s.CurrentErp != null) Add(RobertaAction.DifferentiateCompetitor, 70, "Lead insatisfeito com o sistema atual: procurar diferença real e verificável.");
        Add(RobertaAction.PitchFeature, pitched == 0 ? 60 : pitched == 1 ? 38 : 15, "Conectar a dor a um ângulo do produto.");
        if (depth >= 1 && !s.ImpactKnown) Add(RobertaAction.ExploreImpact, 55 + (s.Engagement == "alto" ? 8 : 0) - (rushed ? 20 : 0), "Impacto da dor ainda desconhecido: explorar frequência/custo.");
        if (depth >= 1) Add(RobertaAction.ShareInsight, 30 + (style.Contains("tecnico") ? 12 : 0), "Compartilhar um insight sobre a causa típica do problema.");
        if (pitched >= 1) Add(RobertaAction.TellUseCase, 36 + (style.Contains("comunicativo") ? 6 : 0) - (rushed ? 15 : 0), "Ilustrar com um cenário de uso genérico (sem cases reais).");
        if (pitched >= 1 && s.Engagement == "baixo") Add(RobertaAction.CreateCuriosity, 40, "Engajamento baixo: despertar curiosidade em vez de perguntar mais.");
        if ((depth >= 2 || s.ImpactKnown) && pitched >= 1) Add(RobertaAction.ConfirmUnderstanding, 45, "Já há contexto suficiente: confirmar o entendimento antes de avançar.");
        if (pitched >= 1) Add(RobertaAction.OfferDemo, s.Temperature >= 65 ? 80 : sig.Yes || sig.Curious ? 75 : s.Temperature >= 55 ? 62 : pitched >= 2 ? 40 : 0, "Dor conectada à solução e interesse presente: convidar para ver na prática.");
        else if (s.Temperature >= 75) Add(RobertaAction.OfferDemo, 85, "Interesse alto desde cedo: convite direto para demonstração.");
        if (cands.Count == 0) return new(RobertaAction.OfferDemo, "Sem melhor alternativa sem repetir o que já foi feito: oferecer demonstração.", true, null, null, 40);
        return cands.OrderByDescending(c => c.Score).First();
    }

    private static bool PriceDiscussedBefore(this ConversationState s) => s.LastAction == nameof(RobertaAction.ExplainPricing);
}
