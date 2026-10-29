using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record WebhookResult(int StatusCode, string Message);

public static class WebhookUtil
{
    public static bool TokenMatches(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Registers the event; returns null when it was already received (duplicate).</summary>
    public static async Task<WebhookEvent?> RegisterAsync(AppDbContext db, string provider, string externalId, string type, string payload)
    {
        if (await db.WebhookEvents.AnyAsync(w => w.Provider == provider && w.ExternalId == externalId)) return null;
        var ev = new WebhookEvent { Provider = provider, ExternalId = externalId, EventType = type, Payload = payload.Length > 20000 ? payload[..20000] : payload };
        db.WebhookEvents.Add(ev);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.Entry(ev).State = EntityState.Detached;
            return null; // lost a race against a concurrent duplicate (unique index)
        }
        return ev;
    }
}

public class BillingService
{
    private readonly AppDbContext _db;
    private readonly IAsaasIntegration _asaas;
    private readonly IEfiIntegration _efi;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly ICurrentUser _user;
    private readonly CommissionService _commissions;

    public BillingService(AppDbContext db, IAsaasIntegration asaas, IEfiIntegration efi, SettingsService settings, AuditService audit, NotificationService notify, ICurrentUser user, CommissionService commissions)
    { _db = db; _asaas = asaas; _efi = efi; _settings = settings; _audit = audit; _notify = notify; _user = user; _commissions = commissions; }

    /// <summary>Commissions are generated (and paid, when automatic) right after a payment is detected. A failure never blocks billing.</summary>
    private async Task CommissionsAsync(CancellationToken ct = default)
    {
        try { await _commissions.ProcessAsync(ct); } catch (Exception ex) when (ex is IntegrationException or DbUpdateException or InvalidOperationException) { }
    }

    public const string ProviderAsaas = "ASAAS", ProviderEfi = "EFI";

    /// <summary>Gateway used for NEW charges. Existing charges keep the gateway that issued them.</summary>
    public string ActiveProvider => string.Equals(_settings.Get(SettingKeys.BillingProvider)?.Trim(), "efi", StringComparison.OrdinalIgnoreCase) ? ProviderEfi : ProviderAsaas;
    public bool IsConfigured => ActiveProvider == ProviderEfi ? _efi.IsConfigured : _asaas.IsConfigured;
    private static string GatewayName(string provider) => provider == ProviderEfi ? "Efí" : "Asaas";

    public static ChargeStatus MapStatus(string asaasStatus) => asaasStatus switch
    {
        "RECEIVED" or "CONFIRMED" or "RECEIVED_IN_CASH" => ChargeStatus.Pago,
        "OVERDUE" => ChargeStatus.Vencido,
        "REFUNDED" or "REFUND_REQUESTED" or "REFUND_IN_PROGRESS" or "CHARGEBACK_REQUESTED" => ChargeStatus.Estornado,
        "DELETED" => ChargeStatus.Cancelado,
        _ => ChargeStatus.Pendente
    };

    private async Task<string> EnsureCustomerAsync(Customer c, CancellationToken ct)
    {
        var req = new AsaasCustomerRequest(c.Name, c.Document, c.Email, c.WhatsApp ?? c.Phone, "samos-customer-" + c.Id);
        if (c.AsaasCustomerId != null) return c.AsaasCustomerId;
        c.AsaasCustomerId = await _asaas.CreateCustomerAsync(req, ct);
        await _db.SaveChangesAsync(ct);
        return c.AsaasCustomerId;
    }

    public async Task SyncCustomerAsync(int customerId, CancellationToken ct = default)
    {
        var c = await _db.Customers.FindAsync(new object[] { customerId }, ct) ?? throw new KeyNotFoundException();
        if (ActiveProvider == ProviderEfi) return; // Efí receives the payer inline with each charge; there is no customer registry
        var req = new AsaasCustomerRequest(c.Name, c.Document, c.Email, c.WhatsApp ?? c.Phone, "samos-customer-" + c.Id);
        if (c.AsaasCustomerId == null) await EnsureCustomerAsync(c, ct);
        else await _asaas.UpdateCustomerAsync(c.AsaasCustomerId, req, ct);
    }

