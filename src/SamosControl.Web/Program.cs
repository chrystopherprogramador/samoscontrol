using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Serilog;
using SamosControl.Application;
using SamosControl.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

var cfg = builder.Configuration;
var runWorkers = !string.Equals(cfg["RUN_WORKERS"], "false", StringComparison.OrdinalIgnoreCase);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear(); // runs behind the EasyPanel reverse proxy
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton<INotificationPublisher, SignalRNotificationPublisher>();
builder.Services.AddSamosDataProtection(cfg);
builder.Services.AddSamosApplication(cfg, runWorkers);
builder.Services.AddScoped<RequestPermissions>();
builder.Services.AddSignalR();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Denied";
        o.Cookie.Name = "samos.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(int.TryParse(cfg["SESSION_HOURS"], out var h) ? h : 12);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = async c =>
        {
            var perms = c.HttpContext.RequestServices.GetRequiredService<PermissionService>();
            if (!int.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) || !(await perms.GetAsync(uid)).Active)
            {
                c.RejectPrincipal();
                await c.HttpContext.SignOutAsync();
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AuthorizeFilter());
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "samos.csrf"; o.Cookie.SameSite = SameSiteMode.Lax; });

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    RateLimitPartition<string> ByIp(HttpContext c, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });
    o.AddPolicy("login", c => ByIp(c, 10));
    o.AddPolicy("public", c => ByIp(c, 30));
    o.AddPolicy("webhook", c => ByIp(c, 600));
});

builder.Services.AddHealthChecks().AddCheck<DbHealthCheck>("database");

var app = builder.Build();
var culture = new CultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = culture; CultureInfo.DefaultThreadCurrentUICulture = culture;
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(culture), SupportedCultures = new[] { culture }, SupportedUICultures = new[] { culture }
});

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Home/Error");
app.UseStatusCodePagesWithReExecute("/Home/Status", "?code={0}");
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<PermissionLoaderMiddleware>();

app.MapControllers();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { status = report.Status.ToString(), checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()) });
    }
}).AllowAnonymous();

await DbInitializer.InitializeAsync(app.Services);
app.Logger.LogInformation("SAMOS CONTROL iniciado. Workers embutidos: {Workers}", runWorkers);
app.Run();

public partial class Program { }
