using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class OllamaTests
{
    private sealed class Settings : ISettingsReader
    {
        public Dictionary<string, string?> V { get; } = new();
        public string? Get(string key) => V.TryGetValue(key, out var v) ? v : null;
    }

    private sealed class Stub(HttpStatusCode firstStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.RequestUri!.ToString(), r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct), r.Headers.Authorization?.ToString()));
            var status = Calls.Count == 1 ? firstStatus : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new StringContent("""{"message":{"content":"{\"tool\":\"x\"}"}}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    [Fact]
    public async Task Token_only_uses_cloud_url_bearer_and_default_model()
    {
        var s = new Settings(); s.V[SettingKeys.OllamaApiKey] = "k123";
        var stub = new Stub(); var p = new OllamaProvider(new Factory(stub), s);
        Assert.True(p.IsConfigured);
        await p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, true);
        Assert.Equal("https://ollama.com/api/chat", stub.Calls[0].Url);
        Assert.Equal("Bearer k123", stub.Calls[0].Auth);
        Assert.Equal("beam", JsonNode.Parse(stub.Calls[0].Body)!["model"]!.ToString());
    }

    [Fact]
    public async Task Falls_back_to_second_model_on_error()
    {
        var s = new Settings(); s.V[SettingKeys.OllamaApiKey] = "k";
        var stub = new Stub(HttpStatusCode.TooManyRequests); var p = new OllamaProvider(new Factory(stub), s);
        await p.ChatAsync(new[] { new AiChatMessage("user", "oi") }, false);
        Assert.Equal(2, stub.Calls.Count);
        Assert.Equal("gpt-oss:20b", JsonNode.Parse(stub.Calls[1].Body)!["model"]!.ToString());
    }

    [Fact]
    public void Not_configured_without_token_on_cloud()
        => Assert.False(new OllamaProvider(new Factory(new Stub()), new Settings()).IsConfigured);
}
