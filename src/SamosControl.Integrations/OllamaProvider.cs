using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public record AiChatMessage(string Role, string Content);

/// <summary>LLM provider abstraction. Only Ollama is implemented; add others by implementing this interface.</summary>
public interface IAiProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<List<string>> ListModelsAsync(CancellationToken ct = default);
    /// <summary>Chat completion. When jsonMode is true the provider is asked to return a JSON document.</summary>
    Task<string> ChatAsync(IEnumerable<AiChatMessage> messages, bool jsonMode, CancellationToken ct = default);
}

/// <summary>
/// Ollama REST API: GET /api/tags, POST /api/chat. Works with Ollama Cloud (https://ollama.com + API key, Bearer)
/// or a self-hosted server. Defaults: cloud URL and the free model "beam"; a cheap fallback model is tried if the main one fails.
/// </summary>
public class OllamaProvider : IAiProvider
{
    public const string DefaultUrl = "https://ollama.com", DefaultModel = "beam", DefaultFallbackModel = "gpt-oss:20b";

    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    public OllamaProvider(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    public string Name => "ollama";
    private string? ApiKey => _s.Get(SettingKeys.OllamaApiKey) is { Length: > 0 } k && !string.IsNullOrWhiteSpace(k) ? k.Trim() : null;
    private string BaseUrl => _s.Get(SettingKeys.OllamaUrl) is { } u && !string.IsNullOrWhiteSpace(u) ? u.Trim() : DefaultUrl;
    private string Model => _s.Get(SettingKeys.OllamaModel) is { } m && !string.IsNullOrWhiteSpace(m) ? m.Trim() : DefaultModel;
    private string Fallback => _s.Get(SettingKeys.OllamaFallbackModel) is { } m && !string.IsNullOrWhiteSpace(m) ? m.Trim() : DefaultFallbackModel;

    // Cloud needs the token; a self-hosted URL set explicitly works without one.
    public bool IsConfigured => ApiKey != null || !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.OllamaUrl)) && !BaseUrl.Contains("ollama.com", StringComparison.OrdinalIgnoreCase);

    private HttpClient Client()
    {
        var c = _http.CreateClient("ollama");
        c.BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(3);
        if (ApiKey != null) c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        return c;
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var c = Client();
            var n = JsonNode.Parse(await c.GetStringAsync("api/tags", ct));
            return (n?["models"] as JsonArray ?? new JsonArray()).Select(m => Json.Str(m?["name"])).Where(x => x != null).Select(x => x!).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new IntegrationException("Falha ao listar modelos do Ollama: " + ex.Message, ex);
        }
    }

    public async Task<string> ChatAsync(IEnumerable<AiChatMessage> messages, bool jsonMode, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new NotConfiguredException("Ollama (token)");
        var list = messages.ToList();
        try { return await ChatWithAsync(Model, list, jsonMode, ct); }
        catch (IntegrationException) when (!string.Equals(Model, Fallback, StringComparison.OrdinalIgnoreCase))
        {
            return await ChatWithAsync(Fallback, list, jsonMode, ct);
        }
    }

    private async Task<string> ChatWithAsync(string model, List<AiChatMessage> messages, bool jsonMode, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            ["options"] = new { temperature = 0.1 }
        };
        if (jsonMode) body["format"] = "json";
        try
        {
            var c = Client();
            using var res = await c.PostAsJsonAsync("api/chat", body, Json.Opts, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var hint = res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? " (verifique o token do Ollama Cloud)" : "";
                throw new IntegrationException($"Ollama {(int)res.StatusCode}{hint}: {(text.Length > 300 ? text[..300] : text)}");
            }
            return Json.Str(JsonNode.Parse(text)?["message"]?["content"]) ?? "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new IntegrationException("Falha ao comunicar com o Ollama: " + ex.Message, ex);
        }
    }
}