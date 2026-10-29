using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public record EfiDebtor(string Name, string Document, string? Email, string? Phone);

/// <summary>Status uses the same vocabulary as Asaas (PENDING, RECEIVED, OVERDUE, REFUNDED, DELETED) so BillingService.MapStatus is shared.</summary>
public record EfiCharge(string Id, string Status, string? Url, string? PixPayload, string? PixQrCodeBase64, decimal? PaidValue, DateTime? PaidAt);

/// <summary>Outgoing Pix (commission payout). Status: EM_PROCESSAMENTO, REALIZADO or NAO_REALIZADO.</summary>
public record EfiPixTransfer(string IdEnvio, string Status, string? E2eId);

public interface IEfiIntegration
{
    Task<EfiPixTransfer> SendPixAsync(string idEnvio, decimal value, string toKey, string? info, CancellationToken ct = default);
    Task<EfiPixTransfer> GetPixTransferAsync(string idEnvio, CancellationToken ct = default);
    bool IsConfigured { get; }
    Task<EfiCharge> CreateAsync(bool pix, EfiDebtor debtor, decimal value, DateTime dueDate, string? description, CancellationToken ct = default);
    Task<EfiCharge> GetAsync(bool pix, string id, CancellationToken ct = default);
    Task<bool> CancelAsync(bool pix, string id, CancellationToken ct = default);
}

/// <summary>
/// Efí Bank (https://dev.efipay.com.br). Routes confirmed in the official SDK (efipay/sdk-dotnet-apis-efi, Constants.cs):
/// Pix API (mTLS with the .p12 certificate): POST /oauth/token, PUT|GET|PATCH /v2/cobv/{txid}, GET /v2/loc/{id}/qrcode.
/// Cobranças API (no certificate): POST /authorize, POST /charge/one-step, GET /charge/{id}, PUT /charge/{id}/cancel.
/// Payment status is obtained by polling (the Pix webhook requires mTLS on the receiving server).
/// </summary>
public class EfiIntegration : IEfiIntegration
{
    private const string PixProd = "https://pix.api.efipay.com.br", PixHomolog = "https://pix-h.api.efipay.com.br";
    private const string CobProd = "https://cobrancas.api.efipay.com.br/v1", CobHomolog = "https://cobrancas-h.api.efipay.com.br/v1";
    private const int PixValidDaysAfterDue = 30;

    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<bool, (string Token, DateTime Expires)> _tokens = new();
    private (string Key, HttpClient Client)? _pixClient;

