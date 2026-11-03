using System.Text.RegularExpressions;
using SamosControl.Application;
using SamosControl.Domain;
using Xunit;
using Xunit.Abstractions;

namespace SamosControl.Tests;

public class RobertaSalesTests
{
    private readonly ITestOutputHelper _out;
    public RobertaSalesTests(ITestOutputHelper o) => _out = o;

    private sealed record Turn(string Client, Decision D, ReplyFormat F, string? Handoff);

    /// <summary>Runs the deterministic part of Roberta (state, decision, format) over a scripted client conversation and prints the transcript.</summary>
    private List<Turn> Simulate(string title, string[] msgs, bool voice = false, bool[]? audioIn = null, Action<ConversationState>? seed = null, bool kb = true)
    {
        var s = new ConversationState { LeadId = 1 }; seed?.Invoke(s);
        var turns = new List<Turn>(); var lastAudio = false;
        _out.WriteLine($"===== {title} =====");
        for (var i = 0; i < msgs.Length; i++)
        {
            var m = msgs[i]; var viaAudio = audioIn != null && audioIn[i];
            var sig = InboundClassifier.Classify(m);
            ConversationStateRules.Apply(s, sig, m, viaAudio, TimeSpan.FromMinutes(1));
            var d = CommercialDecisionEngine.Decide(s, sig, kb, false);
            var f = d.NeedsLlm ? FormatAdvisor.Choose(s, sig, d.Action, voice, viaAudio, lastAudio) : ReplyFormat.Text;
            ConversationStateRules.RecordAction(s, d.Action, null, FormatAdvisor.Code(f), d.HandoffKind);
            lastAudio = f != ReplyFormat.Text;
            string? handoff = d.Action == RobertaAction.HandoffToSeller ? HandoffMessages.Build(d.HandoffKind, s.MainPain, null, 1) : null;
            turns.Add(new(m, d, f, handoff));
            _out.WriteLine($"Cliente : {m}{(viaAudio ? "  [áudio]" : "")}");
            _out.WriteLine($"  estado : temp {s.Temperature} · intenção {s.BuyingIntent} · engaj. {s.Engagement} · dor {s.MainPain ?? "-"} · objetivo {s.CurrentGoal} · estilo [{s.ClientStyle}]");
            _out.WriteLine($"Roberta : {d.Action} · formato {FormatAdvisor.Code(f)}{(d.HandoffKind != null ? " · transferência " + d.HandoffKind : "")}");
            if (handoff != null) _out.WriteLine($"          \"{handoff}\"");
            else if (d.NeedsLlm) _out.WriteLine($"          instrução: {RobertaService.Instruction(d.Action)}");
        }
        State = s;
        return turns;
    }

    private ConversationState State = new();
    private static string[] Acts(List<Turn> t) => t.Select(x => x.D.Action.ToString()).ToArray();

    [Fact]
    public void S1_satisfied_with_current_system_gets_a_light_offer_then_stops()
    {
        var t = Simulate("1. Satisfeito com o sistema atual", new[] { "Oi", "Uso o Bling e funciona bem", "Não precisa, obrigado" });
        Assert.Equal(new[] { "AskCurrentERP", "OfferDemo", "CloseConversation" }, Acts(t));
    }

    [Fact]
    public void S2_stock_pain_is_deepened_and_pitched_instead_of_an_immediate_handoff()
    {
        var t = Simulate("2. Estoque sempre fura (caso real)", new[] { "Oi", "O Datasystem", "1 loja", "Uai a questão de gestão de estoque, sempre fura o estoque", "Mais no inventário, só descobrimos lá", "Sim, isso me interessa" });
        Assert.Equal(new[] { "AskCurrentERP", "AskStoreCount", "AskPainPoint", "DeepenPain", "PitchFeature", "OfferDemo" }, Acts(t));
        Assert.Equal("datasystem", State.CurrentErp); Assert.Equal(1, State.StoreCount); Assert.Equal(Pain.StockDivergence, State.MainPain);
        Assert.DoesNotContain(t.Take(5), x => x.D.Action == RobertaAction.HandoffToSeller);
        Assert.Contains("rastreabilidade", State.FeaturesPresented);
        Assert.True(State.Temperature >= 45);
    }

