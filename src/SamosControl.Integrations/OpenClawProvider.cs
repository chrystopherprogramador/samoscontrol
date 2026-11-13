using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SamosControl.Integrations;

/// <summary>
/// Roberta 2.0: runtime OpenClaw via OpenResponses (POST /v1/responses). O gateway é privado (rede Docker) e autenticado por Bearer.
/// O modelo (ex.: github-copilot/...) é escolhido no Samos e enviado em x-openclaw-model; vazio = padrão do gateway.
/// </summary>
public class OpenClawProvider : IAiProvider
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    public OpenClawProvider(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    public string Name => "openclaw";
    private string? Url => _s.Get(SettingKeys.OpenClawUrl) is { } u && !string.IsNullOrWhiteSpace(u) ? u.Trim().TrimEnd('/') : null;
    private string? Token => _s.Get(SettingKeys.OpenClawToken) is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
    public string? Model => _s.Get(SettingKeys.OpenClawModel) is { } m && !string.IsNullOrWhiteSpace(m) ? m.Trim() : null;
    private int TimeoutSeconds => int.TryParse(_s.Get(SettingKeys.OpenClawTimeout), out var n) && n is >= 5 and <= 300 ? n : 90;
    public bool IsConfigured => Url != null && Token != null;

    /// <summary>O gateway só publica alvos de agente via HTTP; o catálogo de modelos do Copilot vem do CLI do gateway.</summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "/v1/models");
        using var res = await SendAsync(req, ct);
        var n = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return (n?["data"] as JsonArray ?? new JsonArray()).Select(m => Json.Str(m?["id"])).Where(x => x != null).Select(x => x!).ToList();
    }

    public async Task<string> ChatAsync(IEnumerable<AiChatMessage> messages, bool jsonMode, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new NotConfiguredException("OpenClaw (URL e token)");
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == "system").Select(m => m.Content));
        if (jsonMode) system += "\n\nResponda SOMENTE com um documento JSON válido, sem texto fora dele.";
        var body = new JsonObject
        {
            ["model"] = "openclaw/default",
            ["instructions"] = system,
            ["input"] = new JsonArray(list.Where(m => m.Role != "system").Select(m => (JsonNode)new JsonObject
            {
                ["type"] = "message",
                ["role"] = m.Role == "assistant" ? "assistant" : "user",
                ["content"] = m.Content
            }).ToArray())
        };
        using var req = NewRequest(HttpMethod.Post, "/v1/responses");
        if (Model != null) req.Headers.TryAddWithoutValidation("x-openclaw-model", Model);
        req.Content = JsonContent.Create(body);
        using var res = await SendAsync(req, ct);
        return ExtractText(await res.Content.ReadAsStringAsync(ct));
    }

    public static string ExtractText(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["status"]?.GetValue<string>() is "failed") throw new IntegrationException("OpenClaw: execução falhou. " + Json.Str(root["error"]?["message"]));
        var sb = new System.Text.StringBuilder();
        foreach (var item in root?["output"] as JsonArray ?? new JsonArray())
            foreach (var c in item?["content"] as JsonArray ?? new JsonArray())
                if (Json.Str(c?["type"]) == "output_text") sb.Append(Json.Str(c?["text"]));
        return sb.ToString();
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, (Url ?? throw new NotConfiguredException("OpenClaw (URL)")) + path);
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return r;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var res = await _http.CreateClient("openclaw").SendAsync(req, cts.Token);
            if (res.IsSuccessStatusCode) return res;
            var status = (int)res.StatusCode;
            var hint = status is 401 or 403 ? " (token do OpenClaw inválido)" : status == 404 ? " (endpoint /v1/responses desabilitado no gateway?)" : "";
            res.Dispose();
            throw new IntegrationException($"OpenClaw HTTP {status}{hint}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IntegrationException("OpenClaw indisponível ou lento: " + ex.Message, ex);
        }
    }
}

/// <summary>
/// Escolhe o runtime conforme ROBERTA_PROVIDER. Com openclaw ativo e falha/indisponibilidade, cai para o Ollama
/// (desligável em ROBERTA_FALLBACK_OLLAMA=false). Padrão ollama: nada muda até o proprietário ativar.
/// </summary>
public class RoutingAiProvider : IAiProvider
{
    private readonly OpenClawProvider _claw; private readonly OllamaProvider _ollama; private readonly ISettingsReader _s; private readonly ILogger<RoutingAiProvider>? _log;
    public RoutingAiProvider(OpenClawProvider claw, OllamaProvider ollama, ISettingsReader s, ILogger<RoutingAiProvider>? log = null) { _claw = claw; _ollama = ollama; _s = s; _log = log; }

    private bool UseClaw => string.Equals(_s.Get(SettingKeys.RobertaProvider)?.Trim(), "openclaw", StringComparison.OrdinalIgnoreCase) && _claw.IsConfigured;
    private bool FallbackOn => !string.Equals(_s.Get(SettingKeys.RobertaFallbackOllama)?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    public string Name => UseClaw ? _claw.Name : _ollama.Name;
    public bool IsConfigured => UseClaw || _ollama.IsConfigured;
    public Task<List<string>> ListModelsAsync(CancellationToken ct = default) => UseClaw ? _claw.ListModelsAsync(ct) : _ollama.ListModelsAsync(ct);

    public async Task<string> ChatAsync(IEnumerable<AiChatMessage> messages, bool jsonMode, CancellationToken ct = default)
    {
        var list = messages.ToList();
        if (!UseClaw) return await _ollama.ChatAsync(list, jsonMode, ct);
        try { return await _claw.ChatAsync(list, jsonMode, ct); }
        catch (IntegrationException ex) when (FallbackOn && _ollama.IsConfigured && !ct.IsCancellationRequested)
        {
            _log?.LogWarning(ex, "OpenClaw indisponível; usando Ollama como contingência");
            return await _ollama.ChatAsync(list, jsonMode, ct);
        }
    }
}
