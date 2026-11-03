using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SamosControl.Tests;

/// <summary>Roberta 4.0: 20 simulated customer profiles with objective assertions on the decision (not just "an action ran"), plus the new building blocks.</summary>
public class Roberta4Tests
{
    private readonly ITestOutputHelper _out;
    public Roberta4Tests(ITestOutputHelper o) => _out = o;

    private sealed record Turn(string Client, Decision D, ReplyFormat F, CommercialPlan Plan, List<string> Issues);

    private ConversationState State = new();
    private static string[] Acts(List<Turn> t) => t.Select(x => x.D.Action.ToString()).ToArray();

    private List<Turn> Simulate(string title, string[] msgs, bool voice = false, bool[]? audioIn = null, Action<int, ConversationState>? before = null, bool kb = false)
    {
        var s = new ConversationState { LeadId = 1 }; var turns = new List<Turn>(); var lastAudio = false;
        _out.WriteLine($"===== {title} =====");
        for (var i = 0; i < msgs.Length; i++)
        {
            var m = msgs[i]; var viaAudio = audioIn != null && audioIn[i];
            before?.Invoke(i, s);
            var sig = InboundClassifier.Classify(m);
            ConversationStateRules.Apply(s, sig, m, viaAudio, TimeSpan.FromMinutes(1));
            var d = CommercialDecisionEngine.Decide(s, sig, kb, false);
            var f = d.NeedsLlm ? FormatAdvisor.Choose(s, sig, d.Action, voice, viaAudio, lastAudio) : ReplyFormat.Text;
            var plan = PlanBuilder.Build(s, sig, d, f);
            var issues = PlanValidator.Validate(plan, s, sig, voice);
            ConversationStateRules.RecordAction(s, d.Action, null, FormatAdvisor.Code(f), d.HandoffKind);
            lastAudio = f != ReplyFormat.Text;
            turns.Add(new(m, d, f, plan, issues));
            _out.WriteLine($"Cliente : {m}{(viaAudio ? "  [áudio]" : "")}");
            _out.WriteLine($"  memória: temp {s.Temperature} · {s.OpportunityStage} · dor {s.MainPain ?? "-"} · objeções [{s.ObjectionLog}] · canal {s.PreferredChannel ?? "-"} · perda {s.LossSignals}");
            _out.WriteLine($"Roberta : {d.Action}{(d.Topic != null ? "(" + d.Topic + ")" : "")} · {FormatAdvisor.Code(f)} · conf {plan.Confidence} · {d.Reason}");
            if (issues.Count > 0) _out.WriteLine("  PLANO INVÁLIDO: " + string.Join(",", issues));
        }
        State = s;
        return turns;
    }

    private static void NoPlanIssues(List<Turn> t) => Assert.All(t, x => Assert.Empty(x.Issues));

    [Fact]
    public void P01_distrustful_lead_gets_honest_answer_and_an_exit()
    {
        var t = Simulate("1. Desconfiado", new[] { "Oi", "Quem é você? Como conseguiu meu número? Isso é golpe?" });
        Assert.Equal(RobertaAction.HandleObjection, t[1].D.Action); Assert.Equal(Obj.Desconfianca, t[1].D.Topic);
        Assert.Contains("parar", RobertaService.Instruction(t[1].D.Action, t[1].D.Topic)); NoPlanIssues(t);
    }

    [Fact]
    public void P02_interested_but_busy_is_respected_without_questions()
    {
        var t = Simulate("2. Interessado, sem tempo", new[] { "Oi", "Tenho interesse mas estou sem tempo agora, me chama amanhã à tarde" });
        Assert.Equal(RobertaAction.RespectBusy, t[1].D.Action);
        Assert.Contains("tarde", State.PreferredTime ?? ""); Assert.False(string.IsNullOrWhiteSpace(State.Commitments));
    }

    [Fact]
    public void P03_satisfied_with_competitor_is_not_pushed()
    {
        var t = Simulate("3. Satisfeito com concorrente", new[] { "Oi", "Uso o Bling e atende bem", "Já tenho sistema, está tudo certo" });
        Assert.DoesNotContain(t, x => x.D.Action == RobertaAction.PitchFeature);
        Assert.Contains(t.Skip(1), x => x.D.Action is RobertaAction.OfferDemo or RobertaAction.HandleObjection or RobertaAction.CloseConversation);
        Assert.Equal(RobertaAction.CloseConversation, t[^1].D.Action); NoPlanIssues(t);
    }

