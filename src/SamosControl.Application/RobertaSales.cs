using System.Text.RegularExpressions;
using SamosControl.Domain;

namespace SamosControl.Application;

/// <summary>One way of connecting a pain to the product. Only capabilities that exist in the Axis feature list are allowed here.</summary>
public record ProductAngle(string Id, string Text);

/// <summary>Allowed product claims per pain category. Roberta may only pitch what is listed here or in the knowledge base.
/// Add other Samos products by adding catalogs; nothing here is specific to a single conversation.</summary>
public static class ProductCatalog
{
    public const string Product = "Axis ERP";

    public static readonly IReadOnlyDictionary<string, ProductAngle[]> Angles = new Dictionary<string, ProductAngle[]>
    {
        [Pain.StockDivergence] = new[]
        {
            new ProductAngle("rastreabilidade", "controle de estoque com rastreabilidade das movimentações, para ajudar a identificar onde as diferenças surgem"),
            new ProductAngle("inventario", "inventário no sistema e, para operações maiores, WMS e RFID para contagem e conferência"),
            new ProductAngle("ia_estoque", "IA de estoque do Axis para apoiar a gestão do estoque")
        },
        [Pain.Stock] = new[]
        {
            new ProductAngle("controle_estoque", "controle de estoque, movimentações e inventário em um só lugar"),
            new ProductAngle("wms_rfid", "WMS e RFID para operações com estoque mais complexo"),
            new ProductAngle("ia_estoque", "IA de estoque do Axis para apoiar a gestão do estoque")
        },
        [Pain.Fiscal] = new[]
        {
            new ProductAngle("fiscal", "emissão de NF-e, NFC-e, NFS-e, MDF-e e CT-e"),
            new ProductAngle("integrado", "PDV, estoque e fiscal trabalhando juntos no mesmo sistema")
        },
        [Pain.Financial] = new[]
        {
            new ProductAngle("financeiro", "módulo financeiro com boleto e conciliação bancária"),
            new ProductAngle("integrado", "financeiro integrado às vendas e ao estoque")
        },
        [Pain.Sales] = new[]
        {
            new ProductAngle("pdv", "PDV do Axis para a operação de caixa"),
            new ProductAngle("forca_vendas", "força de vendas e o Axis Vendedor, aplicativo para os vendedores")
        },
        [Pain.Ecommerce] = new[]
        {
            new ProductAngle("integracoes", "integrações do Axis para operações com loja física e e-commerce (a plataforma específica precisa ser confirmada pela equipe)")
        },
        [Pain.Multistore] = new[]
        {
            new ProductAngle("multiloja", "controle de múltiplas lojas na mesma operação"),
            new ProductAngle("estoque_lojas", "controle de estoque das lojas, com movimentações rastreáveis")
        },
        [Pain.Customers] = new[]
        {
            new ProductAngle("crm", "CRM, fidelidade e cashback para relacionamento com clientes")
        },
        [Pain.System] = new[]
        {
            new ProductAngle("erp_completo", "ERP completo: PDV, estoque, financeiro, fiscal, CRM e integrações"),
            new ProductAngle("moda", "operação de moda com grade de tamanho e cor")
        },
        [Pain.Other] = new[]
        {
            new ProductAngle("visao_geral", "PDV, estoque, financeiro, fiscal e CRM trabalhando juntos"),
            new ProductAngle("moda", "operação de moda com grade de tamanho e cor")
        }
    };

    /// <summary>Probing questions the model may choose from (it must pick only one, the most adequate to the context).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Probes = new Dictionary<string, string[]>
    {
        [Pain.StockDivergence] = new[]
        {
            "isso aparece mais no inventário ou só na hora de vender?",
            "acontece com frequência ou é de vez em quando?",
            "mais de uma pessoa mexe no estoque? Conseguem saber quem fez cada movimentação?",
            "vocês controlam por tamanho e cor?"
        },
        [Pain.Stock] = new[] { "isso aparece mais no inventário ou na hora de vender?", "vocês controlam por tamanho e cor?" },
        [Pain.Fiscal] = new[] { "o que mais trava: emissão, rejeições ou a conferência depois?" },
        [Pain.Financial] = new[] { "o que dá mais trabalho: cobrança, conciliação ou fechamento do caixa?" },
        [Pain.Sales] = new[] { "isso acontece mais em horário de pico?" },
        [Pain.Ecommerce] = new[] { "qual plataforma de e-commerce vocês usam?", "o estoque da loja física e do online é separado?" },
        [Pain.Multistore] = new[] { "hoje cada loja tem seu controle ou vocês enxergam tudo junto?" },
        [Pain.Customers] = new[] { "hoje vocês conseguem saber quem são os clientes que mais compram?" },
        [Pain.System] = new[] { "o que mais pesa hoje nesse sistema?" },
        [Pain.Other] = new[] { "onde isso mais pesa no dia a dia?" }
    };