    public async Task<Charge> CreateChargeAsync(int customerId, int? contractId, string? serviceName, decimal amount, DateTime dueDate, BillingType type, string? description, CancellationToken ct = default, string kind = "AVULSA", int sequence = 0)
    {
        if (amount <= 0) throw new ArgumentException("O valor deve ser maior que zero.");
        if (dueDate.Date < AppTime.Now.Date) throw new ArgumentException("O vencimento não pode estar no passado.");
        var customer = await _db.Customers.FindAsync(new object[] { customerId }, ct) ?? throw new KeyNotFoundException("Cliente não encontrado.");
        var desc = description ?? serviceName ?? "Cobrança";
        var provider = ActiveProvider;
        var charge = new Charge
        {
            CustomerId = customerId, ContractId = contractId, ServiceName = serviceName, Amount = amount, DueDate = DateTime.SpecifyKind(dueDate.Date, DateTimeKind.Utc),
            BillingType = type, Description = desc, CreatedByUserId = _user.UserId, Kind = kind, Sequence = sequence, Provider = provider
        };
        if (provider == ProviderEfi)
        {
            var e = await _efi.CreateAsync(type == BillingType.Pix, new EfiDebtor(customer.Name, customer.Document ?? "", customer.Email, customer.WhatsApp ?? customer.Phone), amount, dueDate.Date, desc, ct);
            charge.AsaasId = e.Id; charge.InvoiceUrl = e.Url; charge.Status = MapStatus(e.Status);
            charge.PixPayload = e.PixPayload; charge.PixQrCodeBase64 = e.PixQrCodeBase64;
        }
        else
        {
            var asaasCustomer = await EnsureCustomerAsync(customer, ct);
            var p = await _asaas.CreatePaymentAsync(asaasCustomer, type == BillingType.Pix ? "PIX" : "BOLETO", amount, dueDate.Date, desc, null, ct);
            charge.AsaasId = p.Id; charge.InvoiceUrl = p.InvoiceUrl; charge.Status = MapStatus(p.Status);
            if (type == BillingType.Pix)
            {
                try { var pix = await _asaas.GetPixQrCodeAsync(p.Id, ct); charge.PixPayload = pix.Payload; charge.PixQrCodeBase64 = pix.EncodedImage; }
                catch (IntegrationException) { /* the charge exists; QR code can be refreshed later */ }
            }
        }
        _db.Charges.Add(charge);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("billing", "create_charge", "Charge", charge.Id, newData: $"customer={customerId}; amount={amount}; provider={provider}; external={charge.AsaasId}");
        return charge;
    }

    public async Task RefreshAsync(int id, CancellationToken ct = default)
    {
        var ch = await _db.Charges.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        await RefreshChargeAsync(ch, ct);
        await _db.SaveChangesAsync(ct);
        if (ch.Status == ChargeStatus.Pago) await CommissionsAsync(ct);
    }

    private async Task<bool> RefreshChargeAsync(Charge ch, CancellationToken ct)
    {
        if (ch.AsaasId == null) return false;
        var pix = ch.BillingType == BillingType.Pix;
        if (ch.Provider == ProviderEfi)
        {
            var e = await _efi.GetAsync(pix, ch.AsaasId, ct);
            var changed = Apply(ch, MapStatus(e.Status), e.PaidAt, e.PaidValue);
            ch.InvoiceUrl = e.Url ?? ch.InvoiceUrl;
            return changed;
        }
        var p = await _asaas.GetPaymentAsync(ch.AsaasId, ct);
        var ch2 = Apply(ch, MapStatus(p.Status), p.PaymentDate, p.Value);
        ch.InvoiceUrl = p.InvoiceUrl ?? ch.InvoiceUrl;
        if (pix && ch.PixPayload == null && ch.Status == ChargeStatus.Pendente)
        {
            try { var q = await _asaas.GetPixQrCodeAsync(ch.AsaasId, ct); ch.PixPayload = q.Payload; ch.PixQrCodeBase64 = q.EncodedImage; } catch (IntegrationException) { }
        }
        return ch2;
    }

    /// <summary>Efí has no usable webhook behind a reverse proxy (mTLS), so open charges are polled. Returns how many changed status.</summary>
    public async Task<int> RefreshOpenEfiAsync(CancellationToken ct = default, int max = 200)
    {
        if (!_efi.IsConfigured) return 0;
        var open = await _db.Charges.Include(c => c.Customer)
            .Where(c => c.Provider == ProviderEfi && c.AsaasId != null && (c.Status == ChargeStatus.Pendente || c.Status == ChargeStatus.Vencido))
            .OrderBy(c => c.DueDate).Take(max).ToListAsync(ct);
        var changedCount = 0;
        foreach (var ch in open)
        {
            try
            {
                var before = ch.Status;
                if (await RefreshChargeAsync(ch, ct) && ch.Status != before)
                {
                    changedCount++;
                    await _notify.CreateAsync(ch.Status == ChargeStatus.Pago ? NotificationType.Success : NotificationType.Warning, NotificationSource.Billing,
                        $"Cobrança {ch.Status}", $"{ch.Customer?.Name} • {ch.Amount:C2}", "/Charges");
                }
            }
            catch (IntegrationException) { /* try again on the next run */ }
        }
        await _db.SaveChangesAsync(ct);
        if (changedCount > 0) await CommissionsAsync(ct);
        return changedCount;
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        var ch = await _db.Charges.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        if (ch.Status is not (ChargeStatus.Pendente or ChargeStatus.Vencido)) throw new InvalidOperationException("Somente cobranças pendentes ou vencidas podem ser canceladas.");
        if (ch.AsaasId != null)
        {
            var ok = ch.Provider == ProviderEfi
                ? await _efi.CancelAsync(ch.BillingType == BillingType.Pix, ch.AsaasId, ct)
                : await _asaas.CancelPaymentAsync(ch.AsaasId, ct);
            if (!ok) throw new InvalidOperationException($"O {GatewayName(ch.Provider)} não permitiu cancelar esta cobrança.");
        }
        ch.Status = ChargeStatus.Cancelado;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("billing", "cancel_charge", "Charge", id);
    }