    [Fact]
    public void P04_stock_pain_is_explored_with_varied_strategies_not_a_fixed_script()
    {
        var t = Simulate("4. Insatisfeito com estoque", new[] { "Oi", "Datasystem", "1 loja", "Sempre fura o estoque aqui", "Mais no inventário, só descobrimos lá", "Acontece quase toda semana, perdemos venda", "Várias pessoas mexendo no estoque" });
        Assert.DoesNotContain(t, x => x.D.Action == RobertaAction.HandoffToSeller);
        var after = t.Skip(3).Select(x => x.D.Action).ToList();
        Assert.Contains(RobertaAction.DeepenPain, after); Assert.Contains(RobertaAction.PitchFeature, after);
        Assert.True(after.Distinct().Count() >= 4, "deve variar as ações: " + string.Join(",", after));
        Assert.DoesNotContain(Enumerable.Range(0, after.Count - 2), i => after[i] == after[i + 1] && after[i + 1] == after[i + 2]);
        NoPlanIssues(t);
    }

    [Fact]
    public void P05_immediate_price_question_goes_to_human_with_no_numbers()
    {
        var t = Simulate("5. Pergunta preço", new[] { "Quanto custa o sistema?" });
        Assert.Equal(RobertaAction.HandoffToSeller, t[0].D.Action); Assert.Equal("preco", t[0].D.HandoffKind);
        Assert.DoesNotMatch(@"\d", HandoffMessages.Build("preco", null, null, 1));
    }

    [Fact]
    public void P06_monosyllables_lead_is_paused_then_closed_not_chased()
    {
        var t = Simulate("6. Só monossílabos", new[] { "Oi", "ok", "sim", "hm", "ok", "ok", "ok" });
        Assert.Contains(RobertaAction.PauseConversation, t.Select(x => x.D.Action));
        Assert.True(Array.IndexOf(Acts(t), "PauseConversation") < Array.IndexOf(Acts(t), "CloseConversation"));
        Assert.DoesNotContain(t, x => x.D.Action == RobertaAction.HandoffToSeller);
    }

    [Fact]
    public void P07_talkative_lead_gets_a_longer_discovery_and_impact_exploration()
    {
        var t = Simulate("7. Comunicativo", new[] { "Oi, tudo bem? Pode falar!", "Eu uso o Linx há uns cinco anos", "Tenho 2 lojas aqui na cidade, uma de calçados e outra de bolsas", "Meu maior problema é o estoque, sempre fura e a gente não sabe o motivo, ninguém registra direito" });
        Assert.Equal(new[] { "AskCurrentERP", "AskStoreCount", "AskPainPoint", "DeepenPain" }, Acts(t)); NoPlanIssues(t);
        var more = Simulate("7b. Comunicativo segue", new[] { "Uso o Linx e tenho 2 lojas", "O estoque sempre fura e a gente não sabe o motivo, ninguém registra direito", "Acontece muito, quase toda semana, e a gente perde vendas por isso, é bem chato mesmo" });
        Assert.Contains(more.Select(x => x.D.Action), a => a is RobertaAction.ExploreImpact or RobertaAction.PitchFeature);
    }

    [Fact]
    public void P08_audio_preference_is_used_only_with_tts_and_text_request_always_wins()
    {
        var on = Simulate("8a. Prefere áudio (com TTS)", new[] { "Oi", "Prefiro áudio, pode mandar áudio", "Uso o Bling e o estoque sempre fura bastante aqui", "Entendi, sim" }, voice: true, audioIn: new[] { false, true, true, true });
        Assert.Equal("audio", State.PreferredChannel);
        Assert.Contains(on, x => x.F != ReplyFormat.Text);
        var off = Simulate("8b. Prefere áudio (sem TTS)", new[] { "Oi", "Prefiro áudio, pode mandar áudio", "Uso o Bling e o estoque sempre fura bastante aqui" }, voice: false, audioIn: new[] { false, true, true });
        Assert.All(off, x => Assert.Equal(ReplyFormat.Text, x.F));
        var txt = Simulate("8c. Só texto", new[] { "Oi", "Por favor só texto, não mande áudio", "Uso o Bling e o estoque sempre fura bastante aqui", "Entendi, sim" }, voice: true, audioIn: new[] { false, false, true, true });
        Assert.Equal("texto", State.PreferredChannel); Assert.All(txt, x => Assert.Equal(ReplyFormat.Text, x.F)); NoPlanIssues(txt);
    }

