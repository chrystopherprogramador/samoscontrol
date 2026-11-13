using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record AxisCharge(string Tipo, string? Descricao, decimal Valor, DateTime Vencimento, string? Link);
public record AxisSituacao(int Vencidas, decimal ValorVencido, IReadOnlyList<AxisCharge> Cobrancas);

/// <summary>Ponte com o Axis: informa duplicatas vencidas do cliente, recebe os avisos de pagamento da Efí e dá baixa na licença quando a mensalidade é paga.</summary>
public class AxisBridgeService
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly SettingsService _settings;
    private readonly IHttpClientFactory _http;

    public AxisBridgeService(AppDbContext db, BillingService billing, SettingsService settings, IHttpClientFactory http)
    { _db = db; _billing = billing; _settings = settings; _http = http; }

    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    public bool TokenValido(string? provided) => WebhookUtil.TokenMatches(_settings.Get(SettingKeys.AxisEfiToken), provided);

    private string? AxisBase =>
        Uri.TryCreate(_settings.Get(SettingKeys.AxisEfiUrl)?.Trim(), UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>Duplicatas vencidas (ou pendentes já vencidas pela data) de todos os cadastros com o documento informado.</summary>
    public async Task<AxisSituacao> SituacaoAsync(string documento, CancellationToken ct = default)
    {
        var doc = Digits(documento);
        var ids = (await _db.Customers.AsNoTracking().Where(c => c.Document != null).Select(c => new { c.Id, c.Document }).ToListAsync(ct))
            .Where(c => Digits(c.Document) == doc).Select(c => c.Id).ToList();
        if (doc.Length == 0 || ids.Count == 0) return new(0, 0, Array.Empty<AxisCharge>());

        var today = DateTime.SpecifyKind(AppTime.Now.Date, DateTimeKind.Utc);
        var list = await _db.Charges.AsNoTracking()
            .Where(c => ids.Contains(c.CustomerId) && (c.Status == ChargeStatus.Vencido || (c.Status == ChargeStatus.Pendente && c.DueDate < today)))
            .OrderBy(c => c.DueDate).ToListAsync(ct);
        return new(list.Count, list.Sum(c => c.Amount),
            list.Select(c => new AxisCharge(c.Kind, c.Description ?? c.ServiceName, c.Amount, c.DueDate, c.InvoiceUrl)).ToList());
    }

    /// <summary>A Efí avisou (via Axis) que o txid mudou: confere a cobrança e repassa a baixa ao Axis.</summary>
    public async Task<bool> OnEfiWebhookAsync(string txid, CancellationToken ct = default)
    {
        var id = await _db.Charges.Where(c => c.Provider == BillingService.ProviderEfi && c.AsaasId == txid).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        if (id is null) return false;
        await _billing.RefreshAsync(id.Value, ct);
        await SyncPaidAsync(ct);
        return true;
    }

    /// <summary>Envia ao Axis as mensalidades pagas recentemente; o Axis ignora as já registradas, então pode repetir sem risco.</summary>
    public async Task<int> SyncPaidAsync(CancellationToken ct = default)
    {
        var baseUrl = AxisBase;
        var token = _settings.Get(SettingKeys.AxisEfiToken);
        if (baseUrl is null || string.IsNullOrWhiteSpace(token)) return 0;

        var since = DateTime.UtcNow.AddDays(-10);
        var paid = await _db.Charges.AsNoTracking().Include(c => c.Customer)
            .Where(c => c.Kind == "MENSALIDADE" && c.Status == ChargeStatus.Pago && c.PaidAt >= since).ToListAsync(ct);
        var sent = 0;
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        foreach (var ch in paid)
        {
            var doc = Digits(ch.Customer?.Document);
            if (doc.Length is not (11 or 14)) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/integracao/samos/mensalidade-paga")
                { Content = JsonContent.Create(new { cobrancaId = ch.Id.ToString(), documento = doc, meses = 1 }) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) sent++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* tenta de novo no próximo ciclo */ }
        }
        return sent;
    }
}
