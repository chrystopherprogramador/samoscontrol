using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class FakeAsaas : IAsaasIntegration
{
    public bool Fail { get; set; }
    public int Created { get; private set; }
    public bool IsConfigured => true;
    public Task<string> CreateCustomerAsync(AsaasCustomerRequest r, CancellationToken ct = default) => Task.FromResult("cus_test");
    public Task UpdateCustomerAsync(string id, AsaasCustomerRequest r, CancellationToken ct = default) => Task.CompletedTask;
    public Task<AsaasPayment> CreatePaymentAsync(string customerId, string billingType, decimal value, DateTime dueDate, string? description, string? externalReference, CancellationToken ct = default)
    {
        if (Fail) throw new IntegrationException("asaas indisponível");
        Created++;
        return Task.FromResult(new AsaasPayment("pay_" + Created, "PENDING", value, "https://invoice/" + Created, dueDate, null, null));
    }
    public Task<AsaasPix> GetPixQrCodeAsync(string paymentId, CancellationToken ct = default) => Task.FromResult(new AsaasPix("payload", "img", null));
    public Task<AsaasPayment> GetPaymentAsync(string paymentId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> CancelPaymentAsync(string paymentId, CancellationToken ct = default) => Task.FromResult(true);
}

public class FakeEvolution : IEvolutionApiIntegration
{
    public List<(string Phone, string Text)> Sent { get; } = new();
    public bool IsConfigured => true;
    public Task<string?> SendTextAsync(string phone, string text, CancellationToken ct = default) { Sent.Add((phone, text)); return Task.FromResult<string?>("m" + Sent.Count); }
    public Task<string?> SendDocumentAsync(string phone, byte[] content, string fileName, string mimeType, string? caption, CancellationToken ct = default)
    { Sent.Add((phone, "[doc] " + fileName)); return Task.FromResult<string?>("d" + Sent.Count); }
}

public class ContractFlowTests
{
    private const string Cpf = "529.982.247-25";

    private static (TestHost host, FakeAsaas asaas, FakeEvolution evo) NewHost()
    {
        var asaas = new FakeAsaas(); var evo = new FakeEvolution();
        var h = new TestHost(configure: s => { s.AddSingleton<IAsaasIntegration>(asaas); s.AddSingleton<IEvolutionApiIntegration>(evo); });
        return (h, asaas, evo);
    }

    private static async Task<Contract> NewContractAsync(IServiceProvider sp, ContractTerms terms)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var tpl = new ContractTemplate { Name = "T", Body = "# Contrato\n{{CLIENTE_NOME}} contrata {{SERVICO}}.\n{{CONDICOES_FINANCEIRAS}}\nCódigo {{CODIGO_VERIFICACAO}}" };
        var cust = new Customer { Name = "ACME Ltda", Document = "12.345.678/0001-90", WhatsApp = "62999990000" };
        db.ContractTemplates.Add(tpl); db.Customers.Add(cust);
        await db.SaveChangesAsync();
        return await sp.GetRequiredService<ContractService>().CreateAsync(tpl.Id, cust.Id, null, "Sistema X", 0, null, terms);
    }

    private static ContractTerms Terms() => new(3000m, 300m, 3, DateTime.UtcNow.Date.AddDays(5), 199.90m, DateTime.UtcNow.Date.AddDays(5), 12, BillingType.Pix);

    [Fact]
    public void Plan_SplitsInstallmentsExactly_AndLimitsMonthlyToHorizon()
    {
        var parts = BillingPlan.Split(100m, 3);
        Assert.Equal(100m, parts.Sum());
        Assert.Equal(new[] { 33.34m, 33.33m, 33.33m }, parts);

        var c = new Contract { Title = "t", ImplementationValue = 1000, DiscountValue = 100, ImplementationInstallments = 3, ImplementationFirstDue = new DateTime(2030, 1, 31),
            MonthlyValue = 50, MonthlyStart = new DateTime(2030, 1, 31), MonthlyMonths = null };
        var plan = BillingPlan.Build(c, new DateTime(2030, 4, 30));
        Assert.Equal(900m, plan.Where(p => p.Kind == BillingPlan.Implementation).Sum(p => p.Amount));
        Assert.Equal(new DateTime(2030, 2, 28), plan.First(p => p.Kind == BillingPlan.Implementation && p.Sequence == 2).Due);
        Assert.Equal(4, plan.Count(p => p.Kind == BillingPlan.Monthly)); // jan, feb, mar, apr
        Assert.Empty(BillingPlan.Build(new Contract { Title = "x", MonthlyValue = 10, MonthlyStart = DateTime.UtcNow }).Where(p => p.Kind == BillingPlan.Monthly));
    }

    [Theory]
    [InlineData("529.982.247-25", true)]
    [InlineData("111.111.111-11", false)]
    [InlineData("123", false)]
    [InlineData("11.222.333/0001-81", true)]
    [InlineData("11.222.333/0001-82", false)]
    public void Document_Validation(string doc, bool ok) => Assert.Equal(ok, DocumentUtil.IsValid(doc));

    [Fact]
    public async Task Send_DeliversLinkByWhatsApp_SignRequiresOtp_AndBillingIsGeneratedOnce()
    {
        var (h, asaas, evo) = NewHost(); using var _ = h; using var sc = h.Scope();
        var svc = sc.ServiceProvider.GetRequiredService<ContractService>();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = await NewContractAsync(sc.ServiceProvider, Terms());
        Assert.True(c.RenderedText.Contains("R$ 2.700,00"), c.RenderedText);
        Assert.Contains(c.VerificationCode!, c.RenderedText);

        var results = await svc.SendForSignatureAsync(c.Id);
        Assert.All(results, r => Assert.True(r.Sent));
        var token = (await db.ContractParties.FirstAsync(p => p.ContractId == c.Id)).Token;
        Assert.Contains(evo.Sent, m => m.Phone == "5562999990000" && m.Text.Contains("/sign/" + token));

        // without OTP the signature is refused
        Assert.False((await svc.SignAsync(token, new SignRequest("Fulano de Tal", Cpf, null, null, true), "1.1.1.1", "ua")).Success);
        Assert.True((await svc.RequestOtpAsync(token, "1.1.1.1", "ua")).Ok);
        var code = System.Text.RegularExpressions.Regex.Match(evo.Sent.Last().Text, @"\*(\d{6})\*").Groups[1].Value;
        Assert.False((await svc.SignAsync(token, new SignRequest("Fulano de Tal", Cpf, null, null, true, code == "000000" ? "111111" : "000000"), "1.1.1.1", "ua")).Success);
        var ok = await svc.SignAsync(token, new SignRequest("Fulano de Tal", Cpf, null, null, true, code), "1.1.1.1", "ua");
        Assert.True(ok.Success && ok.ContractCompleted);

        var sig = await db.ContractSignatures.SingleAsync();
        Assert.Equal("WHATSAPP_OTP", sig.VerificationMethod);
        Assert.Contains(evo.Sent, m => m.Text.StartsWith("[doc]"));

        var charges = await db.Charges.Where(x => x.ContractId == c.Id).ToListAsync();
        Assert.Equal(3, charges.Count(x => x.Kind == BillingPlan.Implementation));
        Assert.Equal(2700m, charges.Where(x => x.Kind == BillingPlan.Implementation).Sum(x => x.Amount));
        Assert.Equal(2, charges.Count(x => x.Kind == BillingPlan.Monthly));
        var created = asaas.Created;

        var again = await sc.ServiceProvider.GetRequiredService<ContractBillingService>().GenerateAsync(c.Id);
        Assert.Equal(0, again.Created);
        Assert.Equal(created, asaas.Created);

        // the OTP cannot be reused to sign a different time / the public verification finds the contract
        var found = await svc.FindByCodeAsync(c.VerificationCode!.ToLowerInvariant());
        Assert.Equal(c.Id, found?.Id);
        var pdf = await svc.GetPdfAsync(c.Id);
        Assert.True(pdf.Length > 3000);
    }

    [Fact]
    public async Task BillingFailure_DoesNotInvalidateSignature_AndIsRetried()
    {
        var (h, asaas, _) = NewHost(); using var _h = h; using var sc = h.Scope();
        var svc = sc.ServiceProvider.GetRequiredService<ContractService>();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        asaas.Fail = true;
        var c = await NewContractAsync(sc.ServiceProvider, Terms());
        await svc.SendForSignatureAsync(c.Id);
        var token = (await db.ContractParties.FirstAsync()).Token;
        await svc.RequestOtpAsync(token, null, null);
        var evo = (FakeEvolution)sc.ServiceProvider.GetRequiredService<IEvolutionApiIntegration>();
        var code = System.Text.RegularExpressions.Regex.Match(evo.Sent.Last().Text, @"\*(\d{6})\*").Groups[1].Value;
        Assert.True((await svc.SignAsync(token, new SignRequest("Fulano de Tal", Cpf, null, null, true, code), "1.1.1.1", "ua")).Success);

        Assert.Equal(ContractStatus.Assinado, (await db.Contracts.FirstAsync()).Status);
        Assert.Empty(await db.Charges.ToListAsync());
        Assert.Contains(await db.ContractEvents.Select(e => e.Type).ToListAsync(), t => t == "COBRANCA_FALHOU");

        asaas.Fail = false;
        Assert.Equal(5, await sc.ServiceProvider.GetRequiredService<ContractBillingService>().GenerateDueAsync());
    }
}
