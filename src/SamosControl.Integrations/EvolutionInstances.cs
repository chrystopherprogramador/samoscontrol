using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public record EvolutionConnectResult(string State, string? QrBase64, string? PairingCode);
public record EvolutionInstanceInfo(string State, string? Number, string? ProfileName);

/// <summary>Per-instance operations of Evolution API v2 (instance lifecycle, webhook, send by instance).</summary>
public interface IEvolutionInstances
{
    /// <summary>True when only URL + API key are set (instance name is not needed for seller instances).</summary>
    bool ServerConfigured { get; }
    Task<EvolutionConnectResult> CreateInstanceAsync(string instance, string webhookUrl, string webhookToken, CancellationToken ct = default);
    Task<EvolutionConnectResult> ConnectAsync(string instance, CancellationToken ct = default);
    Task<EvolutionInstanceInfo> GetInfoAsync(string instance, CancellationToken ct = default);
    Task SetWebhookAsync(string instance, string webhookUrl, string webhookToken, CancellationToken ct = default);
    Task LogoutAsync(string instance, CancellationToken ct = default);
    Task DeleteAsync(string instance, CancellationToken ct = default);
    Task<string?> SendTextAsync(string instance, string phone, string text, CancellationToken ct = default);
    Task<string?> SendMediaAsync(string instance, string phone, byte[] content, string fileName, string mimeType, string? caption, CancellationToken ct = default);
}

