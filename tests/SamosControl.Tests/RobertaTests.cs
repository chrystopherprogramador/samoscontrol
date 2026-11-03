using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class RobertaClassifierTests
{
    [Theory]
    // opt-out
    [InlineData("Pare", "OptOut")] [InlineData("Pare de me mandar mensagem", "OptOut")] [InlineData("Não quero mais receber", "OptOut")]
    [InlineData("Descadastrar por favor", "OptOut")] [InlineData("Isso é spam", "OptOut")] [InlineData("Não me chame mais", "OptOut")] [InlineData("sair", "OptOut")]
    [InlineData("Posso parar na loja amanhã?", "!OptOut")] [InlineData("Quero saber mais", "!OptOut")]
    // refusal
    [InlineData("Não tenho interesse", "Refusal")] [InlineData("Sem interesse, obrigado", "Refusal")] [InlineData("Não preciso", "Refusal")]
    [InlineData("Não me interessa", "Refusal")] [InlineData("Dispenso", "Refusal")] [InlineData("Tenho interesse", "!Refusal")] [InlineData("Preciso de ajuda", "!Refusal")]
    // human
    [InlineData("Quero falar com um atendente", "HumanRequest")] [InlineData("Você é um robô?", "HumanRequest")] [InlineData("Me passa para um humano", "HumanRequest")]
    [InlineData("Pode falar com o dono?", "HumanRequest")] [InlineData("atendente", "HumanRequest")]
    // price / discount
    [InlineData("Quanto custa?", "Price")] [InlineData("Qual o valor?", "Price")] [InlineData("Qual o preço do sistema", "Price")] [InlineData("Tem plano mensal?", "Price")]
    [InlineData("O valor do meu sistema atual está ok", "!Price")] [InlineData("Uso o Bling", "!Price")]
    [InlineData("Tem desconto?", "Discount")] [InlineData("Dá para negociar?", "Discount")] [InlineData("Estou bem", "!Discount")]
    // demo
    [InlineData("Quero uma demonstração", "DemoRequest")] [InlineData("Podemos marcar uma reunião?", "DemoRequest")] [InlineData("Posso ver o sistema?", "DemoRequest")] [InlineData("Uso o Tiny", "!DemoRequest")]
    // yes / no / maybe
    [InlineData("Sim", "Yes")] [InlineData("Claro!", "Yes")] [InlineData("Pode sim", "Yes")] [InlineData("Não", "No")] [InlineData("Nenhuma", "No")] [InlineData("Nada", "No")]
    [InlineData("Não", "!Yes")] [InlineData("Talvez", "Maybe")] [InlineData("Não sei", "Maybe")] [InlineData("Depende", "Maybe")]
    // satisfied
    [InlineData("Tá tudo bem por aqui", "Satisfied")] [InlineData("Estou satisfeito com o meu sistema", "Satisfied")] [InlineData("Já tenho sistema e funciona bem", "Satisfied")]
    [InlineData("Oi, tudo bem?", "!Satisfied")] [InlineData("Uso planilha", "!Satisfied")]
    // angry
    [InlineData("Que absurdo!", "Angry")] [InlineData("Vou chamar o Procon", "Angry")] [InlineData("Você é muito chato", "Angry")] [InlineData("Obrigado pelo contato", "!Angry")]
    // out of ICP
    [InlineData("Não somos uma loja", "OutOfIcp")] [InlineData("Sou dentista", "OutOfIcp")] [InlineData("Somos uma oficina", "OutOfIcp")] [InlineData("Tenho uma loja de roupas", "!OutOfIcp")]
    // questions / identity / short
    [InlineData("Como funciona?", "AsksQuestion")] [InlineData("Vocês integram com Shopify?", "AsksQuestion")] [InlineData("Uso Bling", "!AsksQuestion")]
    [InlineData("Quem é você?", "Identity")] [InlineData("Oi", "Identity")] [InlineData("ok", "Short")] [InlineData("Tenho duas lojas na cidade toda", "!Short")]
    // erp & stores
    [InlineData("Uso o Bling", "Erp=bling")] [InlineData("Trabalho com Tiny ERP", "Erp=tiny")] [InlineData("Usamos o Microvix", "Erp=microvix")] [InlineData("Uso SAP", "Erp=sap")]
    [InlineData("Conta Azul", "Erp=conta azul")] [InlineData("Uso planilha", "NoErp")] [InlineData("Não tenho sistema", "NoErp")]
    [InlineData("Tenho 3 lojas", "Stores=3")] [InlineData("10 filiais", "Stores=10")] [InlineData("só uma loja", "Stores=1")] [InlineData("duas unidades", "Stores=2")]
    [InlineData("Tenho uma loja em Goiânia", "Stores=1")] [InlineData("Temos cinco lojas", "Stores=5")] [InlineData("Uso Bling", "Stores=")]
    // topics
    [InlineData("Faz nota fiscal?", "TopicFiscal")] [InlineData("Controla estoque?", "TopicStock")] [InlineData("Tenho várias lojas", "TopicMultistore")]
    [InlineData("Quanto tempo leva a implantação?", "TopicImplementation")]
    public void Classifier(string text, string expect)
    {
        var s = InboundClassifier.Classify(text);
        if (expect.StartsWith("Erp=")) { Assert.Equal(expect[4..], s.ErpMention); return; }
        if (expect.StartsWith("Stores=")) { var v = expect[7..]; Assert.Equal(v == "" ? (int?)null : int.Parse(v), s.StoreCount); return; }
        var neg = expect.StartsWith('!'); var name = neg ? expect[1..] : expect;
        var val = (bool)typeof(InboundSignals).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(s)!;
        Assert.Equal(!neg, val);
    }
}

