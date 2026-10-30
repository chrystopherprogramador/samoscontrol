using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class LeadForm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Segment { get; set; }
    public string? Website { get; set; }
    public int? SourceId { get; set; }
    public int? AssignedUserId { get; set; }
    public string? Notes { get; set; }
    public string? TagsText { get; set; }
}

[RequirePermission(Perm.CrmView)]
public class LeadsController : AppController
{
    private readonly AppDbContext _db;
    private readonly LeadService _leads;
    private readonly LeadSyncService _sync;
    private readonly AuditService _audit;
    private readonly IGoogleMapsScraperIntegration _scraper;
    public LeadsController(AppDbContext db, LeadService leads, LeadSyncService sync, AuditService audit, IGoogleMapsScraperIntegration scraper)
    { _db = db; _leads = leads; _sync = sync; _audit = audit; _scraper = scraper; }

    private async Task LoadLookups()
    {
        ViewBag.Sources = await _db.LeadSources.OrderBy(s => s.Name).ToListAsync();
        ViewBag.Users = await _db.Users.Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync();
    }

    public async Task<IActionResult> Index(string? q, LeadStatus? status, int? sourceId, string? state, string? city, int page = 1)
    {
        var query = _db.Leads.Include(l => l.Source).Include(l => l.AssignedUser).Include(l => l.Tags).ThenInclude(t => t.Tag).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(l => EF.Functions.ILike(l.Name, like) || (l.CompanyName != null && EF.Functions.ILike(l.CompanyName, like)) ||
                (l.Phone != null && EF.Functions.ILike(l.Phone, like)) || (l.Email != null && EF.Functions.ILike(l.Email, like)) ||
                (l.City != null && EF.Functions.ILike(l.City, like)) || (l.Segment != null && EF.Functions.ILike(l.Segment, like)));
        }
        if (status.HasValue) query = query.Where(l => l.Status == status);
        if (sourceId.HasValue) query = query.Where(l => l.SourceId == sourceId);
        if (!string.IsNullOrWhiteSpace(state)) { var uf = state.Trim().ToUpper(); query = query.Where(l => l.State == uf); }
        if (!string.IsNullOrWhiteSpace(city)) { var cl = city.Trim(); query = query.Where(l => l.City != null && EF.Functions.ILike(l.City, cl)); }
        const int size = 25;
        var total = await query.CountAsync();
        page = Math.Max(1, page);
        var items = await query.OrderByDescending(l => l.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync();
        ViewBag.Total = total; ViewBag.Page = page; ViewBag.Pages = (int)Math.Ceiling(total / (double)size);
        ViewBag.Q = q; ViewBag.Status = status; ViewBag.SourceId = sourceId; ViewBag.State = state; ViewBag.City = city;
        await LoadLookups();
        return View(items);
    }

    public async Task<IActionResult> Pipeline()
    {
        var leads = await _db.Leads.Include(l => l.AssignedUser).OrderByDescending(l => l.UpdatedAt).Take(600).ToListAsync();
        return View(leads);
    }

