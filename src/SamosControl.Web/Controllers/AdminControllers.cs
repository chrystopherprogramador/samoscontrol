using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class UserForm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Password { get; set; }
}

[RequirePermission(Perm.UsersManage)]
public class UsersController : AppController
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly PermissionService _perms;
    private readonly AuthService _auth;
    private readonly IConfiguration _cfg;
    public UsersController(AppDbContext db, AuditService audit, PermissionService perms, AuthService auth, IConfiguration cfg) { _db = db; _audit = audit; _perms = perms; _auth = auth; _cfg = cfg; }

    public async Task<IActionResult> Index() => View(await _db.Users.Include(u => u.Role).OrderBy(u => u.Name).ToListAsync());

    private async Task Roles() => ViewBag.Roles = await _db.Roles.OrderBy(r => r.Name).ToListAsync();

    public async Task<IActionResult> Create() { await Roles(); return View("Form", new UserForm()); }

    [HttpPost]
    public async Task<IActionResult> Create(UserForm f)
    {
        await Roles();
        var email = (f.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(f.Name) || !email.Contains('@')) { FlashFail("Nome e e-mail válidos são obrigatórios."); return View("Form", f); }
        var pwErr = AuthService.ValidatePassword(f.Password);
        if (pwErr != null) { FlashFail(pwErr); return View("Form", f); }
        if (await _db.Users.AnyAsync(u => u.Email == email)) { FlashFail("Já existe um usuário com este e-mail."); return View("Form", f); }
        if (!await _db.Roles.AnyAsync(r => r.Id == f.RoleId)) { FlashFail("Selecione um perfil."); return View("Form", f); }
        var u = new User { Name = f.Name.Trim(), Email = email, Phone = f.Phone?.Trim(), RoleId = f.RoleId, IsActive = f.IsActive, PasswordHash = AuthService.Hash(f.Password!) };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("users", "user_create", "User", u.Id, newData: $"{u.Email} role={u.RoleId}");
        Flash("Usuário criado.");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null) return NotFound();
        await Roles();
        return View("Form", new UserForm { Id = u.Id, Name = u.Name, Email = u.Email, Phone = u.Phone, RoleId = u.RoleId, IsActive = u.IsActive });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, UserForm f)
    {
        await Roles();
        var u = await _db.Users.Include(x => x.Role).FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return NotFound();
        if (string.IsNullOrWhiteSpace(f.Name)) { FlashFail("Nome é obrigatório."); return View("Form", f); }
        if (!await _db.Roles.AnyAsync(r => r.Id == f.RoleId)) { FlashFail("Selecione um perfil."); return View("Form", f); }
        if (!string.IsNullOrEmpty(f.Password))
        {
            var pwErr = AuthService.ValidatePassword(f.Password);
            if (pwErr != null) { FlashFail(pwErr); return View("Form", f); }
        }
        if (await LosesLastAdmin(u, f)) { FlashFail("Não é possível remover o último administrador ativo."); return View("Form", f); }
        var old = $"role={u.RoleId} active={u.IsActive}";
        u.Name = f.Name.Trim(); u.Phone = f.Phone?.Trim(); u.RoleId = f.RoleId; u.IsActive = f.IsActive;
        if (!string.IsNullOrEmpty(f.Password)) u.PasswordHash = AuthService.Hash(f.Password);
        await _db.SaveChangesAsync();
        _perms.Invalidate(u.Id);
        await _audit.LogAsync("users", "user_update", "User", u.Id, oldData: old, newData: $"role={u.RoleId} active={u.IsActive}{(string.IsNullOrEmpty(f.Password) ? "" : " password_changed")}");
        Flash("Usuário atualizado.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> LosesLastAdmin(User u, UserForm f)
    {
        var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "ADMIN");
        if (adminRole == null || u.RoleId != adminRole.Id || !u.IsActive) return false;
        var stays = f.IsActive && f.RoleId == adminRole.Id;
        if (stays) return false;
        return !await _db.Users.AnyAsync(x => x.Id != u.Id && x.IsActive && x.RoleId == adminRole.Id);
    }

    [HttpPost]
    public async Task<IActionResult> ResetLink(int id)
    {
        var token = await _auth.CreatePasswordResetAsync(id);
        if (token == null) { FlashFail("Usuário não encontrado ou inativo."); return RedirectToAction(nameof(Index)); }
        await _audit.LogAsync("users", "password_reset_link", "User", id);
        var baseUrl = (_cfg["APP_URL"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        ViewBag.Link = $"{baseUrl}/Account/Reset?token={Uri.EscapeDataString(token)}";
        var u = await _db.Users.FindAsync(id);
        return View("ResetLink", u);
    }
}

public class RoleForm { public int Id { get; set; } public string Name { get; set; } = ""; public string? Description { get; set; } public List<string> Permissions { get; set; } = new(); }

[RequirePermission(Perm.RolesManage)]
public class RolesController : AppController
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly PermissionService _perms;
    public RolesController(AppDbContext db, AuditService audit, PermissionService perms) { _db = db; _audit = audit; _perms = perms; }

    public async Task<IActionResult> Index()
    {
        ViewBag.UserCounts = await _db.Users.GroupBy(u => u.RoleId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C);
        return View(await _db.Roles.Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync());
    }

    public IActionResult Create() => View("Form", new RoleForm());

    [HttpPost]
    public async Task<IActionResult> Create(RoleForm f)
    {
        var name = (f.Name ?? "").Trim().ToUpperInvariant();
        if (name.Length < 2) { FlashFail("Informe o nome do perfil."); return View("Form", f); }
        if (await _db.Roles.AnyAsync(r => r.Name == name)) { FlashFail("Já existe um perfil com este nome."); return View("Form", f); }
        var r = new Role { Name = name, Description = f.Description?.Trim() };
        foreach (var p in f.Permissions.Where(Perm.All.Contains).Distinct()) r.Permissions.Add(new RolePermission { Permission = p });
        _db.Roles.Add(r);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("users", "role_create", "Role", r.Id, newData: $"{r.Name}: {string.Join(",", f.Permissions)}");
        Flash("Perfil criado.");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var r = await _db.Roles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id);
        return r == null ? NotFound() : View("Form", new RoleForm { Id = r.Id, Name = r.Name, Description = r.Description, Permissions = r.Permissions.Select(p => p.Permission).ToList() });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, RoleForm f)
    {
        var r = await _db.Roles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();
        var wanted = f.Permissions.Where(Perm.All.Contains).Distinct().ToList();
        if (r.Name == "ADMIN") wanted = Perm.All.ToList(); // ADMIN always keeps every permission, avoiding lock-out
        var old = string.Join(",", r.Permissions.Select(p => p.Permission).OrderBy(x => x));
        _db.RolePermissions.RemoveRange(r.Permissions);
        foreach (var p in wanted) _db.RolePermissions.Add(new RolePermission { RoleId = r.Id, Permission = p });
        r.Description = f.Description?.Trim();
        await _db.SaveChangesAsync();
        foreach (var uid in await _db.Users.Where(u => u.RoleId == id).Select(u => u.Id).ToListAsync()) _perms.Invalidate(uid);
        await _audit.LogAsync("users", "role_update", "Role", r.Id, oldData: old, newData: string.Join(",", wanted.OrderBy(x => x)));
        Flash("Perfil atualizado.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _db.Roles.FindAsync(id);
        if (r == null) return NotFound();
        if (r.IsSystem) { FlashFail("Perfis de sistema não podem ser excluídos."); return RedirectToAction(nameof(Index)); }
        if (await _db.Users.AnyAsync(u => u.RoleId == id)) { FlashFail("Há usuários neste perfil."); return RedirectToAction(nameof(Index)); }
        _db.RolePermissions.RemoveRange(_db.RolePermissions.Where(p => p.RoleId == id));
        _db.Roles.Remove(r);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("users", "role_delete", "Role", id, oldData: r.Name);
        Flash("Perfil excluído.");
        return RedirectToAction(nameof(Index));
    }
}

public record SettingField(string Key, string Label, bool Secret = false, string? Help = null, bool Multiline = false);
public record SettingCategory(string Id, string Title, string Icon, SettingField[] Fields);

public static class SettingCatalog
{
    public static readonly SettingCategory[] All =
    {
        new("empresa", "Empresa", "bi-building", new SettingField[]
        {
            new(SettingKeys.CompanyName, "Razão social / nome"), new(SettingKeys.CompanyDocument, "CNPJ"), new(SettingKeys.CompanyAddress, "Endereço"),
            new(SettingKeys.OwnerName, "Dono / representante legal", false, "Assina TODOS os contratos como CONTRATADA e recebe o link por WhatsApp."),
            new(SettingKeys.OwnerDocument, "CPF do representante"),
            new(SettingKeys.OwnerPhone, "WhatsApp do representante", false, "Com DDI/DDD, ex.: 5562999999999. Se vazio, usa o 1º número dos alertas de monitoramento."),
            new(SettingKeys.OwnerEmail, "E-mail do representante"),
            new(SettingKeys.OwnerTitle, "Cargo", false, "Ex.: Sócio-administrador"),
            new(SettingKeys.PayoutMode, "Repasse de comissões", false, "manual (padrão: você paga e marca como paga) ou efi (Pix automático pela Efí; exige escopo pix.send, webhook na chave Pix e limite de envio liberado).")
        }),
        new("scraper", "Google Scraper", "bi-geo-alt", new SettingField[]
        {
            new(SettingKeys.GoogleUrl, "URL base", false, "Ex.: https://scraper.seudominio.com"),
            new(SettingKeys.GoogleMode, "Modo", false, "gosom = google-maps-scraper (jobs + CSV). Vazio = API JSON genérica (campos abaixo)."),
            new(SettingKeys.GoogleBasicUser, "Usuário (Basic Auth)", false, "Para gateway com HTTP Basic."),
            new(SettingKeys.GoogleBasicPassword, "Senha (Basic Auth)", true),
            new(SettingKeys.GoogleApiKey, "API key", true), new(SettingKeys.GoogleApiKeyHeader, "Cabeçalho da API key", false, "Nome do header que sua API espera."),
            new(SettingKeys.GoogleCreatePath, "Caminho para criar pesquisa", false, "Conforme a documentação da sua API."),
            new(SettingKeys.GoogleCreateBody, "Corpo JSON da criação", false, "Use {{query}} e {{location}}.", true),
            new(SettingKeys.GoogleSearchIdPath, "Caminho JSON do ID da pesquisa na resposta"),
            new(SettingKeys.GoogleResultsPath, "Caminho para buscar resultados", false, "Use {{searchId}} para o ID da pesquisa."),
            new(SettingKeys.GoogleItemsPath, "Caminho JSON da lista de itens"),
            new(SettingKeys.GoogleMapPrefix + "NAME", "Campo: nome"), new(SettingKeys.GoogleMapPrefix + "PHONE", "Campo: telefone"),
            new(SettingKeys.GoogleMapPrefix + "ADDRESS", "Campo: endereço"), new(SettingKeys.GoogleMapPrefix + "WEBSITE", "Campo: website"),
            new(SettingKeys.GoogleMapPrefix + "PLACE_ID", "Campo: Place ID"), new(SettingKeys.GoogleMapPrefix + "CITY", "Campo: cidade"),
            new(SettingKeys.GoogleMapPrefix + "STATE", "Campo: UF"), new(SettingKeys.GoogleMapPrefix + "CATEGORY", "Campo: categoria"),
            new(SettingKeys.GoogleMapPrefix + "EMAIL", "Campo: e-mail"),
            new(SettingKeys.GoogleSyncMinutes, "Sincronizar a cada (min)")
        }),
        new("cobranca", "Cobrança (provedor)", "bi-credit-card", new SettingField[]
        {
            new(SettingKeys.BillingProvider, "Provedor ativo", false, "Digite asaas (padrão) ou efi. Vale para novas cobranças; as já emitidas continuam no provedor de origem.")
        }),
        new("asaas", "Asaas", "bi-cash-coin", new SettingField[]
        {
            new(SettingKeys.AsaasUrl, "URL da API", false, "Sandbox: https://sandbox.asaas.com/api/v3 · Produção: https://api.asaas.com/v3"),
            new(SettingKeys.AsaasKey, "API key", true), new(SettingKeys.AsaasWebhookToken, "Token do webhook", true, "Configure o mesmo valor no Asaas (authToken). Obrigatório para receber webhooks.")
        }),
        new("efi", "Efí Bank", "bi-bank", new SettingField[]
        {
            new(SettingKeys.EfiClientId, "Client_Id"), new(SettingKeys.EfiClientSecret, "Client_Secret", true),
            new(SettingKeys.EfiSandbox, "Homologação (sandbox)?", false, "true para homologação; vazio ou false para produção."),
            new(SettingKeys.EfiPixKey, "Chave Pix", false, "Chave Pix da conta Efí que recebe (obrigatória para cobranças Pix)."),
            new(SettingKeys.EfiCertPath, "Caminho do certificado .p12", false, "Ex.: /data/efi/producao.p12 (dentro do volume /data). Alternativa ao campo Base64."),
            new(SettingKeys.EfiCertBase64, "Certificado .p12 em Base64", true, "Conteúdo do .p12 em Base64 (PowerShell: [Convert]::ToBase64String([IO.File]::ReadAllBytes('arquivo.p12'))). Obrigatório para Pix."),
            new(SettingKeys.EfiCertPassword, "Senha do certificado", true, "Normalmente vazia nos certificados gerados pela Efí.")
        }),
        new("evolution", "Evolution API", "bi-whatsapp", new SettingField[]
        {
            new(SettingKeys.EvolutionUrl, "URL da API"), new(SettingKeys.EvolutionKey, "API key", true), new(SettingKeys.EvolutionInstance, "Instância"),
            new(SettingKeys.EvolutionWebhookToken, "Token do webhook", true, "Envie no header 'apikey' ou na query ?token=. Obrigatório para receber mensagens.")
        }),
        new("ia", "IA", "bi-robot", new SettingField[]
        {
            new(SettingKeys.OllamaApiKey, "Token do Ollama Cloud (API key)", true, "Crie em https://ollama.com/settings/keys. É só o que falta preencher."),
            new(SettingKeys.OllamaModel, "Modelo do assistente (padrão: beam)", false, "beam = gratuito no Ollama Cloud, com ferramentas e raciocínio. Interpreta os comandos do Assistente."),
            new(SettingKeys.OllamaFallbackModel, "Modelo de contingência (padrão: gpt-oss:20b)", false, "Usado automaticamente se o principal falhar ou atingir limite. Muito barato (US$ 0,07/1M tokens de entrada)."),
            new(SettingKeys.OllamaUrl, "URL (padrão: https://ollama.com)", false, "Deixe vazio para o Ollama Cloud. Para servidor próprio: http://ollama:11434")
        }),
        new("monitoramento", "Monitoramento", "bi-activity", new SettingField[]
        {
            new(SettingKeys.AlertNumbers, "Números de alerta WhatsApp", false, "Separados por vírgula, com DDI/DDD."), new(SettingKeys.AlertCooldownMinutes, "Cooldown entre lembretes (min)")
        }),
        new("email", "Email", "bi-envelope", new SettingField[]
        {
            new(SettingKeys.SmtpHost, "SMTP host"), new(SettingKeys.SmtpPort, "SMTP porta"), new(SettingKeys.SmtpUser, "SMTP usuário"),
            new(SettingKeys.SmtpPassword, "SMTP senha", true), new(SettingKeys.SmtpFrom, "Remetente")
        }),
        new("seguranca", "Segurança", "bi-shield-check", new SettingField[]
        {
            new(SettingKeys.SessionHours, "Duração da sessão (h)", false, "Aplicado após reiniciar a aplicação.")
        })
    };
}

[RequirePermission(Perm.SettingsManage)]
public class SettingsController : AppController
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly IAiProvider _ai;
    public SettingsController(AppDbContext db, SettingsService settings, AuditService audit, IAiProvider ai) { _db = db; _settings = settings; _audit = audit; _ai = ai; }

    public async Task<IActionResult> Index(string? category, CancellationToken ct)
    {
        var cat = SettingCatalog.All.FirstOrDefault(c => c.Id == category) ?? SettingCatalog.All[0];
        ViewBag.Settings = _settings;
        ViewBag.Cat = cat;
        ViewBag.Models = new List<string>();
        if (cat.Id == "ia" && _ai.IsConfigured)
        {
            try { ViewBag.Models = await _ai.ListModelsAsync(ct); }
            catch (Exception ex) { ViewBag.ModelsError = ex.Message; }
        }
        ViewBag.Status = SettingCatalog.All.ToDictionary(c => c.Id, c => c.Fields.Where(f => !f.Key.StartsWith(SettingKeys.GoogleMapPrefix)).Take(c.Id is "asaas" or "evolution" or "efi" ? 2 : 1).All(f => _settings.Has(f.Key)));
        ViewBag.AppUrl = (HttpContext.RequestServices.GetRequiredService<IConfiguration>()["APP_URL"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Save(string category, Dictionary<string, string?> values, List<string>? clear)
    {
        var cat = SettingCatalog.All.FirstOrDefault(c => c.Id == category);
        if (cat == null) return NotFound();
        var changed = new List<string>();
        foreach (var f in cat.Fields)
        {
            if (clear?.Contains(f.Key) == true) { await _settings.SetAsync(_db, f.Key, null); changed.Add(f.Key + "=(limpo)"); continue; }
            if (!values.TryGetValue(f.Key, out var v)) continue;
            v = v?.Trim();
            if (f.Secret && string.IsNullOrEmpty(v)) continue; // blank secret keeps the stored value
            if (!f.Secret && v == _settings.Get(f.Key)) continue;
            await _settings.SetAsync(_db, f.Key, v);
            changed.Add(f.Secret ? f.Key + "=(atualizado)" : $"{f.Key}={v}");
        }
        await _audit.LogAsync("settings", "settings_update", "AppSetting", cat.Id, newData: string.Join("; ", changed));
        Flash(changed.Count == 0 ? "Nenhuma alteração." : "Configurações salvas.");
        return RedirectToAction(nameof(Index), new { category });
    }
}

