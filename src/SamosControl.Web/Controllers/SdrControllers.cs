using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

[RequirePermission(Perm.SettingsManage)]
public class SdrController : AppController
{
    private readonly AppDbContext _db;
    private readonly SdrService _sdr;
    public SdrController(AppDbContext db, SdrService sdr) { _db = db; _sdr = sdr; }

    public async Task<IActionResult> Index()
    {
        var list = await _db.SdrCampaigns.OrderByDescending(c => c.Id).ToListAsync();
        var stats = new Dictionary<int, SdrStats>();
        foreach (var c in list) stats[c.Id] = await _sdr.StatsAsync(c.Id);
        ViewBag.Stats = stats;
        ViewBag.Sellers = await _db.Sellers.ToDictionaryAsync(s => s.Id, s => s.Name);
        return View(list);
    }

    private async Task Lookups() => ViewBag.Sellers = await _db.Sellers.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

    public async Task<IActionResult> Edit(int? id)
    {
        await Lookups();
        var c = id.HasValue ? await _db.SdrCampaigns.FindAsync(id.Value) : new SdrCampaign { Name = "Prospecção automática", Variants = SdrService.DefaultVariants, FollowUpText = SdrService.DefaultFollowUp };
        return c == null ? NotFound() : View(c);
    }

    [HttpPost]
    public async Task<IActionResult> Save(SdrCampaign f)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(f.Name)) throw new ArgumentException("Informe o nome da campanha.");
            if (f.StartHour < 0 || f.EndHour > 24 || f.StartHour >= f.EndHour) throw new ArgumentException("Horário inválido (início deve ser menor que o fim).");
            if (f.DailyLimit < 1 || f.DailyLimit > 500) throw new ArgumentException("O limite diário deve ficar entre 1 e 500.");
            if (SdrService.SplitVariants(f.Variants).Count == 0) throw new ArgumentException("Informe ao menos uma mensagem inicial.");
            var c = f.Id == 0 ? new SdrCampaign() : await _db.SdrCampaigns.FindAsync(f.Id) ?? throw new KeyNotFoundException();
            c.Name = f.Name.Trim(); c.SellerId = f.SellerId; c.DailyLimit = f.DailyLimit; c.StartHour = f.StartHour; c.EndHour = f.EndHour;
            c.MinDelaySec = Math.Max(20, f.MinDelaySec); c.MaxDelaySec = Math.Max(c.MinDelaySec, f.MaxDelaySec);
            c.Variants = f.Variants.Trim(); c.FollowUpText = f.FollowUpText?.Trim(); c.FollowUpHours = Math.Max(1, f.FollowUpHours); c.MaxFollowUps = Math.Clamp(f.MaxFollowUps, 0, 3);
            c.Pitch = f.Pitch?.Trim(); c.AiReplies = f.AiReplies; c.HandoffAlertMinutes = Math.Max(1, f.HandoffAlertMinutes);
            c.FilterStatus = f.FilterStatus; c.FilterState = string.IsNullOrWhiteSpace(f.FilterState) ? null : f.FilterState.Trim().ToUpperInvariant(); c.FilterSegment = f.FilterSegment?.Trim();
            if (f.Id == 0) _db.SdrCampaigns.Add(c);
            await _db.SaveChangesAsync();
            var n = await _sdr.EnqueueAsync(c);
            Flash($"Campanha salva. {n} lead(s) na fila. Ela começa pausada: revise e clique em Ativar.");
            return RedirectToAction(nameof(Details), new { id = c.Id });
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            FlashFail(ex.Message); await Lookups(); return View("Edit", f);
        }
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _db.SdrCampaigns.FindAsync(id);
        if (c == null) return NotFound();
        ViewBag.Stats = await _sdr.StatsAsync(id);
        ViewBag.SellerName = c.SellerId.HasValue ? await _db.Sellers.Where(s => s.Id == c.SellerId).Select(s => s.Name + " (" + s.WaNumber + ") — " + s.WaStatus).FirstOrDefaultAsync() : "Número da empresa";
        ViewBag.Contacts = await _db.SdrContacts.Include(x => x.Lead).Where(x => x.CampaignId == id).OrderByDescending(x => x.UpdatedAt).Take(100).ToListAsync();
        ViewBag.Sellers = await _db.Sellers.ToDictionaryAsync(s => s.Id, s => s.Name);
        return View(c);
    }

    [HttpPost]
    public async Task<IActionResult> Toggle(int id)
    {
        var c = await _db.SdrCampaigns.FindAsync(id);
        if (c == null) return NotFound();
        c.IsActive = !c.IsActive; c.NextSendAt = null;
        await _db.SaveChangesAsync();
        Flash(c.IsActive ? "SDR ativado. Os envios respeitam horário, limite diário e intervalos." : "SDR pausado.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Enqueue(int id)
    {
        var c = await _db.SdrCampaigns.FindAsync(id);
        if (c == null) return NotFound();
        Flash($"{await _sdr.EnqueueAsync(c)} novo(s) lead(s) adicionados à fila.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.SdrCampaigns.FindAsync(id);
        if (c != null) { _db.SdrContacts.RemoveRange(_db.SdrContacts.Where(x => x.CampaignId == id)); _db.SdrCampaigns.Remove(c); await _db.SaveChangesAsync(); Flash("Campanha excluída."); }
        return RedirectToAction(nameof(Index));
    }
}

[RequirePermission(Perm.SettingsManage)]
public class TrainingController : AppController
{
    private readonly AppDbContext _db;
    private readonly KnowledgeService _kb;
    public TrainingController(AppDbContext db, KnowledgeService kb) { _db = db; _kb = kb; }

    public async Task<IActionResult> Index()
    {
        await _kb.EnsureSeedAsync();
        var all = await _db.AiKnowledge.OrderBy(k => k.Topic).ThenBy(k => k.Id).ToListAsync();
        ViewBag.AiConfigured = _kb.AiConfigured;
        return View(all);
    }

    [HttpPost]
    public async Task<IActionResult> Answer(int id, string? answer, bool skip = false)
    {
        var k = await _db.AiKnowledge.FindAsync(id);
        if (k == null) return NotFound();
        if (skip) { k.Status = KnowledgeStatus.Ignorada; Flash("Pergunta dispensada."); }
        else if (string.IsNullOrWhiteSpace(answer)) FlashFail("Escreva a resposta antes de salvar.");
        else { k.Answer = answer.Trim(); k.Status = KnowledgeStatus.Respondida; Flash("Aprendido. A IA já usa essa informação."); }
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), null, "q" + id);
    }

    [HttpPost]
    public async Task<IActionResult> Add(string topic, string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) { FlashFail("Informe a pergunta e a resposta."); return RedirectToAction(nameof(Index)); }
        _db.AiKnowledge.Add(new AiKnowledge { Topic = string.IsNullOrWhiteSpace(topic) ? "Geral" : topic.Trim(), Question = question.Trim(), Answer = answer.Trim(), Status = KnowledgeStatus.Respondida, Origin = "Manual" });
        await _db.SaveChangesAsync();
        Flash("Conhecimento adicionado.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Reopen(int id)
    {
        var k = await _db.AiKnowledge.FindAsync(id);
        if (k != null) { k.Status = KnowledgeStatus.Pendente; await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var k = await _db.AiKnowledge.FindAsync(id);
        if (k != null) { _db.AiKnowledge.Remove(k); await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Generate(CancellationToken ct)
    {
        if (!_kb.AiConfigured) { FlashWarn("Configure o token da IA em Configurações para gerar perguntas novas."); return RedirectToAction(nameof(Index)); }
        try { var n = await _kb.GenerateQuestionsAsync(ct); Flash(n > 0 ? $"A IA fez {n} nova(s) pergunta(s) com base no que você já ensinou." : "A IA não encontrou novas lacunas agora."); }
        catch (Exception ex) { FlashFail("Não foi possível gerar perguntas: " + ex.Message); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Test([FromBody] TestVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Question) || vm.Question.Length > 600) return Json(new { error = "Pergunta inválida." });
        try { var (reply, gap) = await _kb.TestAsync(vm.Question.Trim(), ct); return Json(new { reply, gap }); }
        catch (Exception ex) { return Json(new { error = ex.Message }); }
    }
}
public record TestVm(string Question);
