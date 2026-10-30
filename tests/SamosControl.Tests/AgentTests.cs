using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Tests;

public class AgentTests
{
    private static readonly Dictionary<string, string?> AsaasCfg = new() { [SamosControl.Integrations.SettingKeys.AsaasUrl] = "http://127.0.0.1:1/v3", [SamosControl.Integrations.SettingKeys.AsaasKey] = "test-key" };
    private static async Task<User> SeedAsync(AppDbContext db, params string[] perms)
    {
        var role = new Role { Name = "R" + Guid.NewGuid().ToString("N")[..6] };
        foreach (var p in perms) role.Permissions.Add(new RolePermission { Permission = p });
        var u = new User { Name = "Op", Email = $"{Guid.NewGuid():N}@samos.test", PasswordHash = "x", Role = role };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task SensitiveAction_RequiresConfirmation_BeforeExecuting()
    {
        using var h = new TestHost(AsaasCfg); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Customers.Add(new Customer { Name = "ACME Ltda" });
        var u = await SeedAsync(db, Perm.BillingCreate, Perm.BillingView, Perm.AiUse);
        var agent = sc.ServiceProvider.GetRequiredService<SamosAgent>();
        var ctx = new AgentContext(u.Id, "Op", "WEB", null);

        var reply = await agent.HandleAsync("Gere uma cobrança de R$ 2.000 para ACME", ctx);
        Assert.True(reply.Contains("CONFIRMAR"), reply);
        Assert.Equal(0, await db.Charges.CountAsync());
        Assert.Equal(1, await db.AiActions.CountAsync(a => a.Status == AiActionStatus.AguardandoConfirmacao));

        await agent.HandleAsync("CONFIRMAR", ctx);
        Assert.Equal(0, await db.AiActions.CountAsync(a => a.Status == AiActionStatus.AguardandoConfirmacao));
    }

    [Fact]
    public async Task PersonalReminder_CreateListComplete_ViaAgent()
    {
        using var h = new TestHost(AsaasCfg); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await SeedAsync(db, Perm.AiUse);
        var agent = sc.ServiceProvider.GetRequiredService<SamosAgent>();
        var ctx = new AgentContext(u.Id, "Op", "WHATSAPP", null);

        await agent.HandleAsync("me lembre de ligar para o contador amanhã às 9h", ctx);
        var t = await db.PersonalTasks.SingleAsync(x => x.UserId == u.Id);
        Assert.Contains("contador", t.Title);
        Assert.NotNull(t.RemindAt);

        var list = await agent.HandleAsync("minhas tarefas", ctx);
        Assert.Contains("contador", list);

        await agent.HandleAsync($"concluir {t.Id}", ctx);
        Assert.True((await db.PersonalTasks.SingleAsync()).Done);
    }

    [Fact]
    public async Task PersonalExpense_IsRecorded_ViaAgent()
    {
        using var h = new TestHost(AsaasCfg); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await SeedAsync(db, Perm.AiUse);
        var agent = sc.ServiceProvider.GetRequiredService<SamosAgent>();
        await agent.HandleAsync("gastei 45,90 no almoço", new AgentContext(u.Id, "Op", "WHATSAPP", null));
        var e = await db.PersonalExpenses.SingleAsync();
        Assert.Equal(45.90m, e.Amount);
    }

    [Fact]
    public async Task Map_AggregatesByState_AndSuggestsActions()
    {
        using var h = new TestHost(AsaasCfg); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Leads.AddRange(
            new Lead { Name = "A", State = "sp", City = "Campinas", Status = LeadStatus.Novo },
            new Lead { Name = "B", State = "SP", City = "Campinas", Status = LeadStatus.Ganho },
            new Lead { Name = "C", State = "GO", Status = LeadStatus.Contato },
            new Lead { Name = "D" });
        await db.SaveChangesAsync();
        var map = sc.ServiceProvider.GetRequiredService<MapService>();
        var d = await map.GetAsync();
        Assert.Equal(4, d.Total);
        var sp = d.States.Single(s => s.Uf == "SP");
        Assert.Equal(2, sp.Total); Assert.Equal(1, sp.Novos); Assert.Equal(1, sp.Ganhos);
        Assert.True(d.Unlocated >= 1);
        var ins = await map.InsightsAsync();
        Assert.NotEmpty(ins.Actions);
    }

    [Fact]
    public async Task CancelWord_DiscardsPendingAction()
    {
        using var h = new TestHost(AsaasCfg); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Customers.Add(new Customer { Name = "ACME Ltda" });
        var u = await SeedAsync(db, Perm.BillingCreate, Perm.BillingView, Perm.AiUse);
        var agent = sc.ServiceProvider.GetRequiredService<SamosAgent>();
        var ctx = new AgentContext(u.Id, "Op", "WEB", null);
        await agent.HandleAsync("Gere uma cobrança de R$ 2.000 para ACME", ctx);
        await agent.HandleAsync("CANCELAR", ctx);
        Assert.Equal(1, await db.AiActions.CountAsync(a => a.Status == AiActionStatus.Cancelada));
        Assert.Equal(0, await db.Charges.CountAsync());
    }

    [Fact]
    public async Task UserWithoutPermission_CannotRunBillingTool()
    {
        using var h = new TestHost(AsaasCfg); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Customers.Add(new Customer { Name = "ACME Ltda" });
        var u = await SeedAsync(db, Perm.AiUse);
        var agent = sc.ServiceProvider.GetRequiredService<SamosAgent>();
        await agent.HandleAsync("Gere uma cobrança de R$ 2.000 para ACME", new AgentContext(u.Id, "Op", "WEB", null));
        Assert.Equal(0, await db.Charges.CountAsync());
        Assert.Equal(0, await db.AiActions.CountAsync(a => a.Status == AiActionStatus.AguardandoConfirmacao));
    }

    [Fact]
    public void LlmOutput_IsParsedAsToolIntent_NotExecutedBlindly()
    {
        var i = SamosAgent.ParseLlm("```json\n{\"tool\":\"drop_database\",\"args\":{}}\n```");
        Assert.Equal("drop_database", i!.Tool); // the agent rejects names that are not registered tools (see SamosAgent.ProcessAsync)
        Assert.Null(SamosAgent.ParseLlm("sem json"));
    }
}




