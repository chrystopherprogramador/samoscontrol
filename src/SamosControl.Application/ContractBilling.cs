using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record PlanItem(string Kind, int Sequence, decimal Amount, DateTime Due, string Description);

/// <summary>Pure calculation of the charges a contract implies (no I/O).</summary>
public static class BillingPlan
{
    public const string Implementation = "IMPLANTACAO";
    public const string Monthly = "MENSALIDADE";

    public static decimal NetImplementation(Contract c) => Math.Max(0, c.ImplementationValue - c.DiscountValue);

    /// <summary>Splits in cents so the installments always sum exactly to the total (remainder goes to the first ones).</summary>
    public static decimal[] Split(decimal total, int parts)
    {
        parts = Math.Max(1, parts);
        var cents = (long)Math.Round(total * 100m, MidpointRounding.AwayFromZero);
        var baseCents = cents / parts; var rest = (int)(cents % parts);
        return Enumerable.Range(0, parts).Select(i => (baseCents + (i < rest ? 1 : 0)) / 100m).ToArray();
    }

    public static DateTime MonthlyDue(Contract c, int seq) => c.MonthlyStart!.Value.Date.AddMonths(seq - 1);

    /// <summary>Full plan. Monthly items are limited to the term (or to <paramref name="indefiniteHorizon"/> items when the term is indefinite).</summary>
    public static List<PlanItem> Build(Contract c, DateTime? monthlyUntil = null)
    {
        var items = new List<PlanItem>();
        var net = NetImplementation(c);
        if (net > 0 && c.ImplementationFirstDue.HasValue)
        {
            var n = Math.Max(1, c.ImplementationInstallments);
            var parts = Split(net, n);
            for (var i = 0; i < n; i++)
                items.Add(new PlanItem(Implementation, i + 1, parts[i], c.ImplementationFirstDue.Value.Date.AddMonths(i),
                    $"Implantação - {c.ServiceName ?? c.Title} (parcela {i + 1}/{n})"));
        }
        if (c.MonthlyValue > 0 && c.MonthlyStart.HasValue)
        {
            var total = c.MonthlyMonths ?? int.MaxValue;
            for (var s = 1; s <= total; s++)
            {
                var due = MonthlyDue(c, s);
                if (monthlyUntil.HasValue && due > monthlyUntil.Value.Date) break;
                if (!c.MonthlyMonths.HasValue && monthlyUntil is null) break; // indefinite needs a horizon
                items.Add(new PlanItem(Monthly, s, c.MonthlyValue, due,
                    $"Mensalidade - {c.ServiceName ?? c.Title} ({due:MM/yyyy})"));
            }
        }
        return items;
    }
}

public record ContractBillingResult(int Created, int Failed, bool Closed);

/// <summary>Turns a signed contract into Asaas charges. Idempotent: (contract, kind, sequence) is unique.</summary>
public class ContractBillingService
{
    public const int HorizonDays = 45;
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public ContractBillingService(AppDbContext db, BillingService billing, AuditService audit, NotificationService notify)
    { _db = db; _billing = billing; _audit = audit; _notify = notify; }

