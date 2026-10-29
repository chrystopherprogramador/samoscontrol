using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record CommissionRun(int Created, int Paid, int Failed, int Cancelled);

/// <summary>
/// Turns paid contract charges into seller commissions and pays them. Idempotent: one commission per charge (unique ChargeId).
/// Payout is manual by default; with PAYOUT_MODE=efi the Pix is sent through the Efí API (idempotent idEnvio).
/// </summary>
public class CommissionService
{
    public const int MaxAttempts = 5;
    private readonly AppDbContext _db;
    private readonly IEfiIntegration _efi;
    private readonly SettingsService _settings;
    private readonly NotificationService _notify;
    private readonly AuditService _audit;
    private readonly WhatsAppService _wa;

    public CommissionService(AppDbContext db, IEfiIntegration efi, SettingsService settings, NotificationService notify, AuditService audit, WhatsAppService wa)
    { _db = db; _efi = efi; _settings = settings; _notify = notify; _audit = audit; _wa = wa; }

    public bool AutoPayout => string.Equals(_settings.Get(SettingKeys.PayoutMode)?.Trim(), "efi", StringComparison.OrdinalIgnoreCase) && _efi.IsConfigured;

    public static decimal Calc(decimal baseAmount, decimal percent) => Math.Round(baseAmount * percent / 100m, 2, MidpointRounding.AwayFromZero);

    public static void ValidatePercent(decimal pct, string label)
    {
        if (pct < 0 || pct > 100) throw new ArgumentException($"{label}: informe um percentual entre 0 e 100.");
    }

    public async Task<CommissionRun> ProcessAsync(CancellationToken ct = default)
    {
        var created = await AccrueAsync(ct);
        var cancelled = await CancelReversedAsync(ct);
        var (paid, failed) = await ResolveAndPayAsync(ct);
        return new(created, paid, failed, cancelled);
    }

