using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;

namespace SamosControl.Integrations.Voice;

/// <summary>Formato de áudio do stream de chamadas do Evolution Go Calls (meowcaller): PCM16 LE mono, 16 kHz, quadros de 960 amostras (60 ms).</summary>
public static class VoiceAudio
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 960;
    public const int FrameBytes = FrameSamples * 2;
    public const int FrameMs = 60;
}

public enum CallStreamEventKind { Started, Media, Stopped }

public record CallStreamEvent(CallStreamEventKind Kind, byte[]? Pcm = null, string? Reason = null, string? CallId = null);

/// <summary>Conexão de áudio bidirecional de UMA chamada.</summary>
public interface ICallStream : IAsyncDisposable
{
    IAsyncEnumerable<CallStreamEvent> ReadAsync(CancellationToken ct);
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);
    /// <summary>Envia "stop" e fecha. No Evolution Go Calls fechar o socket DESLIGA a chamada.</summary>
    Task CloseAsync();
}

/// <summary>Adaptador do serviço de chamadas do WhatsApp (Evolution Go Calls). Implementação desacoplada: trocável por outro provedor.</summary>
public interface ICallGateway
{
    bool IsConfigured { get; }
    Task<string> DialAsync(string phone, CancellationToken ct);
    Task AnswerAsync(string callCreator, string callId, CancellationToken ct);
    Task RejectAsync(string callCreator, string callId, CancellationToken ct);
    Task HangupAsync(string callId, CancellationToken ct);
    Task<ICallStream> OpenStreamAsync(string callId, CancellationToken ct);
}

/// <summary>Protocolo JSON do stream (formato baseado no Twilio Media Streams), conforme pkg/call/stream/bridge.go do Evolution Go Calls.</summary>
public static class CallStreamProtocol
{
    public static CallStreamEvent? Parse(string json)
    {
        try
        {
            var n = JsonNode.Parse(json);
            var ev = n?["event"]?.GetValue<string>();
            switch (ev)
            {
                case "start": return new CallStreamEvent(CallStreamEventKind.Started, CallId: n?["callId"]?.GetValue<string>());
                case "stop": return new CallStreamEvent(CallStreamEventKind.Stopped, Reason: n?["reason"]?.GetValue<string>());
                case "media" when (n?["track"]?.GetValue<string>() == "inbound"):
                {
                    var p = n["payload"]?.GetValue<string>();
                    return string.IsNullOrEmpty(p) ? null : new CallStreamEvent(CallStreamEventKind.Media, Convert.FromBase64String(p));
                }
                default: return null; // vídeo e eventos futuros são ignorados
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    public static string MediaMessage(ReadOnlySpan<byte> pcm) =>
        JsonSerializer.Serialize(new { @event = "media", track = "outbound", payload = Convert.ToBase64String(pcm) });

    public static string StopMessage() => JsonSerializer.Serialize(new { @event = "stop" });

    /// <summary>Token HMAC-SHA256 por chamada: mensagem "instance|callId|exp" (exp = unix seconds), em hex minúsculo.</summary>
    public static string Sign(string key, string instance, string callId, long exp)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{instance}|{callId}|{exp}"))).ToLowerInvariant();
    }
}

public class EvolutionGoCallsGateway : ICallGateway
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    private readonly TimeProvider _clock;
    public EvolutionGoCallsGateway(IHttpClientFactory http, ISettingsReader s, TimeProvider? clock = null) { _http = http; _s = s; _clock = clock ?? TimeProvider.System; }

    private string? Url => _s.Get(SettingKeys.VoiceGoUrl)?.Trim().TrimEnd('/');
    private string? Key => _s.Get(SettingKeys.VoiceGoApiKey);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Key);

    private HttpClient Client()
    {
        if (!IsConfigured) throw new NotConfiguredException("Evolution Go Calls (URL e chave)");
        var c = _http.CreateClient("evogo-calls");
        c.BaseAddress = new Uri(Url + "/");
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.Remove("apikey");
        c.DefaultRequestHeaders.Add("apikey", Key);
        return c;
    }

