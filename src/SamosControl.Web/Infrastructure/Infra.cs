using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Web.Infrastructure;

public class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;
    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;
    public int? UserId => int.TryParse(_http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? UserName => _http.HttpContext?.User.FindFirstValue(ClaimTypes.Email);
    public string? Ip => _http.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Permissions of the current request user, loaded once per request.</summary>
public class RequestPermissions
{
    private HashSet<string> _set = new();
    public void Set(HashSet<string> s) => _set = s;
    public bool Has(string permission) => _set.Contains(permission);
}

public class PermissionLoaderMiddleware
{
    private readonly RequestDelegate _next;
    public PermissionLoaderMiddleware(RequestDelegate next) => _next = next;
    public async Task InvokeAsync(HttpContext ctx, RequestPermissions rp, PermissionService perms)
    {
        if (int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            rp.Set((await perms.GetAsync(id)).Permissions);
        await _next(ctx);
    }
}

/// <summary>Granular permission check; never depends on role names.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _permission;
    public RequirePermissionAttribute(string permission) => _permission = permission;
    public void OnAuthorization(AuthorizationFilterContext ctx)
    {
        if (ctx.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any()) return;
        var rp = ctx.HttpContext.RequestServices.GetRequiredService<RequestPermissions>();
        if (ctx.HttpContext.User.Identity?.IsAuthenticated == true && !rp.Has(_permission))
            ctx.Result = new ForbidResult();
    }
}

public class NotificationHub : Hub { }

public class SignalRNotificationPublisher : INotificationPublisher
{
    private readonly IHubContext<NotificationHub> _hub;
    public SignalRNotificationPublisher(IHubContext<NotificationHub> hub) => _hub = hub;
    public Task PublishAsync(Notification n) =>
        _hub.Clients.All.SendAsync("notification", new { n.Id, type = n.Type.ToString(), title = n.Title, message = n.Message, link = n.Link, userId = n.UserId });
}

public class DbHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;
    public DbHealthCheck(AppDbContext db) => _db = db;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try { return await _db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Sem conexão com o banco."); }
        catch (Exception ex) { return HealthCheckResult.Unhealthy(ex.Message); }
    }
}

public abstract class AppController : Controller
{
    protected int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    protected void Flash(string msg) => TempData["flash"] = "success|" + msg;
    protected void FlashWarn(string msg) => TempData["flash"] = "warning|" + msg;
    protected void FlashFail(string msg) => TempData["flash"] = "danger|" + msg;
    protected static DateTime? UtcDate(DateTime? d) => d.HasValue ? DateTime.SpecifyKind(d.Value.Date, DateTimeKind.Utc) : null;
    protected static DateTime? ToUtc(DateTime? d) => d.HasValue ? AppTime.ToUtc(DateTime.SpecifyKind(d.Value, DateTimeKind.Unspecified)) : null;
}

public static class ViewHelpers
{
    public static string Local(this DateTime? d, string fmt = "dd/MM/yyyy HH:mm") => d.HasValue ? AppTime.ToLocal(d.Value).ToString(fmt) : "-";
    public static string Local(this DateTime d, string fmt = "dd/MM/yyyy HH:mm") => AppTime.ToLocal(d).ToString(fmt);
    public static string Money(this decimal d) => d.ToString("C2");

    public static string Badge(string status) => status switch
    {
        "Ganho" or "Pago" or "Online" or "Assinado" or "Success" or "Executada" or "Processed" or "Confirmada" or "CONCLUIDA" => "bg-success",
        "Perdido" or "Vencido" or "Offline" or "Cancelado" or "Failed" or "Error" or "Falhou" or "Negada" or "FALHA" => "bg-danger",
        "Degraded" or "Pendente" or "AguardandoAssinatura" or "Warning" or "AguardandoConfirmacao" or "Contato" or "Qualificado" => "bg-warning text-dark",
        "Novo" or "Info" or "Proposta" => "bg-primary",
        _ => "bg-secondary"
    };
}