public class EvolutionInstances : IEvolutionInstances
{
    public static readonly string[] WebhookEvents = { "QRCODE_UPDATED", "CONNECTION_UPDATE", "MESSAGES_UPSERT", "MESSAGES_UPDATE", "SEND_MESSAGE" };

    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    public EvolutionInstances(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    public bool ServerConfigured => !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EvolutionUrl))
        && !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EvolutionKey));

    private HttpClient Client()
    {
        if (!ServerConfigured) throw new NotConfiguredException("Evolution API");
        var c = _http.CreateClient("evolution");
        c.BaseAddress = new Uri(_s.Get(SettingKeys.EvolutionUrl)!.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.Add("apikey", _s.Get(SettingKeys.EvolutionKey));
        return c;
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    private static object WebhookBody(string url, string token) => new
    {
        enabled = true, url, byEvents = false, base64 = false,
        headers = new Dictionary<string, string> { ["x-webhook-token"] = token, ["Content-Type"] = "application/json" },
        events = WebhookEvents
    };

    public async Task<EvolutionConnectResult> CreateInstanceAsync(string instance, string webhookUrl, string webhookToken, CancellationToken ct = default)
    {
        var c = Client();
        var (ok, status, text) = await Send(c, HttpMethod.Post, "instance/create", new
        {
            instanceName = instance, integration = "WHATSAPP-BAILEYS", qrcode = true,
            rejectCall = false, groupsIgnore = true, alwaysOnline = false, readMessages = false, syncFullHistory = false,
            webhook = WebhookBody(webhookUrl, webhookToken)
        }, ct);
        if (!ok)
        {
            // Instância já existe: reaproveita e reconfigura o webhook.
            if (status is 403 or 409 or 400 && text.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                await SetWebhookAsync(instance, webhookUrl, webhookToken, ct);
                return await ConnectAsync(instance, ct);
            }
            throw new IntegrationException($"Evolution API {status}: {Trim(text)}");
        }
        var node = TryParse(text);
        var qr = ParseQr(node?["qrcode"]);
        if (qr.Item1 is null && qr.Item2 is null) qr = ParseQr(node);
        return new EvolutionConnectResult("connecting", qr.Item1, qr.Item2);
    }

    public async Task<EvolutionConnectResult> ConnectAsync(string instance, CancellationToken ct = default)
    {
        var (ok, status, text) = await Send(Client(), HttpMethod.Get, $"instance/connect/{E(instance)}", null, ct);
        if (!ok) throw new IntegrationException($"Evolution API {status}: {Trim(text)}");
        var node = TryParse(text);
        var qr = ParseQr(node);
        var state = Json.Str(node?["instance"]?["state"]) ?? (qr.Item1 is null && qr.Item2 is null ? "open" : "connecting");
        return new EvolutionConnectResult(state, qr.Item1, qr.Item2);
    }

    public async Task<EvolutionInstanceInfo> GetInfoAsync(string instance, CancellationToken ct = default)
    {
        var c = Client();
        var (ok, status, text) = await Send(c, HttpMethod.Get, $"instance/connectionState/{E(instance)}", null, ct);
        if (!ok) throw new IntegrationException($"Evolution API {status}: {Trim(text)}");
        var node = TryParse(text);
        var state = Json.Str(node?["instance"]?["state"]) ?? Json.Str(node?["state"]) ?? "close";
        string? number = null, profile = null;
        if (state == "open")
        {
            var (ok2, _, t2) = await Send(c, HttpMethod.Get, $"instance/fetchInstances?instanceName={E(instance)}", null, ct);
            if (ok2)
            {
                var n = TryParse(t2);
                var first = n is JsonArray arr ? arr.FirstOrDefault() : n;
                var inst = first?["instance"] ?? first;
                var jid = Json.Str(inst?["ownerJid"]) ?? Json.Str(inst?["owner"]);
                if (jid is not null) number = new string(jid.Split('@')[0].Where(char.IsDigit).ToArray());
                profile = Json.Str(inst?["profileName"]);
            }
        }
        return new EvolutionInstanceInfo(state, number, profile);
    }

    public async Task SetWebhookAsync(string instance, string webhookUrl, string webhookToken, CancellationToken ct = default)
    {
        var c = Client();
        var path = $"webhook/set/{E(instance)}";
        var (ok, status, text) = await Send(c, HttpMethod.Post, path, new { webhook = WebhookBody(webhookUrl, webhookToken) }, ct);
        if (ok) return;
        // Versões antigas (2.0–2.2) aceitam o corpo "plano".
        var b = WebhookBody(webhookUrl, webhookToken);
        var (ok2, status2, text2) = await Send(c, HttpMethod.Post, path, b, ct);
        if (!ok2) throw new IntegrationException($"Evolution API webhook/set {status}/{status2}: {Trim(text)} | {Trim(text2)}");
    }

    public async Task LogoutAsync(string instance, CancellationToken ct = default)
    {
        var (ok, status, text) = await Send(Client(), HttpMethod.Delete, $"instance/logout/{E(instance)}", null, ct);
        if (!ok && status != 404 && status != 400) throw new IntegrationException($"Evolution API {status}: {Trim(text)}");
    }

    public async Task DeleteAsync(string instance, CancellationToken ct = default)
    {
        var (ok, status, text) = await Send(Client(), HttpMethod.Delete, $"instance/delete/{E(instance)}", null, ct);
        if (!ok && status != 404) throw new IntegrationException($"Evolution API {status}: {Trim(text)}");
    }

    public Task<string?> SendTextAsync(string instance, string phone, string text, CancellationToken ct = default) =>
        SendMessage($"message/sendText/{E(instance)}", new { number = Require(phone), text }, ct);

    public Task<string?> SendMediaAsync(string instance, string phone, byte[] content, string fileName, string mimeType, string? caption, CancellationToken ct = default)
    {
        var kind = mimeType.StartsWith("image/") ? "image" : mimeType.StartsWith("video/") ? "video" : "document";
        return SendMessage($"message/sendMedia/{E(instance)}", new
        {
            number = Require(phone), mediatype = kind, mimetype = mimeType, caption, fileName, media = Convert.ToBase64String(content)
        }, ct);
    }

    private async Task<string?> SendMessage(string path, object body, CancellationToken ct)
    {
        var (ok, status, text) = await Send(Client(), HttpMethod.Post, path, body, ct);
        if (!ok) throw new IntegrationException($"Evolution API {status}: {Trim(text)}");
        return Json.Str(TryParse(text)?["key"]?["id"]);
    }

    private static string Require(string phone) =>
        PhoneUtil.Normalize(phone) ?? throw new IntegrationException("Telefone inválido para envio de WhatsApp.");

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private static JsonNode? TryParse(string text) { try { return JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { return null; } }

    private static (string?, string?) ParseQr(JsonNode? n)
    {
        if (n is null) return (null, null);
        var b64 = Json.Str(n["base64"]);
        if (b64 is not null && !b64.StartsWith("data:")) b64 = "data:image/png;base64," + b64;
        return (b64, Json.Str(n["pairingCode"]));
    }

    private static async Task<(bool, int, string)> Send(HttpClient c, HttpMethod m, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(m, path);
            if (body is not null) req.Content = JsonContent.Create(body, options: Json.Opts);
            using var res = await c.SendAsync(req, ct);
            return (res.IsSuccessStatusCode, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IntegrationException("Falha ao comunicar com a Evolution API: " + ex.Message, ex);
        }
    }
}
