using System.Text.RegularExpressions;
using SamosControl.Domain;
using SamosControl.Integrations.Voice;

namespace SamosControl.Application.Voice;

public static class Pcm
{
    /// <summary>RMS de um quadro PCM16 LE.</summary>
    public static double Rms(ReadOnlySpan<byte> frame)
    {
        var n = frame.Length / 2;
        if (n == 0) return 0;
        double sum = 0;
        for (var i = 0; i < n; i++) { double v = (short)(frame[2 * i] | (frame[2 * i + 1] << 8)); sum += v * v; }
        return Math.Sqrt(sum / n);
    }

    public static byte[] Silence(int frames = 1) => new byte[VoiceAudio.FrameBytes * frames];

    /// <summary>Quadro de tom puro com a amplitude dada (útil em testes e simulações).</summary>
    public static byte[] Tone(double amplitude, int frames = 1)
    {
        var b = new byte[VoiceAudio.FrameBytes * frames];
        for (var i = 0; i < b.Length / 2; i++)
        {
            var v = (short)(Math.Sin(i * 2 * Math.PI * 440 / VoiceAudio.SampleRate) * amplitude);
            b[2 * i] = (byte)(v & 0xFF); b[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return b;
    }
}

public enum VadEvent { None, SpeechStart, SpeechEnd }

/// <summary>
/// Detector de voz por energia (RMS) com piso de ruído adaptativo e histerese. Opera em quadros de 60 ms.
/// É deliberadamente simples (sem modelo); a qualidade real contra áudio de telefone precisa ser medida no teste autorizado.
/// </summary>
public class VoiceActivityDetector
{
    public double MinThreshold { get; init; } = 350;
    public double NoiseMultiplier { get; init; } = 3.0;
    public int EndSilenceFrames { get; init; } = 12;   // ~720 ms
    /// <summary>Quadros consecutivos de fala para abrir um enunciado. A sessão aumenta isso enquanto a Roberta fala (barge-in/eco).</summary>
    public int MinSpeechFrames { get; set; } = 2;
    /// <summary>Multiplica o limiar (ex.: 2.0 enquanto a Roberta fala, para reduzir eco).</summary>
    public double ThresholdScale { get; set; } = 1.0;

    private double _floor = 150;
    private int _speechRun, _silenceRun;
    public bool InSpeech { get; private set; }
    public bool LastFrameVoiced { get; private set; }

    public double Threshold => Math.Max(MinThreshold, _floor * NoiseMultiplier) * ThresholdScale;

    public VadEvent Process(ReadOnlySpan<byte> frame)
    {
        var rms = Pcm.Rms(frame);
        var voiced = rms > Threshold;
        LastFrameVoiced = voiced;
        if (!voiced && !InSpeech) _floor = _floor * 0.95 + rms * 0.05; // só aprende ruído em silêncio
        if (voiced) { _speechRun++; _silenceRun = 0; } else { _silenceRun++; _speechRun = 0; }
        if (!InSpeech && _speechRun >= MinSpeechFrames) { InSpeech = true; return VadEvent.SpeechStart; }
        if (InSpeech && _silenceRun >= EndSilenceFrames) { InSpeech = false; return VadEvent.SpeechEnd; }
        return VadEvent.None;
    }

    public void Reset() { InSpeech = false; _speechRun = _silenceRun = 0; }
}

public static class VoiceStateMachine
{
    private static readonly Dictionary<VoiceCallState, VoiceCallState[]> Allowed = new()
    {
        [VoiceCallState.Idle] = new[] { VoiceCallState.Dialing, VoiceCallState.Ringing, VoiceCallState.Connected, VoiceCallState.Failed },
        [VoiceCallState.Dialing] = new[] { VoiceCallState.Ringing, VoiceCallState.Connected, VoiceCallState.Ending, VoiceCallState.Completed, VoiceCallState.Failed },
        [VoiceCallState.Ringing] = new[] { VoiceCallState.Connected, VoiceCallState.Ending, VoiceCallState.Completed, VoiceCallState.Failed },
        [VoiceCallState.Connected] = new[] { VoiceCallState.Listening, VoiceCallState.Speaking, VoiceCallState.Ending, VoiceCallState.Failed },
        [VoiceCallState.Listening] = new[] { VoiceCallState.Thinking, VoiceCallState.Speaking, VoiceCallState.Ending, VoiceCallState.Failed },
        [VoiceCallState.Thinking] = new[] { VoiceCallState.Speaking, VoiceCallState.Listening, VoiceCallState.Interrupted, VoiceCallState.Ending, VoiceCallState.Failed },
        [VoiceCallState.Speaking] = new[] { VoiceCallState.Listening, VoiceCallState.Thinking, VoiceCallState.Interrupted, VoiceCallState.Ending, VoiceCallState.Failed },
        [VoiceCallState.Interrupted] = new[] { VoiceCallState.Listening, VoiceCallState.Thinking, VoiceCallState.Ending, VoiceCallState.Failed },
        [VoiceCallState.Ending] = new[] { VoiceCallState.Completed, VoiceCallState.Failed },
        [VoiceCallState.Completed] = Array.Empty<VoiceCallState>(),
        [VoiceCallState.Failed] = Array.Empty<VoiceCallState>(),
    };

    public static bool IsTerminal(VoiceCallState s) => s is VoiceCallState.Completed or VoiceCallState.Failed;
    public static bool IsActive(VoiceCallState s) => !IsTerminal(s) && s != VoiceCallState.Idle;
    public static bool CanMove(VoiceCallState from, VoiceCallState to) => from == to || Allowed[from].Contains(to);
}

/// <summary>
/// Reproduz PCM na chamada em tempo real: envia quadros de 60 ms com antecedência máxima de poucos quadros, porque o
/// Evolution Go Calls não tem comando de "limpar fila" (buffer de ~3 s que descarta quadros ao encher). Assim, cancelar
/// interrompe o som quase na hora.
/// </summary>
public class SpeechPlayer
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
    private readonly TimeProvider _clock;
    private readonly int _lead;
    public SpeechPlayer(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send, TimeProvider clock, int maxLeadFrames = 2) { _send = send; _clock = clock; _lead = maxLeadFrames; }

    /// <summary>Momento (relógio injetado) em que o primeiro quadro foi enviado; null se nada foi enviado.</summary>
    public DateTimeOffset? FirstFrameAt { get; private set; }
    public long FramesSent { get; private set; }
    /// <summary>true enquanto PlayAsync está enviando fala (o keepalive de silêncio não deve intercalar).</summary>
    public volatile bool IsPlaying;

    /// <summary>Devolve true se tocou até o fim; false se cancelado. Exceções do envio sobem ao chamador.</summary>
    public async Task<bool> PlayAsync(IAsyncEnumerable<byte[]> chunks, CancellationToken ct)
    {
        IsPlaying = true;
        try { return await PlayCoreAsync(chunks, ct); }
        finally { IsPlaying = false; }
    }

    private async Task<bool> PlayCoreAsync(IAsyncEnumerable<byte[]> chunks, CancellationToken ct)
    {
        var frame = VoiceAudio.FrameBytes;
        FirstFrameAt = null; FramesSent = 0;
        var carry = new byte[frame]; var carryLen = 0;
        var start = _clock.GetUtcNow(); long k = 0;
        async ValueTask EmitAsync(byte[] f)
        {
            var due = start + TimeSpan.FromMilliseconds((k - _lead) * VoiceAudio.FrameMs);
            var now = _clock.GetUtcNow();
            if (due > now) await Task.Delay(due - now, _clock, ct);
            else if (now - due > TimeSpan.FromMilliseconds(2 * VoiceAudio.FrameMs)) start = now - TimeSpan.FromMilliseconds((k - _lead) * VoiceAudio.FrameMs); // TTS atrasou: não faz rajada
            ct.ThrowIfCancellationRequested();
            await _send(f, ct);
            FirstFrameAt ??= _clock.GetUtcNow();
            k++; FramesSent++;
        }
        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                var off = 0;
                while (off < chunk.Length)
                {
                    var take = Math.Min(frame - carryLen, chunk.Length - off);
                    Buffer.BlockCopy(chunk, off, carry, carryLen, take);
                    carryLen += take; off += take;
                    if (carryLen == frame) { await EmitAsync((byte[])carry.Clone()); carryLen = 0; }
                }
            }
            if (carryLen > 0) { Array.Clear(carry, carryLen, frame - carryLen); await EmitAsync((byte[])carry.Clone()); }
            // espera o áudio ainda "no ar" terminar: Speaking só acaba quando o cliente de fato parou de ouvir
            var rest = start + TimeSpan.FromMilliseconds(k * VoiceAudio.FrameMs) - _clock.GetUtcNow();
            if (rest > TimeSpan.Zero) await Task.Delay(rest, _clock, ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }
}

public record TranscriptLine(string Role, string Text, int AtSecond);

public record BrainTurn(string Reply, bool EndCall, string? Outcome, string Action, bool LlmUsed, bool Handoff = false, bool OptOut = false);

public record BrainSummary(string Outcome, string Summary);

/// <summary>Cérebro comercial da ligação. A implementação real reutiliza o motor da Roberta (mesma memória do WhatsApp).</summary>
public interface IVoiceBrain
{
    /// <summary>Primeira fala (identifica a Roberta como assistente virtual e retoma o contexto já conhecido).</summary>
    Task<string> OpenerAsync(CancellationToken ct);
    Task<BrainTurn> RespondAsync(string userText, CancellationToken ct);
    /// <summary>Chamado uma vez no fim: grava resultado no CRM e devolve resultado/resumo.</summary>
    Task<BrainSummary> FinishAsync(IReadOnlyList<TranscriptLine> transcript, string endReason, CancellationToken ct);
}

public static class VoiceText
{
    private static readonly Regex Emoji = new(@"[\u2600-\u27BF]|[\uD83C-\uDBFF][\uDC00-\uDFFF]|\uFE0F|\u200D", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled);
    private static readonly Regex Md = new(@"[*_`#>~]+", RegexOptions.Compiled);

    /// <summary>Remove o que não se fala: emojis, markdown, links e quebras de linha.</summary>
    public static string ForSpeech(string? text)
    {
        var t = Url.Replace(text ?? "", "");
        t = Emoji.Replace(t, "");
        t = Md.Replace(t, "");
        t = Regex.Replace(t, @"^\s*[-•]\s*", "", RegexOptions.Multiline);
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    /// <summary>Divide em frases para o TTS começar pela primeira enquanto a seguinte é gerada.</summary>
    public static List<string> Sentences(string text)
    {
        var res = new List<string>(); var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            var end = text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));
            if (end && sb.Length >= 25) { res.Add(sb.ToString().Trim()); sb.Clear(); }
        }
        if (sb.ToString().Trim().Length > 0) res.Add(sb.ToString().Trim());
        return res;
    }
}
