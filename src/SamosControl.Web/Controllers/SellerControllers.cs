using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class SellerForm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Document { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public int? UserId { get; set; }
    public string? PixKey { get; set; }
    public string? PixKeyType { get; set; }
    public decimal ImplementationPercent { get; set; }
    public decimal MonthlyPercent { get; set; }
    public int? MonthlyCommissionMonths { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Regions { get; set; }
    public string? Notes { get; set; }
}

[RequirePermission(Perm.SellersView)]
public class SellersController : AppController
{
    private readonly AppDbContext _db;
    private readonly ContractService _contracts;
    public SellersController(AppDbContext db, ContractService contracts) { _db = db; _contracts = contracts; }

    public async Task<IActionResult> Index()
    {
        var sellers = await _db.Sellers.OrderBy(s => s.Name).ToListAsync();
        var sums = await _db.Commissions.GroupBy(c => new { c.SellerId, c.Status }).Select(g => new { g.Key.SellerId, g.Key.Status, Total = g.Sum(x => x.Amount) }).ToListAsync();
        var clients = await _db.Customers.Where(c => c.SellerId != null).GroupBy(c => c.SellerId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync();
        ViewBag.Sums = sums.ToDictionary(x => (x.SellerId, x.Status), x => x.Total);
        ViewBag.Clients = clients.ToDictionary(x => x.Id!.Value, x => x.N);
        return View(sellers);
    }

    [RequirePermission(Perm.SellersManage)]
    public async Task<IActionResult> Create() { await Lookups(); return View("Form", new SellerForm()); }

    [RequirePermission(Perm.SellersManage)]
    public async Task<IActionResult> Edit(int id)
    {
        var s = await _db.Sellers.FindAsync(id);
        if (s == null) return NotFound();
        await Lookups();
        return View("Form", new SellerForm
        {
            Id = s.Id, Name = s.Name, Document = s.Document, Email = s.Email, Phone = s.Phone, Address = s.Address, UserId = s.UserId, PixKey = s.PixKey, PixKeyType = s.PixKeyType,
            ImplementationPercent = s.ImplementationPercent, MonthlyPercent = s.MonthlyPercent, MonthlyCommissionMonths = s.MonthlyCommissionMonths, IsActive = s.IsActive, Regions = s.Regions, Notes = s.Notes
        });
    }

    private async Task Lookups() => ViewBag.Users = await _db.Users.Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync();

    [HttpPost, RequirePermission(Perm.SellersManage)]
    public async Task<IActionResult> Save(SellerForm f)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(f.Name)) throw new ArgumentException("Informe o nome.");
            CommissionService.ValidatePercent(f.ImplementationPercent, "Comissão da implantação");
            CommissionService.ValidatePercent(f.MonthlyPercent, "Comissão da mensalidade");
            var s = f.Id == 0 ? new Seller() : await _db.Sellers.FindAsync(f.Id) ?? throw new KeyNotFoundException();
            s.Name = f.Name.Trim(); s.Document = f.Document?.Trim(); s.Email = f.Email?.Trim(); s.Phone = f.Phone?.Trim(); s.Address = f.Address?.Trim();
            s.UserId = f.UserId; s.PixKey = f.PixKey?.Trim(); s.PixKeyType = f.PixKeyType; s.ImplementationPercent = f.ImplementationPercent; s.MonthlyPercent = f.MonthlyPercent;
            s.MonthlyCommissionMonths = f.MonthlyCommissionMonths; s.IsActive = f.IsActive; s.Notes = f.Notes;
            s.Regions = string.IsNullOrWhiteSpace(f.Regions) ? null : string.Join(',', f.Regions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length == 2).Distinct());
            if (f.Id == 0) _db.Sellers.Add(s);
            await _db.SaveChangesAsync();
            Flash("Vendedor salvo. Alterações de percentual valem para novos contratos; os já emitidos mantêm o percentual combinado.");
            return RedirectToAction(nameof(Details), new { id = s.Id });
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { FlashFail(ex.Message); await Lookups(); return View("Form", f); }
    }

    public async Task<IActionResult> Details(int id)
    {
        var s = await _db.Sellers.FindAsync(id);
        if (s == null) return NotFound();
        ViewBag.Customers = await _db.Customers.Where(c => c.SellerId == id).OrderBy(c => c.Name).ToListAsync();
        ViewBag.Commissions = await _db.Commissions.Include(c => c.Customer).Where(c => c.SellerId == id).OrderByDescending(c => c.Id).Take(100).ToListAsync();
        ViewBag.Templates = await _db.ContractTemplates.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
        return View(s);
    }

    /// <summary>Creates (and optionally sends) the partnership contract between the company and the seller.</summary>
    [HttpPost, RequirePermission(Perm.ContractsCreate)]
    public async Task<IActionResult> PartnerContract(int id, int templateId, bool send)
    {
        try
        {
            var c = await _contracts.CreatePartnershipAsync(templateId, id);
            if (send)
            {
                var res = await _contracts.SendForSignatureAsync(c.Id);
                if (res.All(r => r.Sent)) Flash("Contrato de parceria criado e link enviado por WhatsApp ao vendedor e ao representante da empresa.");
                else FlashWarn("Contrato criado, mas nem todos os links foram enviados: " + string.Join("; ", res.Where(r => !r.Sent).Select(r => $"{r.Party} ({r.Message})")));
            }
            else Flash("Contrato de parceria criado (rascunho).");
            return RedirectToAction("Details", "Contracts", new { id = c.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException) { FlashFail(ex.Message); return RedirectToAction(nameof(Details), new { id }); }
    }
}

[RequirePermission(Perm.SellersView)]
public class CommissionsController : AppController
{
    private readonly AppDbContext _db;
    private readonly CommissionService _svc;
    private readonly SettingsService _settings;
    public CommissionsController(AppDbContext db, CommissionService svc, SettingsService settings) { _db = db; _svc = svc; _settings = settings; }

    public async Task<IActionResult> Index(CommissionStatus? status, int? sellerId)
    {
        var q = _db.Commissions.Include(c => c.Seller).Include(c => c.Customer).AsQueryable();
        if (status.HasValue) q = q.Where(c => c.Status == status);
        if (sellerId.HasValue) q = q.Where(c => c.SellerId == sellerId);
        ViewBag.Status = status; ViewBag.SellerId = sellerId;
        ViewBag.Sellers = await _db.Sellers.OrderBy(s => s.Name).ToListAsync();
        ViewBag.Auto = string.Equals(_settings.Get(Integrations.SettingKeys.PayoutMode), "efi", StringComparison.OrdinalIgnoreCase);
        return View(await q.OrderByDescending(c => c.Id).Take(300).ToListAsync());
    }

    [HttpPost, RequirePermission(Perm.CommissionsPay)]
    public async Task<IActionResult> Process()
    {
        var r = await _svc.ProcessAsync();
        Flash($"Processado: {r.Created} comissão(ões) gerada(s), {r.Paid} paga(s), {r.Failed} com erro, {r.Cancelled} cancelada(s).");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, RequirePermission(Perm.CommissionsPay)]
    public async Task<IActionResult> MarkPaid(int id, string? reference) => await Act(() => _svc.MarkPaidAsync(id, reference), "Comissão marcada como paga.");

    [HttpPost, RequirePermission(Perm.CommissionsPay)]
    public async Task<IActionResult> Retry(int id) => await Act(() => _svc.RetryAsync(id), "Comissão recolocada na fila.");

    [HttpPost, RequirePermission(Perm.CommissionsPay)]
    public async Task<IActionResult> Cancel(int id) => await Act(() => _svc.CancelAsync(id), "Comissão cancelada.");

    private async Task<IActionResult> Act(Func<Task> a, string ok)
    {
        try { await a(); Flash(ok); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { FlashFail(ex.Message); }
        return RedirectToAction(nameof(Index));
    }
}
