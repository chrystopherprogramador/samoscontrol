using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class AuthAndPermissionTests
{
    private static async Task<User> SeedUserAsync(AppDbContext db, string password, bool active = true, params string[] perms)
    {
        var role = new Role { Name = "R" + Guid.NewGuid().ToString("N")[..6] };
        foreach (var p in perms) role.Permissions.Add(new RolePermission { Permission = p });
        db.Roles.Add(role);
        var u = new User { Name = "Teste", Email = $"{Guid.NewGuid():N}@samos.test", PasswordHash = AuthService.Hash(password), IsActive = active, Role = role };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public void PasswordPolicy_RejectsWeak_AcceptsStrong()
    {
        Assert.NotNull(AuthService.ValidatePassword("curta1"));
        Assert.NotNull(AuthService.ValidatePassword("somenteletrasaqui"));
        Assert.Null(AuthService.ValidatePassword("SenhaForte123"));
    }

    [Fact]
    public async Task Login_Succeeds_WithCorrectPassword_AndFailsOtherwise()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = sc.ServiceProvider.GetRequiredService<AuthService>();
        var u = await SeedUserAsync(db, "SenhaForte123");
        Assert.True((await auth.LoginAsync(u.Email, "SenhaForte123")).Success);
        Assert.NotNull((await db.Users.FindAsync(u.Id))!.LastLoginAt);
        Assert.False((await auth.LoginAsync(u.Email, "errada")).Success);
        Assert.False((await auth.LoginAsync("naoexiste@samos.test", "SenhaForte123")).Success);
    }

    [Fact]
    public async Task Login_LocksAccount_AfterRepeatedFailures()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = sc.ServiceProvider.GetRequiredService<AuthService>();
        var u = await SeedUserAsync(db, "SenhaForte123");
        for (var i = 0; i < AuthService.MaxFailures; i++) await auth.LoginAsync(u.Email, "errada");
        var r = await auth.LoginAsync(u.Email, "SenhaForte123");
        Assert.False(r.Success); // correct password is refused while locked
    }

    [Fact]
    public async Task Login_Refuses_InactiveUser()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await SeedUserAsync(db, "SenhaForte123", active: false);
        Assert.False((await sc.ServiceProvider.GetRequiredService<AuthService>().LoginAsync(u.Email, "SenhaForte123")).Success);
    }

    [Fact]
    public async Task PasswordReset_Token_IsSingleUse()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = sc.ServiceProvider.GetRequiredService<AuthService>();
        var u = await SeedUserAsync(db, "SenhaForte123");
        var token = await auth.CreatePasswordResetAsync(u.Id);
        Assert.Null(await auth.ResetPasswordAsync(token!, "NovaSenha12345"));
        Assert.NotNull(await auth.ResetPasswordAsync(token!, "OutraSenha12345"));
        Assert.True((await auth.LoginAsync(u.Email, "NovaSenha12345")).Success);
    }

    [Fact]
    public async Task Permissions_AreGranularPerUser_NotByRoleName()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var perms = sc.ServiceProvider.GetRequiredService<PermissionService>();
        var viewer = await SeedUserAsync(db, "SenhaForte123", true, Perm.CrmView);
        var inactive = await SeedUserAsync(db, "SenhaForte123", false, Perm.CrmView);
        Assert.True(await perms.HasAsync(viewer.Id, Perm.CrmView));
        Assert.False(await perms.HasAsync(viewer.Id, Perm.CrmEdit));
        Assert.False(await perms.HasAsync(inactive.Id, Perm.CrmView));
        Assert.False(await perms.HasAsync(99999, Perm.CrmView));
    }

    [Fact]
    public void DefaultRoles_OnlyUseKnownPermissions()
    {
        foreach (var kv in Perm.DefaultRoles)
            Assert.All(kv.Value, p => Assert.Contains(p, Perm.All));
    }
}

public class LeadDeduplicationTests
{
    private static ScrapedPlace Place(string name, string? phone = null, string? addr = null, string? site = null, string? pid = null) =>
        new(name, phone, addr, site, pid, "Goiânia", "GO", "Loja", null);