    public EfiIntegration(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EfiClientId)) && !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EfiClientSecret));

    private bool Sandbox => string.Equals(_s.Get(SettingKeys.EfiSandbox)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    private string BaseUrl(bool pix) => pix ? (Sandbox ? PixHomolog : PixProd) : (Sandbox ? CobHomolog : CobProd);

    /// <summary>Pix needs the account certificate on every request; tests override this to avoid real TLS.</summary>
    protected virtual HttpClient CreatePixClient()
    {
        var b64 = _s.Get(SettingKeys.EfiCertBase64);
        var path = _s.Get(SettingKeys.EfiCertPath);
        var pwd = _s.Get(SettingKeys.EfiCertPassword) ?? "";
        var key = $"{Sandbox}|{b64}|{path}|{pwd}";
        if (_pixClient is { } c && c.Key == key) return c.Client;

        byte[]? data = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(b64)) data = Convert.FromBase64String(b64.Trim());
            else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            throw new IntegrationException("Certificado Efí (.p12) ilegível: " + ex.Message, ex);
        }
        if (data is null) throw new NotConfiguredException("Efí Pix (certificado .p12)");

        X509Certificate2 cert;
        try { cert = new X509Certificate2(data, pwd, X509KeyStorageFlags.DefaultKeySet); }
        catch (CryptographicException ex) { throw new IntegrationException("Certificado Efí (.p12) inválido ou senha incorreta.", ex); }

        var handler = new SocketsHttpHandler { SslOptions = { ClientCertificates = new X509CertificateCollection { cert } } };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _pixClient = (key, client);
        return client;
    }

    private HttpClient Client(bool pix)
    {
        if (!IsConfigured) throw new NotConfiguredException("Efí");
        if (pix) return CreatePixClient();
        var c = _http.CreateClient("efi");
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    private async Task<string> TokenAsync(bool pix, HttpClient client, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(pix, out var t) && t.Expires > DateTime.UtcNow) return t.Token;
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_s.Get(SettingKeys.EfiClientId)}:{_s.Get(SettingKeys.EfiClientSecret)}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl(pix) + (pix ? "/oauth/token" : "/authorize"))
            {
                Content = JsonContent.Create(new { grant_type = "client_credentials" })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            var node = await Execute(client, req, ct);
            var token = Json.Str(node["access_token"]) ?? throw new IntegrationException("Efí não retornou access_token.");
            var seconds = int.TryParse(Json.Str(node["expires_in"]), out var e) ? e : 600;
            _tokens[pix] = (token, DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 60)));
            return token;
        }
        finally { _lock.Release(); }
    }

    private static async Task<JsonNode> Execute(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new IntegrationException($"Efí {(int)res.StatusCode}: {ErrorMessage(text)}");
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new IntegrationException("Falha ao comunicar com a Efí: " + ex.Message, ex);
        }
    }

    public static string ErrorMessage(string text)
    {
        try
        {
            var n = JsonNode.Parse(text);
            foreach (var k in new[] { "mensagem", "detail", "error_description", "message", "title", "error" })
            {
                var v = Json.Str(n?[k]);
                if (!string.IsNullOrWhiteSpace(v)) return v.Truncate(300);
            }
        }
        catch (JsonException) { }
        return text.Truncate(300);
    }

    private async Task<JsonNode> Send(bool pix, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var client = Client(pix);
        var token = await TokenAsync(pix, client, ct);
        using var req = new HttpRequestMessage(method, BaseUrl(pix) + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = JsonContent.Create(body, options: Json.Opts);
        try { return await Execute(client, req, ct); }
        catch (IntegrationException ex) when (ex.Message.StartsWith("Efí 401")) { _tokens.Remove(pix); throw; }
    }

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public async Task<EfiCharge> CreateAsync(bool pix, EfiDebtor d, decimal value, DateTime dueDate, string? description, CancellationToken ct = default)
    {
        var doc = new string((d.Document ?? "").Where(char.IsDigit).ToArray());
        if (doc.Length is not (11 or 14)) throw new ArgumentException("A cobrança via Efí exige CPF ou CNPJ válido no cadastro do cliente.");
        return pix ? await CreatePixAsync(d, doc, value, dueDate, description, ct) : await CreateBilletAsync(d, doc, value, dueDate, description, ct);
    }

    private async Task<EfiCharge> CreatePixAsync(EfiDebtor d, string doc, decimal value, DateTime due, string? description, CancellationToken ct)
    {
        var key = _s.Get(SettingKeys.EfiPixKey);
        if (string.IsNullOrWhiteSpace(key)) throw new NotConfiguredException("Efí Pix (chave Pix)");
        var txid = Guid.NewGuid().ToString("N"); // 32 chars, within the 26-35 alphanumeric limit
        var devedor = new Dictionary<string, object?> { ["nome"] = d.Name.Truncate(200) };
        devedor[doc.Length == 11 ? "cpf" : "cnpj"] = doc;
        var body = new
        {
            calendario = new { dataDeVencimento = due.ToString("yyyy-MM-dd"), validadeAposVencimento = PixValidDaysAfterDue },
            devedor,
            valor = new { original = Money(value) },
            chave = key.Trim(),
            solicitacaoPagador = (description ?? "Cobrança").Truncate(140)
        };
        var n = await Send(true, HttpMethod.Put, $"/v2/cobv/{txid}", body, ct);
        var payload = Json.Str(n["pixCopiaECola"]);
        string? image = null;
        var locId = Json.Str(n["loc"]?["id"]);
        if (locId != null)
        {
            try
            {
                var qr = await Send(true, HttpMethod.Get, $"/v2/loc/{Uri.EscapeDataString(locId)}/qrcode", null, ct);
                payload ??= Json.Str(qr["qrcode"]);
                image = StripDataUri(Json.Str(qr["imagemQrcode"]));
            }
            catch (IntegrationException) { /* the Pix copy-and-paste code is enough; the image can be fetched later */ }
        }
        return new EfiCharge(Json.Str(n["txid"]) ?? txid, MapPixStatus(Json.Str(n["status"])), null, payload, image, null, null);
    }

    private async Task<EfiCharge> CreateBilletAsync(EfiDebtor d, string doc, decimal value, DateTime due, string? description, CancellationToken ct)
    {
        var customer = new Dictionary<string, object?> { ["name"] = d.Name.Truncate(255) };
        if (doc.Length == 11) customer["cpf"] = doc;
        else customer["juridical_person"] = new { corporate_name = d.Name.Truncate(255), cnpj = doc };
        if (!string.IsNullOrWhiteSpace(d.Email)) customer["email"] = d.Email.Trim();
        var phone = PhoneUtil.Normalize(d.Phone);
        if (phone is { Length: > 11 } && phone.StartsWith("55")) phone = phone[2..];
        if (phone is { Length: 10 or 11 }) customer["phone_number"] = phone;

        var body = new
        {
            items = new[] { new { name = (description ?? "Cobrança").Truncate(255), value = (int)Math.Round(value * 100m, MidpointRounding.AwayFromZero), amount = 1 } },
            payment = new { banking_billet = new { expire_at = due.ToString("yyyy-MM-dd"), customer } }
        };
        var n = await Send(false, HttpMethod.Post, "/charge/one-step", body, ct);
        var data = n["data"] ?? n;
        var id = Json.Str(data["charge_id"]) ?? throw new IntegrationException("Efí não retornou o id da cobrança.");
        return new EfiCharge(id, MapBilletStatus(Json.Str(data["status"])), Json.Str(data["link"]) ?? Json.Str(data["pdf"]?["charge"]), null, null, null, null);
    }

    public async Task<EfiCharge> GetAsync(bool pix, string id, CancellationToken ct = default)
    {
        if (pix)
        {
            var n = await Send(true, HttpMethod.Get, $"/v2/cobv/{Uri.EscapeDataString(id)}", null, ct);
            var status = MapPixStatus(Json.Str(n["status"]));
            var first = (n["pix"] as JsonArray)?.FirstOrDefault();
            decimal? paid = decimal.TryParse(Json.Str(first?["valor"]), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
            DateTime? at = DateTime.TryParse(Json.Str(first?["horario"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var h) ? h : null;
            return new EfiCharge(id, status, null, Json.Str(n["pixCopiaECola"]), null, paid, at);
        }
        var b = await Send(false, HttpMethod.Get, $"/charge/{Uri.EscapeDataString(id)}", null, ct);
        var data = b["data"] ?? b;
        return new EfiCharge(id, MapBilletStatus(Json.Str(data["status"])), Json.Str(data["link"]) ?? Json.Str(data["pdf"]?["charge"]), null, null, null, null);
    }

    public async Task<bool> CancelAsync(bool pix, string id, CancellationToken ct = default)
    {
        if (pix)
        {
            var n = await Send(true, HttpMethod.Patch, $"/v2/cobv/{Uri.EscapeDataString(id)}", new { status = "REMOVIDA_PELO_USUARIO_RECEBEDOR" }, ct);
            return MapPixStatus(Json.Str(n["status"])) == "DELETED";
        }
        await Send(false, HttpMethod.Put, $"/charge/{Uri.EscapeDataString(id)}/cancel", null, ct);
        return true;
    }

    /// <summary>PUT /v3/gn/pix/{idEnvio} (scope pix.send; idempotent per idEnvio). The payer key is the account Pix key (EFI_PIX_KEY).</summary>
    public async Task<EfiPixTransfer> SendPixAsync(string idEnvio, decimal value, string toKey, string? info, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(idEnvio, "^[a-zA-Z0-9]{1,35}$")) throw new ArgumentException("idEnvio inválido.");
        var payer = _s.Get(SettingKeys.EfiPixKey);
        if (string.IsNullOrWhiteSpace(payer)) throw new NotConfiguredException("Efí Pix (chave Pix)");
        var body = new
        {
            valor = Money(value),
            pagador = new { chave = payer.Trim(), infoPagador = (info ?? "Comissão").Truncate(140) },
            favorecido = new { chave = toKey.Trim() }
        };
        var n = await Send(true, HttpMethod.Put, $"/v3/gn/pix/{idEnvio}", body, ct);
        return new EfiPixTransfer(Json.Str(n["idEnvio"]) ?? idEnvio, Json.Str(n["status"]) ?? "EM_PROCESSAMENTO", Json.Str(n["e2eId"]));
    }

    /// <summary>GET /v2/gn/pix/enviados/id-envio/{idEnvio} (scope gn.pix.send.read).</summary>
    public async Task<EfiPixTransfer> GetPixTransferAsync(string idEnvio, CancellationToken ct = default)
    {
        var n = await Send(true, HttpMethod.Get, $"/v2/gn/pix/enviados/id-envio/{Uri.EscapeDataString(idEnvio)}", null, ct);
        return new EfiPixTransfer(Json.Str(n["idEnvio"]) ?? idEnvio, Json.Str(n["status"]) ?? "EM_PROCESSAMENTO", Json.Str(n["endToEndId"]) ?? Json.Str(n["e2eId"]));
    }

    public static string MapPixStatus(string? s) => s switch
    {
        "CONCLUIDA" => "RECEIVED",
        "REMOVIDA_PELO_USUARIO_RECEBEDOR" or "REMOVIDA_PELO_PSP" => "DELETED",
        _ => "PENDING" // ATIVA
    };

    public static string MapBilletStatus(string? s) => s switch
    {
        "paid" or "settled" => "RECEIVED",
        "unpaid" => "OVERDUE",
        "refunded" => "REFUNDED",
        "canceled" or "expired" => "DELETED",
        _ => "PENDING" // new, waiting, identified, contested, link
    };

    private static string? StripDataUri(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var i = s.IndexOf("base64,", StringComparison.Ordinal);
        return i >= 0 ? s[(i + 7)..] : s;
    }
}

internal static class EfiStringExtensions
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
