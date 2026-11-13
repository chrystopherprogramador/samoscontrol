using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Application.Voice;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Integrations.Voice;

namespace SamosControl.Tests;

/// <summary>Ligações futuras da Roberta: agendamento persistente, execução no horário, idempotência, retry, fallback e isolamento.</summary>
public class ScheduledCallTests
{
    private sealed class Gw : ICallGateway
    {
        public bool IsConfigured => true;
        public int Dials; public bool FailDial;
        public Task<string> DialAsync(string phone, CancellationToken ct) { if (FailDial) throw new IntegrationException("falha simulada"); Dials++; return Task.FromResult("call-" + Dials); }
        public Task AnswerAsync(string c, string id, CancellationToken ct) => Task.CompletedTask;
        public Task RejectAsync(string c, string id, CancellationToken ct) => Task.CompletedTask;
        public Task HangupAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<ICallStream> OpenStreamAsync(string id, CancellationToken ct) => Task.FromResult<ICallStream>(new FakeStream());
    }

    private const string Phone = "11999990001";

    private static (TestHost H, Gw G, int UserId) Setup()
    {
        var gw = new Gw();
        var h = new TestHost(new() { ["VOICE_CALLS_ENABLED"] = "true" }, s => { s.AddSingleton<ICallGateway>(gw); s.AddSingleton<IVoiceSpeech>(new FakeSpeech()); });
        return (h, gw, 0);
    }

