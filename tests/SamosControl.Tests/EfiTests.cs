using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class EfiTests
{
    private sealed class Settings : ISettingsReader
    {
        public Dictionary<string, string?> V { get; } = new()
        {
            [SettingKeys.EfiClientId] = "id", [SettingKeys.EfiClientSecret] = "secret", [SettingKeys.EfiPixKey] = "chave-pix"
        };
        public string? Get(string key) => V.TryGetValue(key, out var v) ? v : null;
    }

    private sealed class Stub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body, string? Auth)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            var url = r.RequestUri!.ToString();
            Calls.Add((r.Method, url, body, r.Headers.Authorization?.ToString()));
            string json;
            if (url.EndsWith("/oauth/token") || url.EndsWith("/authorize")) json = """{"access_token":"tok","expires_in":3600}""";
            else if (url.Contains("/v2/cobv/") && r.Method == HttpMethod.Put) json = """{"status":"ATIVA","pixCopiaECola":"00020126PIX","loc":{"id":77}}""";
            else if (url.Contains("/v2/loc/")) json = """{"imagemQrcode":"data:image/png;base64,QUJD"}""";
            else if (url.Contains("/v2/cobv/") && r.Method == HttpMethod.Get) json = """{"status":"CONCLUIDA","pix":[{"valor":"150.00","horario":"2030-01-02T10:00:00Z"}]}""";
            else if (url.EndsWith("/charge/one-step")) json = """{"code":200,"data":{"charge_id":4321,"status":"waiting","link":"https://boleto/4321"}}""";
            else json = "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    private sealed class TestEfi(Stub stub, ISettingsReader s) : EfiIntegration(new Factory(stub), s)
    {
        protected override HttpClient CreatePixClient() => new(stub, false);
    }

    private static readonly DateTime Due = new(2030, 5, 10);

    [Fact]
    public async Task Pix_CreatesCobv_WithCpfOrCnpj_AndStripsQrDataUri()
    {
        var stub = new Stub(); var efi = new TestEfi(stub, new Settings());
        var cnpj = await efi.CreateAsync(true, new EfiDebtor("ACME \"Ltda\"", "12.345.678/0001-90", null, null), 1234.5m, Due, "Sistema X");
        var put = stub.Calls.Single(c => c.Method == HttpMethod.Put);
        Assert.Matches(@"/v2/cobv/[0-9a-f]{32}$", put.Url);
        Assert.StartsWith("Bearer ", put.Auth);
        var b = JsonNode.Parse(put.Body)!;
        Assert.Equal("12345678000190", (string?)b["devedor"]!["cnpj"]);
        Assert.Null(b["devedor"]!["cpf"]);
        Assert.Equal("1234.50", (string?)b["valor"]!["original"]);
        Assert.Equal("2030-05-10", (string?)b["calendario"]!["dataDeVencimento"]);
        Assert.Equal("chave-pix", (string?)b["chave"]);
        Assert.Equal("00020126PIX", cnpj.PixPayload);
        Assert.Equal("QUJD", cnpj.PixQrCodeBase64);

        await efi.CreateAsync(true, new EfiDebtor("Fulano", "529.982.247-25", null, null), 10m, Due, null);
        Assert.Equal("52998224725", (string?)JsonNode.Parse(stub.Calls.Last(c => c.Method == HttpMethod.Put).Body)!["devedor"]!["cpf"]);
    }

    [Fact]
    public async Task Billet_UsesAuthorizeThenOneStep_WithCents()
    {
        var stub = new Stub(); var efi = new TestEfi(stub, new Settings());
        var r = await efi.CreateAsync(false, new EfiDebtor("Fulano", "52998224725", "f@x.com", "(62) 99999-0000"), 199.9m, Due, "Mensalidade");
        Assert.Equal("4321", r.Id);
        Assert.Equal("https://boleto/4321", r.Url);
        Assert.Contains(stub.Calls, c => c.Url == "https://cobrancas.api.efipay.com.br/v1/authorize" && c.Auth!.StartsWith("Basic "));
        var body = JsonNode.Parse(stub.Calls.Single(c => c.Url.EndsWith("/charge/one-step")).Body)!;
        Assert.Equal(19990, (int)body["items"]![0]!["value"]!);
        Assert.Equal("2030-05-10", (string?)body["payment"]!["banking_billet"]!["expire_at"]);
        Assert.Equal("52998224725", (string?)body["payment"]!["banking_billet"]!["customer"]!["cpf"]);
    }

    [Fact]
    public async Task Sandbox_UsesHomologHosts_AndMissingDocumentIsRejected()
    {
        var s = new Settings(); s.V[SettingKeys.EfiSandbox] = "true";
        var stub = new Stub(); var efi = new TestEfi(stub, s);
        await efi.CreateAsync(true, new EfiDebtor("F", "52998224725", null, null), 1m, Due, null);
        Assert.All(stub.Calls, c => Assert.StartsWith("https://pix-h.api.efipay.com.br", c.Url));
        await Assert.ThrowsAsync<ArgumentException>(() => efi.CreateAsync(true, new EfiDebtor("F", "", null, null), 1m, Due, null));
    }

    [Fact]
    public async Task GetAsync_ReadsPaidPix()
    {
        var stub = new Stub(); var efi = new TestEfi(stub, new Settings());
        var r = await efi.GetAsync(true, "abc");
        Assert.Equal("RECEIVED", r.Status);
        Assert.Equal(150m, r.PaidValue);
        Assert.Equal(new DateTime(2030, 1, 2, 10, 0, 0), r.PaidAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void StatusMapping_AndErrorMessage()
    {
        Assert.Equal("PENDING", EfiIntegration.MapPixStatus("ATIVA"));
        Assert.Equal("DELETED", EfiIntegration.MapPixStatus("REMOVIDA_PELO_PSP"));
        Assert.Equal("RECEIVED", EfiIntegration.MapBilletStatus("paid"));
        Assert.Equal("OVERDUE", EfiIntegration.MapBilletStatus("unpaid"));
        Assert.Equal("DELETED", EfiIntegration.MapBilletStatus("canceled"));
        Assert.Equal("chave inválida", EfiIntegration.ErrorMessage("""{"mensagem":"chave inválida"}"""));
        Assert.Equal("texto puro", EfiIntegration.ErrorMessage("texto puro"));
    }

    private sealed class FakeEfi : IEfiIntegration
    {
        public bool IsConfigured => true;
        public string Status { get; set; } = "PENDING";
        public List<bool> PixFlags { get; } = new();
        public Task<EfiCharge> CreateAsync(bool pix, EfiDebtor d, decimal value, DateTime due, string? description, CancellationToken ct = default)
        { PixFlags.Add(pix); return Task.FromResult(new EfiCharge("tx1", "PENDING", null, pix ? "payload" : null, null, null, null)); }
        public Task<EfiCharge> GetAsync(bool pix, string id, CancellationToken ct = default)
            => Task.FromResult(new EfiCharge(id, Status, null, null, null, Status == "RECEIVED" ? 100m : null, Status == "RECEIVED" ? DateTime.UtcNow : null));
        public Task<bool> CancelAsync(bool pix, string id, CancellationToken ct = default) => Task.FromResult(true);
        public List<(string Id, decimal Value, string Key)> Sent { get; } = new();
        public string TransferStatus { get; set; } = "EM_PROCESSAMENTO";
        public Task<EfiPixTransfer> SendPixAsync(string idEnvio, decimal value, string toKey, string? info, CancellationToken ct = default)
        { Sent.Add((idEnvio, value, toKey)); return Task.FromResult(new EfiPixTransfer(idEnvio, TransferStatus, null)); }
        public Task<EfiPixTransfer> GetPixTransferAsync(string idEnvio, CancellationToken ct = default) => Task.FromResult(new EfiPixTransfer(idEnvio, TransferStatus, "E2E"));
    }

    private static TestHost Host(FakeEfi efi, string? provider) => new(
        provider == null ? null : new() { ["BILLING_PROVIDER"] = provider },
        s => { s.AddSingleton<IAsaasIntegration>(new FakeAsaas()); s.AddSingleton<IEfiIntegration>(efi); s.AddSingleton<IEvolutionApiIntegration>(new FakeEvolution()); });

    private static async Task<int> CustomerAsync(AppDbContext db)
    {
        var c = new Customer { Name = "ACME", Document = "52998224725" };
        db.Customers.Add(c); await db.SaveChangesAsync(); return c.Id;
    }

    [Fact]
    public async Task Billing_UsesEfi_WhenSelected_AndPollingMarksPaid()
    {
        var efi = new FakeEfi(); using var h = Host(efi, "efi"); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = sc.ServiceProvider.GetRequiredService<BillingService>();
        var cust = await CustomerAsync(db);

        var ch = await billing.CreateChargeAsync(cust, null, "Sistema", 100m, DateTime.UtcNow.Date.AddDays(3), BillingType.Pix, null);
        Assert.Equal("EFI", ch.Provider);
        Assert.Equal("tx1", ch.AsaasId);
        Assert.Equal("payload", ch.PixPayload);
        Assert.Equal(ChargeStatus.Pendente, ch.Status);

        efi.Status = "RECEIVED";
        Assert.Equal(1, await billing.RefreshOpenEfiAsync());
        db.ChangeTracker.Clear();
        var saved = await db.Charges.SingleAsync();
        Assert.Equal(ChargeStatus.Pago, saved.Status);
        Assert.Equal(0, await billing.RefreshOpenEfiAsync());
    }

    [Fact]
    public async Task Billing_DefaultsToAsaas()
    {
        var efi = new FakeEfi(); using var h = Host(efi, null); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var cust = await CustomerAsync(db);
        var ch = await sc.ServiceProvider.GetRequiredService<BillingService>().CreateChargeAsync(cust, null, "S", 50m, DateTime.UtcNow.Date.AddDays(3), BillingType.Pix, null);
        Assert.Equal("ASAAS", ch.Provider);
        Assert.StartsWith("pay_", ch.AsaasId);
        Assert.Empty(efi.PixFlags);
    }

    private static async Task<(int sellerId, int chargeId, int customerId)> PaidChargeAsync(AppDbContext db, string kind, decimal amount)
    {
        var seller = new Seller { Name = "Vend", PixKey = "vend@pix.com", PixKeyType = "EMAIL", ImplementationPercent = 10, MonthlyPercent = 5, Phone = "62999990000" };
        var cust = new Customer { Name = "ACME", Document = "52998224725" };
        db.Sellers.Add(seller); db.Customers.Add(cust); await db.SaveChangesAsync();
        var ct = new Contract { Title = "t", CustomerId = cust.Id, SellerId = seller.Id, ImplementationCommissionPercent = 10, MonthlyCommissionPercent = 5 };
        db.Contracts.Add(ct); await db.SaveChangesAsync();
        var ch = new Charge { CustomerId = cust.Id, ContractId = ct.Id, Amount = amount, PaidAmount = amount, Status = ChargeStatus.Pago, PaidAt = DateTime.UtcNow, Kind = kind, Sequence = 1, DueDate = DateTime.UtcNow };
        db.Charges.Add(ch); await db.SaveChangesAsync();
        return (seller.Id, ch.Id, cust.Id);
    }

    [Fact]
    public async Task Commission_Accrues_ByKind_Once_AndCanBeMarkedPaidManually()
    {
        var efi = new FakeEfi(); using var h = Host(efi, null); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = sc.ServiceProvider.GetRequiredService<CommissionService>();
        var (sellerId, _, _) = await PaidChargeAsync(db, BillingPlan.Implementation, 1000m);

        var r = await svc.ProcessAsync();
        Assert.Equal(1, r.Created);
        Assert.Equal(0, (await svc.ProcessAsync()).Created);
        var c = await db.Commissions.SingleAsync();
        Assert.Equal(100m, c.Amount); Assert.Equal(10m, c.Percent); Assert.Equal(CommissionStatus.Pendente, c.Status);
        Assert.Empty(efi.Sent); // manual payout by default

        await svc.MarkPaidAsync(c.Id, "comprovante-1");
        db.ChangeTracker.Clear();
        Assert.Equal(CommissionStatus.Paga, (await db.Commissions.SingleAsync()).Status);
        Assert.Equal(sellerId, (await db.Commissions.SingleAsync()).SellerId);
    }

    [Fact]
    public async Task Commission_MonthlyUsesMonthlyPercent_AndEfiPayoutReusesIdempotencyId()
    {
        var efi = new FakeEfi(); using var h = new TestHost(new() { ["PAYOUT_MODE"] = "efi" },
            s => { s.AddSingleton<IAsaasIntegration>(new FakeAsaas()); s.AddSingleton<IEfiIntegration>(efi); s.AddSingleton<IEvolutionApiIntegration>(new FakeEvolution()); });
        using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = sc.ServiceProvider.GetRequiredService<CommissionService>();
        await PaidChargeAsync(db, BillingPlan.Monthly, 200m);

        await svc.ProcessAsync();
        var c = await db.Commissions.SingleAsync();
        Assert.Equal(10m, c.Amount); // 5% of 200
        Assert.Equal(CommissionStatus.EmProcessamento, c.Status);
        Assert.Single(efi.Sent);
        Assert.Equal(("vend@pix.com", 10m), (efi.Sent[0].Key, efi.Sent[0].Value));

        efi.TransferStatus = "REALIZADO";
        await svc.ProcessAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(CommissionStatus.Paga, (await db.Commissions.SingleAsync()).Status);
        Assert.Single(efi.Sent);
    }

    [Fact]
    public async Task Contract_RequiresOwnerSignature_WhenOwnerConfigured_AndPdfRenders()
    {
        var evo = new FakeEvolution();
        using var h = new TestHost(new() { ["OWNER_NAME"] = "Dono da Silva", ["OWNER_PHONE"] = "62988887777" },
            s => { s.AddSingleton<IAsaasIntegration>(new FakeAsaas()); s.AddSingleton<IEfiIntegration>(new FakeEfi()); s.AddSingleton<IEvolutionApiIntegration>(evo); });
        using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = sc.ServiceProvider.GetRequiredService<ContractService>();
        var tpl = new ContractTemplate { Name = "T", Body = "# C\n{{CLIENTE_NOME}} {{CONDICOES_FINANCEIRAS}}" };
        var cust = new Customer { Name = "ACME", Document = "12.345.678/0001-90", WhatsApp = "62999990000" };
        db.ContractTemplates.Add(tpl); db.Customers.Add(cust); await db.SaveChangesAsync();
        var c = await svc.CreateAsync(tpl.Id, cust.Id, null, "Sis", 100m, DateTime.UtcNow.AddDays(3));
        await svc.SendForSignatureAsync(c.Id);

        var parties = await db.ContractParties.Where(p => p.ContractId == c.Id).ToListAsync();
        Assert.Equal(2, parties.Count);
        Assert.Contains(evo.Sent, m => m.Phone == "5562988887777" && m.Text.Contains("/sign/"));
        Assert.True((await svc.GetPdfAsync(c.Id)).Length > 3000);
    }
}
