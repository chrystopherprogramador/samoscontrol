using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

/// <summary>
/// Link público de pagamento do próprio Samos Control (/pay/{token}). O token é assinado pelo Data Protection
/// (não adivinhável e sem coluna nova): quem tem o link vê apenas aquela cobrança, com Pix (QR + copia e cola) e boleto.
/// </summary>
public class PayLinkSigner
{
    private readonly IDataProtector _protector;
    private readonly IConfiguration _cfg;

    public PayLinkSigner(IDataProtectionProvider dp, IConfiguration cfg)
    { _protector = dp.CreateProtector("SamosControl.PayLink.v1"); _cfg = cfg; }

    public string Token(int chargeId) => Convert.ToBase64String(_protector.Protect(System.Text.Encoding.UTF8.GetBytes(chargeId.ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Url(int chargeId) => $"{(_cfg["APP_URL"] ?? "").TrimEnd('/')}/pay/{Token(chargeId)}";

    public int? ParseId(string token)
    {
        try
        {
            var b = token.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            return int.TryParse(System.Text.Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(b))), out var id) ? id : null;
        }
        catch (Exception) { return null; }
    }
}

public class PayLinkService
{
    private readonly PayLinkSigner _signer;
    private readonly AppDbContext _db;
    private readonly BillingService _billing;

    public PayLinkService(PayLinkSigner signer, AppDbContext db, BillingService billing) { _signer = signer; _db = db; _billing = billing; }

    /// <summary>Abre a cobrança pelo token; se ainda não tem Pix/boleto gravados, tenta buscar no provedor.</summary>
    public async Task<Charge?> OpenAsync(string token, CancellationToken ct = default)
    {
        var id = _signer.ParseId(token);
        if (id == null) return null;
        var ch = await _db.Charges.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (ch == null) return null;
        if ((ch.Status == ChargeStatus.Pendente || ch.Status == ChargeStatus.Vencido) && (ch.PixPayload == null || ch.InvoiceUrl == null))
        {
            try { await _billing.RefreshAsync(ch.Id, ct); ch = await _db.Charges.Include(c => c.Customer).FirstAsync(c => c.Id == id, ct); }
            catch (Exception ex) when (ex is IntegrationException or HttpRequestException) { }
        }
        return ch;
    }
}
