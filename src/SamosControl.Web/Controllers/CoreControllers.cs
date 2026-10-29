using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

public class LoginVm { public string Email { get; set; } = ""; public string Password { get; set; } = ""; public string? ReturnUrl { get; set; } }
public class ResetVm { public string Token { get; set; } = ""; public string Password { get; set; } = ""; public string Confirm { get; set; } = ""; }
public class ChangePasswordVm { public string Current { get; set; } = ""; public string Password { get; set; } = ""; public string Confirm { get; set; } = ""; }

[AllowAnonymous]
public class AccountController : AppController
{
    private readonly AuthService _auth;
    private readonly AuditService _audit;
    private readonly AppDbContext _db;
    public AccountController(AuthService auth, AuditService audit, AppDbContext db) { _auth = auth; _audit = audit; _db = db; }

    [HttpGet] public IActionResult Login(string? returnUrl) => User.Identity?.IsAuthenticated == true ? Redirect("/") : View(new LoginVm { ReturnUrl = returnUrl });

    [HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginVm vm)
    {
        var r = await _auth.LoginAsync(vm.Email ?? "", vm.Password ?? "");
        if (!r.Success || r.User == null)
        {
            ViewBag.Error = r.Error ?? "Credenciais inválidas.";
            return View(new LoginVm { Email = vm.Email ?? "", ReturnUrl = vm.ReturnUrl });
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, r.User.Id.ToString()), new(ClaimTypes.Name, r.User.Name), new(ClaimTypes.Email, r.User.Email)
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
        return Url.IsLocalUrl(vm.ReturnUrl) ? LocalRedirect(vm.ReturnUrl!) : Redirect("/");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true) await _audit.LogAsync("auth", "logout");
        await HttpContext.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    public IActionResult Denied() => View();

    [HttpGet] public IActionResult Reset(string token) => View(new ResetVm { Token = token });

    [HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Reset(ResetVm vm)
    {
        if (vm.Password != vm.Confirm) { ViewBag.Error = "As senhas não conferem."; return View(vm); }
        var err = await _auth.ResetPasswordAsync(vm.Token, vm.Password);
        if (err != null) { ViewBag.Error = err; return View(vm); }
        Flash("Senha redefinida. Faça login.");
        return RedirectToAction(nameof(Login));
    }

    [Authorize, HttpGet] public IActionResult Password() => View(new ChangePasswordVm());

    [Authorize, HttpPost]
    public async Task<IActionResult> Password(ChangePasswordVm vm)
    {
        var u = await _db.Users.FindAsync(UserId);
        if (u == null || !AuthService.Verify(u.PasswordHash, vm.Current ?? "")) { ViewBag.Error = "Senha atual incorreta."; return View(new ChangePasswordVm()); }
        if (vm.Password != vm.Confirm) { ViewBag.Error = "As senhas não conferem."; return View(new ChangePasswordVm()); }
        var err = AuthService.ValidatePassword(vm.Password);
        if (err != null) { ViewBag.Error = err; return View(new ChangePasswordVm()); }
        u.PasswordHash = AuthService.Hash(vm.Password);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("users", "change_password", "User", u.Id);
        Flash("Senha alterada.");
        return Redirect("/");
    }
}

[RequirePermission(Perm.DashboardView)]
public class HomeController : AppController
{
    private readonly DashboardService _dash;
    public HomeController(DashboardService dash) => _dash = dash;
    public async Task<IActionResult> Index() => View(await _dash.GetAsync());

    [AllowAnonymous, ResponseCache(NoStore = true)]
    public IActionResult Error() => View("Status", 500);
    [AllowAnonymous, ResponseCache(NoStore = true)]
    public IActionResult Status(int code = 404) => View("Status", code);
}

[RequirePermission(Perm.DashboardView)]
public class NotificationsController : AppController
{
    private readonly AppDbContext _db;
    private readonly NotificationService _svc;
    public NotificationsController(AppDbContext db, NotificationService svc) { _db = db; _svc = svc; }

    private IQueryable<Notification> Mine() => _db.Notifications.Where(n => n.UserId == null || n.UserId == UserId);

    [HttpGet]
    public async Task<IActionResult> Latest()
    {
        var uid = UserId;
        var items = await Mine().OrderByDescending(n => n.CreatedAt).Take(8)
            .Select(n => new { n.Id, n.Title, n.Link, n.CreatedAt, Read = _db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == uid) }).ToListAsync();
        var unread = await Mine().CountAsync(n => !_db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == uid));
        return Json(new { unread, items = items.Select(i => new { i.Id, i.Title, i.Link, at = i.CreatedAt.Local(), read = i.Read }) });
    }

    public async Task<IActionResult> Index()
    {
        var uid = UserId;
        var list = await Mine().OrderByDescending(n => n.CreatedAt).Take(200).ToListAsync();
        ViewBag.Read = (await _db.NotificationReads.Where(r => r.UserId == uid).Select(r => r.NotificationId).ToListAsync()).ToHashSet();
        return View(list);
    }

    [HttpPost]
    public async Task<IActionResult> MarkRead([FromBody] MarkReadVm? vm)
    {
        await _svc.MarkReadAsync(UserId, vm?.Id);
        return Json(new { ok = true });
    }

    [HttpPost, ActionName("MarkAll")]
    public async Task<IActionResult> MarkAllForm() { await _svc.MarkReadAsync(UserId, null); return RedirectToAction(nameof(Index)); }
}
public record MarkReadVm(int? Id);

[RequirePermission(Perm.AiUse)]
public class AssistantController : AppController
{
    private readonly SamosAgent _agent;
    private readonly AppDbContext _db;
    public AssistantController(SamosAgent agent, AppDbContext db) { _agent = agent; _db = db; }

    public async Task<IActionResult> Index()
    {
        var msgs = await _db.AiMessages.Where(m => m.UserId == UserId && m.Channel == "WEB").OrderByDescending(m => m.Id).Take(30).ToListAsync();
        msgs.Reverse();
        return View(msgs);
    }

    [HttpPost]
    public async Task<IActionResult> Ask([FromBody] AskVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Message) || vm.Message.Length > 2000) return Json(new { error = "Mensagem inválida." });
        var name = User.FindFirstValue(ClaimTypes.Name) ?? "Usuário";
        var reply = await _agent.HandleAsync(vm.Message.Trim(), new AgentContext(UserId, name, "WEB", null), ct);
        return Json(new { reply });
    }

    [RequirePermission(Perm.AiHistory)]
    public async Task<IActionResult> History()
        => View(await _db.AiActions.OrderByDescending(a => a.Id).Take(200).ToListAsync());
}
public record AskVm(string Message);