    private static async Task<int> AddUser(TestHost h, string phone, bool allow = true)
    {
        using var sc = h.Scope(); var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = new Role { Name = "R" + Guid.NewGuid().ToString("N")[..6] };
        role.Permissions.Add(new RolePermission { Permission = Perm.AiUse });
        var u = new User { Name = "Dono Teste", Email = $"{Guid.NewGuid():N}@samos.test", PasswordHash = "x", Role = role, Phone = PhoneUtil.Normalize(phone) };
        db.Users.Add(u);
        if (allow) db.VoiceAllowedNumbers.Add(new VoiceAllowedNumber { Phone = PhoneUtil.Normalize(phone)!, PhoneKey = PhoneUtil.Key(phone)!, Label = "Dono", Active = true, ConsentAt = DateTime.UtcNow, ConsentBy = "admin" });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static string InFuture(int minutes) => AppTime.ToLocal(DateTime.UtcNow.AddMinutes(minutes)).ToString("yyyy-MM-ddTHH:mm");

    private static async Task<string> Schedule(TestHost h, int userId, int minutes = 60, string msg = "tomar o remédio e ligar para o contador")
    {
        using var sc = h.Scope();
        var tool = sc.ServiceProvider.GetRequiredService<IEnumerable<IAgentTool>>().OfType<ScheduleCallTool>().Single();
        var args = new JsonObject { ["quando"] = InFuture(minutes), ["mensagem"] = msg };
        var ctx = new AgentContext(userId, "Dono Teste", "WHATSAPP", Phone);
        var plan = await tool.PlanAsync(ctx, args);
        if (plan.Error != null) return plan.Error;
        return await tool.ExecuteAsync(ctx, args, default);
    }

    private static async Task ForceDue(TestHost h, int jobId, int minutesAgo = 1)
    {
        using var sc = h.Scope(); var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var j = await db.AutomationJobs.FindAsync(jobId);
        j!.NextRunAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        await db.SaveChangesAsync();
    }

    private static async Task<int> RunDue(TestHost h) { using var sc = h.Scope(); return await sc.ServiceProvider.GetRequiredService<AutomationService>().RunDueAsync(); }
    private static async Task<AutomationJob> Job(TestHost h, int id) { using var sc = h.Scope(); return await sc.ServiceProvider.GetRequiredService<AppDbContext>().AutomationJobs.AsNoTracking().FirstAsync(j => j.Id == id); }
    private static async Task<bool> Wait(Func<bool> c) { for (var i = 0; i < 160 && !c(); i++) await Task.Delay(50); return c(); }

    [Fact]
    public async Task Schedule_PersistsExecutableJob_ForTheOwnersOwnNumber()
    {
        var (h, _, _) = Setup(); using var _h = h;
        var uid = await AddUser(h, Phone);
        var reply = await Schedule(h, uid);
        Assert.Contains("agendada", reply);
        var job = await Job(h, 1);
        Assert.Equal(JobType.PlaceCall, job.Type); Assert.True(job.IsEnabled); Assert.Equal(uid, job.CreatedByUserId);
        Assert.True(job.NextRunAt > DateTime.UtcNow.AddMinutes(55) && job.NextRunAt < DateTime.UtcNow.AddMinutes(65));
        Assert.Contains("tomar o rem", job.ConfigJson);
    }

    [Fact]
    public async Task Schedule_RefusesWhenNotReallyPossible_NeverFakesSuccess()
    {
        var (h, _, _) = Setup(); using var _h = h;
        var noAllow = await AddUser(h, "11988887777", allow: false);
        Assert.Contains("autorizados", await Schedule(h, noAllow));
        var uid = await AddUser(h, Phone);
        using (var sc = h.Scope())
        {
            var tool = sc.ServiceProvider.GetRequiredService<IEnumerable<IAgentTool>>().OfType<ScheduleCallTool>().Single();
            var ctx = new AgentContext(uid, "x", "WHATSAPP", Phone);
            Assert.Contains("passou", (await tool.PlanAsync(ctx, new JsonObject { ["quando"] = InFuture(-10), ["mensagem"] = "a" })).Error);
            Assert.NotNull((await tool.PlanAsync(ctx, new JsonObject { ["quando"] = InFuture(10) })).Error);
        }
        using var h2 = new TestHost(new() { ["VOICE_CALLS_ENABLED"] = "false" }, s => { s.AddSingleton<ICallGateway>(new Gw()); s.AddSingleton<IVoiceSpeech>(new FakeSpeech()); });
        var u2 = await AddUser(h2, Phone);
        Assert.Contains("desativadas", await Schedule(h2, u2));
        using var sc2 = h2.Scope();
        Assert.Empty(await sc2.ServiceProvider.GetRequiredService<AppDbContext>().AutomationJobs.ToListAsync());
    }

    [Fact]
    public async Task DueJob_DialsOnce_EvenIfSchedulerRunsAgain_AndSurvivesNewScopes()
    {
        var (h, gw, _) = Setup(); using var _h = h;
        var uid = await AddUser(h, Phone);
        await Schedule(h, uid);
        Assert.Equal(0, await RunDue(h));           // ainda não é a hora
        await ForceDue(h, 1);
        Assert.Equal(1, await RunDue(h));           // novo escopo = como após reinício do worker
        Assert.Equal(1, gw.Dials);
        Assert.Equal(0, await RunDue(h));           // a verificação só vence após o tempo de toque
        Assert.Equal(1, gw.Dials);
        var job = await Job(h, 1);
        Assert.Equal("CHECK", JsonNode.Parse(job.ConfigJson!)!["phase"]!.ToString());
        Assert.True(job.NextRunAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task AnsweredCall_CompletesJob()
    {
        var (h, gw, _) = Setup(); using var _h = h;
        var uid = await AddUser(h, Phone);
        await Schedule(h, uid); await ForceDue(h, 1); await RunDue(h);
        var mgr = h.Root.GetRequiredService<VoiceCallManager>();
        await mgr.HangupAsync(1);
        Assert.True(await Wait(() => !mgr.IsActive(1)));
        using (var sc = h.Scope())
        {
            var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
            var c = await db.VoiceCalls.FirstAsync(); c.ConnectedAt = DateTime.UtcNow.AddSeconds(-20); c.State = VoiceCallState.Completed; await db.SaveChangesAsync();
        }
        await ForceDue(h, 1); await RunDue(h);
        var job = await Job(h, 1);
        Assert.False(job.IsEnabled); Assert.Null(job.NextRunAt); Assert.Equal(ExecutionStatus.Success, job.LastStatus);
        Assert.Equal(1, gw.Dials);
    }

    [Fact]
    public async Task UnansweredCall_Retries_ThenFallsBackAndStops()
    {
        var (h, gw, _) = Setup(); using var _h = h;
        var uid = await AddUser(h, Phone);
        await Schedule(h, uid); await ForceDue(h, 1); await RunDue(h);
        var mgr = h.Root.GetRequiredService<VoiceCallManager>();
        await mgr.HangupAsync(1);
        Assert.True(await Wait(() => !mgr.IsActive(1)));
        await ForceDue(h, 1); await RunDue(h);      // CHECK: não atendida → nova tentativa agendada
        var job = await Job(h, 1);
        Assert.Equal("DIAL", JsonNode.Parse(job.ConfigJson!)!["phase"]!.ToString());
        Assert.Equal(2, (int)JsonNode.Parse(job.ConfigJson!)!["attempt"]!);
        Assert.True(job.IsEnabled && job.NextRunAt > DateTime.UtcNow.AddMinutes(3));

        gw.FailDial = true;                          // demais tentativas falham no provedor
        await ForceDue(h, 1); await RunDue(h);
        await ForceDue(h, 1); await RunDue(h);
        job = await Job(h, 1);
        Assert.False(job.IsEnabled); Assert.Null(job.NextRunAt); Assert.Equal(ExecutionStatus.Failed, job.LastStatus);
        await ForceDue(h, 1); Assert.Equal(0, await RunDue(h)); // não tenta de novo
        using var sc = h.Scope();
        Assert.Contains(await sc.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.ToListAsync(), n => n.Title.Contains("não concluída"));
    }

    [Fact]
    public async Task MissedWindow_DoesNotCallLate()
    {
        var (h, gw, _) = Setup(); using var _h = h;
        var uid = await AddUser(h, Phone);
        await Schedule(h, uid);
        await ForceDue(h, 1);
        using (var sc = h.Scope())
        {
            var db = sc.ServiceProvider.GetRequiredService<AppDbContext>(); var j = await db.AutomationJobs.FirstAsync();
            var cfg = JsonNode.Parse(j.ConfigJson!)!.AsObject(); cfg["scheduledFor"] = DateTime.UtcNow.AddHours(-3).ToString("O"); j.ConfigJson = cfg.ToJsonString();
            await db.SaveChangesAsync();
        }
        await RunDue(h);
        Assert.Equal(0, gw.Dials);
        var job = await Job(h, 1);
        Assert.False(job.IsEnabled); Assert.Equal(ExecutionStatus.Failed, job.LastStatus);
    }

    [Fact]
    public async Task Cancel_StopsExecution_AndOtherUsersCannotCancelOrSee()
    {
        var (h, gw, _) = Setup(); using var _h = h;
        var uid = await AddUser(h, Phone); var other = await AddUser(h, "11977776666");
        await Schedule(h, uid);
        using (var sc = h.Scope())
        {
            var cancel = sc.ServiceProvider.GetRequiredService<IEnumerable<IAgentTool>>().OfType<CancelScheduledCallTool>().Single();
            var list = sc.ServiceProvider.GetRequiredService<IEnumerable<IAgentTool>>().OfType<ListScheduledCallsTool>().Single();
            var args = new JsonObject { ["id"] = 1 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => cancel.ExecuteAsync(new AgentContext(other, "o", "WHATSAPP", null), args, default));
            Assert.Contains("não tem", await list.ExecuteAsync(new AgentContext(other, "o", "WHATSAPP", null), new JsonObject(), default));
            Assert.Contains("agendada", await list.ExecuteAsync(new AgentContext(uid, "d", "WHATSAPP", null), new JsonObject(), default));
            Assert.Contains("cancelada", await cancel.ExecuteAsync(new AgentContext(uid, "d", "WHATSAPP", null), args, default));
        }
        await ForceDue(h, 1); await RunDue(h);
        Assert.Equal(0, gw.Dials);
    }

    [Fact]
    public async Task Opener_MentionsTheReminderInSpeakableForm()
    {
        using var h = new TestHost();
        var brain = new RobertaVoiceBrain(h.Root.GetRequiredService<IServiceScopeFactory>(), 1, "Chrys Castro", Phone, false, false,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "pagar R$ 150,00 às 11:55");
        var t = await brain.OpenerAsync(default);
        Assert.Contains("lembrar", t); Assert.Contains("cento e cinquenta reais", t); Assert.DoesNotContain("R$", t);
    }
}