    [Fact]
    public void P09_opt_out_is_final()
    {
        var t = Simulate("9. Não quer mais contato", new[] { "Oi", "Uso Bling", "Não quero mais receber mensagens, pare de me chamar" });
        Assert.Equal(RobertaAction.DoNotContact, t[^1].D.Action); Assert.True(State.OptOut); NoPlanIssues(t);
    }

    [Fact]
    public void P10_multi_store_lead_goes_to_a_human_who_handles_networks()
    {
        var t = Simulate("10. Várias lojas", new[] { "Oi", "Uso Totvs e temos 12 lojas" });
        Assert.Equal(RobertaAction.HandoffToSeller, t[^1].D.Action); Assert.Equal("rede", t[^1].D.HandoffKind);
    }

    [Fact]
    public void P11_demo_request_is_handed_off_immediately()
    {
        var t = Simulate("11. Pede demonstração", new[] { "Oi, queria ver uma demonstração do sistema" });
        Assert.Equal(RobertaAction.HandoffToSeller, t[0].D.Action); Assert.Equal("demo", t[0].D.HandoffKind);
    }

    [Fact]
    public void P12_discount_negotiation_is_never_conducted_by_roberta()
    {
        var t = Simulate("12. Negocia desconto", new[] { "Oi", "Uso Bling", "Consegue fazer um desconto se eu fechar hoje?" });
        Assert.Equal(RobertaAction.HandoffToSeller, t[^1].D.Action);
        Assert.DoesNotMatch(@"\d|R\$|%", HandoffMessages.Build(t[^1].D.HandoffKind, null, null, 1));
    }

    [Fact]
    public void P13_my_system_does_everything_looks_for_real_differences_then_concedes()
    {
        var t = Simulate("13. Meu sistema faz tudo isso", new[] { "Oi", "Uso o Linx", "O meu sistema já faz tudo isso", "Já disse, meu sistema faz tudo" });
        Assert.Equal(RobertaAction.DifferentiateCompetitor, t[2].D.Action);
        Assert.Equal(RobertaAction.CloseConversation, t[3].D.Action);
        var instr = RobertaService.Instruction(RobertaAction.DifferentiateCompetitor);
        Assert.Contains("verificável", instr); Assert.Contains("não houver", instr);
    }

    [Fact]
    public void P14_topic_change_is_acknowledged_without_losing_the_thread()
    {
        var t = Simulate("14. Muda de assunto", new[] { "Oi", "Uso o Bling", "Viu o jogo do Cruzeiro ontem? Foi muito bom!" });
        Assert.Equal(RobertaAction.BuildRapport, t[^1].D.Action);
        Assert.Contains("retome", RobertaService.Instruction(RobertaAction.BuildRapport)); Assert.Contains("sem inventar", RobertaService.Instruction(RobertaAction.BuildRapport));
    }

    [Fact]
    public void P15_lead_returning_days_later_resumes_from_the_known_pain()
    {
        var t = Simulate("15. Volta dias depois", new[] { "Uso Bling e tenho 2 lojas", "O estoque sempre fura aqui", "Mais no inventário mesmo", "Voltei, ainda estou com aquele problema" },
            before: (i, s) => { if (i == 3) s.LastInboundAt = DateTime.UtcNow.AddDays(-4); });
        Assert.True(State.TurnCount >= 4); Assert.Equal(RobertaAction.ConfirmUnderstanding, t[^1].D.Action);
        Assert.Contains("FATOS confirmados", RobertaService.Instruction(RobertaAction.ConfirmUnderstanding));
    }

