using System.Net.Security;
using System.Net.Sockets;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record ProbeResult(ServiceStatus Status, int? StatusCode, int? LatencyMs, DateTime? SslExpiresAt, string? Error);

public class MonitoringService
{
    public const int SslWarnDays = 14;
    private const int FlapGuardMinutes = 5;
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly SettingsService _settings;
    private readonly WhatsAppService _wa;
    private readonly NotificationService _notify;
    private readonly ILogger<MonitoringService> _log;

    public MonitoringService(AppDbContext db, IHttpClientFactory http, SettingsService settings, WhatsAppService wa, NotificationService notify, ILogger<MonitoringService> log)
    { _db = db; _http = http; _settings = settings; _wa = wa; _notify = notify; _log = log; }

    // ---------- Probing (no database access) ----------
    public async Task<ProbeResult> ProbeAsync(MonitoredService s, string serverHost, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(s.TimeoutMs, 500, 60000));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (s.Kind == ServiceKind.Tcp)
            {
                if (s.Port is null) return new(ServiceStatus.Offline, null, null, null, "Porta não informada.");
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(serverHost, s.Port.Value, cts.Token);
                var lat = (int)sw.ElapsedMilliseconds;
                return new(lat > s.DegradedLatencyMs ? ServiceStatus.Degraded : ServiceStatus.Online, null, lat, null, null);
            }