public class RobertaEngineTests
{
    private static List<string> Run(string msgs, bool kb = true, bool pricing = false, Action<ConversationState>? seed = null)
    {
        var s = new ConversationState(); seed?.Invoke(s);
        var res = new List<string>();
        foreach (var m in msgs.Split('|'))
        {
            var sig = InboundClassifier.Classify(m);
            ConversationStateRules.Apply(s, sig, m);
            var d = CommercialDecisionEngine.Decide(s, sig, kb, pricing);
            ConversationStateRules.RecordAction(s, d.Action);
            res.Add(d.Action.ToString());
        }
        return res;
    }

    [Theory]
    [InlineData("Oi, tudo bem?", "AskCurrentERP")]
    [InlineData("Quem é você?", "AskCurrentERP")]
    [InlineData("Oi|Uso o Bling|Tenho 2 lojas|Perde muito tempo conferindo estoque", "AskCurrentERP,AskStoreCount,AskPainPoint,OfferDemo")]
    [InlineData("Oi|Uso Tiny|Tenho 1 loja|Não|Não", "AskCurrentERP,AskStoreCount,AskPainPoint,OfferDemo,CloseConversation")]
    [InlineData("Oi|Uso Bling e funciona bem", "AskCurrentERP,OfferDemo")]
    [InlineData("Oi|Uso Bling e funciona bem|Não precisa, obrigado", "AskCurrentERP,OfferDemo,CloseConversation")]
    [InlineData("Oi|Pare de me mandar mensagem", "AskCurrentERP,DoNotContact")]
    [InlineData("Não tenho interesse", "CloseConversation")]
    [InlineData("Sem interesse, obrigado", "CloseConversation")]
    [InlineData("Não tenho interesse, mas como funciona?", "CloseConversation")]
    [InlineData("Quanto custa?", "HandoffToSeller")]
    [InlineData("Quero falar com um atendente", "HandoffToSeller")]
    [InlineData("Quero ver uma demonstração", "HandoffToSeller")]
    [InlineData("Tem desconto?", "HandoffToSeller")]
    [InlineData("Oi|Uso Totvs|Temos 12 lojas", "AskCurrentERP,AskStoreCount,HandoffToSeller")]
    [InlineData("Que absurdo, vocês me incomodam", "HandoffToSeller")]
    [InlineData("Vocês são chatos", "HandoffToSeller")]
    [InlineData("Não somos uma loja", "CloseConversation")]
    [InlineData("Oi|Talvez", "AskCurrentERP,AskStoreCount")]
    [InlineData("Oi|ok|ok|ok|ok", "AskCurrentERP,AskStoreCount,AskPainPoint,OfferDemo,HandoffToSeller")]
    [InlineData("Oi|Não|Não|Não|Não", "AskCurrentERP,AskStoreCount,OfferDemo,CloseConversation,CloseConversation")]
    [InlineData("Oi|Uso Bling|2 lojas|Tá tudo bem, sem problemas", "AskCurrentERP,AskStoreCount,AskPainPoint,OfferDemo")]
    [InlineData("Oi|Tenho 1 loja|Na verdade tenho 15 lojas", "AskCurrentERP,AskPainPoint,HandoffToSeller")]
    [InlineData("Oi|Tenho interesse", "AskCurrentERP,AskStoreCount")]
    [InlineData("Oi|Eu uso o Linx aqui na loja e tenho cinco lojas", "AskCurrentERP,AskPainPoint")]
    [InlineData("Oi|Uso o Omie e atende bem", "AskCurrentERP,OfferDemo")]
    [InlineData("Oi|Uso Bling|3 lojas|Ocupa tempo no estoque|Sim, pode", "AskCurrentERP,AskStoreCount,AskPainPoint,OfferDemo,HandoffToSeller")]
    [InlineData("Oi|Não uso sistema, uso planilha", "AskCurrentERP,AskStoreCount")]
    [InlineData("Oi|Uso Bling|Quanto custa o plano?", "AskCurrentERP,AskStoreCount,HandoffToSeller")]
    [InlineData("Quero agendar uma demo", "HandoffToSeller")]
    [InlineData("Sem interesse", "CloseConversation")]
    public void Scenarios_never_loop_and_follow_rules(string msgs, string expected) => Assert.Equal(expected.Split(','), Run(msgs));