    [Fact]
    public void P16_asking_if_it_is_an_ai_gets_a_transparent_fixed_answer_not_a_handoff()
    {
        var t = Simulate("16. É IA?", new[] { "Oi", "Você é um robô? Estou falando com uma IA?" });
        Assert.Equal(RobertaAction.AnswerIdentity, t[^1].D.Action); Assert.False(t[^1].D.NeedsLlm); Assert.True(State.AiDisclosed); Assert.False(State.HumanRequested);
        Assert.Contains("inteligência artificial", RobertaService.IdentityReply); Assert.Contains("alguém do nosso time", RobertaService.IdentityReply); NoPlanIssues(t);
    }

    [Fact]
    public void P17_two_different_pains_restart_the_deepening_for_the_second_one()
    {
        var t = Simulate("17. Duas dores", new[] { "Uso Bling e tenho 2 lojas", "O estoque sempre fura aqui", "Mais no inventário", "Além disso a emissão de nota fiscal também dá muito problema, vive travando" });
        Assert.NotEqual(Pain.StockDivergence, State.MainPain);
        Assert.Contains("estoque", State.PainCategories ?? ""); Assert.Equal(RobertaAction.DeepenPain, t[^1].D.Action);
        Assert.False(string.IsNullOrWhiteSpace(State.Hypotheses));
    }

    [Fact]
    public void P18_unmet_need_is_answered_honestly_and_never_promised()
    {
        var t = Simulate("18. Necessidade fora do Axis", new[] { "Oi", "Vocês fazem aplicativo de delivery com entregador rastreado por GPS?" });
        Assert.Contains(t[^1].D.Action, new[] { RobertaAction.HandoffToSeller, RobertaAction.HandleObjection });
        Assert.Contains("não prometa", RobertaService.Instruction(RobertaAction.HandleObjection, Obj.NecessidadeNaoAtendida));
    }

    [Fact]
    public void P19_proof_of_results_never_invents_cases()
    {
        var t = Simulate("19. Pede prova de resultados", new[] { "Oi", "Uso Bling", "Tem algum case de cliente que reduziu perda de estoque? Quero ver resultados comprovados" });
        Assert.Equal(RobertaAction.HandoffToSeller, t[^1].D.Action); Assert.Equal("prova", t[^1].D.HandoffKind);
        var msg = HandoffMessages.Build("prova", null, null, 1);
        Assert.DoesNotMatch(@"\d|%|reduz", msg); Assert.Contains("comprovar", msg);
    }

    [Fact]
    public void P20_irritated_by_insistence_closes_and_does_not_continue()
    {
        var t = Simulate("20. Irritado com insistência", new[] { "Oi", "Uso Bling", "Já falei que não tenho interesse, para de insistir!" });
        Assert.Contains(t[^1].D.Action, new[] { RobertaAction.CloseConversation, RobertaAction.HandoffToSeller, RobertaAction.DoNotContact });
        Assert.NotEqual(RobertaAction.PitchFeature, t[^1].D.Action);
    }

    // ----- Objections (each handled differently, repeated objection respected) -----
    [Theory]
    [InlineData("Já tenho sistema", Obj.JaTenhoSistema)]
    [InlineData("Está muito caro", Obj.Caro)]
    [InlineData("Não quero trocar de sistema", Obj.NaoTrocar)]
    [InlineData("Vou pensar e te falo", Obj.VouPensar)]
    [InlineData("Meu sistema atual faz tudo isso", Obj.FazTudo)]
    [InlineData("Me manda uma apresentação", Obj.Material)]
    [InlineData("Quero ver resultados comprovados de outros clientes", Obj.Prova)]
    public void Objections_are_detected(string text, string expected) => Assert.Equal(expected, Roberta4Rules.Objection(InboundClassifier.Normalize(text)));

    [Fact]
    public void Repeated_objection_is_respected_instead_of_insisted()
    {
        var t = Simulate("Objeção repetida", new[] { "Oi", "Vou pensar", "Ainda vou pensar" });
        Assert.Equal(RobertaAction.HandleObjection, t[1].D.Action); Assert.Equal(RobertaAction.PauseConversation, t[2].D.Action);
    }

    [Fact]
    public void Material_request_without_approved_content_goes_to_a_human_and_with_content_sends_it()
    {
        Assert.Equal(RobertaAction.HandoffToSeller, Simulate("Material sem KB", new[] { "Oi", "Me manda uma apresentação" }, kb: false)[^1].D.Action);
        Assert.Equal(RobertaAction.SendUsefulInformation, Simulate("Material com KB", new[] { "Oi", "Me manda uma apresentação" }, kb: true)[^1].D.Action);
    }