    public static string Topic(string? pain) => pain switch
    {
        Pain.StockDivergence => "essa parte de estoque",
        Pain.Stock => "a parte de estoque",
        Pain.Fiscal => "a parte fiscal",
        Pain.Financial => "a parte financeira",
        Pain.Sales => "a parte de vendas e PDV",
        Pain.Ecommerce => "a integração com o e-commerce",
        Pain.Multistore => "a gestão das suas lojas",
        Pain.Customers => "a parte de relacionamento com clientes",
        _ => "como o Axis funcionaria na sua operação"
    };

    public static ProductAngle Next(string? pain, string? presentedCsv)
    {
        var list = Angles.TryGetValue(pain ?? Pain.Other, out var a) ? a : Angles[Pain.Other];
        var done = ConversationStateRules.Set(presentedCsv);
        return list.FirstOrDefault(x => !done.Contains(x.Id)) ?? list[0];
    }

    public static string[] ProbesFor(string? pain) => Probes.TryGetValue(pain ?? Pain.Other, out var p) ? p : Probes[Pain.Other];

    /// <summary>All claims and probes for a pain, so the validator treats them as grounded text.</summary>
    public static string AllText(string? pain) =>
        string.Join(' ', (Angles.TryGetValue(pain ?? Pain.Other, out var a) ? a : Angles[Pain.Other]).Select(x => x.Text).Concat(ProbesFor(pain)));
}

/// <summary>"Feeling comercial": deterministic temperature, engagement, intent and style from observable signals.</summary>
public static class FeelingRules
{
    public static void Update(ConversationState s, InboundSignals sig, TimeSpan? replyDelay, bool spontaneousInfo, bool storeChanged)
    {
        if (s.Temperature == 0 && s.TurnCount <= 1) s.Temperature = 20;
        var d = 0;
        if (sig.WordCount >= 8) d += 6; else if (sig.WordCount >= 4) d += 3; else if (sig.Short && !sig.Yes && !sig.Curious) d -= 2;
        if (spontaneousInfo) d += 4;
        if (sig.PainCategory != null) d += 8 + (sig.PainIntensity >= 2 ? 6 : 0);
        if (sig.Dissatisfied) d += 8;
        if (sig.Price) d += 10;
        if (sig.TopicImplementation) d += 6;
        if (sig.Curious || sig.AsksQuestion) d += 6;
        if (sig.Yes) d += 6;
        if (sig.DemoRequest) d += 25;
        if (sig.HumanRequest) d += 8;
        if (sig.Discount) d += 15;
        if (storeChanged && sig.StoreCount >= 2) d += 5;
        if (sig.Satisfied) d -= 15;
        if (sig.Maybe) d -= 6;
        if (sig.Busy) d -= 6;
        if (sig.No) d -= 5;
        if (sig.Refusal) d -= 30;
        if (sig.OptOut || sig.Angry) d -= 40;
        if (replyDelay is { } t) { if (t < TimeSpan.FromMinutes(2)) d += 3; else if (t > TimeSpan.FromHours(24)) d -= 4; }
        s.Temperature = Math.Clamp(s.Temperature + d, 0, 100);
        s.BuyingIntent = s.Temperature >= 75 ? "alta" : s.Temperature >= 45 ? "media" : "baixa";
        var avg = s.TurnCount == 0 ? 0 : (double)s.InboundWords / s.TurnCount;
        s.Engagement = avg >= 8 || (avg >= 5 && replyDelay is { } q && q < TimeSpan.FromMinutes(3)) ? "alto" : avg >= 4 ? "medio" : "baixo";
        s.ReadyForHandoff = s.Temperature >= 80 || s.DemoAccepted || s.HumanRequested;
        UpdateStyle(s, sig, avg);
    }

