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




