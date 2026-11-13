using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class OpenClawTests
{
    private sealed class Settings : ISettingsReader
    {
        public Dictionary<string, string?> V { get; } = new();
        public string? Get(string key) => V.TryGetValue(key, out var v) ? v : null;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth, string? Model)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.RequestUri!.ToString(), r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct), r.Headers.Authorization?.ToString(),
                r.Headers.TryGetValues("x-openclaw-model", out var m) ? m.First() : null));
            return respond(r);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    private const string Ok = """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi, tudo certo"}]}]}""";
    private static HttpResponseMessage Json(HttpStatusCode c, string body) => new(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Settings Cfg(string provider = "openclaw")
    {
        var s = new Settings();
        s.V[SettingKeys.RobertaProvider] = provider; s.V[SettingKeys.OpenClawUrl] = "http://gw:18789/"; s.V[SettingKeys.OpenClawToken] = "tok";
        s.V[SettingKeys.OpenClawModel] = "github-copilot/modelo-x"; s.V[SettingKeys.OllamaApiKey] = "ok";
        return s;
    }

    private static (RoutingAiProvider P, Stub Claw, Stub Oll) Build(Settings s, Func<HttpRequestMessage, HttpResponseMessage> claw)
    {
        var c = new Stub(claw);
        var o = new Stub(_ => Json(HttpStatusCode.OK, """{"message":{"content":"resposta ollama"}}"""));
        return (new RoutingAiProvider(new OpenClawProvider(new Factory(c), s), new OllamaProvider(new Factory(o), s), s), c, o);
    }

    [Fact]
    public async Task OpenClaw_sends_bearer_model_header_and_parses_output()
    {
        var (p, claw, oll) = Build(Cfg(), _ => Json(HttpStatusCode.OK, Ok));
        var text = await p.ChatAsync(new[] { new AiChatMessage("system", "regras"), new AiChatMessage("user", "oi") }, true);
        Assert.Equal("oi, tudo certo", text);
        Assert.Equal("http://gw:18789/v1/responses", claw.Calls[0].Url);
        Assert.Equal("Bearer tok", claw.Calls[0].Auth);
        Assert.Equal("github-copilot/modelo-x", claw.Calls[0].Model);
        var body = JsonNode.Parse(claw.Calls[0].Body)!;
        Assert.Contains("regras", body["instructions"]!.ToString());
        Assert.Equal("user", body["input"]![0]!["role"]!.ToString());
        Assert.Empty(oll.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Falls_back_to_ollama_when_openclaw_fails(HttpStatusCode code)
    {
        var (p, claw, oll) = Build(Cfg(), _ => Json(code, "{}"));
        Assert.Equal("resposta ollama", await p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, false));
        Assert.Single(claw.Calls); Assert.Single(oll.Calls);
    }

    [Fact]
    public async Task Fallback_disabled_surfaces_the_error()
    {
        var s = Cfg(); s.V[SettingKeys.RobertaFallbackOllama] = "false";
        var (p, _, oll) = Build(s, _ => Json(HttpStatusCode.BadGateway, "{}"));
        await Assert.ThrowsAsync<IntegrationException>(() => p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, false));
        Assert.Empty(oll.Calls);
    }

    [Fact]
    public async Task Network_failure_falls_back_to_ollama()
    {
        var (p, _, oll) = Build(Cfg(), _ => throw new HttpRequestException("recusada"));
        Assert.Equal("resposta ollama", await p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, false));
        Assert.Single(oll.Calls);
    }

    [Fact]
    public async Task Default_provider_never_touches_openclaw()
    {
        var (p, claw, oll) = Build(Cfg("ollama"), _ => Json(HttpStatusCode.OK, Ok));
        await p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, false);
        Assert.Empty(claw.Calls); Assert.Single(oll.Calls);
    }

    [Fact]
    public async Task Failed_status_in_body_is_an_error_and_falls_back()
    {
        var (p, _, oll) = Build(Cfg(), _ => Json(HttpStatusCode.OK, """{"status":"failed","error":{"message":"cota"}}"""));
        Assert.Equal("resposta ollama", await p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, false));
        Assert.Single(oll.Calls);
    }
}
