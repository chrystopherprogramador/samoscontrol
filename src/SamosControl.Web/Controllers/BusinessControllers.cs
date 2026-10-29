using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class ContractCreateForm
{
    public int TemplateId { get; set; }
    public int CustomerId { get; set; }
    public string? Title { get; set; }
    public string? Service { get; set; }
    public decimal Value { get; set; }
    public DateTime? Due { get; set; }
    public decimal ImplementationValue { get; set; }
    public decimal Discount { get; set; }
    public int Installments { get; set; } = 1;
    public DateTime? FirstDue { get; set; }
    public decimal MonthlyValue { get; set; }
    public DateTime? MonthlyStart { get; set; }
    public int? MonthlyMonths { get; set; }
    public BillingType BillingType { get; set; } = BillingType.Pix;
    public int? SellerId { get; set; }
}

[RequirePermission(Perm.ContractsView)]
public class ContractsController : AppController
{
    private readonly AppDbContext _db;
    private readonly ContractService _svc;
    private readonly WhatsAppService _wa;
    private readonly ContractBillingService _cb;
    public ContractsController(AppDbContext db, ContractService svc, WhatsAppService wa, ContractBillingService cb) { _db = db; _svc = svc; _wa = wa; _cb = cb; }

    public async Task<IActionResult> Index(ContractStatus? status)
    {
        var q = _db.Contracts.Include(c => c.Customer).AsQueryable();
        if (status.HasValue) q = q.Where(c => c.Status == status);
        ViewBag.Status = status;
        return View(await q.OrderByDescending(c => c.Id).Take(300).ToListAsync());
    }