            if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return new(ServiceStatus.Offline, null, null, null, "URL inválida.");
            var client = _http.CreateClient("monitor");
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("SamosControl-Monitor/1.0");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var latency = (int)sw.ElapsedMilliseconds;
            var code = (int)res.StatusCode;
            var ok = s.ExpectedStatus > 0 ? code == s.ExpectedStatus : code < 400;
            DateTime? ssl = null;
            if (uri.Scheme == "https") ssl = await GetSslExpiryAsync(uri.Host, uri.Port, timeout, ct);
            if (!ok) return new(ServiceStatus.Offline, code, latency, ssl, $"Status HTTP inesperado: {code}");
            var degraded = latency > s.DegradedLatencyMs || (ssl.HasValue && ssl.Value < DateTime.UtcNow.AddDays(SslWarnDays));
            return new(degraded ? ServiceStatus.Degraded : ServiceStatus.Online, code, latency, ssl,
                ssl.HasValue && ssl.Value < DateTime.UtcNow.AddDays(SslWarnDays) ? "Certificado SSL expira em breve." : latency > s.DegradedLatencyMs ? "Latência alta." : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ServiceStatus.Offline, null, null, null, "Timeout.");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return new(ServiceStatus.Offline, null, null, null, (ex.InnerException?.Message ?? ex.Message).Truncate(300));
        }
    }

    private static async Task<DateTime?> GetSslExpiryAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            DateTime? expiry = null;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => { if (cert != null) expiry = DateTime.SpecifyKind(((System.Security.Cryptography.X509Certificates.X509Certificate2)new System.Security.Cryptography.X509Certificates.X509Certificate2(cert)).NotAfter.ToUniversalTime(), DateTimeKind.Utc); return true; });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return expiry;
        }
        catch { return null; }
    }

    // ---------- State machine ----------
    public async Task<ServiceCheck> CheckAsync(MonitoredService s, ProbeResult? probe = null, CancellationToken ct = default)
    {
        await _db.Entry(s).Reference(x => x.Server).LoadAsync(ct);
        probe ??= await ProbeAsync(s, s.Server!.Host, ct);
        return await ApplyAsync(s, probe);
    }

    public async Task<ServiceCheck> ApplyAsync(MonitoredService s, ProbeResult r)
    {
        var now = DateTime.UtcNow;
        var check = new ServiceCheck { ServiceId = s.Id, At = now, Status = r.Status, StatusCode = r.StatusCode, LatencyMs = r.LatencyMs, SslExpiresAt = r.SslExpiresAt, Error = r.Error };
        _db.ServiceChecks.Add(check);
        s.LastCheckAt = now; s.LastLatencyMs = r.LatencyMs; s.SslExpiresAt = r.SslExpiresAt ?? s.SslExpiresAt;

        var incident = await _db.Incidents.Where(i => i.ServiceId == s.Id && i.ResolvedAt == null).FirstOrDefaultAsync();
        var name = s.Server?.Name ?? (await _db.Servers.Where(x => x.Id == s.ServerId).Select(x => x.Name).FirstOrDefaultAsync()) ?? "-";

        if (r.Status == ServiceStatus.Offline)
        {
            s.ConsecutiveFailures++;
            if (s.ConsecutiveFailures >= Math.Max(1, s.FailureThreshold))
            {
                s.Status = ServiceStatus.Offline;
                var cooldown = Math.Max(5, int.TryParse(_settings.Get(SettingKeys.AlertCooldownMinutes), out var c) ? c : 60);
                if (incident is null)
                {
                    incident = new Incident { ServiceId = s.Id, StartedAt = now, Description = r.Error };
                    _db.Incidents.Add(incident);
                    var suppress = s.LastAlertAt.HasValue && (now - s.LastAlertAt.Value).TotalMinutes < FlapGuardMinutes;
                    await _db.SaveChangesAsync();
                    await _notify.CreateAsync(NotificationType.Error, NotificationSource.Monitoring, $"Serviço offline: {s.Name}", $"{name} • {r.Error}", "/Monitoring/Incidents");
                    if (!suppress) await AlertAsync(s, incident, $"🚨 SAMOS CONTROL\n\nServiço indisponível:\n\nServidor: {name}\nServiço: {s.Name}\nStatus: OFFLINE\nHorário: {AppTime.Now:HH:mm}\n{(r.Error != null ? "Motivo: " + r.Error : "")}".TrimEnd());
                }
                else if (s.LastAlertAt.HasValue && (now - s.LastAlertAt.Value).TotalMinutes >= cooldown)
                {
                    var mins = (int)(now - incident.StartedAt).TotalMinutes;
                    await AlertAsync(s, incident, $"🚨 SAMOS CONTROL\n\nServiço continua indisponível há {mins} min:\n\nServidor: {name}\nServiço: {s.Name}\nStatus: OFFLINE");
                }
            }
        }
        else
        {
            s.ConsecutiveFailures = 0;
            s.Status = r.Status;
            if (incident != null)
            {
                incident.ResolvedAt = now;
                await _db.SaveChangesAsync();
                await _notify.CreateAsync(NotificationType.Success, NotificationSource.Monitoring, $"Serviço restaurado: {s.Name}", name, "/Monitoring/Incidents");
                if (incident.AlertSentAt != null)
                {
                    var dur = (int)(now - incident.StartedAt).TotalMinutes;
                    if (await SendAlertAsync($"✅ SAMOS CONTROL\n\nServiço restaurado.\n\nServidor: {name}\nServiço: {s.Name}\nStatus: {r.Status.ToString().ToUpperInvariant()}\nIndisponível por {dur} min."))
                        incident.RecoveryAlertSentAt = now;
                }
            }
        }
        await _db.SaveChangesAsync();
        return check;
    }

    private async Task AlertAsync(MonitoredService s, Incident inc, string text)
    {
        if (await SendAlertAsync(text)) { inc.AlertSentAt ??= DateTime.UtcNow; s.LastAlertAt = DateTime.UtcNow; }
        else { s.LastAlertAt = DateTime.UtcNow; } // dedupe even when WhatsApp is unavailable
    }

    private async Task<bool> SendAlertAsync(string text)
    {
        var numbers = (_settings.Get(SettingKeys.AlertNumbers) ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length == 0 || !_wa.IsConfigured) return false;
        var sent = false;
        foreach (var n in numbers)
        {
            try { await _wa.SendTextAsync(n, text, null, fromAgent: true); sent = true; }
            catch (Exception ex) { _log.LogWarning("Falha ao enviar alerta WhatsApp: {Msg}", ex.Message); }
        }
        return sent;
    }

    // ---------- Scheduling ----------
    public async Task<int> RunDueChecksAsync(bool force = false, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var services = await _db.Services.Include(s => s.Server).Where(s => s.IsEnabled).ToListAsync(ct);
        var due = services.Where(s => force || s.LastCheckAt == null || s.LastCheckAt.Value.AddSeconds(Math.Max(15, s.IntervalSeconds)) <= now).ToList();
        if (due.Count == 0) return 0;
        using var gate = new SemaphoreSlim(8);
        var probes = await Task.WhenAll(due.Select(async s =>
        {
            await gate.WaitAsync(ct);
            try { return (s, r: await ProbeAsync(s, s.Server!.Host, ct)); } finally { gate.Release(); }
        }));
        foreach (var (s, r) in probes) await ApplyAsync(s, r);
        return due.Count;
    }

    public async Task PurgeOldChecksAsync(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var old = await _db.ServiceChecks.Where(c => c.At < cutoff).Take(5000).ToListAsync();
        if (old.Count > 0) { _db.ServiceChecks.RemoveRange(old); await _db.SaveChangesAsync(); }
    }

    public async Task<double?> UptimeAsync(int serviceId, int days)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var total = await _db.ServiceChecks.CountAsync(c => c.ServiceId == serviceId && c.At >= since);
        if (total == 0) return null;
        var up = await _db.ServiceChecks.CountAsync(c => c.ServiceId == serviceId && c.At >= since && c.Status != ServiceStatus.Offline);
        return Math.Round(100.0 * up / total, 2);
    }
}
