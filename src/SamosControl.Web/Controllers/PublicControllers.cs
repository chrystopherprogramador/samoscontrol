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

[AllowAnonymous, IgnoreAntiforgeryToken, EnableRateLimiting("webhook")]
[Route("api/integracao/axis")]
public class AxisIntegrationController : ControllerBase
{
    private readonly AxisBridgeService _axis;
    public AxisIntegrationController(AxisBridgeService axis) => _axis = axis;

    private bool Autorizado()
    {
        var auth = Request.Headers.Authorization.ToString();
        return _axis.TokenValido(auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null);
    }

    /// <summary>Duplicatas vencidas do cliente; o Axis bloqueia a renovação da licença quando há alguma.</summary>
    [HttpGet("situacao")]
    public async Task<IActionResult> Situacao(string? documento, CancellationToken ct)
    {
        if (!Autorizado()) return Unauthorized();
        var s = await _axis.SituacaoAsync(documento ?? "", ct);
        return Ok(new
        {
            vencidas = s.Vencidas, valorVencido = s.ValorVencido,
            cobrancas = s.Cobrancas.Select(c => new { tipo = c.Tipo, descricao = c.Descricao, valor = c.Valor, vencimento = c.Vencimento, link = c.Link })
        });
    }

    public sealed record LicenseRenewedReq(string? Subdominio, string? Documento, string? ValidadeAnterior, int Meses = 1, string? Referencia = null);

    /// <summary>O Axis avisa que o cliente renovou a licença pagando no Axis: o Samos dá baixa nas mensalidades correspondentes.</summary>
    [HttpPost("licenca-renovada")]
    public async Task<IActionResult> LicencaRenovada([FromBody] LicenseRenewedReq r, CancellationToken ct)
    {
        if (!Autorizado()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(r.Referencia) || r.Meses is < 1 or > 24) return BadRequest(new { erro = "Informe referencia e meses (1 a 24)." });
        DateTime? anterior = DateTime.TryParseExact(r.ValidadeAnterior, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        try
        {
            var res = await _axis.OnLicenseRenewedAsync(r.Subdominio, r.Documento ?? "", anterior, r.Meses, r.Referencia, ct);
            return Ok(new { baixadas = res.Settled, duplicada = res.Duplicate, mensagem = res.Message });
        }
        catch (ArgumentException ex) { return BadRequest(new { erro = ex.Message }); }
    }

    public sealed record EfiWebhookReq(string? Txid);

    /// <summary>O Axis recebe o webhook da Efí e repassa aqui o txid das cobranças que pertencem ao SamosControl.</summary>
    [HttpPost("efi-webhook")]
    public async Task<IActionResult> EfiWebhook([FromBody] EfiWebhookReq r, CancellationToken ct)
    {
        if (!Autorizado()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(r.Txid)) return BadRequest();
        return Ok(new { encontrada = await _axis.OnEfiWebhookAsync(r.Txid.Trim(), ct) });
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