    private static void UpdateStyle(ConversationState s, InboundSignals sig, double avg)
    {
        var tags = ConversationStateRules.Set(s.ClientStyle);
        if (sig.Informal) tags.Add("informal");
        if (sig.Technical) tags.Add("tecnico");
        if (sig.Busy) tags.Add("apressado");
        if (s.TurnCount >= 2 && avg <= 3.5) { tags.Add("objetivo"); tags.Remove("comunicativo"); }
        if (avg >= 14 || sig.WordCount >= 25) { tags.Add("comunicativo"); tags.Remove("objetivo"); }
        s.ClientStyle = ConversationStateRules.Join(tags);
    }

    public static string StyleGuidance(ConversationState s)
    {
        var t = ConversationStateRules.Set(s.ClientStyle);
        var g = new List<string>();
        if (t.Contains("objetivo")) g.Add("cliente objetivo: respostas curtas e diretas");
        if (t.Contains("comunicativo")) g.Add("cliente comunicativo: pode conversar um pouco mais");
        if (t.Contains("informal")) g.Add("cliente informal: linguagem informal moderada, sem forçar intimidade");
        if (t.Contains("tecnico")) g.Add("cliente técnico: pode dar um pouco mais de detalhe técnico");
        if (t.Contains("apressado")) g.Add("cliente com pressa: reduzir perguntas e ir direto ao ponto");
        return g.Count == 0 ? "sem perfil definido ainda: tom cordial e natural" : string.Join("; ", g);
    }
}

public enum ReplyFormat { Text, Audio, AudioPlusText }

