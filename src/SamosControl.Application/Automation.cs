using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Application.Voice;
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
                // Each step is isolated: a failure in one (e.g. Axis offline) must never prevent charges from being generated or notices from being sent.
                var errors = new List<string>();
                async Task<T> Step<T>(string name, Func<Task<T>> run, T fallback)
                {
                    try { return await run(); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add(name);
                        log($"Falha em '{name}': {ex.Message}", "ERROR");
                        _log.LogError(ex, "CheckBilling: etapa {Step} falhou", name);
                        return fallback;
                    }
                }
                var n = await Step("vencidas", () => _sp.GetRequiredService<BillingService>().MarkOverdueAsync(), 0);
                L($"Cobranças marcadas como vencidas: {n}");
                var paid = await Step("atualização Efí", () => _sp.GetRequiredService<BillingService>().RefreshOpenEfiAsync(ct), 0);
                L($"Cobranças Efí atualizadas: {paid}");
                var lic = await Step("licenças do Axis", () => _sp.GetRequiredService<AxisBridgeService>().GenerateLicenseChargesAsync(ct), 0);
                if (lic > 0) L($"Cobranças de licenças do Axis geradas: {lic}");
                var axis = await Step("baixa no Axis", () => _sp.GetRequiredService<AxisBridgeService>().SyncPaidAsync(ct), 0);
                if (axis > 0) L($"Mensalidades confirmadas no Axis: {axis}");
                if (n > 0) await Step("notificação de vencidas", async () => { await _sp.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Warning, NotificationSource.Billing, $"{n} cobranças vencidas", null, "/Charges"); return 0; }, 0);
                var gen = await Step("contratos", () => _sp.GetRequiredService<ContractBillingService>().GenerateDueAsync(ct), 0);
                L($"Cobranças de contratos geradas: {gen}");
                var notices = await Step("avisos de cobrança", () => _sp.GetRequiredService<ChargeNoticeService>().RunAsync(ct), new NoticeRunResult(0, 0, 0, null));
                if (notices.Note != null) L("Avisos de cobrança: " + notices.Note);
                L($"Avisos de cobrança: {notices.Sent} enviado(s), {notices.Failed} com erro, {notices.Skipped} ignorado(s) (sem contato, canal não configurado, já enviado ou tentativas esgotadas)");
                var com = await Step("comissões", () => _sp.GetRequiredService<CommissionService>().ProcessAsync(ct), new CommissionRun(0, 0, 0, 0));
                L($"Comissões: {com.Created} gerada(s), {com.Paid} paga(s), {com.Failed} com erro, {com.Cancelled} cancelada(s)");
                if (errors.Count > 0) throw new InvalidOperationException("Etapas com falha: " + string.Join(", ", errors) + ". Veja o log da execução.");
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
            case JobType.PlaceCall: return await PlaceCallAsync(job, cfg, log, ct);
            default: throw new NotSupportedException("Tipo de job desconhecido.");
        }
    }

    public const int CallMaxLateMinutes = 15;

    /// <summary>
    /// Ligação agendada da Roberta para o próprio dono. Máquina de estados persistida em ConfigJson (DIAL → CHECK → fim/retry),
    /// então sobrevive a reinício. A discagem usa chave idempotente por job+tentativa: nenhuma ligação duplicada.
    /// </summary>
    private async Task<string> PlaceCallAsync(AutomationJob job, JsonObject cfg, Action<string, string> log, CancellationToken ct)
    {
        string? C(string k) => (cfg[k] as JsonValue)?.ToString();
        int I(string k, int d) => int.TryParse(C(k), out var n) ? n : d;
        var now = DateTime.UtcNow;
        int? userId = job.CreatedByUserId;
        if (userId == null && int.TryParse(C("userId"), out var u)) userId = u;
        var msg = C("message") ?? "";
        var attempt = I("attempt", 1); var max = Math.Clamp(I("maxAttempts", 3), 1, 5); var retryMin = Math.Clamp(I("retryMinutes", 5), 1, 60);
        var phase = C("phase") ?? "DIAL";
        var user = userId.HasValue ? await _db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, ct) : null;
        if (user == null || string.IsNullOrWhiteSpace(user.Phone)) { job.IsEnabled = false; throw new InvalidOperationException("Usuário dono da ligação sem telefone cadastrado ou inativo."); }
        var voice = _sp.GetRequiredService<VoiceCallManager>();

        string? failure = null;
        if (phase == "CHECK")
        {
            var callId = I("callId", 0);
            var call = await _db.VoiceCalls.AsNoTracking().FirstOrDefaultAsync(x => x.Id == callId, ct);
            if (call == null) failure = "registro da ligação não encontrado";
            else if (call.State is not (VoiceCallState.Completed or VoiceCallState.Failed))
            {
                if (now - call.CreatedAt < TimeSpan.FromMinutes(15)) { job.NextRunAt = now.AddSeconds(30); return "Ligação em andamento; verificação em 30 s."; }
                failure = "ligação sem desfecho após 15 min";
            }
            else if (call.ConnectedAt != null) { job.IsEnabled = false; job.NextRunAt = null; return $"Ligação atendida ({call.DurationSeconds}s)."; }
            else failure = call.EndReason == null ? "não atendida" : $"não atendida ({call.EndReason})";
        }
        else
        {
            var scheduled = DateTime.TryParse(C("scheduledFor"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var s) ? s.ToUniversalTime() : now;
            if (attempt == 1 && now - scheduled > TimeSpan.FromMinutes(CallMaxLateMinutes))
            {
                await FallbackAsync(job, user, msg, "o sistema estava indisponível no horário combinado", log, ct);
                job.IsEnabled = false; job.NextRunAt = null;
                throw new InvalidOperationException($"Ligação não realizada: horário combinado passou há mais de {CallMaxLateMinutes} min (aviso enviado por WhatsApp).");
            }
            var r = await voice.StartTestCallAsync(user.Phone!, user.Id, "Roberta (agendada)", $"job{job.Id}-a{attempt}", ct, purpose: msg);
            if (r.Ok && r.CallId.HasValue)
            {
                cfg["phase"] = "CHECK"; cfg["callId"] = r.CallId.Value;
                job.ConfigJson = cfg.ToJsonString();
                job.NextRunAt = now.AddSeconds(voice.Limits.RingSeconds + 25);
                return $"Ligação #{r.CallId} iniciada (tentativa {attempt}/{max}).";
            }
            failure = r.Error ?? "falha ao iniciar a ligação";
        }

        log($"Tentativa {attempt}/{max} sem sucesso: {failure}", "WARN");
        if (attempt < max)
        {
            cfg["attempt"] = attempt + 1; cfg["phase"] = "DIAL"; cfg.Remove("callId");
            job.ConfigJson = cfg.ToJsonString();
            job.NextRunAt = now.AddMinutes(retryMin);
            return $"Tentativa {attempt}/{max} falhou ({failure}). Nova tentativa em {retryMin} min.";
        }
        await FallbackAsync(job, user, msg, failure!, log, ct);
        job.IsEnabled = false; job.NextRunAt = null;
        throw new InvalidOperationException($"Ligação não concluída após {max} tentativa(s): {failure}. Aviso enviado por WhatsApp.");
    }

    private async Task FallbackAsync(AutomationJob job, User user, string msg, string reason, Action<string, string> log, CancellationToken ct)
    {
        var wa = _sp.GetRequiredService<WhatsAppService>();
        if (job.Config("fallbackWhatsApp") != "false" && wa.IsConfigured)
        {
            try { await wa.SendTextAsync(user.Phone!, $"📞 Não consegui te ligar ({reason}). Lembrete: {msg}", null, fromAgent: true, ct); log("Aviso alternativo enviado por WhatsApp", "INFO"); }
            catch (Exception ex) { log("Falha no aviso por WhatsApp: " + ex.Message, "ERROR"); }
        }
        await _sp.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Warning, NotificationSource.Automation, "Ligação agendada não concluída", $"{msg.Truncate(150)} — {reason}", "/Voice", user.Id);
    }
}

internal static class JobConfigExt
{
    public static string? Config(this AutomationJob j, string key)
    {
        try { return string.IsNullOrWhiteSpace(j.ConfigJson) ? null : (JsonNode.Parse(j.ConfigJson) as JsonObject)?[key]?.ToString(); } catch { return null; }
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
                await scope.ServiceProvider.GetRequiredService<PersonalService>().RunRemindersAsync(ct);
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
