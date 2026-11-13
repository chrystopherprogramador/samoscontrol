using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SamosControl.Integrations.Voice;

public record SttResult(string Text, bool Final);

/// <summary>Sessão de transcrição em tempo real de uma fala.</summary>
public interface ISttSession : IAsyncDisposable
{
    /// <summary>Envia PCM16 16 kHz. commit=true fecha o enunciado e pede a transcrição final.</summary>
    Task SendAsync(ReadOnlyMemory<byte> pcm, bool commit, CancellationToken ct);
    IAsyncEnumerable<SttResult> ResultsAsync(CancellationToken ct);
}

/// <summary>TTS e STT da chamada. Implementação atual: ElevenLabs (mesma chave e voz já usadas nos áudios do WhatsApp).</summary>
public interface IVoiceSpeech
{
    bool IsConfigured { get; }
    /// <summary>Texto → PCM16 16 kHz em pedaços, à medida que a ElevenLabs gera (output_format=pcm_16000).</summary>
    IAsyncEnumerable<byte[]> StreamTtsAsync(string text, CancellationToken ct);
    Task<ISttSession> OpenSttAsync(CancellationToken ct);
    /// <summary>Plano B (STT em lote): transcreve um enunciado inteiro quando o realtime falha.</summary>
    Task<string?> TranscribeBatchAsync(byte[] pcm16k, CancellationToken ct);
}

public class ElevenLabsVoiceSpeech : IVoiceSpeech
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    private readonly IElevenLabs _batch;
    public ElevenLabsVoiceSpeech(IHttpClientFactory http, ISettingsReader s, IElevenLabs batch) { _http = http; _s = s; _batch = batch; }

    private string? Key => _s.Get(SettingKeys.ElevenLabsKey);
    private string? Voice => _s.Get(SettingKeys.ElevenLabsVoiceId);
    private string BaseUrl => (string.IsNullOrWhiteSpace(_s.Get(SettingKeys.ElevenLabsUrl)) ? ElevenLabsIntegration.DefaultBaseUrl : _s.Get(SettingKeys.ElevenLabsUrl)!).Trim().TrimEnd('/');
    public bool IsConfigured => _batch.IsConfigured;

    public async IAsyncEnumerable<byte[]> StreamTtsAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!IsConfigured) throw new NotConfiguredException("ElevenLabs (API key e voz)");
        var model = _s.Get(SettingKeys.VoiceTtsModel);
        if (string.IsNullOrWhiteSpace(model)) model = _s.Get(SettingKeys.ElevenLabsModel);
        if (string.IsNullOrWhiteSpace(model)) model = ElevenLabsIntegration.DefaultTtsModel;
        var body = new JsonObject
        {
            ["text"] = text,
            ["model_id"] = model.Trim(),
            ["language_code"] = "pt",
            ["voice_settings"] = new JsonObject
            {
                ["stability"] = Num(SettingKeys.ElevenLabsStability, 0.4),
                ["similarity_boost"] = Num(SettingKeys.ElevenLabsSimilarity, 0.8),
                ["style"] = Num(SettingKeys.ElevenLabsStyle, 0.35),
                ["use_speaker_boost"] = Bool(SettingKeys.ElevenLabsSpeakerBoost, true)
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/text-to-speech/{Uri.EscapeDataString(Voice!.Trim())}/stream?output_format=pcm_16000")
        { Content = JsonContent.Create(body) };
        req.Headers.Add("xi-api-key", Key);
        HttpResponseMessage res;
        var c = _http.CreateClient("elevenlabs-stream");
        c.Timeout = Timeout.InfiniteTimeSpan;
        try { res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is HttpRequestException) { throw new IntegrationException("Falha ao comunicar com a ElevenLabs: " + ex.Message, ex); }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                throw new IntegrationException($"ElevenLabs TTS {(int)res.StatusCode}: {(err.Length > 200 ? err[..200] : err)}");
            }
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            var buf = new byte[VoiceAudio.FrameBytes * 4];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0) yield return buf.AsSpan(0, n).ToArray();
        }
    }

    public async Task<ISttSession> OpenSttAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Key)) throw new NotConfiguredException("ElevenLabs");
        var ub = new UriBuilder(BaseUrl) { Scheme = BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/v1/speech-to-text/realtime" };
        // Parâmetros documentados: model_id, audio_format, language_code, commit_strategy (manual = nosso VAD decide o fim da fala).
        ub.Query = "model_id=scribe_v2_realtime&audio_format=pcm_16000&language_code=pt&commit_strategy=manual";
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("xi-api-key", Key!);
        try { await ws.ConnectAsync(ub.Uri, ct); }
        catch (Exception ex) { ws.Dispose(); throw new IntegrationException("Não foi possível abrir o STT em tempo real: " + ex.Message, ex); }
        return new RealtimeSttSession(ws);
    }

    public async Task<string?> TranscribeBatchAsync(byte[] pcm16k, CancellationToken ct) =>
        await _batch.TranscribeAsync(Wav.FromPcm16(pcm16k, VoiceAudio.SampleRate), "fala.wav", "audio/wav", ct);

    private double Num(string key, double d) =>
        double.TryParse(_s.Get(key)?.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1 ? v : d;

    private bool Bool(string key, bool d) => bool.TryParse(_s.Get(key), out var v) ? v : d;
}

