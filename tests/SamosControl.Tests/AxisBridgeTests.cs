using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class AxisBridgeTests
{
    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    [Fact]
    public async Task CallMeTool_RefusesWhenVoiceDisabled_AndUnauthorizedNumbers()
    {
        using var host = new TestHost();
        using var scope = host.Scope();
        var tool = scope.ServiceProvider.GetServices<IAgentTool>().OfType<CallMeTool>().Single();
        var ctx = new AgentContext(1, "t", "whatsapp", "5562999990000");
        Assert.Contains("desativadas", (await tool.PlanAsync(ctx, new())).Error);
    }

    [Fact]
    public async Task LicenseTools_ListResolveAndCharge_Idempotently()
    {
        var venc = DateTime.UtcNow.Date.AddDays(-2).ToString("yyyy-MM-dd");
        var json = $$"""
        [{"subdominio":"loja","nome":"Loja Azul","documento":"12345678000190","valorMensal":150,"validade":"{{venc}}","ativo":true},
         {"subdominio":"mercado","nome":"Mercado","documento":"","valorMensal":0,"validade":null,"ativo":false}]
        """;
        using var host = new TestHost(new() { ["AXIS_EFI_TOKEN"] = "tok", ["AXIS_EFI_CONFIG_URL"] = "https://axis.test/x" },
            s => { s.AddSingleton<IAsaasIntegration>(new FakeAsaas()); s.AddSingleton<IHttpClientFactory>(new Factory(new Handler(json))); });
        using var scope = host.Scope();
        var bridge = scope.ServiceProvider.GetRequiredService<AxisBridgeService>();
        var ctx = new AgentContext(1, "t", "web", null);

        var list = await new QueryLicensesTool(bridge).ExecuteAsync(ctx, new() { ["situacao"] = "vencidas" }, default);
        Assert.Contains("Loja Azul", list);
        Assert.DoesNotContain("Mercado", list);

        var plan = await new RenewLicenseTool(bridge).PlanAsync(ctx, new() { ["painel"] = "azul", ["meses"] = 2 });
        Assert.Null(plan.Error);
        Assert.Equal("loja", plan.Args["painel"]!.ToString());
        Assert.NotNull((await new SetLicenseStatusTool(bridge).PlanAsync(ctx, new() { ["painel"] = "mercado", ["acao"] = "suspender" })).Error);

        var charge = new ChargeLicenseTool(bridge);
        Assert.Contains("criada", await charge.ExecuteAsync(ctx, new() { ["painel"] = "loja" }, default));
        Assert.Contains("Já existe", await charge.ExecuteAsync(ctx, new() { ["painel"] = "loja" }, default));
    }

    [Fact]
    public async Task LicenseCharges_GeneratedWithoutContract_OncePerExpiry_AndSkipsContractedCustomers()
    {
        var venc = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd");
        var json = $$"""
        [{"subdominio":"loja","nome":"Loja","documento":"12.345.678/0001-90","valorMensal":199.9,"validade":"{{venc}}"},
         {"subdominio":"longe","nome":"Longe","documento":"11222333000181","valorMensal":99,"validade":"{{DateTime.UtcNow.Date.AddDays(60):yyyy-MM-dd}}"},
         {"subdominio":"comcontrato","nome":"CC","documento":"529.982.247-25","valorMensal":50,"validade":"{{venc}}"}]
        """;
        var asaas = new FakeAsaas();
        using var host = new TestHost(new()
        {
            ["AXIS_EFI_TOKEN"] = "tok", ["AXIS_EFI_CONFIG_URL"] = "https://axis.test/api/integracao/samos/efi-config", ["AXIS_COBRANCA_LICENCAS"] = "true"
        }, s => { s.AddSingleton<IAsaasIntegration>(asaas); s.AddSingleton<IHttpClientFactory>(new Factory(new Handler(json))); });
        using var scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cc = new Customer { Name = "CC", Document = "52998224725" };
        db.Customers.Add(cc);
        await db.SaveChangesAsync();
        db.Contracts.Add(new Contract { Title = "c", CustomerId = cc.Id, Status = ContractStatus.Assinado, MonthlyValue = 50, MonthlyStart = DateTime.UtcNow.Date });
        await db.SaveChangesAsync();

        var bridge = scope.ServiceProvider.GetRequiredService<AxisBridgeService>();
        Assert.Equal(1, await bridge.GenerateLicenseChargesAsync());
        Assert.Equal(0, await bridge.GenerateLicenseChargesAsync());

        var ch = Assert.Single(db.Charges.Where(c => c.ExternalRef != null).ToList());
        Assert.Equal("MENSALIDADE", ch.Kind);
        Assert.Equal(199.9m, ch.Amount);
        Assert.Equal($"axis:loja:{venc.Replace("-", "")}", ch.ExternalRef);
    }

    [Fact]
    public async Task Situacao_CountsOverdueAndPastDuePending_MatchingDocumentByDigits()
    {
        using var host = new TestHost(new() { ["AXIS_EFI_TOKEN"] = "tok" });
        using var scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = new Customer { Name = "ACME", Document = "12.345.678/0001-90" };
        var outro = new Customer { Name = "Outro", Document = "99.999.999/0001-99" };
        db.Customers.AddRange(c, outro);
        await db.SaveChangesAsync();
        var past = DateTime.SpecifyKind(AppTime.Now.Date.AddDays(-3), DateTimeKind.Utc);
        var future = DateTime.SpecifyKind(AppTime.Now.Date.AddDays(5), DateTimeKind.Utc);
        db.Charges.AddRange(
            new Charge { CustomerId = c.Id, Amount = 100, DueDate = past, Status = ChargeStatus.Vencido, Kind = "IMPLANTACAO" },
            new Charge { CustomerId = c.Id, Amount = 50, DueDate = past, Status = ChargeStatus.Pendente, Kind = "MENSALIDADE" },
            new Charge { CustomerId = c.Id, Amount = 70, DueDate = future, Status = ChargeStatus.Pendente, Kind = "IMPLANTACAO" },
            new Charge { CustomerId = c.Id, Amount = 30, DueDate = past, Status = ChargeStatus.Pago, Kind = "MENSALIDADE" },
            new Charge { CustomerId = outro.Id, Amount = 999, DueDate = past, Status = ChargeStatus.Vencido });
        await db.SaveChangesAsync();

        var s = await scope.ServiceProvider.GetRequiredService<AxisBridgeService>().SituacaoAsync("12345678000190");

        Assert.Equal(2, s.Vencidas);
        Assert.Equal(150m, s.ValorVencido);
    }
}
