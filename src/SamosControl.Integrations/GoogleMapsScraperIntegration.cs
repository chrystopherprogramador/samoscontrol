using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public record ScrapedPlace(string? Name, string? Phone, string? Address, string? Website, string? PlaceId,
    string? City, string? State, string? Category, string? Email);

public record ScraperSearchCreated(string? ExternalId, List<ScrapedPlace> Items);

/// <summary>Unknown = the scraper has no status endpoint (generic mode): results are fetched directly.</summary>
public enum ScraperJobStatus { Unknown, Running, Completed, Failed }

public interface IGoogleMapsScraperIntegration
{
    bool IsConfigured { get; }
    Task<ScraperSearchCreated> CreateSearchAsync(string query, string? location, CancellationToken ct = default);
    Task<ScraperJobStatus> GetStatusAsync(string externalId, CancellationToken ct = default);
    Task<List<ScrapedPlace>> GetResultsAsync(string externalId, CancellationToken ct = default);
}

/// <summary>
/// Adapter for the user's own Google Maps scraper. Mode "gosom" implements the documented API of
/// gosom/google-maps-scraper v1.18.1 (jobs + CSV). Otherwise paths, body template and JSON field names are
/// configuration (see INTEGRATIONS.md); defaults for field names are only fallbacks.
/// </summary>
public class GoogleMapsScraperIntegration : IGoogleMapsScraperIntegration
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;

    public GoogleMapsScraperIntegration(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    // gosom/google-maps-scraper v1.18.1 web API (web/web.go): POST jobs, GET jobs/{id}, GET jobs/{id}/download (CSV).
    private const string GosomJobs = "api/v1/jobs";
    // The body is JSON-escaped per placeholder. max_time is in seconds (web.go multiplies by time.Second).
    private const string GosomDefaultBody =
        "{\"name\":\"{{search}}\",\"keywords\":[\"{{search}}\"],\"lang\":\"pt\",\"zoom\":15,\"depth\":10,\"max_time\":600,\"fast_mode\":false,\"email\":false}";

    private bool Gosom => string.Equals(_s.Get(SettingKeys.GoogleMode)?.Trim(), "gosom", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.GoogleUrl))
                                && (Gosom || !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.GoogleCreatePath)));

    private HttpClient Client()
    {
        if (!IsConfigured) throw new NotConfiguredException("Google Maps Scraper");
        var c = _http.CreateClient("google-scraper");
        c.BaseAddress = new Uri(_s.Get(SettingKeys.GoogleUrl)!.Trim().TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(60);
        var basicUser = _s.Get(SettingKeys.GoogleBasicUser);
        if (!string.IsNullOrWhiteSpace(basicUser))
        {
            var raw = Encoding.UTF8.GetBytes(basicUser.Trim() + ":" + (_s.Get(SettingKeys.GoogleBasicPassword) ?? ""));
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
        var key = _s.Get(SettingKeys.GoogleApiKey);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var header = _s.Get(SettingKeys.GoogleApiKeyHeader);
            var name = string.IsNullOrWhiteSpace(header) ? "X-API-Key" : header.Trim();
            // Basic auth already owns Authorization; never send two values.
            if (c.DefaultRequestHeaders.Authorization is null || !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                c.DefaultRequestHeaders.TryAddWithoutValidation(name, key);
        }
        return c;
    }

    /// <summary>Clear messages for auth/not-found/timeout. Never echoes headers or credentials.</summary>
    private static void EnsureSuccess(HttpResponseMessage res, string body)
    {
        if (res.IsSuccessStatusCode) return;
        var code = (int)res.StatusCode;
        if (code is 401 or 403)
            throw new IntegrationException($"Scraper recusou a autenticação (HTTP {code}). Verifique usuário/senha (Basic) ou API key em Configurações.");
        throw new IntegrationException($"Scraper retornou {code}: {Trunc(body)}");
    }

    private static IntegrationException Wrap(Exception ex, CancellationToken ct) =>
        ex is TaskCanceledException && !ct.IsCancellationRequested
            ? new IntegrationException("Tempo esgotado ao comunicar com o Google Scraper.", ex)
            : new IntegrationException("Falha ao comunicar com o Google Scraper: " + ex.Message, ex);

    public async Task<ScraperSearchCreated> CreateSearchAsync(string query, string? location, CancellationToken ct = default)
    {
        var c = Client();
        var template = _s.Get(SettingKeys.GoogleCreateBody);
        if (string.IsNullOrWhiteSpace(template)) template = Gosom ? GosomDefaultBody : "{\"query\":\"{{query}}\",\"location\":\"{{location}}\"}";
        // Values are JSON-escaped before insertion so user input cannot break out of the template.
        string Esc(string? v) => System.Text.Json.JsonSerializer.Serialize(v ?? "")[1..^1];
        var search = $"{query} {location}".Trim();
        var body = template.Replace("{{query}}", Esc(query)).Replace("{{location}}", Esc(location)).Replace("{{search}}", Esc(search));
        var path = Gosom ? GosomJobs : _s.Get(SettingKeys.GoogleCreatePath)!.TrimStart('/');
        try
        {
            using var res = await c.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            EnsureSuccess(res, text);
            var node = JsonNode.Parse(text);
            if (Gosom)
            {
                var jobId = Json.Str(Json.AtIgnoreCase(node, "id"));
                if (string.IsNullOrWhiteSpace(jobId)) throw new IntegrationException("Resposta do scraper sem o campo 'id'.");
                return new ScraperSearchCreated(jobId, new List<ScrapedPlace>());
            }
            var id = Json.Str(Json.At(node, _s.Get(SettingKeys.GoogleSearchIdPath)));
            var items = string.IsNullOrWhiteSpace(_s.Get(SettingKeys.GoogleSearchIdPath)) ? ParseItems(node) : new List<ScrapedPlace>();
            return new ScraperSearchCreated(id, items);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw Wrap(ex, ct);
        }
    }

    public async Task<ScraperJobStatus> GetStatusAsync(string externalId, CancellationToken ct = default)
    {
        if (!Gosom) return ScraperJobStatus.Unknown;
        var c = Client();
        try
        {
            using var res = await c.GetAsync($"{GosomJobs}/{Uri.EscapeDataString(externalId)}", ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            EnsureSuccess(res, text);
            var status = Json.Str(Json.AtIgnoreCase(JsonNode.Parse(text), "status"))?.Trim().ToLowerInvariant();
            return status switch
            {
                "ok" => ScraperJobStatus.Completed,
                "failed" => ScraperJobStatus.Failed,
                "pending" or "working" => ScraperJobStatus.Running,
                _ => throw new IntegrationException($"Status desconhecido retornado pelo scraper: '{Trunc(status ?? "")}'.")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw Wrap(ex, ct);
        }
    }

    public async Task<List<ScrapedPlace>> GetResultsAsync(string externalId, CancellationToken ct = default)
    {
        var c = Client();
        string path;
        if (Gosom) path = $"{GosomJobs}/{Uri.EscapeDataString(externalId)}/download";
        else
        {
            var tpl = _s.Get(SettingKeys.GoogleResultsPath);
            if (string.IsNullOrWhiteSpace(tpl)) throw new IntegrationException("GOOGLE_SCRAPER_RESULTS_PATH não configurado.");
            path = tpl.Replace("{{searchId}}", Uri.EscapeDataString(externalId)).TrimStart('/');
        }
        try
        {
            using var res = await c.GetAsync(path, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            EnsureSuccess(res, text);
            if (!Gosom) return ParseItems(JsonNode.Parse(text));
            var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new IntegrationException($"Download do scraper retornou '{mediaType}' em vez de CSV.");
            return GoogleScraperCsv.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw Wrap(ex, ct);
        }
    }

    private List<ScrapedPlace> ParseItems(JsonNode? root)
    {
        var list = new List<ScrapedPlace>();
        if (Json.At(root, _s.Get(SettingKeys.GoogleItemsPath)) is not JsonArray arr) return list;
        string? Map(JsonNode item, string key, string def)
        {
            var p = _s.Get(SettingKeys.GoogleMapPrefix + key);
            return Json.Str(Json.At(item, string.IsNullOrWhiteSpace(p) ? def : p));
        }
        foreach (var item in arr)
        {
            if (item is null) continue;
            list.Add(new ScrapedPlace(Map(item, "NAME", "name"), Map(item, "PHONE", "phone"), Map(item, "ADDRESS", "address"),
                Map(item, "WEBSITE", "website"), Map(item, "PLACE_ID", "place_id"), Map(item, "CITY", "city"),
                Map(item, "STATE", "state"), Map(item, "CATEGORY", "category"), Map(item, "EMAIL", "email")));
        }
        return list;
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] : s;
}