    [Theory]
    [InlineData("Vocês controlam estoque por tamanho e cor?", "ExplainStockControl")]
    [InlineData("Funciona para várias lojas?", "ExplainMultistore")]
    [InlineData("Emite nota fiscal?", "ExplainFeature")]
    [InlineData("Quanto tempo leva a implantação?", "ExplainFeature")]
    public void Questions_use_the_knowledge_base(string msg, string expected) => Assert.Equal(expected, Run(msg)[0]);

    [Theory]
    [InlineData("Emite nota fiscal?")] [InlineData("Vocês integram com Shopify?")] [InlineData("Como funciona o suporte?")]
    public void Question_outside_knowledge_base_goes_to_human(string msg) => Assert.Equal("HandoffToSeller", Run(msg, kb: false)[0]);

    [Fact]
    public void Pricing_is_explained_only_with_knowledge_and_only_once() =>
        Assert.Equal(new[] { "ExplainPricing", "HandoffToSeller" }, Run("Quanto custa?|Quanto custa?", pricing: true));

    [Fact]
    public void Known_lead_data_is_never_asked_again()
    {
        Assert.Equal("AskPainPoint", Run("Oi", seed: s => { s.CurrentErp = "bling"; s.StoreCount = 3; })[0]);
        Assert.Equal("OfferDemo", Run("Oi", seed: s => { s.CurrentErp = "bling"; s.StoreCount = 3; s.PainPoints = "estoque"; })[0]);
    }

    [Fact]
    public void Turn_cap_forces_handoff() => Assert.Equal("HandoffToSeller", Run("Oi", seed: s => s.TurnCount = 8)[0]);

    [Fact]
    public void Same_question_is_never_asked_twice_over_long_conversation()
    {
        var acts = Run("Oi|hm|hm|hm|hm|hm|hm");
        foreach (var q in new[] { "AskCurrentERP", "AskStoreCount", "AskPainPoint" }) Assert.True(acts.Count(a => a == q) <= 1, q);
    }
}

public class RobertaValidatorTests
{
    private static ValidationResult V(string reply, RobertaAction a, string[]? recent = null, string[]? asked = null, string grounded = "", string inbound = "")
        => ResponseValidator.Validate(reply, new ValidationContext(a, recent ?? Array.Empty<string>(), (asked ?? Array.Empty<string>()).ToHashSet(), grounded, inbound));