    public async Task<ContractBillingResult> GenerateAsync(int contractId, CancellationToken ct = default)
    {
        var c = await _db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new KeyNotFoundException();
        if (c.Status != ContractStatus.Assinado || c.BillingClosed || c.CustomerId is null) return new(0, 0, c.BillingClosed);

        var today = AppTime.Now.Date;
        var horizon = today.AddDays(HorizonDays);
        var wanted = BillingPlan.Build(c, horizon);
        // Implementation installments are always created in full (future-dated); monthly ones only inside the horizon.
        var existing = (await _db.Charges.Where(x => x.ContractId == contractId && x.Kind != "AVULSA").Select(x => new { x.Kind, x.Sequence }).ToListAsync(ct))
            .Select(x => (x.Kind, x.Sequence)).ToHashSet();

        int created = 0, failed = 0;
        foreach (var item in wanted.Where(w => !existing.Contains((w.Kind, w.Sequence))))
        {
            try
            {
                var due = item.Due < today ? today : item.Due;
                await _billing.CreateChargeAsync(c.CustomerId.Value, c.Id, c.ServiceName, item.Amount, due, c.BillingType, item.Description, ct, item.Kind, item.Sequence);
                created++;
            }
            catch (Exception ex) when (ex is IntegrationException or ArgumentException or DbUpdateException)
            {
                failed++;
                _db.ChangeTracker.Clear();
                _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, Type = "COBRANCA_FALHOU", Details = $"{item.Kind} {item.Sequence}: {ex.Message.Truncate(300)}" });
                await _db.SaveChangesAsync(ct);
            }
        }

        // Closed once the full plan (implementation + finite monthly term) exists as charges.
        var closed = false;
        if (failed == 0 && (c.MonthlyValue <= 0 || c.MonthlyMonths.HasValue))
        {
            var full = BillingPlan.Build(c, DateTime.MaxValue.Date);
            var have = (await _db.Charges.Where(x => x.ContractId == contractId && x.Kind != "AVULSA").Select(x => new { x.Kind, x.Sequence }).ToListAsync(ct)).Select(x => (x.Kind, x.Sequence)).ToHashSet();
            closed = full.All(f => have.Contains((f.Kind, f.Sequence)));
        }
        c = await _db.Contracts.FirstAsync(x => x.Id == contractId, ct);
        if (created > 0 || closed != c.BillingClosed)
        {
            c.BillingClosed = closed;
            if (created > 0) _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, Type = "COBRANCAS_GERADAS", Details = $"{created} cobrança(s) criada(s)" });
            await _db.SaveChangesAsync(ct);
            if (created > 0) await _audit.LogAsync("contracts", "generate_charges", "Contract", c.Id, newData: $"created={created}");
        }
        if (failed > 0)
            await _notify.CreateAsync(NotificationType.Error, NotificationSource.Billing, "Falha ao gerar cobranças do contrato", $"{c.Title}: {failed} cobrança(s) não criada(s). Será tentado novamente.", $"/Contracts/Details/{c.Id}");
        return new(created, failed, closed);
    }

    /// <summary>Daily job: extends monthly charges of signed contracts and retries failures.</summary>
    public async Task<int> GenerateDueAsync(CancellationToken ct = default)
    {
        var ids = await _db.Contracts.Where(c => c.Status == ContractStatus.Assinado && !c.BillingClosed).Select(c => c.Id).ToListAsync(ct);
        var total = 0;
        foreach (var id in ids) total += (await GenerateAsync(id, ct)).Created;
        return total;
    }

    /// <summary>Stops recurring billing and cancels charges not yet due. Paid/past-due charges are kept.</summary>
    public async Task<int> EndBillingAsync(int contractId, CancellationToken ct = default)
    {
        var c = await _db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new KeyNotFoundException();
        if (c.Status != ContractStatus.Assinado) throw new InvalidOperationException("Somente contratos assinados possuem cobrança recorrente.");
        c.BillingClosed = true;
        var today = DateTime.SpecifyKind(AppTime.Now.Date, DateTimeKind.Utc);
        var future = await _db.Charges.Where(x => x.ContractId == contractId && x.Kind == BillingPlan.Monthly && x.Status == ChargeStatus.Pendente && x.DueDate > today).Select(x => x.Id).ToListAsync(ct);
        var cancelled = 0;
        foreach (var id in future)
        {
            try { await _billing.CancelAsync(id, ct); cancelled++; } catch (InvalidOperationException) { }
        }
        _db.ContractEvents.Add(new ContractEvent { ContractId = contractId, Type = "COBRANCA_ENCERRADA", Details = $"{cancelled} cobrança(s) futura(s) cancelada(s)" });
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("contracts", "end_billing", "Contract", contractId, newData: $"cancelled={cancelled}");
        return cancelled;
    }
}
