using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class WaSendRequest { public int Id { get; set; } public string? Text { get; set; } }
public class WaNewChatRequest { public int Scope { get; set; } public string? Phone { get; set; } public string? Name { get; set; } }

/// <summary>
/// Chat do WhatsApp. Quem tem whatsapp.all vê tudo (empresa + todos os vendedores);
/// vendedor ligado a um usuário vê só o próprio chat; os demais veem só o número da empresa.
/// </summary>
[RequirePermission(Perm.WhatsAppView)]
public class WhatsAppController : AppController
{
    private const long MaxMediaBytes = 16 * 1024 * 1024;
    private readonly AppDbContext _db;
    private readonly WhatsAppService _wa;
    private readonly IEvolutionApiIntegration _evo;
    private readonly RequestPermissions _perms;
    private readonly IConfiguration _cfg;

    public WhatsAppController(AppDbContext db, WhatsAppService wa, IEvolutionApiIntegration evo, RequestPermissions perms, IConfiguration cfg)
    { _db = db; _wa = wa; _evo = evo; _perms = perms; _cfg = cfg; }

    private string AppUrl => (_cfg["APP_URL"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');

    private async Task<(bool All, int? Mine)> AccessAsync()
    {
        var all = _perms.Has(Perm.WhatsAppAll);
        int? mine = all ? null : await _db.Sellers.Where(s => s.UserId == UserId).Select(s => (int?)s.Id).FirstOrDefaultAsync();
        return (all, mine);
    }

    private IQueryable<WhatsAppConversation> Visible((bool All, int? Mine) a) =>
        a.All ? _db.WhatsAppConversations
        : a.Mine.HasValue ? _db.WhatsAppConversations.Where(c => c.SellerId == a.Mine)
        : _db.WhatsAppConversations.Where(c => c.SellerId == null);

    /// <summary>scope: "all" | "0" (empresa) | id do vendedor.</summary>
    private static IQueryable<WhatsAppConversation> ApplyScope(IQueryable<WhatsAppConversation> q, string? scope) =>
        string.IsNullOrEmpty(scope) || scope == "all" ? q
        : int.TryParse(scope, out var n) ? (n == 0 ? q.Where(c => c.SellerId == null) : q.Where(c => c.SellerId == n))
        : q;

    private static string Initial(string? s) => string.IsNullOrWhiteSpace(s) ? "#" : s.Trim()[..1].ToUpperInvariant();

    public async Task<IActionResult> Index(string? scope, int? id)
    {
        await _wa.MergeDuplicateConversationsAsync();
        var a = await AccessAsync();
        var channels = new List<object>();
        if (a.All || !a.Mine.HasValue)
            channels.Add(new { id = 0, name = "Empresa", status = _evo.IsConfigured ? "CONECTADO" : "DESCONECTADO", number = (string?)null, unread = await _db.WhatsAppConversations.Where(c => c.SellerId == null).SumAsync(c => (int?)c.UnreadCount) ?? 0 });
        if (a.All || a.Mine.HasValue)
        {
            var sellers = await _db.Sellers.Where(s => s.IsActive && (a.All || s.Id == a.Mine)).OrderBy(s => s.Name).ToListAsync();
            var unread = await _db.WhatsAppConversations.Where(c => c.SellerId != null).GroupBy(c => c.SellerId).Select(g => new { g.Key, N = g.Sum(c => c.UnreadCount) }).ToDictionaryAsync(x => x.Key!.Value, x => x.N);
            foreach (var s in sellers)
                channels.Add(new { id = s.Id, name = s.Name, status = s.WaStatus, number = s.WaNumber, unread = unread.GetValueOrDefault(s.Id) });
        }
        // Sem escopo explícito: vendedor cai no próprio chat, os demais em "todos".
        scope ??= a.All ? "all" : (a.Mine?.ToString() ?? "0");
        if (!a.All) scope = a.Mine?.ToString() ?? "0";
        ViewBag.Channels = channels;
        ViewBag.Scope = scope;
        ViewBag.CurrentId = id;
        ViewBag.IsAll = a.All;
        ViewBag.CanSend = _perms.Has(Perm.WhatsAppSend);
        ViewBag.CanManage = _perms.Has(Perm.WhatsAppManage);
        ViewBag.Configured = _evo.IsConfigured || _wa.ServerConfigured;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> List(string? scope, string? q)
    {
        var a = await AccessAsync();
        var query = ApplyScope(Visible(a), scope);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim().ToLower();
            query = query.Where(c => c.Phone.Contains(t) || (c.ContactName != null && c.ContactName.ToLower().Contains(t)));
        }
        var rows = await query.OrderByDescending(c => c.LastMessageAt).Take(200).ToListAsync();
        var sellerNames = await _db.Sellers.ToDictionaryAsync(s => s.Id, s => s.Name);
        return Json(rows.Select(c => new
        {
            id = c.Id, name = (string.IsNullOrWhiteSpace(c.ContactName) ? c.Phone : c.ContactName), phone = c.Phone, initial = Initial((string.IsNullOrWhiteSpace(c.ContactName) ? c.Phone : c.ContactName)),
            preview = c.LastMessagePreview, time = ShortTime(c.LastMessageAt), unread = c.UnreadCount,
            seller = c.SellerId.HasValue ? sellerNames.GetValueOrDefault(c.SellerId.Value) : null
        }));
    }

    private static string ShortTime(DateTime? utc)
    {
        if (utc is null) return "";
        var l = AppTime.ToLocal(utc.Value);
        var today = AppTime.ToLocal(DateTime.UtcNow).Date;
        return l.Date == today ? l.ToString("HH:mm") : l.Date == today.AddDays(-1) ? "Ontem" : l.ToString("dd/MM");
    }

    [HttpGet]
    public async Task<IActionResult> Messages(int id, int after = 0)
    {
        var a = await AccessAsync();
        var conv = await Visible(a).FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (conv.UnreadCount > 0) { conv.UnreadCount = 0; await _db.SaveChangesAsync(); }
        var msgs = await _db.WhatsAppMessages.Where(m => m.ConversationId == id && m.Id > after).OrderBy(m => m.Id).Take(500).ToListAsync();
        string? sellerName = conv.SellerId.HasValue ? await _db.Sellers.Where(s => s.Id == conv.SellerId).Select(s => s.Name).FirstOrDefaultAsync() : null;
        var canSend = conv.SellerId == null ? _evo.IsConfigured
            : await _db.Sellers.AnyAsync(s => s.Id == conv.SellerId && s.WaStatus == "CONECTADO");
        return Json(new
        {
            conversation = new { id = conv.Id, name = conv.ContactName ?? conv.Phone, phone = conv.Phone, leadId = conv.LeadId, customerId = conv.CustomerId, seller = sellerName, canSend },
            messages = msgs.Select(m => new
            {
                id = m.Id, dir = m.Direction == MessageDirection.In ? "in" : "out", body = m.Body, type = m.Type, status = m.Status,
                time = AppTime.ToLocal(m.Timestamp).ToString("HH:mm"), day = AppTime.ToLocal(m.Timestamp).ToString("dd/MM/yyyy"),
                agent = m.FromAgent
            })
        });
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> Send([FromBody] WaSendRequest req)
    {
        var a = await AccessAsync();
        var conv = await Visible(a).FirstOrDefaultAsync(c => c.Id == req.Id);
        if (conv == null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Mensagem vazia." });
        try
        {
            if (conv.SellerId.HasValue && !a.All && conv.LeadId.HasValue) await _wa.ClaimLeadAsync(conv.LeadId.Value, conv.SellerId.Value, false);
            if (conv.SellerId.HasValue) await _wa.SendSellerTextAsync(conv.SellerId.Value, conv.Phone, req.Text.Trim(), UserId);
            else await _wa.SendTextAsync(conv.Phone, req.Text.Trim(), UserId);
            return Json(new { ok = true });
        }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend), RequestSizeLimit(MaxMediaBytes + 1024 * 1024)]
    public async Task<IActionResult> SendMedia(int id, IFormFile? file, string? caption)
    {
        var a = await AccessAsync();
        var conv = await Visible(a).FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (file == null || file.Length == 0) return BadRequest(new { error = "Arquivo vazio." });
        if (file.Length > MaxMediaBytes) return BadRequest(new { error = "Arquivo acima de 16 MB." });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var name = Path.GetFileName(file.FileName);
        try
        {
            if (conv.SellerId.HasValue && !a.All && conv.LeadId.HasValue) await _wa.ClaimLeadAsync(conv.LeadId.Value, conv.SellerId.Value, false);
            if (conv.SellerId.HasValue) await _wa.SendSellerMediaAsync(conv.SellerId.Value, conv.Phone, ms.ToArray(), name, mime, caption, UserId);
            else await _wa.SendDocumentAsync(conv.Phone, ms.ToArray(), name, caption ?? "", UserId);
            return Json(new { ok = true });
        }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> NewChat([FromBody] WaNewChatRequest req)
    {
        var a = await AccessAsync();
        int? sellerId = req.Scope > 0 ? req.Scope : null;
        if (!a.All && sellerId != a.Mine) return Forbid();
        if (PhoneUtil.Normalize(req.Phone ?? "") is null) return BadRequest(new { error = "Telefone inválido." });
        try { if (sellerId.HasValue) await _wa.ClaimByPhoneAsync(req.Phone!, sellerId.Value, a.All); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        var conv = await _wa.GetOrCreateConversationAsync(req.Phone!, req.Name, sellerId);
        return Json(new { id = conv.Id });
    }

    /// <summary>Botão "Chamar no WhatsApp" na tela do lead.</summary>
    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> LeadChat(int id, int? sellerId)
    {
        var a = await AccessAsync();
        var sid = a.All ? sellerId : a.Mine;
        if (sid is null) { FlashFail(a.All ? "Escolha o vendedor." : "Seu usuário não está vinculado a um vendedor."); return RedirectToAction("Details", "Leads", new { id }); }
        var lead = await _db.Leads.FindAsync(id);
        if (lead == null) return NotFound();
        var phone = lead.WhatsApp ?? lead.Phone;
        if (PhoneUtil.Normalize(phone) is null) { FlashFail("Lead sem telefone/WhatsApp."); return RedirectToAction("Details", "Leads", new { id }); }
        try { await _wa.ClaimLeadAsync(id, sid.Value, a.All); }
        catch (InvalidOperationException ex) { FlashWarn(ex.Message); return RedirectToAction("Details", "Leads", new { id }); }
        var conv = await _wa.GetOrCreateConversationAsync(phone!, lead.Name, sid);
        if (conv.LeadId != id) { conv.LeadId = id; await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Index), new { scope = sid.ToString(), id = conv.Id });
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> RequestLead(int id)
    {
        var a = await AccessAsync();
        if (a.Mine is null) { FlashFail("Seu usuário não está vinculado a um vendedor."); return RedirectToAction("Details", "Leads", new { id }); }
        await _wa.RequestLeadAsync(id, a.Mine.Value, HttpContext.RequestServices);
        Flash("Pedido enviado ao administrador.");
        return RedirectToAction("Details", "Leads", new { id });
    }

    /// <summary>Administrador autoriza/transfere o lead para um vendedor (ou libera).</summary>
    [HttpPost, RequirePermission(Perm.WhatsAppAll)]
    public async Task<IActionResult> AssignLead(int id, int? sellerId)
    {
        var lead = await _db.Leads.FindAsync(id);
        if (lead == null) return NotFound();
        lead.SellerId = sellerId;
        await _db.SaveChangesAsync();
        Flash(sellerId.HasValue ? "Lead atribuído." : "Lead liberado.");
        return RedirectToAction("Details", "Leads", new { id });
    }

    // ---------- Conexão do WhatsApp do vendedor ----------

    [RequirePermission(Perm.WhatsAppManage)]
    public async Task<IActionResult> Connect(int id)
    {
        var s = await _db.Sellers.FindAsync(id);
        if (s == null) return NotFound();
        ViewBag.ServerConfigured = _wa.ServerConfigured;
        ViewBag.WebhookUrl = AppUrl + "/api/webhooks/evolution";
        return View(s);
    }

    [HttpPost, RequirePermission(Perm.WhatsAppManage)]
    public async Task<IActionResult> ConnectStart(int id)
    {
        try
        {
            var r = await _wa.ConnectSellerAsync(id, AppUrl);
            return Json(new { state = r.State, qr = r.QrBase64, pairingCode = r.PairingCode });
        }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or InvalidOperationException or KeyNotFoundException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost, RequirePermission(Perm.WhatsAppManage)]
    public async Task<IActionResult> ConnectQr(int id)
    {
        try
        {
            var r = await _wa.RefreshQrAsync(id);
            return Json(new { state = r.State, qr = r.QrBase64, pairingCode = r.PairingCode });
        }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or InvalidOperationException or KeyNotFoundException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet, RequirePermission(Perm.WhatsAppManage)]
    public async Task<IActionResult> ConnectStatus(int id)
    {
        try
        {
            var s = await _wa.RefreshSellerStatusAsync(id);
            return Json(new { status = s.WaStatus, number = s.WaNumber, profile = s.WaProfileName });
        }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or KeyNotFoundException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost, RequirePermission(Perm.WhatsAppManage)]
    public async Task<IActionResult> Disconnect(int id, bool delete = false)
    {
        try { await _wa.DisconnectSellerAsync(id, delete); Flash(delete ? "Instância excluída." : "WhatsApp desconectado."); }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Connect), new { id });
    }

    [HttpPost, RequirePermission(Perm.WhatsAppManage)]
    public async Task<IActionResult> Webhook(int id)
    {
        try { await _wa.ReconfigureWebhookAsync(id, AppUrl); Flash("Webhook reconfigurado."); }
        catch (Exception ex) when (ex is NotConfiguredException or IntegrationException or InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Connect), new { id });
    }
}