    [RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Create() { await LoadLookups(); return View("Form", new LeadForm()); }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Create(LeadForm f)
    {
        if (string.IsNullOrWhiteSpace(f.Name)) { ModelState.AddModelError(nameof(f.Name), "Nome é obrigatório."); await LoadLookups(); return View("Form", f); }
        var lead = new Lead { AssignedUserId = f.AssignedUserId ?? UserId };
        Apply(lead, f);
        _db.Leads.Add(lead);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { FlashFail("Já existe um lead com o mesmo identificador (telefone/site/Place ID)."); await LoadLookups(); return View("Form", f); }
        await SetTags(lead.Id, f.TagsText);
        await _audit.LogAsync("crm", "lead_create", "Lead", lead.Id, newData: lead.Name);
        Flash("Lead criado.");
        return RedirectToAction(nameof(Details), new { id = lead.Id });
    }

    [RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Edit(int id)
    {
        var l = await _db.Leads.Include(x => x.Tags).ThenInclude(t => t.Tag).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        await LoadLookups();
        return View("Form", new LeadForm
        {
            Id = l.Id, Name = l.Name, CompanyName = l.CompanyName, Phone = l.Phone, WhatsApp = l.WhatsApp, Email = l.Email, Address = l.Address,
            City = l.City, State = l.State, Segment = l.Segment, Website = l.Website, SourceId = l.SourceId, AssignedUserId = l.AssignedUserId,
            Notes = l.Notes, TagsText = string.Join(", ", l.Tags.Select(t => t.Tag!.Name))
        });
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Edit(int id, LeadForm f)
    {
        var l = await _db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (string.IsNullOrWhiteSpace(f.Name)) { ModelState.AddModelError(nameof(f.Name), "Nome é obrigatório."); await LoadLookups(); return View("Form", f); }
        var old = l.Name;
        Apply(l, f);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { FlashFail("Conflito com outro lead (telefone/site)."); await LoadLookups(); return View("Form", f); }
        await SetTags(l.Id, f.TagsText);
        await _audit.LogAsync("crm", "lead_update", "Lead", l.Id, oldData: old, newData: l.Name);
        Flash("Lead atualizado.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static void Apply(Lead l, LeadForm f)
    {
        l.Name = f.Name.Trim().Truncate(200); l.CompanyName = N(f.CompanyName)?.Truncate(200); l.Phone = N(f.Phone)?.Truncate(40); l.WhatsApp = N(f.WhatsApp)?.Truncate(40);
        l.Email = N(f.Email)?.Truncate(200); l.Address = N(f.Address)?.Truncate(300); l.City = N(f.City)?.Truncate(100);
        l.State = N(f.State)?.ToUpperInvariant().Truncate(2); l.Segment = N(f.Segment)?.Truncate(100); l.Website = N(f.Website)?.Truncate(300);
        l.SourceId = f.SourceId; l.AssignedUserId = f.AssignedUserId; l.Notes = N(f.Notes)?.Truncate(4000);
        LeadService.ApplyKeys(l);
    }

    private async Task SetTags(int leadId, string? text)
    {
        var names = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.Truncate(50)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _db.LeadTags.RemoveRange(_db.LeadTags.Where(t => t.LeadId == leadId));
        foreach (var n in names)
        {
            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == n) ?? _db.Tags.Add(new Tag { Name = n }).Entity;
            _db.LeadTags.Add(new LeadTag { LeadId = leadId, Tag = tag });
        }
        await _db.SaveChangesAsync();
    }

    public async Task<IActionResult> Details(int id)
    {
        var l = await _db.Leads.Include(x => x.Source).Include(x => x.AssignedUser).Include(x => x.Tags).ThenInclude(t => t.Tag)
            .Include(x => x.Interactions).FirstOrDefaultAsync(x => x.Id == id);
        if (l != null)
        {
            var sellers = await _db.Sellers.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            ViewBag.Sellers = sellers;
            ViewBag.OwnerName = sellers.FirstOrDefault(s => s.Id == l.SellerId)?.Name;
            ViewBag.MySellerId = await _db.Sellers.Where(s => s.UserId == UserId).Select(s => (int?)s.Id).FirstOrDefaultAsync();
        }
        return l == null ? NotFound() : View(l);
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Interaction(int id, InteractionType type, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { FlashFail("Descreva a interação."); return RedirectToAction(nameof(Details), new { id }); }
        await _leads.AddInteractionAsync(id, null, type, text.Trim().Truncate(4000));
        Flash("Interação registrada.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Status(int id, LeadStatus status, string? back)
    {
        await _leads.ChangeStatusAsync(id, status);
        await _audit.LogAsync("crm", "lead_status", "Lead", id, newData: status.ToString());
        return back == "pipeline" ? RedirectToAction(nameof(Pipeline)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Convert(int id)
    {
        var c = await _leads.ConvertToCustomerAsync(id);
        await _audit.LogAsync("crm", "lead_convert", "Lead", id, newData: "Customer " + c.Id);
        Flash("Lead convertido em cliente.");
        return RedirectToAction("Details", "Customers", new { id = c.Id });
    }

    [HttpPost, RequirePermission(Perm.CrmDelete)]
    public async Task<IActionResult> Delete(int id)
    {
        var l = await _db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (l.CustomerId != null) { FlashFail("Lead já convertido em cliente não pode ser excluído."); return RedirectToAction(nameof(Details), new { id }); }
        _db.Interactions.RemoveRange(_db.Interactions.Where(i => i.LeadId == id));
        _db.LeadTags.RemoveRange(_db.LeadTags.Where(t => t.LeadId == id));
        _db.Leads.Remove(l);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("crm", "lead_delete", "Lead", id, oldData: l.Name);
        Flash("Lead excluído.");
        return RedirectToAction(nameof(Index));
    }

    // ---- Google Maps scraper ----
    public async Task<IActionResult> Searches()
    {
        ViewBag.Configured = _scraper.IsConfigured;
        ViewBag.Runs = await _db.LeadSyncRuns.OrderByDescending(r => r.Id).Take(15).ToListAsync();
        return View(await _db.ScraperSearches.OrderByDescending(s => s.Id).Take(50).ToListAsync());
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> NewSearch(string query, string? location, string? segment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) { FlashFail("Informe o termo de busca."); return RedirectToAction(nameof(Searches)); }
        try
        {
            var s = await _sync.CreateSearchAsync(query.Trim(), N(location), N(segment), ct);
            await _audit.LogAsync("crm", "scraper_search", "ScraperSearch", s.Id, newData: query);
            Flash("Pesquisa criada.");
        }
        catch (NotConfiguredException) { FlashWarn("Google Maps Scraper não configurado. Veja Configurações."); }
        catch (IntegrationException ex) { FlashFail("Falha na integração: " + ex.Message); }
        return RedirectToAction(nameof(Searches));
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        try
        {
            var run = await _sync.SyncAsync(ct);
            await _audit.LogAsync("crm", "scraper_sync", "LeadSyncRun", run.Id, newData: $"{run.Created} criados, {run.Updated} atualizados");
            Flash($"Sincronização: {run.Found} encontrados, {run.Created} criados, {run.Updated} atualizados, {run.Errors} erros.");
        }
        catch (NotConfiguredException) { FlashWarn("Google Maps Scraper não configurado."); }
        catch (IntegrationException ex) { FlashFail("Falha na integração: " + ex.Message); }
        return RedirectToAction(nameof(Searches));
    }
}

public class CustomerForm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Document { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

[RequirePermission(Perm.CrmView)]
public class CustomersController : AppController
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly LeadService _leads;
    public CustomersController(AppDbContext db, AuditService audit, LeadService leads) { _db = db; _audit = audit; _leads = leads; }

    public async Task<IActionResult> Index(string? q)
    {
        var query = _db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, like) || (c.Document != null && EF.Functions.ILike(c.Document, like)) || (c.Email != null && EF.Functions.ILike(c.Email, like)));
        }
        ViewBag.Q = q;
        return View(await query.OrderBy(c => c.Name).Take(300).ToListAsync());
    }

    [RequirePermission(Perm.CrmEdit)] public IActionResult Create() => View("Form", new CustomerForm());

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Create(CustomerForm f)
    {
        if (string.IsNullOrWhiteSpace(f.Name)) { ModelState.AddModelError(nameof(f.Name), "Nome é obrigatório."); return View("Form", f); }
        var c = new Customer(); Apply(c, f);
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("crm", "customer_create", "Customer", c.Id, newData: c.Name);
        Flash("Cliente criado.");
        return RedirectToAction(nameof(Details), new { id = c.Id });
    }

    [RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Edit(int id)
    {
        var c = await _db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        return View("Form", new CustomerForm
        {
            Id = c.Id, Name = c.Name, Document = c.Document, Email = c.Email, Phone = c.Phone, WhatsApp = c.WhatsApp, Address = c.Address,
            City = c.City, State = c.State, PostalCode = c.PostalCode, Notes = c.Notes, IsActive = c.IsActive
        });
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Edit(int id, CustomerForm f)
    {
        var c = await _db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        if (string.IsNullOrWhiteSpace(f.Name)) { ModelState.AddModelError(nameof(f.Name), "Nome é obrigatório."); return View("Form", f); }
        var old = c.Name; Apply(c, f); c.IsActive = f.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("crm", "customer_update", "Customer", c.Id, oldData: old, newData: c.Name);
        Flash("Cliente atualizado.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static void Apply(Customer c, CustomerForm f)
    {
        c.Name = f.Name.Trim().Truncate(200); c.Document = N(f.Document)?.Truncate(30); c.Email = N(f.Email)?.Truncate(200); c.Phone = N(f.Phone)?.Truncate(40);
        c.WhatsApp = N(f.WhatsApp)?.Truncate(40); c.Address = N(f.Address)?.Truncate(300); c.City = N(f.City)?.Truncate(100);
        c.State = N(f.State)?.ToUpperInvariant().Truncate(2); c.PostalCode = N(f.PostalCode)?.Truncate(10); c.Notes = N(f.Notes)?.Truncate(4000);
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _db.Customers.Include(x => x.Contacts).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewBag.Contracts = await _db.Contracts.Where(x => x.CustomerId == id).OrderByDescending(x => x.Id).ToListAsync();
        ViewBag.Charges = await _db.Charges.Where(x => x.CustomerId == id).OrderByDescending(x => x.Id).ToListAsync();
        ViewBag.Interactions = await _db.Interactions.Where(x => x.CustomerId == id).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync();
        return View(c);
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> Interaction(int id, InteractionType type, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) await _leads.AddInteractionAsync(null, id, type, text.Trim().Truncate(4000));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.CrmEdit)]
    public async Task<IActionResult> AddContact(int id, string name, string? role, string? email, string? phone)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _db.Contacts.Add(new Contact { CustomerId = id, Name = name.Trim().Truncate(200), Role = N(role)?.Truncate(100), Email = N(email)?.Truncate(200), Phone = N(phone)?.Truncate(40) });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Details), new { id });
    }
}


[RequirePermission(Perm.CrmView)]
public class MapController : AppController
{
    private readonly MapService _map;
    public MapController(MapService map) => _map = map;

    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Data(CancellationToken ct) => Json(await _map.GetAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Insights(CancellationToken ct) => Json(await _map.InsightsAsync(ct));
}