    [Fact]
    public async Task Import_DedupsByPhone_AcrossFormats()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var svc = sc.ServiceProvider.GetRequiredService<LeadService>();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var r1 = await svc.ImportAsync(new[] { Place("Loja A", "(62) 99999-1111") }, "lojas", "Goiânia");
        var r2 = await svc.ImportAsync(new[] { Place("Loja A Matriz", "+55 62 99999 1111") }, "lojas", "Goiânia");
        Assert.Equal(1, r1.Created);
        Assert.Equal(0, r2.Created);
        Assert.Equal(1, await db.Leads.CountAsync());
    }

    [Fact]
    public async Task Import_DedupsByPlaceId_AndWebsite_AndNameAddress()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var svc = sc.ServiceProvider.GetRequiredService<LeadService>();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        await svc.ImportAsync(new[] { Place("Alpha", pid: "PID1"), Place("Beta", site: "https://www.beta.com.br/"), Place("Gama", addr: "Rua 1, 10") }, null, null);
        var r = await svc.ImportAsync(new[] { Place("Outro nome", pid: "PID1"), Place("Beta 2", site: "http://beta.com.br"), Place("gama", addr: "rua 1, 10") }, null, null);
        Assert.Equal(0, r.Created);
        Assert.Equal(3, await db.Leads.CountAsync());
    }

    [Fact]
    public async Task Import_CountsInvalidItemsAsErrors_AndCreatesDistinctLeads()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var svc = sc.ServiceProvider.GetRequiredService<LeadService>();
        var r = await svc.ImportAsync(new[] { Place("", "6211112222"), Place("Um", "6233334444"), Place("Dois", "6255556666") }, null, null);
        Assert.Equal(3, r.Found);
        Assert.Equal(2, r.Created);
        Assert.Equal(1, r.Errors);
    }
}

