using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Application;

public class DashboardData
{
    public int LeadsToday, LeadsMonth, ActiveCustomers, ContractsPending, ContractsSigned;
    public int ChargesOpen, ChargesOverdue, ServicesOnline, ServicesDegraded, ServicesOffline, ServicesTotal;
    public int MessagesToday, AutomationsToday, FailuresRecent;
    public decimal AmountOpen, AmountOverdue, ReceivedMonth;
    public Dictionary<string, int> LeadsByStatus = new();
    public Dictionary<string, int> LeadsPerDay = new();
    public Dictionary<string, int> ChargesByStatus = new();
    public List<(string Name, double? Uptime)> Uptime = new();
    public List<string> RecentFailures = new();
}

public class DashboardService
{
    private readonly AppDbContext _db;
    private readonly MonitoringService _mon;
    public DashboardService(AppDbContext db, MonitoringService mon) { _db = db; _mon = mon; }

    public async Task<DashboardData> GetAsync()
    {
        var today = AppTime.TodayStartUtc;
        var month = AppTime.MonthStartUtc;
        var d = new DashboardData
        {
            LeadsToday = await _db.Leads.CountAsync(l => l.CreatedAt >= today),
            LeadsMonth = await _db.Leads.CountAsync(l => l.CreatedAt >= month),
            ActiveCustomers = await _db.Customers.CountAsync(c => c.IsActive),
            ContractsPending = await _db.Contracts.CountAsync(c => c.Status == ContractStatus.AguardandoAssinatura),
            ContractsSigned = await _db.Contracts.CountAsync(c => c.Status == ContractStatus.Assinado),
            ChargesOpen = await _db.Charges.CountAsync(c => c.Status == ChargeStatus.Pendente),
            ChargesOverdue = await _db.Charges.CountAsync(c => c.Status == ChargeStatus.Vencido),
            AmountOpen = await _db.Charges.Where(c => c.Status == ChargeStatus.Pendente).SumAsync(c => (decimal?)c.Amount) ?? 0,
            AmountOverdue = await _db.Charges.Where(c => c.Status == ChargeStatus.Vencido).SumAsync(c => (decimal?)c.Amount) ?? 0,
            ReceivedMonth = await _db.Charges.Where(c => c.Status == ChargeStatus.Pago && c.PaidAt >= month).SumAsync(c => (decimal?)(c.PaidAmount ?? c.Amount)) ?? 0,
            ServicesTotal = await _db.Services.CountAsync(s => s.IsEnabled),
            ServicesOnline = await _db.Services.CountAsync(s => s.IsEnabled && s.Status == ServiceStatus.Online),
            ServicesDegraded = await _db.Services.CountAsync(s => s.IsEnabled && s.Status == ServiceStatus.Degraded),
            ServicesOffline = await _db.Services.CountAsync(s => s.IsEnabled && s.Status == ServiceStatus.Offline),
            MessagesToday = await _db.WhatsAppMessages.CountAsync(m => m.Timestamp >= today),
            AutomationsToday = await _db.AutomationExecutions.CountAsync(e => e.StartedAt >= today),
            FailuresRecent = await _db.AutomationExecutions.CountAsync(e => e.Status == ExecutionStatus.Failed && e.StartedAt >= DateTime.UtcNow.AddDays(-7))
        };
        foreach (var g in await _db.Leads.GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync()) d.LeadsByStatus[g.Key.ToString()] = g.N;
        foreach (var s in Enum.GetValues<LeadStatus>()) d.LeadsByStatus.TryAdd(s.ToString(), 0);
        d.LeadsByStatus = Enum.GetValues<LeadStatus>().ToDictionary(s => s.ToString(), s => d.LeadsByStatus[s.ToString()]);
        foreach (var g in await _db.Charges.GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync()) d.ChargesByStatus[g.Key.ToString()] = g.N;

        var from = AppTime.ToUtc(AppTime.Now.Date.AddDays(-13));
        var dates = await _db.Leads.Where(l => l.CreatedAt >= from).Select(l => l.CreatedAt).ToListAsync();
        for (var i = 13; i >= 0; i--)
        {
            var day = AppTime.Now.Date.AddDays(-i);
            d.LeadsPerDay[day.ToString("dd/MM")] = dates.Count(x => AppTime.ToLocal(x).Date == day);
        }
        foreach (var s in await _db.Services.Where(s => s.IsEnabled).OrderBy(s => s.Name).Take(12).ToListAsync())
            d.Uptime.Add((s.Name, await _mon.UptimeAsync(s.Id, 7)));

        d.RecentFailures = (await _db.AutomationExecutions.Include(e => e.Job).Where(e => e.Status == ExecutionStatus.Failed).OrderByDescending(e => e.StartedAt).Take(5).ToListAsync())
            .Select(e => $"{AppTime.ToLocal(e.StartedAt):dd/MM HH:mm} • {e.Job?.Name}: {e.Result}").ToList();
        d.RecentFailures.AddRange((await _db.Incidents.Include(i => i.Service).Where(i => i.ResolvedAt == null).Take(5).ToListAsync())
            .Select(i => $"{AppTime.ToLocal(i.StartedAt):dd/MM HH:mm} • Incidente aberto: {i.Service?.Name}"));
        return d;
    }

    public async Task<string> SummaryTextAsync()
    {
        var d = await GetAsync();
        var contactedToday = await _db.Leads.CountAsync(l => l.CreatedAt >= AppTime.TodayStartUtc && l.Status != LeadStatus.Novo);
        var jobs = await _db.AutomationJobs.CountAsync(j => j.IsEnabled && j.NextRunAt != null);
        var br = new System.Globalization.CultureInfo("pt-BR");
        return $"📊 Resumo SAMOS — {AppTime.Now:dd/MM HH:mm}\n" +
               $"• Leads hoje: {d.LeadsToday} ({contactedToday} já contatados) | mês: {d.LeadsMonth}\n" +
               $"• Clientes ativos: {d.ActiveCustomers}\n" +
               $"• Contratos: {d.ContractsPending} aguardando assinatura, {d.ContractsSigned} assinados\n" +
               $"• Cobranças: {d.ChargesOpen} abertas ({d.AmountOpen.ToString("C2", br)}), {d.ChargesOverdue} vencidas ({d.AmountOverdue.ToString("C2", br)})\n" +
               $"• Recebido no mês: {d.ReceivedMonth.ToString("C2", br)}\n" +
               $"• Serviços: {d.ServicesOnline} online, {d.ServicesDegraded} degradados, {d.ServicesOffline} offline\n" +
               $"• Tarefas agendadas: {jobs}";
    }
}
