using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class WaSendRequest { public int Id { get; set; } public string? Text { get; set; } }
public class WaTemplateRequest { public int Id { get; set; } public int Conv { get; set; } public string? Name { get; set; } public string? Body { get; set; } public bool Shared { get; set; } }
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
            channels.Add(new { id = 0, name = "Empresa", status = _evo.IsConfigured ? "CONECTADO" : "DESCONECTADO", number = (string?)null, pic = (string?)null, unread = await _db.WhatsAppConversations.Where(c => c.SellerId == null).SumAsync(c => (int?)c.UnreadCount) ?? 0 });
        if (a.All || a.Mine.HasValue)
        {
            var sellers = await _db.Sellers.Where(s => s.IsActive && (a.All || s.Id == a.Mine)).OrderBy(s => s.Name).ToListAsync();
            var unread = await _db.WhatsAppConversations.Where(c => c.SellerId != null).GroupBy(c => c.SellerId).Select(g => new { g.Key, N = g.Sum(c => c.UnreadCount) }).ToDictionaryAsync(x => x.Key!.Value, x => x.N);
            foreach (var s in sellers) await _wa.EnsureSellerPictureAsync(s);
            foreach (var s in sellers)
                channels.Add(new { id = s.Id, name = s.Name, status = s.WaStatus, number = s.WaNumber, unread = unread.GetValueOrDefault(s.Id), pic = s.WaPictureUrl });
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
            pic = c.PictureUrl, preview = c.LastMessagePreview, time = ShortTime(c.LastMessageAt), unread = c.UnreadCount,
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
        if (after == 0) await _wa.EnsurePictureAsync(conv.Id);
        var lead = conv.LeadId.HasValue ? await _db.Leads.Where(l => l.Id == conv.LeadId).Select(l => new { l.Status, l.CompanyName, l.City, l.State }).FirstOrDefaultAsync() : null;
        var msgs = await _db.WhatsAppMessages.Where(m => m.ConversationId == id && m.Id > after).OrderBy(m => m.Id).Take(500).ToListAsync();
        string? sellerName = conv.SellerId.HasValue ? await _db.Sellers.Where(s => s.Id == conv.SellerId).Select(s => s.Name).FirstOrDefaultAsync() : null;
        var canSend = conv.SellerId == null ? _evo.IsConfigured
            : await _db.Sellers.AnyAsync(s => s.Id == conv.SellerId && s.WaStatus == "CONECTADO");
        return Json(new
        {
            conversation = new { id = conv.Id, name = conv.ContactName ?? conv.Phone, phone = conv.Phone, leadId = conv.LeadId, customerId = conv.CustomerId, seller = sellerName, canSend, pic = conv.PictureUrl, leadStatus = lead?.Status.ToString(), leadInfo = lead == null ? null : string.Join(" · ", new[] { lead.CompanyName, lead.City == null ? null : lead.City + (lead.State == null ? "" : "/" + lead.State) }.Where(x => !string.IsNullOrWhiteSpace(x))), statuses = Enum.GetNames<LeadStatus>(), canEditLead = _perms.Has(Perm.CrmEdit), canDelete = _perms.Has(Perm.WhatsAppAll) },
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
        string phone;
        try
        {
            phone = await _wa.ValidateNumberAsync(sellerId, req.Phone!);
            if (sellerId.HasValue) await _wa.ClaimByPhoneAsync(phone, sellerId.Value, a.All);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        var conv = await _wa.GetOrCreateConversationAsync(phone, req.Name, sellerId);
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
        string valid;
        try { valid = await _wa.ValidateNumberAsync(sid, phone!); await _wa.ClaimLeadAsync(id, sid.Value, a.All); }
        catch (InvalidOperationException ex) { FlashWarn(ex.Message); return RedirectToAction("Details", "Leads", new { id }); }
        var conv = await _wa.GetOrCreateConversationAsync(valid, lead.Name, sid);
        if (conv.LeadId != id) conv.LeadId = id;
        if (lead.Status == LeadStatus.Novo)
        {
            lead.Status = LeadStatus.Contato;
            _db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.Sistema, Description = "Status alterado para Contato (conversa iniciada no WhatsApp)", UserId = UserId });
        }
        await _db.SaveChangesAsync();
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

    [HttpPost, RequirePermission(Perm.WhatsAppAll)]
    public async Task<IActionResult> DeleteConversation(int id)
    {
        await _wa.DeleteConversationAsync(id);
        return Json(new { ok = true });
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> SetLeadStatus(int id, string status)
    {
        var a = await AccessAsync();
        var conv = await Visible(a).FirstOrDefaultAsync(c => c.Id == id);
        if (conv?.LeadId == null || !Enum.TryParse<LeadStatus>(status, out var st)) return BadRequest(new { error = "Status inválido." });
        var lead = await _db.Leads.FindAsync(conv.LeadId.Value);
        if (lead == null) return NotFound();
        if (lead.Status != st)
        {
            _db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.Sistema, Description = $"Status alterado de {lead.Status} para {st} (chat WhatsApp)", UserId = UserId });
            lead.Status = st;
            await _db.SaveChangesAsync();
        }
        return Json(new { ok = true });
    }

    [HttpGet]
    public async Task<IActionResult> Templates(int conv)
    {
        var a = await AccessAsync();
        var c = await Visible(a).FirstOrDefaultAsync(x => x.Id == conv);
        if (c == null) return NotFound();
        var rows = await _db.WhatsAppTemplates.Where(t => t.SellerId == null || t.SellerId == c.SellerId).OrderBy(t => t.Name).ToListAsync();
        return Json(rows.Select(t => new { id = t.Id, name = t.Name, body = t.Body, shared = t.SellerId == null, editable = t.SellerId == null ? a.All : (a.All || t.SellerId == a.Mine) }));
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> TemplateSave([FromBody] WaTemplateRequest req)
    {
        var a = await AccessAsync();
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Body)) return BadRequest(new { error = "Informe nome e texto." });
        int? convSeller = req.Conv > 0 ? await Visible(a).Where(c => c.Id == req.Conv).Select(c => c.SellerId).FirstOrDefaultAsync() : null;
        int? owner = req.Shared && a.All ? null : (a.All ? convSeller : a.Mine);
        if (!(req.Shared && a.All) && owner is null && !a.All) return BadRequest(new { error = "Seu usuário não está vinculado a um vendedor." });
        WhatsAppTemplate? t = req.Id > 0 ? await _db.WhatsAppTemplates.FindAsync(req.Id) : new WhatsAppTemplate();
        if (t == null) return NotFound();
        if (req.Id > 0 && !(a.All || t.SellerId == a.Mine)) return Forbid();
        t.Name = req.Name.Trim(); t.Body = req.Body.Trim();
        if (req.Id == 0) { t.SellerId = owner; _db.WhatsAppTemplates.Add(t); }
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> TemplateDelete(int id)
    {
        var a = await AccessAsync();
        var t = await _db.WhatsAppTemplates.FindAsync(id);
        if (t == null) return NotFound();
        if (!(a.All || t.SellerId == a.Mine)) return Forbid();
        _db.WhatsAppTemplates.Remove(t);
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    /// <summary>Leve: usado em todas as páginas para push/som de novas mensagens.</summary>
    [HttpGet]
    public async Task<IActionResult> Poll(int after = 0)
    {
        var a = await AccessAsync();
        var vis = Visible(a);
        var unread = await vis.SumAsync(c => (int?)c.UnreadCount) ?? 0;
        if (after == 0) return Json(new { unread, last = await _db.WhatsAppMessages.MaxAsync(m => (int?)m.Id) ?? 0, items = Array.Empty<object>() });
        var items = await _wa.NewIncomingAsync(vis, after);
        return Json(new { unread, last = items.Count > 0 ? items.Max(i => i.Id) : after, items = items.Select(i => new { id = i.Id, conv = i.ConversationId, name = i.Name, text = i.Preview, scope = i.SellerId ?? 0 }) });
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
