using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public class AutomationService
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _sp;
    private readonly ILogger<AutomationService> _log;
    public AutomationService(AppDbContext db, IServiceProvider sp, ILogger<AutomationService> log) { _db = db; _sp = sp; _log = log; }

    public async Task<AutomationJob> CreateAsync(string name, JobType type, string? configJson, DateTime? nextRunUtc, int? recurrenceMinutes, int? userId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Informe o nome.");
        if (recurrenceMinutes is < 1) throw new ArgumentException("Recorrência inválida.");
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try { JsonNode.Parse(configJson); } catch (JsonException) { throw new ArgumentException("Configuração JSON inválida."); }
        }
        var j = new AutomationJob { Name = name.Trim(), Type = type, ConfigJson = string.IsNullOrWhiteSpace(configJson) ? null : configJson, NextRunAt = nextRunUtc ?? (recurrenceMinutes.HasValue ? DateTime.UtcNow.AddMinutes(recurrenceMinutes.Value) : null), RecurrenceMinutes = recurrenceMinutes, CreatedByUserId = userId };
        _db.AutomationJobs.Add(j);
        await _db.SaveChangesAsync();
        return j;
    }

    private static DateTime? NextAfter(AutomationJob j, DateTime now) =>
        j.RecurrenceMinutes is > 0 ? now.AddMinutes(j.RecurrenceMinutes.Value) : null;

    /// <summary>Runs every enabled job whose NextRunAt is due. The next run is reserved before executing to prevent double runs.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = await _db.AutomationJobs.Where(j => j.IsEnabled && j.NextRunAt != null && j.NextRunAt <= now).OrderBy(j => j.NextRunAt).Take(20).ToListAsync(ct);
        foreach (var j in due) j.NextRunAt = NextAfter(j, now);
        await _db.SaveChangesAsync(ct);
        foreach (var j in due) await RunJobAsync(j.Id, "SCHEDULE", ct);
        return due.Count;
    }

    public async Task<AutomationExecution> RunJobAsync(int jobId, string trigger, CancellationToken ct = default)
    {
        var job = await _db.AutomationJobs.FindAsync(new object[] { jobId }, ct) ?? throw new KeyNotFoundException("Job não encontrado.");
        var exec = new AutomationExecution { JobId = jobId, Trigger = trigger };
        _db.AutomationExecutions.Add(exec);
        await _db.SaveChangesAsync(ct);
        void Log(string msg, string level = "INFO") => _db.AutomationLogs.Add(new AutomationLog { ExecutionId = exec.Id, Level = level, Message = msg.Truncate(2000) });
        try
        {
            Log($"Iniciando {job.Type}");
            exec.Result = await ExecuteAsync(job, Log, ct);
            exec.Status = ExecutionStatus.Success;
            Log("Concluído: " + exec.Result);
        }
        catch (Exception ex)
        {
            exec.Status = ExecutionStatus.Failed;
            exec.Result = ex.Message.Truncate(1000);
            Log(ex.Message, "ERROR");
            _log.LogError(ex, "Job {Job} falhou", job.Name);
            try { await _sp.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Error, NotificationSource.Automation, $"Automação falhou: {job.Name}", ex.Message.Truncate(300), "/Automation"); } catch { }
        }
        exec.FinishedAt = DateTime.UtcNow;
        job.LastRunAt = exec.FinishedAt; job.LastStatus = exec.Status;
        await _db.SaveChangesAsync(CancellationToken.None);
        return exec;
    }

    private async Task<string> ExecuteAsync(AutomationJob job, Action<string, string> log, CancellationToken ct)
    {
        var cfg = string.IsNullOrWhiteSpace(job.ConfigJson) ? new JsonObject() : JsonNode.Parse(job.ConfigJson)!.AsObject();
        string? C(string k) => (cfg[k] as JsonValue)?.ToString();
        void L(string m) => log(m, "INFO");
        switch (job.Type)
        {
            case JobType.SyncLeads:
            {
                var r = await _sp.GetRequiredService<LeadSyncService>().SyncAsync(ct);
                var waiting = await _db.ScraperSearches.CountAsync(s => s.Status == "ATIVA", ct);
                var done = await _db.ScraperSearches.CountAsync(s => s.Status == "CONCLUIDA", ct);
                L($"Pesquisas: {waiting} aguardando o scraper, {done} já importadas.");
                if (!string.IsNullOrEmpty(r.Message)) log(r.Message, r.Status == "OK" ? "INFO" : "WARN");
                var hint = r.Status == "NAO_CONFIGURADO" ? " Configure em Configurações → Google Scraper."
                    : r.Found == 0 && waiting > 0 ? $" Nada novo ainda: {waiting} pesquisa(s) seguem em andamento no scraper."
                    : r.Found == 0 ? " Nenhuma pesquisa ativa. Crie uma em Leads → Pesquisas." : "";
                return $"{r.Status}: encontrados={r.Found}, criados={r.Created}, atualizados={r.Updated}, erros={r.Errors}.{hint}";
            }
            case JobType.CheckServices:
            {
                var n = await _sp.GetRequiredService<MonitoringService>().RunDueChecksAsync(force: true, ct);
                if (n == 0) return "Nenhum serviço ativo para verificar. Cadastre em Monitoramento.";
                var st = await _db.Services.Where(s => s.IsEnabled).GroupBy(s => s.Status).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
                var sum = string.Join(", ", st.Select(x => $"{x.Key}={x.C}"));
                L("Situação atual: " + sum);
                return $"{n} serviços verificados ({sum})";
            }
            case JobType.CheckBilling:
            {
                var n = await _sp.GetRequiredService<BillingService>().MarkOverdueAsync();
                L($"Cobranças marcadas como vencidas: {n}");
                var paid = await _sp.GetRequiredService<BillingService>().RefreshOpenEfiAsync(ct);
                L($"Cobranças Efí atualizadas: {paid}");
                if (n > 0) await _sp.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Warning, NotificationSource.Billing, $"{n} cobranças vencidas", null, "/Charges");
                var gen = await _sp.GetRequiredService<ContractBillingService>().GenerateDueAsync(ct);
                L($"Cobranças de contratos geradas: {gen}");
                var com = await _sp.GetRequiredService<CommissionService>().ProcessAsync(ct);
                L($"Comissões: {com.Created} gerada(s), {com.Paid} paga(s), {com.Failed} com erro, {com.Cancelled} cancelada(s)");
                return $"{n} cobranças marcadas como vencidas; {gen} cobrança(s) de contratos geradas; {paid} cobrança(s) Efí atualizadas; comissões: {com.Created} geradas, {com.Paid} pagas";
            }
            case JobType.SendWhatsApp:
            {
                var phone = C("phone") ?? throw new InvalidOperationException("Config 'phone' ausente.");
                var text = C("text") ?? throw new InvalidOperationException("Config 'text' ausente.");
                await _sp.GetRequiredService<WhatsAppService>().SendTextAsync(phone, text, job.CreatedByUserId);
                return "Mensagem enviada";
            }
            case JobType.Reminder:
            {
                var msg = C("message") ?? C("text") ?? job.Name;
                var userId = int.TryParse(C("userId"), out var u) ? u : job.CreatedByUserId;
                await _sp.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Info, NotificationSource.Automation, "Lembrete: " + msg.Truncate(150), msg, null, userId);
                var wa = _sp.GetRequiredService<WhatsAppService>();
                var phone = userId.HasValue ? await _db.Users.Where(x => x.Id == userId).Select(x => x.Phone).FirstOrDefaultAsync(ct) : null;
                if (phone != null && wa.IsConfigured) { await wa.SendTextAsync(phone, "⏰ Lembrete SAMOS: " + msg, null, fromAgent: true, ct); L("Lembrete enviado por WhatsApp"); }
                return "Lembrete criado";
            }
            case JobType.GenerateReport:
            {
                var text = await _sp.GetRequiredService<DashboardService>().SummaryTextAsync();
                await _sp.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Info, NotificationSource.Automation, "Relatório diário gerado", text, "/");
                var wa = _sp.GetRequiredService<WhatsAppService>();
                var sent = 0;
                if (wa.IsConfigured)
                    foreach (var p in (C("phones") ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    { await wa.SendTextAsync(p, text, job.CreatedByUserId, fromAgent: true, ct); sent++; }
                L(text);
                if (sent == 0) L(wa.IsConfigured ? "Nenhum número em 'phones': relatório só na notificação do painel." : "WhatsApp não configurado: relatório só na notificação do painel.");
                return $"Relatório gerado (veja em Notificações); enviado a {sent} número(s)";
            }
            default: throw new NotSupportedException("Tipo de job desconhecido.");
        }
    }
}

/// <summary>Polls the database for due jobs. Run a single instance (Web with RUN_WORKERS=true OR the Worker container).</summary>
public class SchedulerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SchedulerWorker> _log;
    public SchedulerWorker(IServiceScopeFactory scopes, ILogger<SchedulerWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AutomationService>().RunDueAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Erro no agendador"); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ContinueWith(_ => { });
        }
    }
}

public class MonitoringWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MonitoringWorker> _log;
    public MonitoringWorker(IServiceScopeFactory scopes, ILogger<MonitoringWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        var lastPurge = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var mon = scope.ServiceProvider.GetRequiredService<MonitoringService>();
                await mon.RunDueChecksAsync(false, ct);
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(6)) { await mon.PurgeOldChecksAsync(); lastPurge = DateTime.UtcNow; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Erro no monitoramento"); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ContinueWith(_ => { });
        }
    }
}
