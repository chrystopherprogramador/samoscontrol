using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public interface IEvolutionApiIntegration
{
    bool IsConfigured { get; }
    Task<string?> SendTextAsync(string phone, string text, CancellationToken ct = default);
    Task<string?> SendDocumentAsync(string phone, byte[] content, string fileName, string mimeType, string? caption, CancellationToken ct = default);
}

/// <summary>Evolution API v2 (header apikey, /message/sendText/{instance}, /message/sendMedia/{instance}).</summary>
public class EvolutionApiIntegration : IEvolutionApiIntegration
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    public EvolutionApiIntegration(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EvolutionUrl))
        && !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EvolutionKey))
        && !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.EvolutionInstance));

    private (HttpClient, string) Client()
    {
        if (!IsConfigured) throw new NotConfiguredException("Evolution API");
        var c = _http.CreateClient("evolution");
        c.BaseAddress = new Uri(_s.Get(SettingKeys.EvolutionUrl)!.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.Add("apikey", _s.Get(SettingKeys.EvolutionKey));
        return (c, Uri.EscapeDataString(_s.Get(SettingKeys.EvolutionInstance)!));
    }

    public Task<string?> SendTextAsync(string phone, string text, CancellationToken ct = default)
    {
        var (c, inst) = Client();
        return Post(c, $"message/sendText/{inst}", new { number = Require(phone), text }, ct);
    }

    public Task<string?> SendDocumentAsync(string phone, byte[] content, string fileName, string mimeType, string? caption, CancellationToken ct = default)
    {
        var (c, inst) = Client();
        return Post(c, $"message/sendMedia/{inst}", new
        {
            number = Require(phone), mediatype = "document", mimetype = mimeType, caption, fileName, media = Convert.ToBase64String(content)
        }, ct);
    }

    private static string Require(string phone) =>
        PhoneUtil.Normalize(phone) ?? throw new IntegrationException("Telefone inválido para envio de WhatsApp.");

    private static async Task<string?> Post(HttpClient c, string path, object body, CancellationToken ct)
    {
        try
        {
            using var res = await c.PostAsJsonAsync(path, body, Json.Opts, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new IntegrationException($"Evolution API {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
            try { return Json.Str(JsonNode.Parse(text)?["key"]?["id"]); } catch (JsonException) { return null; }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IntegrationException("Falha ao comunicar com a Evolution API: " + ex.Message, ex);
        }
    }
}

public record EvolutionIncoming(string ExternalId, string Phone, string? PushName, string? Text, string Type, bool FromMe, DateTime Timestamp);
public record EvolutionStatusUpdate(string ExternalId, string Status);

/// <summary>Defensive parser for Evolution webhook payloads (event MESSAGES_UPSERT / MESSAGES_UPDATE / SEND_MESSAGE).</summary>
public static class EvolutionWebhookParser
{
    public static string EventName(JsonNode root) => (Json.Str(root["event"]) ?? "").Replace('.', '_').ToUpperInvariant();

    public static List<EvolutionIncoming> ParseMessages(JsonNode root)
    {
        var res = new List<EvolutionIncoming>();
        var data = root["data"];
        IEnumerable<JsonNode?> items = data is JsonArray a ? a : data?["messages"] is JsonArray m ? m : new[] { data };
        foreach (var d in items)
        {
            if (d is null) continue;
            var key = d["key"];
            var id = Json.Str(key?["id"]);
            var jid = Json.Str(key?["remoteJid"]) ?? "";
            if (id is null || jid.Length == 0 || jid.EndsWith("@g.us") || jid.Contains("status@broadcast")) continue; // ignora grupos/status
            // Quando remoteJid é um LID, o número real pode vir em remoteJidAlt/senderPn.
            var alt = Json.Str(key?["remoteJidAlt"]) ?? Json.Str(key?["senderPn"]);
            if (jid.EndsWith("@lid") && alt is not null) jid = alt;
            var phone = new string(jid.Split('@')[0].Where(char.IsDigit).ToArray());
            if (phone.Length < 8) continue;
            var msg = d["message"];
            var text = Json.Str(msg?["conversation"]) ?? Json.Str(msg?["extendedTextMessage"]?["text"])
                       ?? Json.Str(msg?["imageMessage"]?["caption"]) ?? Json.Str(msg?["documentMessage"]?["caption"]);
            var type = Json.Str(d["messageType"]) ?? (msg is JsonObject o ? o.Select(kv => kv.Key).FirstOrDefault() : null) ?? "text";
            var ts = DateTime.UtcNow;
            if (long.TryParse(Json.Str(d["messageTimestamp"]), out var secs) && secs > 0)
                ts = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            res.Add(new EvolutionIncoming(id, phone, Json.Str(d["pushName"]), text, type, key?["fromMe"]?.GetValue<bool>() ?? false, ts));
        }
        return res;
    }

    public static List<EvolutionStatusUpdate> ParseStatus(JsonNode root)
    {
        var res = new List<EvolutionStatusUpdate>();
        var data = root["data"];
        IEnumerable<JsonNode?> items = data is JsonArray a ? a : new[] { data };
        foreach (var d in items)
        {
            if (d is null) continue;
            var id = Json.Str(d["keyId"]) ?? Json.Str(d["key"]?["id"]);
            var st = Json.Str(d["status"]);
            if (id is not null && st is not null) res.Add(new EvolutionStatusUpdate(id, st));
        }
        return res;
    }
}
