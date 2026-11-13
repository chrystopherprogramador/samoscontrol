using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Application.Voice;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

[RequirePermission(Perm.VoiceView)]
public class VoiceController : AppController
{
    private readonly AppDbContext _db;
    private readonly VoiceCallManager _voice;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly IAiProvider _ai;
    public VoiceController(AppDbContext db, VoiceCallManager voice, SettingsService settings, AuditService audit, IAiProvider ai) { _db = db; _voice = voice; _settings = settings; _audit = audit; _ai = ai; }

    public async Task<IActionResult> Index()
    {
        ViewBag.Calls = await _db.VoiceCalls.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync();
        ViewBag.Scheduled = await _db.AutomationJobs.AsNoTracking().Where(j => j.Type == JobType.PlaceCall).OrderByDescending(j => j.Id).Take(20).ToListAsync();
        ViewBag.Allowed = await _db.VoiceAllowedNumbers.AsNoTracking().OrderBy(a => a.Label).ToListAsync();
        ViewBag.Readiness = _voice.Readiness();
        ViewBag.Limits = _voice.Limits;
        ViewBag.Key = Guid.NewGuid().ToString("N");
        ViewBag.AiEngine = _ai.Name;
        ViewBag.AiConfigured = _ai.IsConfigured;
        ViewBag.AiModel = _settings.Get(SettingKeys.OpenClawModel) ?? _settings.Get(SettingKeys.OllamaModel);
        ViewBag.VoiceId = _settings.Get(SettingKeys.ElevenLabsVoiceId);
        ViewBag.TtsModel = string.IsNullOrWhiteSpace(_settings.Get(SettingKeys.VoiceTtsModel)) ? ElevenLabsIntegration.DefaultTtsModel : _settings.Get(SettingKeys.VoiceTtsModel);
        ViewBag.SttModel = _settings.Get(SettingKeys.ElevenLabsSttModel);
        var recent = await _db.VoiceCalls.AsNoTracking().Where(c => c.AvgResponseLatencyMs > 0).OrderByDescending(c => c.CreatedAt).Take(20).Select(c => c.AvgResponseLatencyMs).ToListAsync();
        ViewBag.LatencyText = recent.Count > 0 ? $"~{(int)recent.Average()} ms (média das últimas {recent.Count} ligações)" : "sem dados ainda";
        return View();
    }

    /// <summary>Estado das ligações para o polling da tela.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(int? id)
    {
        var q = _db.VoiceCalls.AsNoTracking().AsQueryable();
        if (id.HasValue) q = q.Where(c => c.Id == id);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Take(id.HasValue ? 1 : 50).ToListAsync();
        return Json(rows.Select(c => new { c.Id, state = c.State.ToString(), c.EndReason, c.DurationSeconds, active = _voice.IsActive(c.Id) }));
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _db.VoiceCalls.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewBag.Events = await _db.VoiceCallEvents.AsNoTracking().Where(e => e.VoiceCallId == id).OrderBy(e => e.Id).ToListAsync();
        ViewBag.Active = _voice.IsActive(id);
        return View(c);
    }

    [HttpPost, RequirePermission(Perm.VoiceCall)]
    public async Task<IActionResult> Start(string phone, string key, CancellationToken ct)
    {
        var r = await _voice.StartTestCallAsync(phone, UserId, User.Identity?.Name, key, ct);
        await _audit.LogAsync("voice", "voice_call_start", "VoiceCall", r.CallId?.ToString(), newData: $"{PhoneUtil.Key(phone)} ok={r.Ok} dup={r.Duplicate}");
        if (r.Ok) { if (r.Duplicate) FlashWarn("Pedido já processado."); else Flash("Ligando… atenda no WhatsApp."); }
        else FlashFail(r.Error ?? "Não foi possível ligar.");
        return r.CallId.HasValue ? RedirectToAction(nameof(Details), new { id = r.CallId }) : RedirectToAction(nameof(Index));
    }