    [Theory]
    [InlineData("Qual sistema vocês usam hoje para vender e controlar o estoque?", RobertaAction.AskCurrentERP)]
    [InlineData("Quantas lojas vocês têm hoje?", RobertaAction.AskStoreCount)]
    [InlineData("O que mais toma tempo na rotina da loja?", RobertaAction.AskPainPoint)]
    [InlineData("Que bom! Se um dia quiser conhecer algo diferente, posso te mostrar rapidinho. Topa uma demonstração sem compromisso?", RobertaAction.OfferDemo)]
    [InlineData("Obrigada pelo retorno, qualquer coisa estamos por aqui.", RobertaAction.CloseConversation)]
    public void Good_replies_pass(string reply, RobertaAction a) => Assert.True(V(reply, a).Ok, string.Join(",", V(reply, a).Issues));

    [Theory]
    [InlineData("", RobertaAction.AskCurrentERP, "empty")]
    [InlineData("Qual sistema vocês usam e quantas lojas têm?", RobertaAction.AskCurrentERP, "")]
    [InlineData("Combinado.", RobertaAction.AskCurrentERP, "action_requires_one_question")]
    [InlineData("Entendi. Posso ajudar com algo mais?", RobertaAction.CloseConversation, "question_not_allowed")]
    [InlineData("Quantas lojas você tem? E quantos funcionários?", RobertaAction.AskStoreCount, "multiple_questions")]
    [InlineData("Garanto que você vai economizar tempo.", RobertaAction.ExplainFeature, "forbidden:garanto")]
    [InlineData("Temos 100% de satisfação.", RobertaAction.ExplainFeature, "forbidden:100%")]
    [InlineData("Oferecemos um período grátis.", RobertaAction.ExplainFeature, "forbidden:gratis")]
    [InlineData("Posso fazer um desconto para você.", RobertaAction.ExplainFeature, "forbidden:desconto")]
    [InlineData("Sou humano, pode confiar.", RobertaAction.ExplainFeature, "forbidden:sou humano")]
    [InlineData("O plano custa R$ 199 por mês.", RobertaAction.ExplainPricing, "unsupported_number")]
    [InlineData("Reduzimos 30% dos erros de estoque.", RobertaAction.ExplainFeature, "unsupported_number")]
    [InlineData("Veja em www.exemplo.com.br", RobertaAction.ExplainFeature, "unsupported_url")]
    [InlineData("Integramos com o Bling sem problemas.", RobertaAction.ExplainFeature, "unsupported_erp:bling")]
    [InlineData("Então vocês usam Totvs, certo?", RobertaAction.OfferDemo, "unsupported_erp:totvs")]
    public void Bad_replies_are_flagged(string reply, RobertaAction a, string issue)
    {
        var r = V(reply, a);
        Assert.False(r.Ok);
        if (issue != "") Assert.Contains(r.Issues, i => i.StartsWith(issue));
    }

    [Fact]
    public void Repeated_question_topic_is_flagged() =>
        Assert.Contains(V("Quantas lojas você tem?", RobertaAction.OfferDemo, asked: new[] { "stores" }).Issues, i => i.StartsWith("repeated_question"));

    [Fact]
    public void Repeated_message_is_flagged() =>
        Assert.Contains("repeated_message", V("Qual sistema vocês usam hoje para controlar o estoque?", RobertaAction.AskCurrentERP, recent: new[] { "Qual sistema vocês usam hoje para controlar o estoque?" }).Issues);

    [Fact]
    public void Too_long_and_too_many_sentences_flagged()
    {
        Assert.Contains("too_long", V(new string('a', 450) + "?", RobertaAction.AskStoreCount).Issues);
        Assert.Contains("too_many_sentences", V("Um. Dois. Três. Quatro.", RobertaAction.ExplainFeature).Issues);
    }

    [Fact]
    public void Facts_in_knowledge_base_make_numbers_and_erps_acceptable()
    {
        Assert.True(V("O plano custa R$ 199 por mês.", RobertaAction.ExplainPricing, grounded: "Plano inicial: R$ 199 por mês").Ok);
        Assert.True(V("Integramos com o Bling.", RobertaAction.ExplainFeature, inbound: "Uso o Bling hoje").Ok);
    }

    [Fact]
    public void Scores_reflect_hallucination_and_repetition()
    {
        var bad = V("Reduzimos 30% dos erros.", RobertaAction.ExplainFeature);
        Assert.True(bad.Scores["HallucinationScore"] < 100);
        var rep = V("Qual sistema vocês usam hoje para controlar o estoque?", RobertaAction.AskCurrentERP, recent: new[] { "Qual sistema vocês usam hoje para controlar o estoque?" });
        Assert.True(rep.Scores["RepetitionScore"] <= 10);
    }
}

