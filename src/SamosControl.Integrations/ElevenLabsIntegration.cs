using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

public interface IElevenLabs
{
    /// <summary>True quando a API key e o ID da voz estão configurados.</summary>
    bool IsConfigured { get; }
    /// <summary>True quando a IA deve responder em áudio a quem manda áudio.</summary>
    bool VoiceRepliesEnabled { get; }
    /// <summary>Texto → áudio MP3 (POST /v1/text-to-speech/{voice_id}).</summary>
    Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default);
    /// <summary>Áudio → texto (POST /v1/speech-to-text, modelo Scribe).</summary>
    Task<string?> TranscribeAsync(byte[] audio, string fileName, string? mimeType, CancellationToken ct = default);
}

/// <summary>ElevenLabs: voz da IA (text-to-speech) e entendimento de áudios (speech-to-text). Header xi-api-key.</summary>
public class ElevenLabsIntegration : IElevenLabs
{
    public const string DefaultTtsModel = "eleven_multilingual_v2";
    public const string DefaultSttModel = "scribe_v2";
    public const string DefaultBaseUrl = "https://api.elevenlabs.io";

    private readonly IHttpClientFactory _http;
    private readonly ISettingsReader _s;
    public ElevenLabsIntegration(IHttpClientFactory http, ISettingsReader s) { _http = http; _s = s; }

    private string? Key => _s.Get(SettingKeys.ElevenLabsKey);
    private string? Voice => _s.Get(SettingKeys.ElevenLabsVoiceId);
    private static string Or(string? v, string d) => string.IsNullOrWhiteSpace(v) ? d : v.Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Voice);
    public bool VoiceRepliesEnabled => IsConfigured && !string.Equals(_s.Get(SettingKeys.ElevenLabsVoiceReplies), "false", StringComparison.OrdinalIgnoreCase);

    private HttpClient Client()
    {
        if (string.IsNullOrWhiteSpace(Key)) throw new NotConfiguredException("ElevenLabs");
        var c = _http.CreateClient("elevenlabs");
        c.BaseAddress = new Uri(Or(_s.Get(SettingKeys.ElevenLabsUrl), DefaultBaseUrl).TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(90);
        c.DefaultRequestHeaders.Remove("xi-api-key");
        c.DefaultRequestHeaders.Add("xi-api-key", Key);
        return c;
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new NotConfiguredException("ElevenLabs (API key e voz)");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Texto vazio.");
        var c = Client();
        var body = new JsonObject
        {
            ["text"] = text,
            ["model_id"] = Or(_s.Get(SettingKeys.ElevenLabsModel), DefaultTtsModel),
            ["voice_settings"] = new JsonObject
            {
                ["stability"] = Num(SettingKeys.ElevenLabsStability, 0.5),
                ["similarity_boost"] = Num(SettingKeys.ElevenLabsSimilarity, 0.75)
            }
        };
        try
        {
            using var res = await c.PostAsJsonAsync($"v1/text-to-speech/{Uri.EscapeDataString(Voice!.Trim())}?output_format=mp3_44100_64", body, ct);
            if (!res.IsSuccessStatusCode) throw new IntegrationException($"ElevenLabs {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) throw new IntegrationException("ElevenLabs devolveu áudio vazio.");
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IntegrationException("Falha ao comunicar com a ElevenLabs: " + ex.Message, ex);
        }
    }

    public async Task<string?> TranscribeAsync(byte[] audio, string fileName, string? mimeType, CancellationToken ct = default)
    {
        var c = Client();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Or(_s.Get(SettingKeys.ElevenLabsSttModel), DefaultSttModel)), "model_id");
        var lang = _s.Get(SettingKeys.ElevenLabsSttLanguage);
        if (!string.IsNullOrWhiteSpace(lang)) form.Add(new StringContent(lang.Trim()), "language_code");
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(string.IsNullOrWhiteSpace(mimeType) ? "audio/ogg" : mimeType, out var mt) ? mt : new MediaTypeHeaderValue("audio/ogg");
        form.Add(file, "file", fileName);
        try
        {
            using var res = await c.PostAsync("v1/speech-to-text", form, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new IntegrationException($"ElevenLabs {(int)res.StatusCode}: {Trim(text)}");
            var t = Json.Str(JsonNode.Parse(text)?["text"])?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new IntegrationException("Falha ao transcrever áudio na ElevenLabs: " + ex.Message, ex);
        }
    }

    private double Num(string key, double d) =>
        double.TryParse(_s.Get(key)?.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1 ? v : d;

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
