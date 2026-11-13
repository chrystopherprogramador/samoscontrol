using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamosControl.Integrations.Voice;

namespace SamosControl.Application.Voice;

/// <summary>
/// Cérebro da ligação = o MESMO SamosAgent do WhatsApp (mesma identidade, provedor de IA, modelo, ferramentas, permissões, memória e histórico do usuário).
/// A diferença é só o canal: entra a fala transcrita, sai texto preparado para fala. Um escopo novo por turno evita estado sujo quando um turno é cancelado.
/// </summary>
public class RobertaVoiceBrain : IVoiceBrain
{
    public const int MaxSpokenSentences = 4;
    private static readonly Regex Goodbye = new(@"^\W*(tchau|pode desligar|vou desligar|vamos encerrar|pode encerrar|encerra|desliga)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly IServiceScopeFactory _scopes;
    private readonly int _userId;
    private readonly string _userName, _phone;
    private readonly bool _inbound, _storeTranscript;
    private readonly ILogger _log;
    private readonly IVoiceSpeech? _speech;
    private readonly string? _purpose;
    private readonly CancellationTokenSource _openerCts = new();
    private readonly Task<(string Text, List<byte[]>? Audio)> _openerPrewarm;
    private bool _closed;

    public RobertaVoiceBrain(IServiceScopeFactory scopes, int userId, string userName, string phone,     bool inbound, bool storeTranscript, ILogger log, string? purpose = null, IVoiceSpeech? speech = null)
    {
        _scopes = scopes; _userId = userId; _userName = userName; _phone = phone; _inbound = inbound; _storeTranscript = storeTranscript; _log = log; _purpose = purpose; _speech = speech;
        // Começa a preparar a abertura (texto da IA + áudio) já na discagem/toque: quando o cliente atende, a fala já está pronta
        // e não existe mais o silêncio de alguns segundos esperando a IA e o TTS responderem.
        _openerPrewarm = Task.Run(() => PrewarmOpenerAsync(_openerCts.Token));
    }

    private async Task<(string Text, List<byte[]>? Audio)> PrewarmOpenerAsync(CancellationToken ct)
    {
        var text = await ComposeOpenerTextAsync(ct);
        List<byte[]>? audio = null;
        if (_speech != null)
        {
            try
            {
                var chunks = new List<byte[]>();
                foreach (var s in VoiceText.Sentences(VoiceText.ForSpeech(text)))
                    await foreach (var b in _speech.StreamTtsAsync(s, ct)) chunks.Add(b);
                audio = chunks;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Voz: falha ao pré-sintetizar o áudio da abertura; vai sintetizar ao vivo"); }
        }
        return (text, audio);
    }

    private async Task<string> ComposeOpenerTextAsync(CancellationToken ct)
    {
        var first = _userName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var hello = first != null ? $"Oi, {first}!" : "Oi!";
        try
        {
            using var scope = _scopes.CreateScope();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            var agent = scope.ServiceProvider.GetRequiredService<SamosAgent>();
            var composed = await agent.ComposeVoiceOpenerAsync(new AgentContext(_userId, _userName, AgentContext.Voice, _phone, Spoken: true), _purpose, _inbound, limit.Token);
            var spoken = composed == null ? "" : SpeechText.Normalize(composed);
            if (spoken.Length > 0) return spoken;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Voz: falha ao compor a abertura pela IA; usando frase padrão"); }
        if (!_inbound && !string.IsNullOrWhiteSpace(_purpose))
        {
            var reminder = SpeechText.Normalize(_purpose).TrimEnd('.', '!', '?', ' ');
            return $"{hello} Aqui é a Roberta, como combinado. Estou ligando para te lembrar: {reminder}. Quer que eu faça mais alguma coisa?";
        }
        return _inbound ? $"{hello} Aqui é a Roberta. No que posso te ajudar?" : $"{hello} Aqui é a Roberta, estou te ligando como combinado. No que posso te ajudar?";
    }

    public async Task<string> OpenerAsync(CancellationToken ct) => (await _openerPrewarm).Text;
    public async Task<IReadOnlyList<byte[]>?> OpenerAudioAsync(CancellationToken ct) => (await _openerPrewarm).Audio;

    public async Task<BrainTurn> RespondAsync(string userText, CancellationToken ct)
    {
        if (Goodbye.IsMatch(userText))
        {
            _closed = true;
            return new BrainTurn("Tudo bem! Se precisar, é só me chamar. Tchau!", true, "encerrado_pelo_usuario", "Goodbye", false);
        }
        using var scope = _scopes.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<SamosAgent>();
        var sw = Stopwatch.StartNew();
        var raw = await agent.HandleAsync(userText, new AgentContext(_userId, _userName, AgentContext.Voice, _phone, Spoken: true), ct);
        var spoken = SpeechText.Normalize(raw);
        var sentences = VoiceText.Sentences(spoken);
        var truncated = sentences.Count > MaxSpokenSentences;
        if (truncated) spoken = string.Join(" ", sentences.Take(MaxSpokenSentences)) + " O restante eu te mando por mensagem.";
        if (spoken.Length == 0) spoken = "Desculpa, não consegui formular uma resposta agora. Pode repetir?";
        _log.LogInformation("Voz: canal=VOZ agente_ms={Ms} chars_orig={Orig} chars_fala={Spoken} truncada={Trunc}", sw.ElapsedMilliseconds, raw.Length, spoken.Length, truncated);

        if (truncated)
        {
            // o detalhe completo vai ao próprio usuário (número já autorizado e autenticado na chamada); falha aqui não derruba a ligação
            try { await scope.ServiceProvider.GetRequiredService<WhatsAppService>().SendTextAsync(_phone, raw, _userId, fromAgent: true, ct: CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "Voz: não foi possível enviar o detalhe por WhatsApp"); }
        }
        return new BrainTurn(spoken, false, null, "Agent", true);
    }

    public Task<BrainSummary> FinishAsync(IReadOnlyList<TranscriptLine> transcript, string endReason, CancellationToken ct)
    {
        try { _openerCts.Cancel(); } catch (ObjectDisposedException) { } // encerra o prewarm se ainda estiver rodando (ex.: ligação não atendida)
        var turns = transcript.Count(t => t.Role == "Cliente");
        var outcome = turns == 0 ? "sem_conversa" : _closed ? "encerrado" : "conversa";
        var sb = new System.Text.StringBuilder($"Ligação da Roberta ({(_inbound ? "recebida" : "realizada")}) com {_userName}. Resultado: {outcome}. Término: {endReason}. Falas do usuário: {turns}.");
        if (_storeTranscript && turns > 0)
        {
            var last = transcript.Last(t => t.Role == "Cliente");
            sb.Append($" Última fala: \"{(last.Text.Length > 200 ? last.Text[..200] : last.Text)}\".");
        }
        return Task.FromResult(new BrainSummary(outcome, sb.ToString()));
    }
}