    // ----- Plan contract: validated, never trusted blindly -----
    [Fact]
    public void Plan_roundtrips_and_malformed_plan_is_rejected()
    {
        var s = new ConversationState { LeadId = 1 }; var sig = InboundClassifier.Classify("Sempre fura o estoque");
        ConversationStateRules.Apply(s, sig, "Sempre fura o estoque", false, null);
        var d = CommercialDecisionEngine.Decide(s, sig, false, false);
        var plan = PlanBuilder.Build(s, sig, d, ReplyFormat.Text);
        var back = CommercialPlan.TryParse("lixo antes " + plan.ToJson() + " lixo depois");
        Assert.NotNull(back); Assert.Equal(plan.SelectedStrategy, back!.SelectedStrategy); Assert.Contains("\"selected_strategy\"", plan.ToJson());
        Assert.Null(CommercialPlan.TryParse("não é json")); Assert.Null(CommercialPlan.TryParse("{ quebrado"));
    }

    [Fact]
    public void Validator_rejects_plans_that_break_hard_rules()
    {
        var s = new ConversationState { LeadId = 1, PreferredChannel = "texto" };
        var ok = new CommercialPlan { SelectedStrategy = "discover_need", ResponseModality = "text", Confidence = 0.8 };
        Assert.Empty(PlanValidator.Validate(ok, s, InboundClassifier.Classify("oi"), false));
        Assert.Contains("audio_without_tts", PlanValidator.Validate(ok with { ResponseModality = "audio" }, new ConversationState(), InboundClassifier.Classify("oi"), false));
        Assert.Contains("audio_against_preference", PlanValidator.Validate(ok with { ResponseModality = "audio" }, s, InboundClassifier.Classify("oi"), true));
        Assert.Contains("unknown_strategy", PlanValidator.Validate(ok with { SelectedStrategy = "inventada" }, s, InboundClassifier.Classify("oi"), false));
        Assert.Contains("confidence_out_of_range", PlanValidator.Validate(ok with { Confidence = 7 }, s, InboundClassifier.Classify("oi"), false));
        Assert.Contains("opt_out_ignored", PlanValidator.Validate(ok, s, InboundClassifier.Classify("pare de me mandar mensagem"), false));
        Assert.Contains("identity_not_disclosed", PlanValidator.Validate(ok, s, InboundClassifier.Classify("você é uma IA?"), false));
    }

    // ----- Follow-up gate -----
    [Fact]
    public void Follow_up_policy_respects_consent_limits_interval_hours_and_reason()
    {
        var now = new DateTime(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);
        var withPain = new ConversationState { LeadId = 1, MainPain = Pain.StockDivergence, PainPoints = "estoque" };
        Assert.True(FollowUpPolicy.Evaluate(withPain, 1, 3, false, now.AddHours(-25), now, 10, false).Allowed);
        Assert.False(FollowUpPolicy.Evaluate(new ConversationState { OptOut = true }, 1, 3, false, now.AddHours(-25), now, 10, false).Allowed);
        Assert.True(FollowUpPolicy.Evaluate(withPain, 1, 3, true, now.AddHours(-25), now, 10, false).Close);
        Assert.True(FollowUpPolicy.Evaluate(withPain, 4, 9, false, now.AddHours(-25), now, 10, false).Close); // hard cap
        var soft = FollowUpPolicy.Evaluate(withPain, 1, 3, false, now.AddHours(-2), now, 10, false); Assert.False(soft.Allowed); Assert.False(soft.Close);
        Assert.False(FollowUpPolicy.Evaluate(withPain, 1, 3, false, now.AddHours(-25), now, 22, false).Allowed);
        Assert.False(FollowUpPolicy.Evaluate(withPain, 1, 3, false, now.AddHours(-25), now, 10, true).Allowed);
        Assert.True(FollowUpPolicy.Evaluate(new ConversationState(), 2, 3, false, now.AddHours(-25), now, 10, false).Close); // nothing useful to resume
    }

