using System.Net.Http.Headers;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record AxisCharge(string Tipo, string? Descricao, decimal Valor, DateTime Vencimento, string? Link);
public record AxisLicenca(string Subdominio, string Nome, string Documento, string? Email, decimal ValorMensal, DateTime Validade);
public record AxisSituacao(int Vencidas, decimal ValorVencido, IReadOnlyList<AxisCharge> Cobrancas);

/// <summary>Ponte com o Axis: informa duplicatas vencidas do cliente, recebe os avisos de pagamento da Efí e dá baixa na licença quando a mensalidade é paga.</summary>
public class AxisBridgeService
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly SettingsService _settings;
    private readonly IHttpClientFactory _http;
    private readonly AuditService _audit;

    public AxisBridgeService(AppDbContext db, BillingService billing, SettingsService settings, IHttpClientFactory http, AuditService audit)
    { _db = db; _billing = billing; _settings = settings; _http = http; _audit = audit; }

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
        // Charges settled because the client already paid in Axis must not extend the license a second time.
        var paidIds = paid.Select(c => c.Id.ToString()).ToList();
        var viaAxis = (await _db.AuditLogs.AsNoTracking().Where(a => a.Action == "settle_external" && a.EntityId != null && paidIds.Contains(a.EntityId)).Select(a => a.EntityId!).ToListAsync(ct)).ToHashSet();
        paid = paid.Where(c => !viaAxis.Contains(c.Id.ToString())).ToList();
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
                { Content = JsonContent.Create(new { cobrancaId = ch.Id.ToString(), documento = doc, meses = 1, subdominio = SubdominioDe(ch.ExternalRef) }) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) sent++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* tenta de novo no próximo ciclo */ }
        }
        return sent;
    }

    private static string? SubdominioDe(string? externalRef)
    {
        var p = (externalRef ?? "").Split(':');
        return p.Length == 3 && p[0] == "axis" ? p[1] : null;
    }

    public record LicenseRenewedResult(int Settled, bool Duplicate, string Message);

    /// <summary>
    /// O Axis informa que a licença foi renovada mediante pagamento feito no próprio Axis: dá baixa nas mensalidades em aberto do cliente
    /// (primeiro a da validade anterior, depois as mais antigas) e cancela as cobranças no gateway. Idempotente por referência.
    /// </summary>
    public async Task<LicenseRenewedResult> OnLicenseRenewedAsync(string? subdominio, string documento, DateTime? validadeAnterior, int meses, string reference, CancellationToken ct = default)
    {
        var doc = Digits(documento);
        if (doc.Length is not (11 or 14) || string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("Informe documento (CPF/CNPJ) e referência.");
        var marker = "ref=" + reference.Trim();
        if (await _db.AuditLogs.AnyAsync(a => a.Action == "axis_license_renewed" && a.NewData == marker, ct))
            return new(0, true, "Renovação já processada.");

        var ids = (await _db.Customers.AsNoTracking().Where(c => c.Document != null).Select(c => new { c.Id, c.Document }).ToListAsync(ct))
            .Where(c => Digits(c.Document) == doc).Select(c => c.Id).ToList();
        var open = ids.Count == 0 ? new List<Charge>()
            : await _db.Charges.Where(c => ids.Contains(c.CustomerId) && c.Kind == BillingPlan.Monthly && (c.Status == ChargeStatus.Pendente || c.Status == ChargeStatus.Vencido))
                .OrderBy(c => c.DueDate).ToListAsync(ct);
        var key = !string.IsNullOrWhiteSpace(subdominio) && validadeAnterior.HasValue ? $"axis:{subdominio.Trim().ToLowerInvariant()}:{validadeAnterior.Value:yyyyMMdd}" : null;
        var pick = open.Where(c => key != null && c.ExternalRef == key).Concat(open.Where(c => key == null || c.ExternalRef != key))
            .Take(Math.Max(1, meses)).ToList();

        var settled = 0;
        foreach (var ch in pick)
            if (await _billing.SettleExternallyAsync(ch.Id, "Axis", reference, ct)) settled++;
        // Registered even when nothing was open, so a retry never settles a charge created later.
        await _audit.LogAsync("billing", "axis_license_renewed", "Customer", doc, newData: marker);
        return new(settled, false, settled > 0 ? $"{settled} cobrança(s) baixada(s)." : "Nenhuma cobrança em aberto para este cliente.");
    }

    private sealed record LicencaDto(string? Subdominio, string? Nome, string? Documento, string? Email, decimal ValorMensal, string? Validade, string? Plano = null, bool Ativo = true);

    public record AxisPainel(string Subdominio, string Nome, string Documento, string? Email, string? Plano, decimal ValorMensal, DateTime? Validade, bool Ativo);

    private HttpRequestMessage AxisRequest(HttpMethod m, string path, object? body = null)
    {
        var baseUrl = AxisBase;
        var token = _settings.Get(SettingKeys.AxisEfiToken);
        if (baseUrl is null || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Integração com o Axis não configurada (URL e token).");
        var req = new HttpRequestMessage(m, baseUrl + path);
        if (body != null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return req;
    }

    /// <summary>Todos os painéis do Axis (inclusive suspensos), com validade e valor, para consulta e ações da Roberta.</summary>
    public async Task<List<AxisPainel>> ListPanelsAsync(CancellationToken ct = default)
    {
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        using var req = AxisRequest(HttpMethod.Get, "/api/integracao/samos/licencas?todas=true");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Axis respondeu {(int)res.StatusCode} ao listar licenças.");
        var raw = await res.Content.ReadFromJsonAsync<List<LicencaDto>>(cancellationToken: ct) ?? new();
        return raw.Select(x => new AxisPainel(x.Subdominio ?? "", x.Nome ?? x.Subdominio ?? "", Digits(x.Documento), x.Email, x.Plano, x.ValorMensal,
            DateTime.TryParseExact(x.Validade, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null, x.Ativo)).ToList();
    }

    /// <summary>Renova, suspende ou reativa a licença de um painel no Axis. Devolve a validade/estado resultante.</summary>
    public async Task<string> LicenseActionAsync(string subdominio, string acao, int meses, CancellationToken ct = default)
    {
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        using var req = AxisRequest(HttpMethod.Post, "/api/integracao/samos/licenca-acao", new { subdominio, acao, meses });
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Axis recusou a ação ({(int)res.StatusCode}): {body}");
        return body;
    }

    /// <summary>Gera a cobrança da licença de um painel específico (idempotente por validade). Devolve null se já existe.</summary>
    public async Task<Charge?> ChargeLicenseAsync(AxisPainel p, CancellationToken ct = default)
    {
        if (p.Validade is null) throw new InvalidOperationException("O painel não tem validade definida.");
        if (p.Documento.Length is not (11 or 14)) throw new InvalidOperationException("O painel não tem CPF/CNPJ cadastrado no Axis.");
        if (p.ValorMensal <= 0) throw new InvalidOperationException("O painel não tem valor mensal (plano ou valor negociado).");
        var key = $"axis:{p.Subdominio.ToLowerInvariant()}:{p.Validade:yyyyMMdd}";
        if (await _db.Charges.AnyAsync(c => c.ExternalRef == key && c.Status != ChargeStatus.Cancelado, ct)) return null;
        var cust = (await _db.Customers.Where(c => c.Document != null).ToListAsync(ct)).FirstOrDefault(c => Digits(c.Document) == p.Documento);
        if (cust == null)
        {
            cust = new Customer { Name = p.Nome, Document = p.Documento, Email = p.Email };
            _db.Customers.Add(cust);
            await _db.SaveChangesAsync(ct);
        }
        var today = AppTime.Now.Date;
        var due = p.Validade.Value.Date < today ? today : p.Validade.Value.Date;
        return await _billing.CreateChargeAsync(cust.Id, null, "Licença Axis", p.ValorMensal, due, BillingType.Pix,
            $"Licença Axis {p.Nome} - vencimento {p.Validade:dd/MM/yyyy}", ct, "MENSALIDADE", 0, key);
    }

    /// <summary>Gera a mensalidade de cada licença do Axis perto do vencimento, com ou sem contrato; contrato de mensalidade ativo tem precedência. Idempotente por (conta, validade).</summary>
    public async Task<int> GenerateLicenseChargesAsync(CancellationToken ct = default)
    {
        var baseUrl = AxisBase;
        var token = _settings.Get(SettingKeys.AxisEfiToken);
        if (baseUrl is null || string.IsNullOrWhiteSpace(token) || !string.Equals(_settings.Get(SettingKeys.AxisLicenseBilling)?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) return 0;
        var daysBefore = int.TryParse(_settings.Get(SettingKeys.AxisLicenseDaysBefore), out var d) && d >= 0 ? d : 10;
        var maxOverdue = int.TryParse(_settings.Get(SettingKeys.AxisLicenseMaxOverdue), out var m) && m >= 0 ? m : 60;

        List<AxisLicenca> licencas;
        try
        {
            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/integracao/samos/licencas");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Axis respondeu {(int)res.StatusCode} ao listar licenças para cobrança.");
            var raw = await res.Content.ReadFromJsonAsync<List<LicencaDto>>(cancellationToken: ct) ?? new();
            licencas = raw.Where(x => DateTime.TryParseExact(x.Validade, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .Select(x => new AxisLicenca(x.Subdominio ?? "", x.Nome ?? x.Subdominio ?? "", Digits(x.Documento), x.Email, x.ValorMensal,
                    DateTime.ParseExact(x.Validade!, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(x => x.Subdominio.Length > 0 && x.Documento.Length is 11 or 14 && x.ValorMensal > 0).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { throw new InvalidOperationException("Não foi possível consultar as licenças no Axis: " + ex.Message, ex); }

        var today = AppTime.Now.Date;
        var customers = await _db.Customers.Where(c => c.Document != null).ToListAsync(ct);
        var contracted = (await _db.Contracts.AsNoTracking()
            .Where(c => c.Status == ContractStatus.Assinado && !c.BillingClosed && c.MonthlyValue > 0 && c.CustomerId != null)
            .Select(c => c.CustomerId!.Value).ToListAsync(ct)).ToHashSet();
        var created = 0;
        var errors = new List<string>();
        foreach (var l in licencas)
        {
            if (l.Validade.Date > today.AddDays(daysBefore) || l.Validade.Date < today.AddDays(-maxOverdue)) continue;
            var cust = customers.FirstOrDefault(c => Digits(c.Document) == l.Documento);
            if (cust != null && contracted.Contains(cust.Id)) continue;
            var key = $"axis:{l.Subdominio.ToLowerInvariant()}:{l.Validade:yyyyMMdd}";
            if (await _db.Charges.AnyAsync(c => c.ExternalRef == key, ct)) continue;
            try
            {
                if (cust == null)
                {
                    cust = new Customer { Name = l.Nome, Document = l.Documento, Email = l.Email };
                    _db.Customers.Add(cust);
                    await _db.SaveChangesAsync(ct);
                    customers.Add(cust);
                }
                var due = l.Validade.Date < today ? today : l.Validade.Date;
                await _billing.CreateChargeAsync(cust.Id, null, "Licença Axis", l.ValorMensal, due, BillingType.Pix,
                    $"Licença Axis {l.Nome} - vencimento {l.Validade:dd/MM/yyyy}", ct, "MENSALIDADE", 0, key);
                created++;
            }
            catch (Exception ex) when (ex is IntegrationException or ArgumentException or DbUpdateException or InvalidOperationException)
            {
                _db.ChangeTracker.Clear();
                customers = await _db.Customers.Where(c => c.Document != null).ToListAsync(ct);
                errors.Add($"{l.Subdominio}: {ex.Message.Truncate(200)}");
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException($"{created} licença(s) cobrada(s), {errors.Count} com erro: " + string.Join(" | ", errors.Take(5)));
        return created;
    }
}