public static class Wav
{
    public static byte[] FromPcm16(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }
}

/// <summary>Mensagens conforme a documentação do Scribe v2 Realtime: input_audio_chunk → partial_transcript / committed_transcript.</summary>
public static class RealtimeSttProtocol
{
    public static string Chunk(ReadOnlySpan<byte> pcm, bool commit) =>
        JsonSerializer.Serialize(new { message_type = "input_audio_chunk", audio_base_64 = Convert.ToBase64String(pcm), commit, sample_rate = VoiceAudio.SampleRate });

    /// <summary>Devolve null para mensagens sem texto; lança IntegrationException nas mensagens de erro.</summary>
    public static SttResult? Parse(string json)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(json); } catch (JsonException) { return null; }
        var type = n?["message_type"]?.GetValue<string>();
        switch (type)
        {
            case "partial_transcript": return new SttResult(n?["text"]?.GetValue<string>() ?? "", false);
            case "committed_transcript": return new SttResult(n?["text"]?.GetValue<string>() ?? "", true);
            case "error" or "auth_error" or "quota_exceeded" or "rate_limited" or "input_error" or "transcriber_error":
                throw new IntegrationException($"ElevenLabs STT {type}: {n?["error"]?.ToString() ?? n?["message"]?.ToString()}");
            default: return null; // session_started, commit_throttled, *_with_timestamps...
        }
    }
}

public sealed class RealtimeSttSession : ISttSession
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _write = new(1, 1);
    public RealtimeSttSession(ClientWebSocket ws) => _ws = ws;

    public async Task SendAsync(ReadOnlyMemory<byte> pcm, bool commit, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(RealtimeSttProtocol.Chunk(pcm.Span, commit));
        await _write.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch (WebSocketException ex) { throw new IntegrationException("STT em tempo real desconectou: " + ex.Message, ex); }
        finally { _write.Release(); }
    }

    public async IAsyncEnumerable<SttResult> ResultsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            ValueWebSocketReceiveResult r;
            do
            {
                try { r = await _ws.ReceiveAsync(buf.AsMemory(), ct); }
                catch (OperationCanceledException) { yield break; }
                catch (WebSocketException ex) { throw new IntegrationException("STT em tempo real desconectou: " + ex.Message, ex); }
                if (r.MessageType == WebSocketMessageType.Close) yield break;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            var res = RealtimeSttProtocol.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            if (res != null) yield return res;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ws.State == WebSocketState.Open) { using var c = new CancellationTokenSource(2000); await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", c.Token); } } catch { }
        _ws.Dispose();
    }
}
