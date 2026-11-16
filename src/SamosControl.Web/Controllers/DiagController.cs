using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Web.Controllers;

/// <summary>Diagnóstico somente leitura da IA (OpenClaw/Ollama) e do fluxo WhatsApp. Autenticado pelo token do webhook de voz; não expõe conteúdo de mensagens nem segredos.</summary>
[AllowAnonymous, IgnoreAntiforgeryToken, EnableRateLimiting("webhook")]
[Route("api/diag/ai")]
public class DiagController : ControllerBase
{
    private readonly ISettingsReader _s;
    private readonly AppDbContext _db;
    private readonly OpenClawProvider _claw;
    private readonly OllamaProvider _ollama;
    public DiagController(ISettingsReader s, AppDbContext db, OpenClawProvider claw, OllamaProvider ollama) { _s = s; _db = db; _claw = claw; _ollama = ollama; }

    [HttpGet("")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var expected = _s.Get(SettingKeys.VoiceWebhookToken);
        var given = Request.Headers["x-webhook-token"].FirstOrDefault() ?? Request.Query["token"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return Unauthorized();

        var probe = new[] { new AiChatMessage("user", "Responda apenas: ok") };
        async Task<object> Ping(IAiProvider p)
        {
            if (!p.IsConfigured) return new { configured = false };
            var sw = Stopwatch.StartNew();
            try { var r = await p.ChatAsync(probe, false, ct); return new { configured = true, ok = true, ms = sw.ElapsedMilliseconds, reply = r.Length > 80 ? r[..80] : r }; }
            catch (Exception ex) { return new { configured = true, ok = false, ms = sw.ElapsedMilliseconds, error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message, inner = ex.InnerException?.Message }; }
        }

        var since = DateTime.UtcNow.AddDays(-2);
        var inbound = await _db.WhatsAppMessages.Where(m => m.Timestamp >= since).OrderByDescending(m => m.Id).Take(12)
            .Select(m => new { m.Timestamp, m.Direction, m.Type, m.Status, m.FromAgent }).ToListAsync(ct);
        var actions = await _db.AiActions.OrderByDescending(a => a.Id).Take(8)
            .Select(a => new { a.CreatedAt, a.Channel, a.Tool, a.Status, result = a.Result != null && a.Result.Length > 160 ? a.Result.Substring(0, 160) : a.Result }).ToListAsync(ct);
        var lastAi = await _db.AiMessages.OrderByDescending(m => m.Id).Take(6).Select(m => new { m.CreatedAt, m.Channel, m.Role }).ToListAsync(ct);

        return Ok(new
        {
            nowUtc = DateTime.UtcNow,
            provider = _s.Get(SettingKeys.RobertaProvider),
            openClawUrl = _s.Get(SettingKeys.OpenClawUrl),
            model = _s.Get(SettingKeys.OpenClawModel),
            fallbackOllama = _s.Get(SettingKeys.RobertaFallbackOllama),
            openclaw = await Ping(_claw),
            ollama = await Ping(_ollama),
            whatsappRecent = inbound,
            agentLastMessages = lastAi,
            agentLastActions = actions
        });
    }
}