    [Fact]
    public void S3_price_question_goes_to_a_human_with_a_value_message()
    {
        var t = Simulate("3. Pergunta preço", new[] { "Oi", "Quanto custa?" });
        Assert.Equal("HandoffToSeller", t[1].D.Action.ToString()); Assert.Equal("preco", t[1].D.HandoffKind);
        Assert.Contains("valores", t[1].Handoff!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void S4_objective_client_gets_text_only_even_with_voice_available()
    {
        var t = Simulate("4. Cliente muito objetivo", new[] { "Oi", "Bling", "2", "estoque furando" }, voice: true);
        Assert.All(t, x => Assert.Equal(ReplyFormat.Text, x.F));
        Assert.Contains("objetivo", State.ClientStyle);
    }

    [Fact]
    public void S5_client_audio_is_answered_in_audio_when_voice_is_available()
    {
        var t = Simulate("5. Cliente manda áudio", new[] { "Oi", "Uso o Bling aqui na loja, tenho duas lojas e o estoque sempre fura, é um problema que eu queria resolver" }, voice: true, audioIn: new[] { false, true });
        Assert.NotEqual(ReplyFormat.Text, t[1].F);
        Assert.True(State.ClientSentAudio);
    }

    [Fact]
    public void S6_follow_up_resumes_from_the_pain_and_is_not_generic()
    {
        var i = RobertaService.Instruction(RobertaAction.FollowUp);
        Assert.Contains("dor", i); Assert.Contains("conseguiu ver", i);
        Simulate("6. Cliente para de responder (follow-up parte do estado salvo)", new[] { "Oi", "Uso Bling", "2 lojas", "O estoque sempre fura" });
        Assert.Equal(Pain.StockDivergence, State.MainPain);
        Assert.Contains("estoque", ProductCatalog.Topic(State.MainPain));
    }

    [Fact]
    public void S7_demo_request_hands_off_with_a_pain_aware_message()
    {
        var t = Simulate("7. Quer demonstração", new[] { "Oi", "Uso Bling", "2 lojas", "O estoque sempre fura", "Quero ver uma demonstração disso" });
        Assert.Equal("HandoffToSeller", t[^1].D.Action.ToString()); Assert.Equal("demo", t[^1].D.HandoffKind);
        Assert.Contains("estoque", t[^1].Handoff!);
    }

    [Fact]
    public void S8_no_time_gets_a_short_reply_without_questions_then_closes_if_it_repeats()
    {
        var t = Simulate("8. Sem tempo", new[] { "Oi", "Estou sem tempo agora, corrido aqui", "Ainda sem tempo, depois a gente fala" });
        Assert.Equal(new[] { "AskCurrentERP", "RespectBusy", "CloseConversation" }, Acts(t));
        Assert.Equal(ReplyFormat.Text, t[1].F);
    }

    [Fact]
    public void S9_big_network_goes_to_a_human_as_network()
    {
        var t = Simulate("9. Várias lojas", new[] { "Oi", "Uso Totvs", "Temos 12 lojas" });
        Assert.Equal("rede", t[^1].D.HandoffKind);
    }

    [Fact]
    public void S10_complaint_about_current_system_is_explored_not_transferred()
    {
        var t = Simulate("10. Reclama do sistema concorrente", new[] { "Oi", "Uso o Datasystem", "2 lojas", "O sistema é muito lento e trava toda hora, estou cansado" });
        Assert.Equal("DeepenPain", t[^1].D.Action.ToString());
        Assert.Equal(Pain.System, State.MainPain);
    }

    [Fact]
    public void A_pain_overrides_an_earlier_satisfied_state()
    {
        var s = new ConversationState();
        var a = "Está tudo bem com o sistema"; ConversationStateRules.Apply(s, InboundClassifier.Classify(a), a);
        var b = "Só o estoque que sempre fura"; ConversationStateRules.Apply(s, InboundClassifier.Classify(b), b);
        Assert.False(s.SatisfiedWithCurrent); Assert.Equal(Pain.StockDivergence, s.MainPain);
    }

    [Theory]
    [InlineData(false, false, false)]
    public void Without_voice_the_format_is_always_text(bool voice, bool clientAudio, bool lastAudio)
    {
        var s = new ConversationState { Temperature = 90, Engagement = "alto" };
        foreach (RobertaAction a in Enum.GetValues<RobertaAction>())
            Assert.Equal(ReplyFormat.Text, FormatAdvisor.Choose(s, InboundClassifier.Classify("conta mais sobre isso, estou curioso"), a, voice, clientAudio, lastAudio));
    }

    [Fact]
    public void Two_audios_in_a_row_are_avoided_unless_the_client_sent_audio()
    {
        var s = new ConversationState { Temperature = 70, Engagement = "alto" };
        var sig = InboundClassifier.Classify("achei interessante, e como isso ajudaria na minha loja?");
        Assert.Equal(ReplyFormat.Text, FormatAdvisor.Choose(s, sig, RobertaAction.PitchFeature, true, false, true));
        Assert.NotEqual(ReplyFormat.Text, FormatAdvisor.Choose(s, sig, RobertaAction.PitchFeature, true, true, true));
    }

    [Theory]
    [InlineData("Custa 199 reais por mês")]
    [InlineData("Acesse www.samos.com.br")]
    public void Audio_with_numbers_or_links_is_downgraded_to_text(string script) =>
        Assert.Equal(ReplyFormat.Text, FormatAdvisor.Finalize(ReplyFormat.Audio, script));

    [Fact]
    public void Audio_plus_text_needs_its_companion_and_splits_on_the_separator()
    {
        Assert.Equal(ReplyFormat.Audio, FormatAdvisor.Finalize(ReplyFormat.AudioPlusText, "Fala curta sem número"));
        var (a, t) = FormatAdvisor.Split("Roteiro falado ### Texto registrado");
        Assert.Equal("Roteiro falado", a); Assert.Equal("Texto registrado", t);
    }

    [Theory]
    [InlineData("Sou a assistente virtual da Samos.")]
    [InlineData("Eu sou um robô, mas posso ajudar.")]
    [InlineData("Sou uma inteligência artificial da Samos.")]
    public void Validator_blocks_ai_disclosure(string reply) =>
        Assert.Contains("ai_disclosure", ResponseValidator.Validate(reply, new(RobertaAction.ExplainFeature, Array.Empty<string>(), new HashSet<string>(), "", "")).Issues);

    [Fact]
    public void Validator_blocks_repeated_openers_and_banned_opener()
    {
        var ctx = new ValidationContext(RobertaAction.PitchFeature, new[] { "Show, então o estoque pesa bastante aí." }, new HashSet<string>(), "", "");
        Assert.Contains("repeated_opener", ResponseValidator.Validate("Show, então isso costuma dar dor de cabeça mesmo.", ctx).Issues);
        Assert.Contains("banned_opener", ResponseValidator.Validate("Entendi, faz sentido. Isso é comum.", new(RobertaAction.PitchFeature, Array.Empty<string>(), new HashSet<string>(), "", "")).Issues);
    }

    [Fact]
    public void Deepen_pain_requires_exactly_one_question_and_allows_pain_topic()
    {
        var ctx = new ValidationContext(RobertaAction.DeepenPain, Array.Empty<string>(), new HashSet<string> { "pain" }, ProductCatalog.AllText(Pain.StockDivergence), "sempre fura o estoque");
        Assert.True(ResponseValidator.Validate("Estoque furando dá dor de cabeça mesmo. Você percebe isso mais no inventário ou na hora de vender?", ctx).Ok);
        Assert.Contains("action_requires_one_question", ResponseValidator.Validate("Estoque furando dá dor de cabeça mesmo.", ctx).Issues);
    }

    [Fact]
    public void Pitch_without_question_is_valid_and_busy_reply_cannot_ask()
    {
        Assert.True(ResponseValidator.Validate("Isso costuma nascer em movimentações sem registro; o Axis rastreia cada uma delas.", new(RobertaAction.PitchFeature, Array.Empty<string>(), new HashSet<string>(), ProductCatalog.AllText(Pain.StockDivergence), "")).Ok);
        Assert.Contains("question_not_allowed", ResponseValidator.Validate("Sem problema! Quando é melhor falar?", new(RobertaAction.RespectBusy, Array.Empty<string>(), new HashSet<string>(), "", "")).Issues);
    }

    [Fact]
    public void Handoff_messages_create_value_and_never_say_ai_or_the_old_generic_line()
    {
        foreach (var k in new[] { "demo", "preco", "negociacao", "implantacao", "humano", "rede", "irritado", "duvida", "maduro", null })
            foreach (var pain in new string?[] { null, Pain.StockDivergence })
            {
                var m = HandoffMessages.Build(k, pain, null, 3);
                Assert.DoesNotContain("Perfeito! Vou passar", m);
                Assert.DoesNotMatch(@"assistente virtual|\bbot\b|robô", m.ToLowerInvariant());
            }
        Assert.Contains("estoque", HandoffMessages.Build("maduro", Pain.StockDivergence, null, 0));
    }

    [Fact]
    public void Catalog_only_contains_the_features_from_the_brief()
    {
        var all = string.Join(' ', ProductCatalog.Angles.Values.SelectMany(x => x).Select(a => a.Text)).ToLowerInvariant();
        foreach (var ok in new[] { "wms", "rfid", "rastreabilidade", "inventário" }) Assert.Contains(ok, all);
    }
}