    [HttpPost, RequirePermission(Perm.VoiceCall)]
    public async Task<IActionResult> Hangup(int id)
    {
        var ok = await _voice.HangupAsync(id);
        await _audit.LogAsync("voice", "voice_call_hangup", "VoiceCall", id.ToString());
        if (ok) Flash("Encerrando a ligação."); else FlashWarn("Ligação já encerrada.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.VoiceManage)]
    public async Task<IActionResult> KillSwitch(bool on)
    {
        await _settings.SetAsync(_db, SettingKeys.VoiceKillSwitch, on ? "true" : "false");
        if (on) _voice.KillAll();
        await _audit.LogAsync("voice", "voice_kill_switch", "AppSetting", SettingKeys.VoiceKillSwitch, newData: on.ToString());
        if (on) FlashWarn("Kill switch ATIVADO: ligações bloqueadas e encerradas."); else Flash("Kill switch desativado.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, RequirePermission(Perm.VoiceManage)]
    public async Task<IActionResult> AddAllowed(string phone, string label, bool consent)
    {
        var norm = PhoneUtil.Normalize(phone); var key = PhoneUtil.Key(phone);
        if (norm == null || key == null || string.IsNullOrWhiteSpace(label)) { FlashFail("Informe nome e telefone válidos."); return RedirectToAction(nameof(Index)); }
        if (!consent) { FlashFail("Confirme que o titular do número autorizou receber ligações de teste."); return RedirectToAction(nameof(Index)); }
        var a = await _db.VoiceAllowedNumbers.FirstOrDefaultAsync(x => x.PhoneKey == key);
        if (a == null) _db.VoiceAllowedNumbers.Add(a = new VoiceAllowedNumber { PhoneKey = key });
        a.Phone = norm; a.Label = label.Trim(); a.Active = true; a.ConsentBy = User.Identity?.Name; a.ConsentAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("voice", "voice_allowed_add", "VoiceAllowedNumber", a.Id.ToString(), newData: key);
        Flash("Número autorizado.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, RequirePermission(Perm.VoiceManage)]
    public async Task<IActionResult> RemoveAllowed(int id)
    {
        var a = await _db.VoiceAllowedNumbers.FindAsync(id);
        if (a != null) { a.Active = false; await _db.SaveChangesAsync(); await _audit.LogAsync("voice", "voice_allowed_remove", "VoiceAllowedNumber", id.ToString()); }
        Flash("Número removido da lista.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Webhook do Evolution Go Calls (CallOffer/CallAccept/CallTerminate). Autenticado por token; campos lidos de forma tolerante.</summary>
[AllowAnonymous, IgnoreAntiforgeryToken, EnableRateLimiting("webhook")]
[Route("api/webhooks/voice")]
public class VoiceWebhookController : ControllerBase
{
    private readonly VoiceCallManager _voice;
    private readonly ISettingsReader _s;
    private readonly ILogger<VoiceWebhookController> _log;
    public VoiceWebhookController(VoiceCallManager voice, ISettingsReader s, ILogger<VoiceWebhookController> log) { _voice = voice; _s = s; _log = log; }

    [HttpPost("")]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var expected = _s.Get(SettingKeys.VoiceWebhookToken);
        var given = Request.Headers["x-webhook-token"].FirstOrDefault() ?? Request.Query["token"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return Unauthorized();
        using var r = new StreamReader(Request.Body);
        var body = await r.ReadToEndAsync(ct);
        var (ev, callId, from) = Parse(body);
        if (ev == null) return Ok(new { message = "ignored" });
        try { var res = await _voice.HandleWebhookAsync(ev, callId, from, ct, body); return Ok(new { message = res }); }
        catch (Exception ex) { _log.LogWarning(ex, "Voz: erro ao tratar webhook {Event}", ev); return Ok(new { message = "error" }); }
    }

    /// <summary>Os nomes exatos dos campos não foram confirmados com webhook real: busca recursiva sem diferenciar maiúsculas.</summary>
    public static (string? Event, string? CallId, string? From) Parse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            string? Find(JsonElement e, params string[] names)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in e.EnumerateObject())
                        if (names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            return p.Value.ToString();
                    foreach (var p in e.EnumerateObject()) { var x = Find(p.Value, names); if (x != null) return x; }
                }
                else if (e.ValueKind == JsonValueKind.Array) foreach (var i in e.EnumerateArray()) { var x = Find(i, names); if (x != null) return x; }
                return null;
            }
            var ev = Find(d.RootElement, "event", "eventType", "type");
            return (ev, Find(d.RootElement, "callId", "call_id", "callID"), Find(d.RootElement, "callCreator", "call_creator", "from"));
        }
        catch (JsonException) { return (null, null, null); }
    }
}