    private async Task<JsonNode?> PostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            using var res = await Client().PostAsJsonAsync(path, body, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new IntegrationException($"Evolution Go Calls {path} → {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new IntegrationException($"Falha ao falar com o Evolution Go Calls ({path}): {ex.Message}", ex);
        }
    }

    /// <summary>POST /call/dial {number, video:false} → {message, callId}. Marcado como EXPERIMENTAL pelo projeto.</summary>
    public async Task<string> DialAsync(string phone, CancellationToken ct)
    {
        var r = await PostAsync("call/dial", new { number = phone, video = false }, ct);
        var id = r?["callId"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(id) ? throw new IntegrationException("O Evolution Go Calls não devolveu callId no /call/dial.") : id;
    }

    public Task AnswerAsync(string callCreator, string callId, CancellationToken ct) => PostAsync("call/answer", new { callCreator, callId }, ct);
    public Task RejectAsync(string callCreator, string callId, CancellationToken ct) => PostAsync("call/reject", new { callCreator, callId }, ct);
    public Task HangupAsync(string callId, CancellationToken ct) => PostAsync("call/hangup", new { callId }, ct);

    public async Task<ICallStream> OpenStreamAsync(string callId, CancellationToken ct)
    {
        if (!IsConfigured) throw new NotConfiguredException("Evolution Go Calls (URL e chave)");
        var b = new UriBuilder(Url!) { Scheme = Url!.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/call/stream/" + Uri.EscapeDataString(callId) };
        var instance = _s.Get(SettingKeys.VoiceGoInstanceId);
        var signKey = _s.Get(SettingKeys.VoiceGoStreamKey);
        if (!string.IsNullOrWhiteSpace(signKey) && !string.IsNullOrWhiteSpace(instance))
        {
            var exp = _clock.GetUtcNow().ToUnixTimeSeconds() + 60;
            b.Query = $"instance={Uri.EscapeDataString(instance)}&exp={exp}&token={CallStreamProtocol.Sign(signKey, instance, callId, exp)}";
        }
        else b.Query = "apikey=" + Uri.EscapeDataString(Key!); // caminho legado do projeto; evite expor fora da rede interna
        var ws = new ClientWebSocket();
        try { await ws.ConnectAsync(b.Uri, ct); }
        catch (Exception ex) { ws.Dispose(); throw new IntegrationException("Não foi possível abrir o stream de áudio da chamada: " + ex.Message, ex); }
        return new WebSocketCallStream(ws);
    }
}

public sealed class WebSocketCallStream : ICallStream
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _write = new(1, 1);
    public WebSocketCallStream(ClientWebSocket ws) => _ws = ws;

    public async IAsyncEnumerable<CallStreamEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var disconnected = false;
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            ValueWebSocketReceiveResult r = default;
            var closed = false;
            do
            {
                try { r = await _ws.ReceiveAsync(buf.AsMemory(), ct); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { disconnected = true; break; }
                if (r.MessageType == WebSocketMessageType.Close) { closed = true; break; }
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (disconnected) break;
            if (closed) { yield return new CallStreamEvent(CallStreamEventKind.Stopped, Reason: "ws_closed"); yield break; }
            var ev = CallStreamProtocol.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            if (ev != null) { yield return ev; if (ev.Kind == CallStreamEventKind.Stopped) yield break; }
        }
        if (disconnected && !ct.IsCancellationRequested) yield return new CallStreamEvent(CallStreamEventKind.Stopped, Reason: "ws_disconnected");
    }

    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(CallStreamProtocol.MediaMessage(pcm.Span));
        await _write.WaitAsync(ct);
        try { if (_ws.State == WebSocketState.Open) await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _write.Release(); }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _write.WaitAsync(cts.Token);
                try { await _ws.SendAsync(Encoding.UTF8.GetBytes(CallStreamProtocol.StopMessage()), WebSocketMessageType.Text, true, cts.Token); }
                finally { _write.Release(); }
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch { /* já fechado */ }
    }

    public async ValueTask DisposeAsync() { await CloseAsync(); _ws.Dispose(); _write.Dispose(); }
}
