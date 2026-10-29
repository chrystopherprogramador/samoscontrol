using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

[RequirePermission(Perm.AutomationView)]
public class AutomationController : AppController
{
    private readonly AppDbContext _db;
    private readonly AutomationService _svc;
    private readonly AuditService _audit;
    public AutomationController(AppDbContext db, AutomationService svc, AuditService audit) { _db = db; _svc = svc; _audit = audit; }

    public async Task<IActionResult> Index()
    {
        ViewBag.Executions = await _db.AutomationExecutions.Include(e => e.Job).OrderByDescending(e => e.Id).Take(30).ToListAsync();
        return View(await _db.AutomationJobs.OrderBy(j => j.Name).ToListAsync());
    }

    [RequirePermission(Perm.AutomationManage)]
    public IActionResult Create() => View();

    [HttpPost, RequirePermission(Perm.AutomationManage)]
    public async Task<IActionResult> Create(string name, JobType type, string? configJson, DateTime? nextRun, int? recurrenceMinutes)
    {
        if (string.IsNullOrWhiteSpace(name)) { FlashFail("Informe o nome."); return View(); }
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try { System.Text.Json.JsonDocument.Parse(configJson); }
            catch (System.Text.Json.JsonException) { FlashFail("Configuração deve ser um JSON válido."); return View(); }
        }
        if (recurrenceMinutes is < 1) recurrenceMinutes = null;
        var job = await _svc.CreateAsync(name.Trim(), type, string.IsNullOrWhiteSpace(configJson) ? null : configJson, ToUtc(nextRun), recurrenceMinutes, UserId);
        await _audit.LogAsync("automation", "job_create", "AutomationJob", job.Id, newData: job.Name);
        Flash("Automação criada.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, RequirePermission(Perm.AutomationManage)]
    public async Task<IActionResult> Run(int id, CancellationToken ct)
    {
        var ex = await _svc.RunJobAsync(id, "MANUAL", ct);
        await _audit.LogAsync("automation", "job_run", "AutomationJob", id, newData: ex.Status.ToString());
        if (ex.Status == ExecutionStatus.Success) Flash($"Executada: {ex.Result}"); else FlashFail($"Falhou: {ex.Result}");
        return RedirectToAction(nameof(Details), new { id = ex.Id });
    }

    [HttpPost, RequirePermission(Perm.AutomationManage)]
    public async Task<IActionResult> Toggle(int id)
    {
        var j = await _db.AutomationJobs.FindAsync(id);
        if (j == null) return NotFound();
        j.IsEnabled = !j.IsEnabled;
        if (j.IsEnabled && j.NextRunAt == null && j.RecurrenceMinutes.HasValue) j.NextRunAt = DateTime.UtcNow.AddMinutes(j.RecurrenceMinutes.Value);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("automation", j.IsEnabled ? "job_enable" : "job_disable", "AutomationJob", id);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, RequirePermission(Perm.AutomationManage)]
    public async Task<IActionResult> Delete(int id)
    {
        var j = await _db.AutomationJobs.FindAsync(id);
        if (j == null) return NotFound();
        if (j.IsSystem) { FlashFail("Automações de sistema não podem ser excluídas (apenas desabilitadas)."); return RedirectToAction(nameof(Index)); }
        var execIds = await _db.AutomationExecutions.Where(e => e.JobId == id).Select(e => e.Id).ToListAsync();
        _db.AutomationLogs.RemoveRange(_db.AutomationLogs.Where(l => execIds.Contains(l.ExecutionId)));
        _db.AutomationExecutions.RemoveRange(_db.AutomationExecutions.Where(e => e.JobId == id));
        _db.AutomationJobs.Remove(j);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("automation", "job_delete", "AutomationJob", id, oldData: j.Name);
        Flash("Automação excluída.");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(int id)
    {
        var e = await _db.AutomationExecutions.Include(x => x.Job).Include(x => x.Logs).FirstOrDefaultAsync(x => x.Id == id);
        return e == null ? NotFound() : View(e);
    }
}

public class ServerForm { public int Id { get; set; } public string Name { get; set; } = ""; public string Host { get; set; } = ""; public string? Type { get; set; } public int? Port { get; set; } public string? Notes { get; set; } }
public class ServiceForm
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Name { get; set; } = "";
    public ServiceKind Kind { get; set; } = ServiceKind.Https;
    public string? Url { get; set; }
    public int? Port { get; set; }
    public int ExpectedStatus { get; set; } = 200;
    public int TimeoutMs { get; set; } = 10000;
    public int DegradedLatencyMs { get; set; } = 2000;
    public int IntervalSeconds { get; set; } = 60;
    public int FailureThreshold { get; set; } = 2;
    public bool IsEnabled { get; set; } = true;
}

[RequirePermission(Perm.MonitoringView)]
public class MonitoringController : AppController
{
    private readonly AppDbContext _db;
    private readonly MonitoringService _mon;
    private readonly AuditService _audit;
    public MonitoringController(AppDbContext db, MonitoringService mon, AuditService audit) { _db = db; _mon = mon; _audit = audit; }