    // ----- Memory: facts vs hypotheses, state versioning -----
    [Fact]
    public void Hypotheses_are_kept_apart_from_confirmed_facts_and_state_is_versioned()
    {
        var s = new ConversationState { LeadId = 1 };
        var v0 = s.StateVersion;
        ConversationStateRules.Apply(s, InboundClassifier.Classify("O estoque sempre fura aqui"), "O estoque sempre fura aqui", false, null);
        Assert.Equal(Pain.StockDivergence, s.MainPain); Assert.False(string.IsNullOrWhiteSpace(s.Hypotheses));
        Assert.DoesNotContain(s.Hypotheses!, s.PainPoints ?? "x"); Assert.False(s.ImpactKnown); Assert.True(s.StateVersion > v0);
        var facts = typeof(RobertaService).GetMethod("FactsBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { new Lead { Name = "Loja X" }, s })!.ToString()!;
        Assert.Contains("HIPÓTESE", facts); Assert.DoesNotContain("FATO: " + s.Hypotheses, facts);
    }

    // ----- Metrics -----
    [Fact]
    public async Task Metrics_are_computed_per_strategy_objection_and_prompt_version_excluding_shadow()
    {
        using var h = new TestHost(); using var sc = h.Scope(); var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var lead = new Lead { Name = "L" }; db.Leads.Add(lead); await db.SaveChangesAsync();
        db.SdrCampaigns.Add(new SdrCampaign { Name = "c" }); await db.SaveChangesAsync();
        var camp = await db.SdrCampaigns.FirstAsync();
        var contact = new SdrContact { CampaignId = camp.Id, LeadId = lead.Id, Phone = "5531999990000", PhoneKey = "31999990000", Stage = SdrStage.Respondeu };
        db.SdrContacts.Add(contact); await db.SaveChangesAsync();
        db.SdrSuggestions.Add(new SdrSuggestion { ContactId = contact.Id, LeadId = lead.Id, Action = "HandleObjection", Strategy = "handle_objection", ObjectionKind = Obj.Caro, PromptVersion = 2, Status = SuggestionStatus.Enviada, ValidatorOk = true });
        db.SdrSuggestions.Add(new SdrSuggestion { ContactId = contact.Id, LeadId = lead.Id, Action = "OfferDemo", Strategy = "offer_demo", PromptVersion = 2, Status = SuggestionStatus.Shadow, ValidatorOk = true });
        await db.SaveChangesAsync();
        db.ConversationStates.Add(new ConversationState { LeadId = lead.Id, ObjectionLog = Obj.Caro });
        await db.SaveChangesAsync();
        var r = await RobertaMetrics.ComputeAsync(db, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(1, r.Suggestions); Assert.Equal(1, r.Strategies["handle_objection"]); Assert.Equal(1, r.Objections[Obj.Caro]);
        Assert.False(r.Strategies.ContainsKey("offer_demo"));
        var withShadow = await RobertaMetrics.ComputeAsync(db, DateTime.UtcNow.AddDays(-1), includeShadow: true);
        Assert.Equal(2, withShadow.Suggestions);
    }

    // ----- Guardrails in the prompt and validator -----
    [Fact]
    public void Prompt_v3_states_personality_identity_and_no_invention_rules()
    {
        var p = PromptService.DefaultContent;
        Assert.Contains("SDR consultiva e vendedora", p); Assert.Contains("PERSONALIDADE", p); Assert.Contains("IDENTIDADE", p);
        Assert.Contains("nunca cite cases", p); Assert.Contains("HIPÓTESE", p);
        Assert.DoesNotContain("Nunca diga que é IA", p);
    }

    [Theory]
    [InlineData(RobertaAction.ExploreImpact)] [InlineData(RobertaAction.ShareInsight)] [InlineData(RobertaAction.TellUseCase)] [InlineData(RobertaAction.CreateCuriosity)]
    [InlineData(RobertaAction.DifferentiateCompetitor)] [InlineData(RobertaAction.ConfirmUnderstanding)] [InlineData(RobertaAction.AskForMeeting)] [InlineData(RobertaAction.PauseConversation)]
    public void Every_new_action_has_its_own_instruction(RobertaAction a) => Assert.DoesNotContain("Encerre com cordialidade", RobertaService.Instruction(a));

    [Fact]
    public void Use_case_instruction_forbids_real_clients_and_numbers() =>
        Assert.Matches(new Regex("NUNCA cite clientes reais"), RobertaService.Instruction(RobertaAction.TellUseCase));
}