    /// <summary>Applies a status transition; a paid charge only moves on to refunded/cancelled.</summary>
    private static bool Apply(Charge ch, ChargeStatus st, DateTime? paidAt, decimal? value)
    {
        if (ch.Status == st) return false;
        if (ch.Status == ChargeStatus.Pago && st is ChargeStatus.Pendente or ChargeStatus.Vencido) return false;
        if (ch.Status is ChargeStatus.Cancelado or ChargeStatus.Estornado && st is ChargeStatus.Pendente or ChargeStatus.Vencido) return false;
        ch.Status = st;
        if (st == ChargeStatus.Pago) { ch.PaidAt = paidAt ?? DateTime.UtcNow; ch.PaidAmount = value ?? ch.Amount; }
        return true;
    }

    public async Task<WebhookResult> HandleWebhookAsync(string body, string? providedToken)
    {
        var expected = _settings.Get(SettingKeys.AsaasWebhookToken);
        if (string.IsNullOrEmpty(expected)) return new(503, "Webhook do Asaas não configurado (ASAAS_WEBHOOK_TOKEN).");
        if (!WebhookUtil.TokenMatches(expected, providedToken)) return new(401, "Token inválido.");

        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { return new(400, "JSON inválido."); }
        var evType = Json(root?["event"]);
        if (root is null || evType is null) return new(400, "Payload inválido.");
        var extId = Json(root["id"]) ?? "sha:" + ContractService.Sha256(body);

        var ev = await WebhookUtil.RegisterAsync(_db, "asaas", extId, evType, body);
        if (ev is null) return new(200, "Evento duplicado ignorado.");

        try
        {
            var payment = root["payment"];
            var asaasId = Json(payment?["id"]);
            var ch = asaasId == null ? null : await _db.Charges.Include(c => c.Customer).FirstOrDefaultAsync(c => c.AsaasId == asaasId);
            if (ch is null) { ev.Status = WebhookStatus.Ignored; ev.Error = "Cobrança não encontrada."; }
            else
            {
                var newStatus = evType switch
                {
                    "PAYMENT_RECEIVED" or "PAYMENT_CONFIRMED" => ChargeStatus.Pago,
                    "PAYMENT_OVERDUE" => ChargeStatus.Vencido,
                    "PAYMENT_DELETED" => ChargeStatus.Cancelado,
                    "PAYMENT_REFUNDED" => ChargeStatus.Estornado,
                    _ => (ChargeStatus?)null
                };
                if (newStatus is null) ev.Status = WebhookStatus.Ignored;
                else
                {
                    decimal? val = decimal.TryParse(Json(payment?["value"]), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
                    DateTime? paid = DateTime.TryParse(Json(payment?["paymentDate"]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                    var changed = Apply(ch, newStatus.Value, paid, val);
                    ev.Status = WebhookStatus.Processed;
                    if (changed)
                    {
                        await _notify.CreateAsync(newStatus == ChargeStatus.Pago ? NotificationType.Success : NotificationType.Warning, NotificationSource.Billing,
                            $"Cobrança {newStatus}", $"{ch.Customer?.Name} • {ch.Amount:C2}", "/Charges");
                    }
                }
            }
            ev.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            if (ch?.Status == ChargeStatus.Pago) await CommissionsAsync();
            return new(200, "OK");
        }
        catch (Exception ex)
        {
            ev.Status = WebhookStatus.Failed; ev.Error = ex.Message.Truncate(500);
            await _db.SaveChangesAsync();
            return new(500, "Erro ao processar.");
        }
    }

    private static string? Json(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;

    public async Task<int> MarkOverdueAsync()
    {
        var today = DateTime.SpecifyKind(AppTime.Now.Date, DateTimeKind.Utc);
        var list = await _db.Charges.Where(c => c.Status == ChargeStatus.Pendente && c.DueDate < today).ToListAsync();
        foreach (var c in list) c.Status = ChargeStatus.Vencido;
        if (list.Count > 0) await _db.SaveChangesAsync();
        return list.Count;
    }
}