public class ContractTests
{
    private static async Task<(Contract c, string token)> NewSentContractAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var svc = sp.GetRequiredService<ContractService>();
        var tpl = new ContractTemplate { Name = "Padrão", Body = "# Contrato\nCliente {{CLIENTE_NOME}} ({{cliente_cpf_cnpj}}) contrata {{SERVICO}} por {{VALOR}}. {{DESCONHECIDO}}" };
        var cust = new Customer { Name = "ACME Ltda", Document = "12.345.678/0001-90", Email = "acme@x.com" };
        db.ContractTemplates.Add(tpl); db.Customers.Add(cust);
        await db.SaveChangesAsync();
        var c = await svc.CreateAsync(tpl.Id, cust.Id, null, "Hospedagem", 2000m, DateTime.UtcNow.AddDays(10));
        await svc.SendForSignatureAsync(c.Id);
        var token = (await db.ContractParties.FirstAsync(p => p.ContractId == c.Id)).Token;
        return (c, token);
    }

    [Fact]
    public void Render_ReplacesKnownPlaceholders_AndKeepsUnknown()
    {
        var text = ContractService.Render("Olá {{ CLIENTE_NOME }} {{X}}", new Dictionary<string, string> { ["CLIENTE_NOME"] = "Ana" });
        Assert.Equal("Olá Ana {{X}}", text);
    }

    [Fact]
    public async Task Create_RendersTemplate_AndSendComputesHash()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var (c, _) = await NewSentContractAsync(sc.ServiceProvider);
        Assert.Contains("ACME Ltda", c.RenderedText);
        Assert.Contains("12.345.678/0001-90", c.RenderedText);
        Assert.Contains("Hospedagem", c.RenderedText);
        Assert.Contains("{{DESCONHECIDO}}", c.RenderedText);
        Assert.Equal(ContractService.ComputeHash(c), c.DocumentHash);
        Assert.Equal(ContractStatus.AguardandoAssinatura, c.Status);
    }

    [Fact]
    public async Task Sign_RequiresAcceptance_AndIdentification()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var (_, token) = await NewSentContractAsync(sc.ServiceProvider);
        var svc = sc.ServiceProvider.GetRequiredService<ContractService>();
        Assert.False((await svc.SignAsync(token, new SignRequest("Fulano de Tal", "529.982.247-25", null, null, false), "1.1.1.1", "ua")).Success);
        Assert.False((await svc.SignAsync(token, new SignRequest("Fulano de Tal", null, null, null, true), "1.1.1.1", "ua")).Success);
        Assert.False((await svc.SignAsync("token-invalido", new SignRequest("Fulano de Tal", "529.982.247-25", null, null, true), "1.1.1.1", "ua")).Success);
    }

    [Fact]
    public async Task Sign_RecordsEvidence_GeneratesFinalPdf_AndBlocksResign()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var (c, token) = await NewSentContractAsync(sc.ServiceProvider);
        var svc = sc.ServiceProvider.GetRequiredService<ContractService>();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        await svc.OpenForSigningAsync(token, "9.9.9.9", "agent");

        var res = await svc.SignAsync(token, new SignRequest("Fulano de Tal", "529.982.247-25", "f@x.com", "62999990000", true), "9.9.9.9", "agent");
        Assert.True(res.Success);
        Assert.True(res.ContractCompleted);

        var saved = await db.Contracts.FirstAsync(x => x.Id == c.Id);
        Assert.Equal(ContractStatus.Assinado, saved.Status);
        Assert.False(string.IsNullOrEmpty(saved.FinalPdfHash));
        var sig = await db.ContractSignatures.SingleAsync();
        Assert.Equal(saved.DocumentHash, sig.DocumentHash);
        Assert.Equal("9.9.9.9", sig.Ip);
        Assert.Equal("agent", sig.UserAgent);
        Assert.True(sig.AcceptedTerms);
        Assert.Equal(64, sig.SignatureHash.Length);
        var events = await db.ContractEvents.Where(e => e.ContractId == c.Id).Select(e => e.Type).ToListAsync();
        Assert.Contains("LINK_ACESSADO", events);
        Assert.Contains("ASSINADO", events);
        Assert.Contains("DOCUMENTO_FINAL_GERADO", events);

        var pdf = await svc.GetPdfAsync(c.Id);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal(saved.FinalPdfHash, ContractService.Sha256(pdf));
        Assert.True((await svc.VerifyIntegrityAsync(c.Id)).PdfOk);

        Assert.False((await svc.SignAsync(token, new SignRequest("Fulano de Tal", "529.982.247-25", null, null, true), "9.9.9.9", "agent")).Success);
    }

    [Fact]
    public async Task Sign_FailsWhenDocumentWasTampered()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var (c, token) = await NewSentContractAsync(sc.ServiceProvider);
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Contracts.FindAsync(c.Id))!.RenderedText += " alterado";
        await db.SaveChangesAsync();
        var r = await sc.ServiceProvider.GetRequiredService<ContractService>().SignAsync(token, new SignRequest("Fulano de Tal", "529.982.247-25", null, null, true), "1.1.1.1", "ua");
        Assert.False(r.Success);
    }

    [Fact]
    public async Task SignedContract_CannotBeCancelled()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var (c, token) = await NewSentContractAsync(sc.ServiceProvider);
        var svc = sc.ServiceProvider.GetRequiredService<ContractService>();
        await svc.SignAsync(token, new SignRequest("Fulano de Tal", "529.982.247-25", null, null, true), "1.1.1.1", "ua");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CancelAsync(c.Id));
    }
}

public class WebhookTests
{
    private static async Task<Charge> SeedChargeAsync(AppDbContext db, string asaasId)
    {
        var cust = new Customer { Name = "Cliente" };
        var ch = new Charge { Customer = cust, Amount = 100, DueDate = DateTime.UtcNow.AddDays(3), AsaasId = asaasId };
        db.Charges.Add(ch);
        await db.SaveChangesAsync();
        return ch;
    }

    private static string AsaasBody(string eventId, string ev, string paymentId) =>
        new JsonObject { ["id"] = eventId, ["event"] = ev, ["payment"] = new JsonObject { ["id"] = paymentId, ["value"] = 100, ["paymentDate"] = "2030-01-05" } }.ToJsonString();

