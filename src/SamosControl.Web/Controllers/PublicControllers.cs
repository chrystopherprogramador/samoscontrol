using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SamosControl.Application;
using SamosControl.Web.Infrastructure;

namespace SamosControl.Web.Controllers;

[AllowAnonymous, IgnoreAntiforgeryToken, EnableRateLimiting("webhook")]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly BillingService _billing;
    private readonly WhatsAppService _wa;
    public WebhooksController(BillingService billing, WhatsAppService wa) { _billing = billing; _wa = wa; }

    private async Task<string> BodyAsync()
    {
        using var r = new StreamReader(Request.Body);
        return await r.ReadToEndAsync();
    }

    [HttpPost("asaas")]
    public async Task<IActionResult> Asaas()
    {
        var body = await BodyAsync();
        var res = await _billing.HandleWebhookAsync(body, Request.Headers["asaas-access-token"].FirstOrDefault());
        return StatusCode(res.StatusCode, new { message = res.Message });
    }

    [HttpPost("evolution")]
    [HttpPost("evolution/{**rest}")]
    public async Task<IActionResult> Evolution()
    {
        var body = await BodyAsync();
        var token = Request.Headers["apikey"].FirstOrDefault() ?? Request.Headers["x-webhook-token"].FirstOrDefault() ?? Request.Query["token"].FirstOrDefault();
        var res = await _wa.HandleWebhookAsync(body, token);
        return StatusCode(res.StatusCode, new { message = res.Message });
    }
}

[AllowAnonymous]
[Route("sign")]
public class SignController : Controller
{
    private readonly ContractService _contracts;
    private readonly SamosControl.Infrastructure.AppDbContext _db;
    public SignController(ContractService contracts, SamosControl.Infrastructure.AppDbContext db) { _contracts = contracts; _db = db; }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string? Ua => Request.Headers.UserAgent.ToString() is { Length: > 0 } u ? u[..Math.Min(u.Length, 500)] : null;

    [HttpGet("{token}"), EnableRateLimiting("public")]
    public async Task<IActionResult> Index(string token)
    {
        var party = await _contracts.OpenForSigningAsync(token, Ip, Ua);
        if (party == null) return View("Invalid");
        ViewBag.Svc = _contracts;
        return View(party);
    }

    [HttpPost("{token}"), EnableRateLimiting("public")]
    public async Task<IActionResult> Submit(string token, SignForm f)
    {
        var res = await _contracts.SignAsync(token, new SignRequest(f.Name ?? "", f.Document, f.Email, f.Phone, f.Accept, f.Otp), Ip, Ua);
        if (!res.Success)
        {
            TempData["flash"] = "danger|" + res.Error;
            return Redirect("/sign/" + token);
        }
        return Redirect($"/sign/{token}/done");
    }

    [HttpPost("{token}/code"), EnableRateLimiting("public")]
    public async Task<IActionResult> Code(string token)
    {
        var (ok, msg) = await _contracts.RequestOtpAsync(token, Ip, Ua);
        TempData["flash"] = (ok ? "success|" : "danger|") + msg;
        return Redirect("/sign/" + token);
    }

    [HttpGet("{token}/done"), EnableRateLimiting("public")]
    public async Task<IActionResult> Done(string token)
    {
        var party = await _contracts.OpenForSigningAsync(token, Ip, Ua);
        return party?.Signature == null ? View("Invalid") : View(party);
    }

    [HttpGet("{token}/pdf"), EnableRateLimiting("public")]
    public async Task<IActionResult> Pdf(string token)
    {
        var party = await _contracts.OpenForSigningAsync(token, Ip, Ua);
        if (party?.Signature == null) return NotFound();
        return File(await _contracts.GetPdfAsync(party.ContractId), "application/pdf", $"contrato-{party.ContractId}.pdf");
    }
}

public class SignForm
{
    public string? Name { get; set; }
    public string? Document { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool Accept { get; set; }
    public string? Otp { get; set; }
}

[AllowAnonymous]
[Route("verify")]
public class VerifyController : Controller
{
    private readonly ContractService _contracts;
    public VerifyController(ContractService contracts) { _contracts = contracts; }

    [HttpGet(""), EnableRateLimiting("public")]
    public IActionResult Index() => View("Index", (SamosControl.Domain.Contract?)null);

    [HttpPost(""), EnableRateLimiting("public")]
    public IActionResult Lookup(string? code) => Redirect("/verify/" + Uri.EscapeDataString(code ?? ""));

    [HttpGet("{code}"), EnableRateLimiting("public")]
    public async Task<IActionResult> Show(string code)
    {
        var c = await _contracts.FindByCodeAsync(code);
        ViewBag.NotFound = c == null;
        return View("Index", c);
    }
}