    public async Task<IActionResult> Servers() => View(await _db.Servers.Include(s => s.Services).OrderBy(s => s.Name).ToListAsync());

    [RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> EditServer(int? id)
    {
        if (id == null) return View(new ServerForm());
        var s = await _db.Servers.FindAsync(id);
        return s == null ? NotFound() : View(new ServerForm { Id = s.Id, Name = s.Name, Host = s.Host, Type = s.Type, Port = s.Port, Notes = s.Notes });
    }

    [HttpPost, RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> EditServer(ServerForm f)
    {
        if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Host)) { FlashFail("Nome e host são obrigatórios."); return View(f); }
        var s = f.Id == 0 ? new Server() : await _db.Servers.FindAsync(f.Id);
        if (s == null) return NotFound();
        s.Name = f.Name.Trim().Truncate(100); s.Host = f.Host.Trim().Truncate(200); s.Type = f.Type?.Trim().Truncate(50); s.Port = f.Port; s.Notes = f.Notes?.Trim().Truncate(1000);
        if (f.Id == 0) _db.Servers.Add(s);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("monitoring", f.Id == 0 ? "server_create" : "server_update", "Server", s.Id, newData: s.Name);
        Flash("Servidor salvo.");
        return RedirectToAction(nameof(Servers));
    }