    [Fact]
    public async Task Asaas_RejectsBadToken_AndUnconfiguredWebhook()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var billing = sc.ServiceProvider.GetRequiredService<BillingService>();
        Assert.Equal(401, (await billing.HandleWebhookAsync(AsaasBody("e1", "PAYMENT_RECEIVED", "pay_1"), "errado")).StatusCode);
        Assert.Equal(401, (await billing.HandleWebhookAsync(AsaasBody("e1", "PAYMENT_RECEIVED", "pay_1"), null)).StatusCode);

        using var h2 = new TestHost(new() { ["ASAAS_WEBHOOK_TOKEN"] = null }); using var sc2 = h2.Scope();
        Assert.Equal(503, (await sc2.ServiceProvider.GetRequiredService<BillingService>().HandleWebhookAsync("{}", "x")).StatusCode);
    }

    [Fact]
    public async Task Asaas_ProcessesPayment_AndIgnoresDuplicateDelivery()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = sc.ServiceProvider.GetRequiredService<BillingService>();
        var ch = await SeedChargeAsync(db, "pay_1");

        var body = AsaasBody("evt_1", "PAYMENT_RECEIVED", "pay_1");
        Assert.Equal(200, (await billing.HandleWebhookAsync(body, "asaas-secret")).StatusCode);
        Assert.Equal(ChargeStatus.Pago, (await db.Charges.FindAsync(ch.Id))!.Status);
        var notifications = await db.Notifications.CountAsync();

        var dup = await billing.HandleWebhookAsync(body, "asaas-secret");
        Assert.Equal(200, dup.StatusCode);
        Assert.Contains("duplicado", dup.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.WebhookEvents.CountAsync(w => w.Provider == "asaas"));
        Assert.Equal(notifications, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Asaas_LateOverdueEvent_DoesNotReopenPaidCharge()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = sc.ServiceProvider.GetRequiredService<BillingService>();
        var ch = await SeedChargeAsync(db, "pay_2");
        await billing.HandleWebhookAsync(AsaasBody("evt_a", "PAYMENT_RECEIVED", "pay_2"), "asaas-secret");
        await billing.HandleWebhookAsync(AsaasBody("evt_b", "PAYMENT_OVERDUE", "pay_2"), "asaas-secret");
        Assert.Equal(ChargeStatus.Pago, (await db.Charges.FindAsync(ch.Id))!.Status);
    }

    [Fact]
    public async Task Asaas_InvalidJson_Returns400()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        Assert.Equal(400, (await sc.ServiceProvider.GetRequiredService<BillingService>().HandleWebhookAsync("{nao-json", "asaas-secret")).StatusCode);
    }

    private static string EvoBody(string id, string text) => new JsonObject
    {
        ["event"] = "messages.upsert",
        ["data"] = new JsonObject
        {
            ["key"] = new JsonObject { ["id"] = id, ["remoteJid"] = "5511988887777@s.whatsapp.net", ["fromMe"] = false },
            ["pushName"] = "Maria",
            ["message"] = new JsonObject { ["conversation"] = text },
            ["messageTimestamp"] = 1893456000
        }
    }.ToJsonString();

    [Fact]
    public async Task Evolution_RejectsBadToken()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        Assert.Equal(401, (await wa.HandleWebhookAsync(EvoBody("m1", "oi"), "errado")).StatusCode);
    }

    [Fact]
    public async Task Evolution_StoresMessageOnce_AndLinksConversation()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        db.Leads.Add(new Lead { Name = "Maria Lead", WhatsApp = "(11) 98888-7777", PhoneKey = PhoneUtil.Key("11988887777") });
        await db.SaveChangesAsync();

        var body = EvoBody("MSG1", "Olá");
        Assert.Equal(200, (await wa.HandleWebhookAsync(body, "evo-secret")).StatusCode);
        Assert.Equal(200, (await wa.HandleWebhookAsync(body, "evo-secret")).StatusCode);

        Assert.Equal(1, await db.WhatsAppMessages.CountAsync());
        var conv = await db.WhatsAppConversations.SingleAsync();
        Assert.Equal("5511988887777", conv.Phone);
        Assert.Equal(1, conv.UnreadCount);
        Assert.Equal(1, await db.WebhookEvents.CountAsync(w => w.Provider == "evolution"));
    }

    [Fact]
    public async Task Evolution_IgnoresGroupMessages()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var body = EvoBody("G1", "grupo").Replace("5511988887777@s.whatsapp.net", "1203630@g.us");
        await sc.ServiceProvider.GetRequiredService<WhatsAppService>().HandleWebhookAsync(body, "evo-secret");
        Assert.Equal(0, await db.WhatsAppMessages.CountAsync());
    }
}