    private async Task<int> AccrueAsync(CancellationToken ct)
    {
        var candidates = await (from ch in _db.Charges
                                join k in _db.Contracts on ch.ContractId equals k.Id
                                where ch.Status == ChargeStatus.Pago && k.SellerId != null && k.Kind == "SERVICO" && ch.Kind != "AVULSA"
                                      && !_db.Commissions.Any(c => c.ChargeId == ch.Id)
                                select new { Charge = ch, Contract = k }).Take(200).ToListAsync(ct);
        var created = 0;
        foreach (var x in candidates)
        {
            var seller = await _db.Sellers.FindAsync(new object[] { x.Contract.SellerId!.Value }, ct);
            if (seller is null) continue;
            var isImpl = x.Charge.Kind == BillingPlan.Implementation;
            var pct = isImpl ? x.Contract.ImplementationCommissionPercent : x.Contract.MonthlyCommissionPercent;
            if (!isImpl && seller.MonthlyCommissionMonths is { } max && x.Charge.Sequence > max) continue;
            var baseAmount = x.Charge.PaidAmount ?? x.Charge.Amount;
            var amount = Calc(baseAmount, pct);
            if (amount < 0.01m) continue;
            _db.Commissions.Add(new Commission
            {
                SellerId = seller.Id, CustomerId = x.Charge.CustomerId, ContractId = x.Contract.Id, ChargeId = x.Charge.Id, Kind = x.Charge.Kind,
                Sequence = x.Charge.Sequence, BaseAmount = baseAmount, Percent = pct, Amount = amount, PixKey = seller.PixKey
            });
            try { await _db.SaveChangesAsync(ct); created++; }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); /* created concurrently */ }
        }
        if (created > 0) await _notify.CreateAsync(NotificationType.Info, NotificationSource.Billing, $"{created} comissão(ões) gerada(s)", "Pagamentos de clientes das carteiras dos vendedores.", "/Commissions");
        return created;
    }

    private async Task<int> CancelReversedAsync(CancellationToken ct)
    {
        var list = await (from c in _db.Commissions
                          join ch in _db.Charges on c.ChargeId equals ch.Id
                          where (c.Status == CommissionStatus.Pendente || c.Status == CommissionStatus.Erro) && (ch.Status == ChargeStatus.Estornado || ch.Status == ChargeStatus.Cancelado)
                          select c).ToListAsync(ct);
        foreach (var c in list) { c.Status = CommissionStatus.Cancelada; c.Error = "Cobrança estornada/cancelada."; }
        if (list.Count > 0) await _db.SaveChangesAsync(ct);
        return list.Count;
    }

    private async Task<(int Paid, int Failed)> ResolveAndPayAsync(CancellationToken ct)
    {
        if (!AutoPayout) return (0, 0);
        int paid = 0, failed = 0;

        foreach (var c in await _db.Commissions.Include(x => x.Seller).Where(x => x.Status == CommissionStatus.EmProcessamento && x.PayoutId != null).ToListAsync(ct))
        {
            try
            {
                var t = await _efi.GetPixTransferAsync(c.PayoutId!, ct);
                if (await ApplyTransferAsync(c, t, ct)) paid++; else if (c.Status == CommissionStatus.Erro) failed++;
            }
            catch (IntegrationException ex) { c.Error = ex.Message.Truncate(300); await _db.SaveChangesAsync(ct); }
        }

        foreach (var c in await _db.Commissions.Include(x => x.Seller).Where(x => x.Status == CommissionStatus.Pendente).OrderBy(x => x.Id).Take(50).ToListAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(c.PixKey ?? c.Seller?.PixKey)) { c.Error = "Vendedor sem chave Pix cadastrada."; await _db.SaveChangesAsync(ct); continue; }
            c.PixKey = c.Seller?.PixKey ?? c.PixKey;
            // The id is created once and reused on retries: Efí never debits twice for the same idEnvio.
            c.PayoutId ??= $"samoscom{c.Id:D8}a{c.Attempts + 1}";
            try
            {
                c.Attempts++;
                var t = await _efi.SendPixAsync(c.PayoutId, c.Amount, c.PixKey!, $"Comissao Samos #{c.Id}", ct);
                c.Error = null;
                if (await ApplyTransferAsync(c, t, ct)) paid++;
            }
            catch (Exception ex) when (ex is IntegrationException or ArgumentException)
            {
                c.Error = ex.Message.Truncate(300);
                // 4xx (except rate limit) needs a human; 5xx/network keeps the same idEnvio for a safe retry on the next run.
                var permanent = ex is ArgumentException || (ex.Message.StartsWith("Efí 4") && !ex.Message.StartsWith("Efí 429")) || c.Attempts >= MaxAttempts;
                if (permanent) { c.Status = CommissionStatus.Erro; failed++; }
                await _db.SaveChangesAsync(ct);
            }
        }
        if (failed > 0) await _notify.CreateAsync(NotificationType.Error, NotificationSource.Billing, "Falha no repasse de comissão", $"{failed} repasse(s) precisam de atenção.", "/Commissions");
        return (paid, failed);
    }

    /// <summary>Returns true when the transfer is confirmed (commission paid).</summary>
    private async Task<bool> ApplyTransferAsync(Commission c, EfiPixTransfer t, CancellationToken ct)
    {
        switch (t.Status)
        {
            case "REALIZADO":
                c.Status = CommissionStatus.Paga; c.PaidAt = DateTime.UtcNow; c.PayoutReference = t.E2eId?.Truncate(60); c.Error = null;
                await _db.SaveChangesAsync(ct);
                await _audit.LogAsync("commissions", "paid_pix", "Commission", c.Id, newData: $"amount={c.Amount}; e2e={t.E2eId}");
                await TellSellerAsync(c);
                return true;
            case "NAO_REALIZADO":
                c.Status = CommissionStatus.Erro; c.Error = "Pix não realizado pelo banco. Verifique a chave do vendedor e tente novamente.";
                break;
            default:
                c.Status = CommissionStatus.EmProcessamento;
                break;
        }
        await _db.SaveChangesAsync(ct);
        return false;
    }

    public async Task MarkPaidAsync(int id, string? reference, CancellationToken ct = default)
    {
        var c = await _db.Commissions.Include(x => x.Seller).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (c.Status is CommissionStatus.Paga or CommissionStatus.Cancelada) throw new InvalidOperationException("Esta comissão não pode ser marcada como paga.");
        c.Status = CommissionStatus.Paga; c.PaidAt = DateTime.UtcNow; c.PayoutReference = reference?.Trim().Truncate(60); c.Error = null;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("commissions", "mark_paid", "Commission", id, newData: $"amount={c.Amount}");
        await TellSellerAsync(c);
    }

    public async Task RetryAsync(int id, CancellationToken ct = default)
    {
        var c = await _db.Commissions.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        if (c.Status != CommissionStatus.Erro) throw new InvalidOperationException("Somente comissões com erro podem ser reenviadas.");
        c.Status = CommissionStatus.Pendente; c.Error = null; c.Attempts = 0; c.PayoutId = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        var c = await _db.Commissions.FindAsync(new object[] { id }, ct) ?? throw new KeyNotFoundException();
        if (c.Status is not (CommissionStatus.Pendente or CommissionStatus.Erro)) throw new InvalidOperationException("Somente comissões pendentes ou com erro podem ser canceladas.");
        c.Status = CommissionStatus.Cancelada;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("commissions", "cancel", "Commission", id);
    }

    private async Task TellSellerAsync(Commission c)
    {
        var phone = c.Seller?.Phone ?? (await _db.Sellers.Where(s => s.Id == c.SellerId).Select(s => s.Phone).FirstOrDefaultAsync());
        if (string.IsNullOrWhiteSpace(phone) || !_wa.IsConfigured) return;
        try { await _wa.SendTextAsync(phone, $"💰 Comissão paga: {ContractService.Money(c.Amount)} ({c.Kind.ToLowerInvariant()}, {c.Percent:0.##}% de {ContractService.Money(c.BaseAmount)}). Enviada para a sua chave Pix."); }
        catch (Exception ex) when (ex is IntegrationException or ArgumentException) { /* best effort */ }
    }
}