    [RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> Create(int? customerId)
    {
        await Lookups();
        return View(new ContractCreateForm { CustomerId = customerId ?? 0 });
    }

    private async Task Lookups()
    {
        ViewBag.Templates = await _db.ContractTemplates.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
        ViewBag.Customers = await _db.Customers.Where(c => c.IsActive).OrderBy(c => c.Name).Take(1000).ToListAsync();
        ViewBag.Sellers = await _db.Sellers.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.OwnerConfigured = _svc.OwnerInfo().Configured;
    }

    [HttpPost, RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> Create(ContractCreateForm f)
    {
        try
        {
            var tpl = await _db.ContractTemplates.FindAsync(f.TemplateId);
            if (tpl != null && (ContractService.IsPartnershipTemplate(tpl) || (f.CustomerId == 0 && f.SellerId.HasValue)))
            {
                if (f.SellerId is null) throw new ArgumentException("Selecione o vendedor do contrato de parceria.");
                var pc = await _svc.CreatePartnershipAsync(f.TemplateId, f.SellerId.Value, f.Title);
                Flash("Contrato de parceria criado. Revise e envie para assinatura.");
                return RedirectToAction(nameof(Details), new { id = pc.Id });
            }
            var terms = new ContractTerms(f.ImplementationValue, f.Discount, f.Installments, UtcDate(f.FirstDue), f.MonthlyValue, UtcDate(f.MonthlyStart), f.MonthlyMonths, f.BillingType);
            var c = await _svc.CreateAsync(f.TemplateId, f.CustomerId, f.Title?.Trim(), f.Service?.Trim(), f.Value, UtcDate(f.Due), terms, f.SellerId);
            Flash("Contrato criado.");
            return RedirectToAction(nameof(Details), new { id = c.Id });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException) { FlashFail(ex.Message); await Lookups(); return View(f); }
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _db.Contracts.Include(x => x.Customer).Include(x => x.Parties).ThenInclude(p => p.Signature).Include(x => x.Events.OrderByDescending(e => e.At)).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewBag.Svc = _svc;
        ViewBag.Charges = await _db.Charges.Where(x => x.ContractId == id).OrderBy(x => x.DueDate).ToListAsync();
        ViewBag.WaConfigured = _wa.IsConfigured;
        return View(c);
    }

    [HttpPost, RequirePermission(Perm.BillingCreate)]
    public async Task<IActionResult> GenerateCharges(int id)
    {
        try
        {
            var r = await _cb.GenerateAsync(id);
            if (r.Failed > 0) FlashWarn($"{r.Created} cobrança(s) criada(s); {r.Failed} falharam (veja a trilha de eventos).");
            else Flash(r.Created > 0 ? $"{r.Created} cobrança(s) criada(s)." : "Nenhuma cobrança nova a gerar.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.BillingCreate)]
    public async Task<IActionResult> EndBilling(int id)
    {
        try { var n = await _cb.EndBillingAsync(id); Flash($"Cobrança recorrente encerrada; {n} cobrança(s) futura(s) cancelada(s)."); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> AddParty(int id, string role, string name, string? doc, string? email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(name)) FlashFail("Informe o nome da parte.");
        else
        {
            try { await _svc.AddPartyAsync(id, string.IsNullOrWhiteSpace(role) ? "TESTEMUNHA" : role.Trim().ToUpperInvariant(), name.Trim(), doc?.Trim(), email?.Trim(), phone?.Trim()); Flash("Parte adicionada."); }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> Send(int id)
    {
        try
        {
            var res = await _svc.SendForSignatureAsync(id);
            var ok = res.Count(r => r.Sent);
            if (ok == res.Count) Flash($"Contrato enviado: link de assinatura entregue por WhatsApp a {ok} signatário(s).");
            else FlashWarn("Contrato liberado para assinatura, mas nem todos os links foram enviados: " + string.Join("; ", res.Where(r => !r.Sent).Select(r => $"{r.Party} ({r.Message})")) + ". Use \"Reenviar link\".");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> Cancel(int id)
    {
        try { await _svc.CancelAsync(id); Flash("Contrato cancelado."); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> SendLink(int id, int partyId)
    {
        try
        {
            var r = await _svc.ResendLinkAsync(id, partyId);
            if (r.Sent) Flash("Link enviado por WhatsApp."); else FlashFail($"Não foi possível enviar: {r.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Pdf(int id)
    {
        var c = await _db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        return File(await _svc.GetPdfAsync(id), "application/pdf", $"contrato-{id}.pdf");
    }
}

public class TemplateForm { public int Id { get; set; } public string Name { get; set; } = ""; public string Body { get; set; } = ""; public bool IsActive { get; set; } = true; }

[RequirePermission(Perm.ContractsTemplates)]
public class TemplatesController : AppController
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    public TemplatesController(AppDbContext db, AuditService audit) { _db = db; _audit = audit; }

    public async Task<IActionResult> Index() => View(await _db.ContractTemplates.OrderBy(t => t.Name).ToListAsync());
    public IActionResult Create() => View("Form", new TemplateForm());

    [HttpPost]
    public async Task<IActionResult> Create(TemplateForm f)
    {
        if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Body)) { FlashFail("Nome e texto são obrigatórios."); return View("Form", f); }
        var t = new ContractTemplate { Name = f.Name.Trim(), Body = f.Body, IsActive = f.IsActive };
        _db.ContractTemplates.Add(t);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "template_create", "ContractTemplate", t.Id, newData: t.Name);
        Flash("Modelo criado.");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var t = await _db.ContractTemplates.FindAsync(id);
        return t == null ? NotFound() : View("Form", new TemplateForm { Id = t.Id, Name = t.Name, Body = t.Body, IsActive = t.IsActive });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, TemplateForm f)
    {
        var t = await _db.ContractTemplates.FindAsync(id);
        if (t == null) return NotFound();
        if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Body)) { FlashFail("Nome e texto são obrigatórios."); return View("Form", f); }
        t.Name = f.Name.Trim(); t.Body = f.Body; t.IsActive = f.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "template_update", "ContractTemplate", t.Id, newData: t.Name);
        Flash("Modelo salvo. Contratos já criados não são alterados.");
        return RedirectToAction(nameof(Index));
    }
}

public class ChargeForm
{
    public int CustomerId { get; set; }
    public int? ContractId { get; set; }
    public string? Service { get; set; }
    public decimal Amount { get; set; }
    public DateTime? Due { get; set; }
    public BillingType Type { get; set; } = BillingType.Pix;
    public string? Description { get; set; }
}

[RequirePermission(Perm.BillingView)]
public class ChargesController : AppController
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly WhatsAppService _wa;
    public ChargesController(AppDbContext db, BillingService billing, WhatsAppService wa) { _db = db; _billing = billing; _wa = wa; }

    public async Task<IActionResult> Index(ChargeStatus? status)
    {
        var q = _db.Charges.Include(c => c.Customer).AsQueryable();
        if (status.HasValue) q = q.Where(c => c.Status == status);
        ViewBag.Status = status;
        return View(await q.OrderByDescending(c => c.Id).Take(300).ToListAsync());
    }

    [RequirePermission(Perm.BillingCreate)]
    public async Task<IActionResult> Create(int? customerId)
    {
        ViewBag.Customers = await _db.Customers.Where(c => c.IsActive).OrderBy(c => c.Name).Take(1000).ToListAsync();
        return View(new ChargeForm { CustomerId = customerId ?? 0, Due = DateTime.Today.AddDays(3) });
    }

    [HttpPost, RequirePermission(Perm.BillingCreate)]
    public async Task<IActionResult> Create(ChargeForm f, CancellationToken ct)
    {
        if (f.Amount <= 0 || f.Due == null || f.CustomerId == 0) { FlashFail("Informe cliente, valor positivo e vencimento."); return RedirectToAction(nameof(Create), new { customerId = f.CustomerId }); }
        try
        {
            var c = await _billing.CreateChargeAsync(f.CustomerId, f.ContractId, f.Service?.Trim(), f.Amount, UtcDate(f.Due)!.Value, f.Type, f.Description?.Trim(), ct);
            Flash("Cobrança criada.");
            return RedirectToAction(nameof(Details), new { id = c.Id });
        }
        catch (NotConfiguredException ex) { FlashWarn(ex.Message + " Veja Configurações."); }
        catch (IntegrationException ex) { FlashFail("Falha no provedor de cobrança: " + ex.Message); }
        catch (ArgumentException ex) { FlashFail(ex.Message); }
        catch (KeyNotFoundException ex) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Create), new { customerId = f.CustomerId });
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _db.Charges.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
        return c == null ? NotFound() : View(c);
    }

    [HttpPost]
    public async Task<IActionResult> Refresh(int id, CancellationToken ct)
    {
        try { await _billing.RefreshAsync(id, ct); Flash("Cobrança atualizada."); }
        catch (NotConfiguredException ex) { FlashWarn(ex.Message); }
        catch (IntegrationException ex) { FlashFail("Falha no provedor de cobrança: " + ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.BillingCancel)]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        try { await _billing.CancelAsync(id); Flash("Cobrança cancelada."); }
        catch (NotConfiguredException ex) { FlashWarn(ex.Message); }
        catch (Exception ex) when (ex is IntegrationException or InvalidOperationException) { FlashFail("Não foi possível cancelar: " + ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, RequirePermission(Perm.WhatsAppSend)]
    public async Task<IActionResult> SendWhatsApp(int id)
    {
        try { await _wa.SendChargeLinkAsync(id, UserId); Flash("Cobrança enviada por WhatsApp."); }
        catch (NotConfiguredException) { FlashWarn("Evolution API não configurada."); }
        catch (Exception ex) when (ex is IntegrationException or InvalidOperationException) { FlashFail("Falha no envio: " + ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }
}