    [HttpPost, RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> DeleteServer(int id)
    {
        var s = await _db.Servers.Include(x => x.Services).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        foreach (var sv in s.Services) await RemoveService(sv.Id);
        _db.Servers.Remove(s);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("monitoring", "server_delete", "Server", id, oldData: s.Name);
        Flash("Servidor removido.");
        return RedirectToAction(nameof(Servers));
    }

    private async Task RemoveService(int id)
    {
        _db.ServiceChecks.RemoveRange(_db.ServiceChecks.Where(c => c.ServiceId == id));
        _db.Incidents.RemoveRange(_db.Incidents.Where(c => c.ServiceId == id));
        var sv = await _db.Services.FindAsync(id);
        if (sv != null) _db.Services.Remove(sv);
        await _db.SaveChangesAsync();
    }

    public async Task<IActionResult> Services()
    {
        var list = await _db.Services.Include(s => s.Server).OrderBy(s => s.Server!.Name).ThenBy(s => s.Name).ToListAsync();
        var up = new Dictionary<int, double?>();
        foreach (var s in list) up[s.Id] = await _mon.UptimeAsync(s.Id, 7);
        ViewBag.Uptime = up;
        return View(list);
    }

    [RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> EditService(int? id, int? serverId)
    {
        ViewBag.Servers = await _db.Servers.OrderBy(s => s.Name).ToListAsync();
        if (id == null) return View(new ServiceForm { ServerId = serverId ?? 0 });
        var s = await _db.Services.FindAsync(id);
        if (s == null) return NotFound();
        return View(new ServiceForm { Id = s.Id, ServerId = s.ServerId, Name = s.Name, Kind = s.Kind, Url = s.Url, Port = s.Port, ExpectedStatus = s.ExpectedStatus, TimeoutMs = s.TimeoutMs, DegradedLatencyMs = s.DegradedLatencyMs, IntervalSeconds = s.IntervalSeconds, FailureThreshold = s.FailureThreshold, IsEnabled = s.IsEnabled });
    }

    [HttpPost, RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> EditService(ServiceForm f)
    {
        ViewBag.Servers = await _db.Servers.OrderBy(s => s.Name).ToListAsync();
        if (string.IsNullOrWhiteSpace(f.Name) || f.ServerId == 0) { FlashFail("Nome e servidor são obrigatórios."); return View(f); }
        if (f.Kind == ServiceKind.Tcp && (f.Port is null or < 1 or > 65535)) { FlashFail("Informe uma porta TCP válida (1-65535)."); return View(f); }
        if (f.Kind != ServiceKind.Tcp && !string.IsNullOrWhiteSpace(f.Url) && !Uri.TryCreate(f.Url.Trim(), UriKind.Absolute, out _)) { FlashFail("URL inválida."); return View(f); }
        var s = f.Id == 0 ? new MonitoredService() : await _db.Services.FindAsync(f.Id);
        if (s == null) return NotFound();
        s.ServerId = f.ServerId; s.Name = f.Name.Trim().Truncate(100); s.Kind = f.Kind; s.Url = f.Url?.Trim().Truncate(500); s.Port = f.Port;
        s.ExpectedStatus = f.ExpectedStatus is >= 100 and <= 599 ? f.ExpectedStatus : 200;
        s.TimeoutMs = Math.Clamp(f.TimeoutMs, 1000, 60000); s.DegradedLatencyMs = Math.Clamp(f.DegradedLatencyMs, 100, 60000);
        s.IntervalSeconds = Math.Clamp(f.IntervalSeconds, 15, 86400); s.FailureThreshold = Math.Clamp(f.FailureThreshold, 1, 20); s.IsEnabled = f.IsEnabled;
        if (f.Id == 0) _db.Services.Add(s);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("monitoring", f.Id == 0 ? "service_create" : "service_update", "MonitoredService", s.Id, newData: s.Name);
        Flash("Serviço salvo.");
        return RedirectToAction(nameof(Services));
    }

    [HttpPost, RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> DeleteService(int id)
    {
        await RemoveService(id);
        await _audit.LogAsync("monitoring", "service_delete", "MonitoredService", id);
        Flash("Serviço removido.");
        return RedirectToAction(nameof(Services));
    }

    [HttpPost, RequirePermission(Perm.MonitoringManage)]
    public async Task<IActionResult> CheckNow(int id, CancellationToken ct)
    {
        var s = await _db.Services.Include(x => x.Server).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        var c = await _mon.CheckAsync(s, null, ct);
        Flash($"Resultado: {c.Status}{(c.LatencyMs.HasValue ? $" ({c.LatencyMs} ms)" : "")}{(c.Error != null ? " - " + c.Error : "")}");
        return RedirectToAction(nameof(Services));
    }

    public async Task<IActionResult> Incidents()
        => View(await _db.Incidents.Include(i => i.Service).ThenInclude(s => s!.Server).OrderByDescending(i => i.StartedAt).Take(200).ToListAsync());

    public async Task<IActionResult> History(int id)
    {
        var s = await _db.Services.Include(x => x.Server).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        ViewBag.Checks = await _db.ServiceChecks.Where(c => c.ServiceId == id).OrderByDescending(c => c.At).Take(100).ToListAsync();
        return View(s);
    }
}

[RequirePermission(Perm.AuditView)]
public class AuditController : AppController
{
    private readonly AppDbContext _db;
    public AuditController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? module, string? q, int page = 1)
    {
        var query = _db.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(module)) query = query.Where(a => a.Module == module);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(a => EF.Functions.ILike(a.Action, like) || (a.UserName != null && EF.Functions.ILike(a.UserName, like)) || (a.Entity != null && EF.Functions.ILike(a.Entity, like))); }
        page = Math.Max(1, page);
        ViewBag.Modules = await _db.AuditLogs.Select(a => a.Module).Distinct().OrderBy(m => m).ToListAsync();
        ViewBag.Module = module; ViewBag.Q = q; ViewBag.Page = page;
        return View(await query.OrderByDescending(a => a.Id).Skip((page - 1) * 50).Take(50).ToListAsync());
    }
}