/// <summary>Chooses text vs voice from context (never by a fixed rhythm). Without ElevenLabs it is always text.</summary>
public static class FormatAdvisor
{
    public const string Separator = "###";
    private static readonly Regex Risky = new(@"\d|https?://|www\.|R\$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public const int MaxAudioWords = 110;

    public static ReplyFormat Choose(ConversationState s, InboundSignals sig, RobertaAction a, bool voiceAvailable, bool clientSentAudio, bool lastOutboundWasAudio)
    {
        if (!voiceAvailable) return ReplyFormat.Text;
        // A written-only request always wins; the same for content that must stay written or is too short/sensitive for a voice note.
        if (s.PreferredChannel == "texto") return ReplyFormat.Text;
        if (a is RobertaAction.DoNotContact or RobertaAction.CloseConversation or RobertaAction.HandoffToSeller or RobertaAction.ExplainPricing
            or RobertaAction.AskCurrentERP or RobertaAction.AskStoreCount or RobertaAction.FollowUp or RobertaAction.RespectBusy or RobertaAction.ScheduleDemo
            or RobertaAction.AnswerIdentity or RobertaAction.PauseConversation or RobertaAction.SendUsefulInformation or RobertaAction.AskForMeeting or RobertaAction.ConfirmUnderstanding)
            return ReplyFormat.Text;
        if (s.PreferredChannel == "audio") clientSentAudio = true;
        var style = ConversationStateRules.Set(s.ClientStyle);
        var informative = a is RobertaAction.ExplainFeature or RobertaAction.ExplainStockControl or RobertaAction.ExplainMultistore or RobertaAction.OfferDemo;
        if (clientSentAudio) return informative || style.Contains("tecnico") ? ReplyFormat.AudioPlusText : ReplyFormat.Audio;
        if (sig.Short && !sig.AsksQuestion && !sig.Curious) return ReplyFormat.Text;
        if (style.Contains("objetivo") || style.Contains("apressado") || style.Contains("tecnico")) return ReplyFormat.Text;
        if (lastOutboundWasAudio) return ReplyFormat.Text;
        var warm = s.Engagement == "alto" || style.Contains("informal") || s.Temperature >= 55;
        return a switch
        {
            RobertaAction.PitchFeature or RobertaAction.HandleObjection or RobertaAction.ShareInsight or RobertaAction.TellUseCase or RobertaAction.CreateCuriosity
                or RobertaAction.DifferentiateCompetitor => warm ? (s.Temperature >= 65 ? ReplyFormat.AudioPlusText : ReplyFormat.Audio) : ReplyFormat.Text,
            RobertaAction.DeepenPain => warm && s.Temperature >= 45 && s.PainIntensity >= 1 ? ReplyFormat.Audio : ReplyFormat.Text,
            RobertaAction.OfferDemo => warm && s.Temperature >= 65 ? ReplyFormat.AudioPlusText : ReplyFormat.Text,
            _ => informative && s.Engagement == "alto" && sig.WordCount >= 8 ? ReplyFormat.AudioPlusText : ReplyFormat.Text
        };
    }

    /// <summary>(spoken script, text companion). Without the separator the whole reply is the script.</summary>
    public static (string Audio, string? Text) Split(string reply)
    {
        var i = reply.IndexOf(Separator, StringComparison.Ordinal);
        if (i < 0) return (reply.Trim(), null);
        var t = reply[(i + Separator.Length)..].Trim();
        return (reply[..i].Trim(), t.Length == 0 ? null : t);
    }

    /// <summary>Downgrades to text when the content must be read (numbers, links, money) or the script is malformed or too long.</summary>
    public static ReplyFormat Finalize(ReplyFormat f, string reply)
    {
        if (f == ReplyFormat.Text) return f;
        var (audio, text) = Split(reply);
        if (audio.Length == 0 || Risky.IsMatch(audio)) return ReplyFormat.Text;
        if (audio.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxAudioWords) return ReplyFormat.Text;
        if (f == ReplyFormat.AudioPlusText && text == null) return ReplyFormat.Audio;
        return f;
    }

    public static string Code(ReplyFormat f) => f switch { ReplyFormat.Audio => "audio", ReplyFormat.AudioPlusText => "audio_text", _ => "text" };
    public static ReplyFormat Parse(string? c) => c switch { "audio" => ReplyFormat.Audio, "audio_text" => ReplyFormat.AudioPlusText, _ => ReplyFormat.Text };
    public static string Plain(string reply) => reply.Replace(Separator, " ").Replace("  ", " ").Trim();
}

/// <summary>Handoff messages that create value before passing the lead on (fixed templates: no model, no invention).</summary>
public static class HandoffMessages
{
    public static string Build(string? kind, string? pain, string? who, int seed)
    {
        var someone = string.IsNullOrWhiteSpace(who) ? "alguém do nosso time" : $"{who}, do nosso time";
        var topic = ProductCatalog.Topic(pain);
        var hasPain = !string.IsNullOrWhiteSpace(pain);
        string[] v = kind switch
        {
            "demo" => new[]
            {
                $"Show! Vou chamar {someone} para te mostrar {topic} na prática e combinar o melhor horário com você.",
                $"Combinado! {Cap(someone)} vai te mostrar {topic} funcionando e alinhar o melhor horário por aqui."
            },
            "preco" or "negociacao" => new[]
            {
                $"Sobre valores e condições, o ideal é {someone} montar isso com você, porque depende do tamanho da operação. Já vou chamar para continuar essa conversa por aqui.",
                $"Valores e condições dependem do formato da sua operação, então prefiro que {someone} veja isso com você. Já peço para continuar a conversa por aqui."
            },
            "implantacao" => new[] { $"Sobre implantação, quem conhece o passo a passo é o nosso time. Vou chamar {someone} para te explicar direitinho." },
            "prova" => new[] { $"Boa pergunta, e prefiro não te passar nada que eu não possa comprovar. Vou chamar {someone} para conversar com você sobre isso com informações reais." },
            "material" => new[] { $"Vou pedir para {someone} te enviar o material certinho e, se fizer sentido, combinar o próximo passo com você por aqui." },
            "humano" => new[] { $"Claro! Vou chamar {someone} para continuar com você por aqui.", $"Sem problema. Já peço para {someone} falar com você por aqui." },
            "rede" => new[] { $"Com uma operação desse tamanho, vale conversar com quem cuida de redes aqui na Samos. Vou chamar {someone} para falar com você." },
            "irritado" => new[] { $"Desculpa o incômodo. Vou pedir para {someone} falar com você pessoalmente." },
            "duvida" => new[] { $"Essa é uma dúvida que prefiro que {someone} responda com precisão. Vou chamar para te explicar direitinho." },
            _ when hasPain => new[]
            {
                $"Pelo que você me contou, acho que vale bastante você ver como isso funciona na prática. Vou chamar {someone} para te mostrar especificamente {topic}.",
                $"Pelo que conversamos, faz sentido você ver {topic} funcionando no Axis. Vou chamar {someone} para te mostrar isso de perto."
            },
            _ => new[] { $"Vou chamar {someone} para te ajudar melhor com isso, por aqui mesmo.", $"Acho que {someone} consegue te ajudar melhor nesse ponto. Já peço para continuar a conversa por aqui." }
        };
        return v[Math.Abs(seed) % v.Length];
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