public class RobertaFlowTests
{
    private sealed class FakeLlm : ILlmService
    {
        public bool IsConfigured { get; set; } = true;
        public Queue<string> Replies = new(); public string Fallback = "";
        public int Calls;
        public Task<string> ChatAsync(LlmCall call, CancellationToken ct = default)
        {
            Calls++;
            if (call.Task == "roberta_judge") return Task.FromResult("{\"ok\":true,\"issues\":\"\"}");
            return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : Fallback);
        }
    }

    private sealed class FailingAi : IAiProvider
    {
        public int Calls;
        public string Name => "fail"; public bool IsConfigured => true;
        public Task<List<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<string> ChatAsync(IEnumerable<AiChatMessage> messages, bool jsonMode, CancellationToken ct = default) { Calls++; throw new InvalidOperationException("boom"); }
    }

    private static async Task<(SdrContact c, AppDbContext db, SdrService sdr)> Setup(IServiceScope sc, SdrMode mode)
    {
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var lead = new Lead { Name = "Maria", CompanyName = "Loja da Maria", City = "Goiânia", State = "GO", Segment = "Moda", Phone = "62999990001", Status = LeadStatus.Contato };
        var camp = new SdrCampaign { Name = "T", Mode = mode, AiReplies = true };
        db.Leads.Add(lead); db.SdrCampaigns.Add(camp); await db.SaveChangesAsync();
        var c = new SdrContact { CampaignId = camp.Id, LeadId = lead.Id, Phone = "5562999990001", PhoneKey = PhoneUtil.Key("5562999990001")!, Stage = SdrStage.Enviado };
        db.SdrContacts.Add(c); await db.SaveChangesAsync();
        return (c, db, sc.ServiceProvider.GetRequiredService<SdrService>());
    }

    [Fact]
    public async Task Shadow_mode_records_progression_and_sends_nothing()
    {
        var llm = new FakeLlm(); llm.Replies.Enqueue("Qual sistema vocês usam hoje para vender e controlar o estoque?"); llm.Replies.Enqueue("Quantas lojas vocês têm hoje?");
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var (c, db, sdr) = await Setup(sc, SdrMode.Shadow);
        await sdr.HandleReplyAsync(null, c.Phone, "Oi");
        await sdr.HandleReplyAsync(null, c.Phone, "Uso o Bling");
        var sugs = await db.SdrSuggestions.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(new[] { "AskCurrentERP", "AskStoreCount" }, sugs.Select(x => x.Action));
        Assert.All(sugs, x => { Assert.Equal(SuggestionStatus.Shadow, x.Status); Assert.True(x.ValidatorOk); Assert.Equal(1, x.PromptVersion); });
        Assert.Equal(0, await db.WhatsAppMessages.CountAsync());
        var st = await db.ConversationStates.SingleAsync();
        Assert.Equal("bling", st.CurrentErp); Assert.Equal("erp,stores", st.QuestionsAsked);
        Assert.NotEqual(SdrStage.Repassado, (await db.SdrContacts.FindAsync(c.Id))!.Stage);
    }

    [Fact]
    public async Task Invalid_reply_is_regenerated_once_then_escalated()
    {
        var llm = new FakeLlm { Fallback = "Garanto 100% de resultado. Qual sistema e quantas lojas?" };
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var (c, db, sdr) = await Setup(sc, SdrMode.Shadow);
        await sdr.HandleReplyAsync(null, c.Phone, "Oi");
        var s = await db.SdrSuggestions.SingleAsync();
        Assert.Equal("HandoffToSeller", s.Action); Assert.False(s.ValidatorOk); Assert.Null(s.Reply);
        Assert.Equal(2, llm.Calls);
    }

    [Fact]
    public async Task Invalid_first_attempt_is_fixed_by_regeneration()
    {
        var llm = new FakeLlm(); llm.Replies.Enqueue("Garanto 100% de resultado. Qual sistema e quantas lojas?"); llm.Replies.Enqueue("Qual sistema vocês usam hoje para controlar o estoque?");
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var (c, db, sdr) = await Setup(sc, SdrMode.Shadow);
        await sdr.HandleReplyAsync(null, c.Phone, "Oi");
        var s = await db.SdrSuggestions.SingleAsync();
        Assert.Equal("AskCurrentERP", s.Action); Assert.True(s.ValidatorOk);
    }

    [Fact]
    public async Task Without_llm_the_decision_is_escalated_not_invented()
    {
        var llm = new FakeLlm { IsConfigured = false };
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var (c, db, sdr) = await Setup(sc, SdrMode.Shadow);
        await sdr.HandleReplyAsync(null, c.Phone, "Oi");
        Assert.Equal("HandoffToSeller", (await db.SdrSuggestions.SingleAsync()).Action);
    }

    [Fact]
    public async Task Opt_out_is_honored_in_every_mode_without_sending_in_shadow()
    {
        var llm = new FakeLlm();
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var (c, db, sdr) = await Setup(sc, SdrMode.Shadow);
        await sdr.HandleReplyAsync(null, c.Phone, "Pare de me mandar mensagem");
        Assert.Equal(SdrStage.OptOut, (await db.SdrContacts.FindAsync(c.Id))!.Stage);
        Assert.Equal(0, await db.WhatsAppMessages.CountAsync());
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Review_mode_queues_and_reject_sends_nothing()
    {
        var llm = new FakeLlm(); llm.Replies.Enqueue("Qual sistema vocês usam hoje para vender e controlar o estoque?");
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var (c, db, sdr) = await Setup(sc, SdrMode.Review);
        await sdr.HandleReplyAsync(null, c.Phone, "Oi");
        var s = await db.SdrSuggestions.SingleAsync();
        Assert.Equal(SuggestionStatus.Pendente, s.Status);
        await sdr.ReviewAsync(s.Id, "reject", null, "tester");
        db.ChangeTracker.Clear();
        var after = await db.SdrSuggestions.SingleAsync();
        Assert.Equal(SuggestionStatus.Rejeitada, after.Status); Assert.Equal("tester", after.ReviewedBy);
        Assert.Equal(0, await db.WhatsAppMessages.CountAsync());
        Assert.Contains("já foi tratada", await sdr.ReviewAsync(s.Id, "approve", null, "tester"));
    }

    [Fact]
    public async Task Prompt_activation_requires_regression_unless_forced()
    {
        var llm = new FakeLlm { IsConfigured = false };
        using var h = new TestHost(configure: s => s.AddScoped<ILlmService>(_ => llm)); using var sc = h.Scope();
        var ps = sc.ServiceProvider.GetRequiredService<PromptService>();
        var v1 = await ps.ActiveAsync();
        var v2 = await ps.CreateAsync(PromptService.DefaultContent + "\nExtra.", "teste", "t");
        Assert.Equal(2, v2.Version);
        Assert.False((await ps.ActivateAsync(v2.Id, false)).Ok);
        Assert.True((await ps.ActivateAsync(v2.Id, true)).Ok);
        Assert.Equal(2, (await ps.ActiveAsync()).Version);
        Assert.Equal(1, v1.Version);
    }

    [Fact]
    public async Task Llm_service_retries_logs_and_opens_circuit_breaker()
    {
        LlmService.ResetBreaker();
        var ai = new FailingAi();
        using var h = new TestHost(configure: s => s.AddSingleton<IAiProvider>(ai)); using var sc = h.Scope();
        var llm = sc.ServiceProvider.GetRequiredService<ILlmService>();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var call = new LlmCall("t", new[] { new AiChatMessage("user", "oi") });
        for (var i = 0; i < LlmService.BreakerThreshold; i++) await Assert.ThrowsAsync<InvalidOperationException>(() => llm.ChatAsync(call));
        Assert.Equal(LlmService.BreakerThreshold * 2, ai.Calls);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => llm.ChatAsync(call));
        Assert.Contains("Circuit breaker", ex.Message);
        Assert.Equal(LlmService.BreakerThreshold * 2, ai.Calls);
        Assert.Equal(LlmService.BreakerThreshold + 1, await db.LlmExecutions.CountAsync());
        LlmService.ResetBreaker();
    }
}

