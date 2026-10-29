using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public record AsaasCustomerRequest(string Name, string? CpfCnpj, string? Email, string? Phone, string? ExternalReference);
public record AsaasPayment(string Id, string Status, decimal Value, string? InvoiceUrl, DateTime? DueDate, decimal? NetValue, DateTime? PaymentDate);
public record AsaasPix(string Payload, string EncodedImage, DateTime? ExpirationDate);

public interface IAsaasIntegration
{
    bool IsConfigured { get; }
    Task<string> CreateCustomerAsync(AsaasCustomerRequest r, CancellationToken ct = default);
    Task UpdateCustomerAsync(string id, AsaasCustomerRequest r, CancellationToken ct = default);
    Task<AsaasPayment> CreatePaymentAsync(string customerId, string billingType, decimal value, DateTime dueDate, string? description, string? externalReference, CancellationToken ct = default);
    Task<AsaasPix> GetPixQrCodeAsync(string paymentId, CancellationToken ct = default);
    Task<AsaasPayment> GetPaymentAsync(string paymentId, CancellationToken ct = default);
    Task<bool> CancelPaymentAsync(string paymentId, CancellationToken ct = default);
}

/// <summary>Asaas API v3 (https://docs.asaas.com). Auth header: access_token.</summary>
public class AsaasIntegration : IAsaasIntegration
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    public AsaasIntegration(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.AsaasUrl)) && !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.AsaasKey));

    private HttpClient Client()
    {
        if (!IsConfigured) throw new NotConfiguredException("Asaas");
        var c = _http.CreateClient("asaas");
        c.BaseAddress = new Uri(_s.Get(SettingKeys.AsaasUrl)!.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.Add("access_token", _s.Get(SettingKeys.AsaasKey));
        c.DefaultRequestHeaders.Add("User-Agent", "SamosControl");
        return c;
    }

    private static async Task<JsonNode> Send(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            using var res = await call();
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                string msg = text.Length > 300 ? text[..300] : text;
                try { msg = string.Join("; ", (JsonNode.Parse(text)?["errors"] as JsonArray ?? new JsonArray()).Select(e => Json.Str(e?["description"]))); } catch { }
                throw new IntegrationException($"Asaas {(int)res.StatusCode}: {msg}");
            }
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new IntegrationException("Falha ao comunicar com o Asaas: " + ex.Message, ex);
        }
    }

    private static object CustomerBody(AsaasCustomerRequest r) => new
    {
        name = r.Name, cpfCnpj = r.CpfCnpj, email = r.Email, mobilePhone = r.Phone, externalReference = r.ExternalReference
    };

    public async Task<string> CreateCustomerAsync(AsaasCustomerRequest r, CancellationToken ct = default)
    {
        var c = Client();
        var n = await Send(() => c.PostAsJsonAsync("customers", CustomerBody(r), Json.Opts, ct));
        return Json.Str(n["id"]) ?? throw new IntegrationException("Asaas não retornou o id do cliente.");
    }

    public async Task UpdateCustomerAsync(string id, AsaasCustomerRequest r, CancellationToken ct = default)
    {
        var c = Client();
        await Send(() => c.PostAsJsonAsync($"customers/{Uri.EscapeDataString(id)}", CustomerBody(r), Json.Opts, ct));
    }

    public async Task<AsaasPayment> CreatePaymentAsync(string customerId, string billingType, decimal value, DateTime dueDate, string? description, string? externalReference, CancellationToken ct = default)
    {
        var c = Client();
        var body = new
        {
            customer = customerId, billingType, value, dueDate = dueDate.ToString("yyyy-MM-dd"), description, externalReference
        };
        return ToPayment(await Send(() => c.PostAsJsonAsync("payments", body, Json.Opts, ct)));
    }

    public async Task<AsaasPix> GetPixQrCodeAsync(string paymentId, CancellationToken ct = default)
    {
        var c = Client();
        var n = await Send(() => c.GetAsync($"payments/{Uri.EscapeDataString(paymentId)}/pixQrCode", ct));
        return new AsaasPix(Json.Str(n["payload"]) ?? "", Json.Str(n["encodedImage"]) ?? "", ParseDate(Json.Str(n["expirationDate"])));
    }

    public async Task<AsaasPayment> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var c = Client();
        return ToPayment(await Send(() => c.GetAsync($"payments/{Uri.EscapeDataString(paymentId)}", ct)));
    }

    public async Task<bool> CancelPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var c = Client();
        var n = await Send(() => c.DeleteAsync($"payments/{Uri.EscapeDataString(paymentId)}", ct));
        return n["deleted"]?.GetValue<bool>() ?? false;
    }

    public static AsaasPayment ToPayment(JsonNode n) => new(
        Json.Str(n["id"]) ?? "", Json.Str(n["status"]) ?? "", decimal.TryParse(Json.Str(n["value"]), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0,
        Json.Str(n["invoiceUrl"]), ParseDate(Json.Str(n["dueDate"])),
        decimal.TryParse(Json.Str(n["netValue"]), NumberStyles.Any, CultureInfo.InvariantCulture, out var nv) ? nv : null,
        ParseDate(Json.Str(n["paymentDate"])));

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