public class AutomationTests
{
    [Fact]
    public async Task ManualRun_RecordsExecutionAndLogs_AndUpdatesJob()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var auto = sc.ServiceProvider.GetRequiredService<AutomationService>();
        var job = await auto.CreateAsync("Lembrete", JobType.Reminder, "{\"message\":\"ligar para X\"}", null, null, null);
        var ex = await auto.RunJobAsync(job.Id, "MANUAL");
        Assert.Equal(ExecutionStatus.Success, ex.Status);
        Assert.True(await db.AutomationLogs.AnyAsync(l => l.ExecutionId == ex.Id));
        Assert.Equal(ExecutionStatus.Success, (await db.AutomationJobs.FindAsync(job.Id))!.LastStatus);
        Assert.True(await db.Notifications.AnyAsync(n => n.Title.Contains("ligar para X")));
    }

    [Fact]
    public async Task FailingJob_IsRecordedAsFailed_WithoutThrowing()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var auto = sc.ServiceProvider.GetRequiredService<AutomationService>();
        var job = await auto.CreateAsync("Zap", JobType.SendWhatsApp, "{}", null, null, null);
        var ex = await auto.RunJobAsync(job.Id, "MANUAL");
        Assert.Equal(ExecutionStatus.Failed, ex.Status);
    }

    [Fact]
    public async Task RunDue_ExecutesOnlyDueEnabledJobs_AndReschedulesRecurring()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var auto = sc.ServiceProvider.GetRequiredService<AutomationService>();
        var due = await auto.CreateAsync("Due", JobType.Reminder, "{\"message\":\"a\"}", DateTime.UtcNow.AddMinutes(-1), 60, null);
        var future = await auto.CreateAsync("Future", JobType.Reminder, "{\"message\":\"b\"}", DateTime.UtcNow.AddHours(5), null, null);
        var disabled = await auto.CreateAsync("Off", JobType.Reminder, "{\"message\":\"c\"}", DateTime.UtcNow.AddMinutes(-1), null, null);
        disabled.IsEnabled = false; await db.SaveChangesAsync();

        Assert.Equal(1, await auto.RunDueAsync());
        Assert.Equal(1, await db.AutomationExecutions.CountAsync(e => e.JobId == due.Id));
        Assert.Equal(0, await db.AutomationExecutions.CountAsync(e => e.JobId == future.Id));
        Assert.Equal(0, await db.AutomationExecutions.CountAsync(e => e.JobId == disabled.Id));
        Assert.True((await db.AutomationJobs.FindAsync(due.Id))!.NextRunAt > DateTime.UtcNow.AddMinutes(30));
        Assert.Equal(0, await auto.RunDueAsync()); // no double execution
    }

    [Fact]
    public async Task RecurringJob_WithoutFirstRun_GetsNextRunAfterInterval()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var auto = sc.ServiceProvider.GetRequiredService<AutomationService>();
        var job = await auto.CreateAsync("Rec", JobType.Reminder, "{\"message\":\"a\"}", null, 60, null);
        Assert.InRange(job.NextRunAt!.Value, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task OneShotJob_DoesNotRunAgain()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var auto = sc.ServiceProvider.GetRequiredService<AutomationService>();
        await auto.CreateAsync("Once", JobType.Reminder, "{\"message\":\"a\"}", DateTime.UtcNow.AddMinutes(-1), null, null);
        Assert.Equal(1, await auto.RunDueAsync());
        Assert.Equal(0, await auto.RunDueAsync());
    }
}
