using System.Threading.Channels;
using SamosControl.Domain;
using SamosControl.Integrations;
using SamosControl.Integrations.Voice;

namespace SamosControl.Application.Voice;

public class VoiceSessionOptions
{
    public int MaxSeconds { get; set; } = 300;
    public int RingSeconds { get; set; } = 40;
    public int SilenceSeconds { get; set; } = 8;
    public int MaxSilencePrompts { get; set; } = 2;
    public int FillerAfterMs { get; set; } = 2500;
    public int BrainTimeoutMs { get; set; } = 20000;
    public int SttCommitTimeoutMs { get; set; } = 6000;
    public int BargeInFrames { get; set; } = 3;
    public int EndSilenceFrames { get; set; } = 12;
    /// <summary>true = pula o STT em tempo real e usa só o modo em lote.</summary>
    public bool BatchOnly { get; set; }
    public string FillerText { get; set; } = "Só um instante, deixa eu confirmar isso pra você.";
    public string NotUnderstoodText { get; set; } = "Desculpa, não consegui te ouvir direito. Pode repetir?";
    public string SilencePromptText { get; set; } = "Alô? Você consegue me ouvir?";
    public string SilenceByeText { get; set; } = "Parece que o áudio ficou sem retorno, então vou encerrar por aqui. Qualquer coisa é só me chamar no WhatsApp. Tchau!";
    public string MaxDurationText { get; set; } = "Chegamos ao tempo limite desta ligação, obrigada pela conversa! Continuamos pelo WhatsApp. Tchau!";
    public string BrainSlowText { get; set; } = "Desculpa, estou com uma instabilidade aqui. Se preferir, posso pedir para alguém do nosso time te ligar. O que acha?";
}

public interface IVoiceSessionHost
{
    void StateChanged(VoiceCallState state);
    void Event(string type, string? detail = null);
}

public record LatencySample(int EndpointMs, int SttMs, int BrainMs, int TtsFirstByteMs, int TotalMs, bool Filler);

public class VoiceSessionResult
{
    public string EndReason { get; set; } = "";
    public VoiceCallState FinalState { get; set; }
    public int DurationSeconds { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public int Turns { get; set; }
    public int BargeIns { get; set; }
    public double SttSeconds { get; set; }
    public int TtsCharacters { get; set; }
    public int LlmCalls { get; set; }
    public List<LatencySample> Latencies { get; set; } = new();
    public List<TranscriptLine> Transcript { get; set; } = new();
    public BrainSummary? Summary { get; set; }
}

public class SttFailedException : Exception { public SttFailedException(string m, Exception? i = null) : base(m, i) { } }

/// <summary>
/// Uma ligação: lê áudio do cliente, detecta fala (VAD), transcreve (STT), chama o cérebro, sintetiza (TTS) e reproduz em tempo real.
/// Barge-in: fala sustentada do cliente enquanto a Roberta fala cancela reprodução, TTS e pensamento pendente.
/// </summary>
public class VoiceSession
{
    private readonly ICallStream _stream;
    private readonly IVoiceSpeech _speech;
    private readonly IVoiceBrain _brain;
    private readonly VoiceSessionOptions _opt;
    private readonly TimeProvider _clock;
    private readonly IVoiceSessionHost _host;
    private readonly SpeechPlayer _player;
    private readonly VoiceActivityDetector _vad;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<byte[]> _preroll = new();
    private readonly List<byte> _inBuf = new();
    private readonly List<TranscriptLine> _transcript = new();
    private readonly List<LatencySample> _latencies = new();

    private VoiceCallState _state = VoiceCallState.Idle;
    private CancellationTokenSource _turnCts = new();
    private Task _turnTask = Task.CompletedTask;
    private int _turnId;
    private Utterance? _utt;
    private DateTimeOffset _startedAt, _lastActivity, _lastVoicedAt, _speechEndAt;
    private DateTimeOffset? _connectedAt;
    private string? _endReason;
    private string _carry = "";
    private int _silencePrompts, _sttFailures, _brainTimeouts, _turns, _bargeIns, _ttsChars, _llmCalls;
    private double _sttSeconds;
    private bool _wrapUp, _opened;

    public VoiceSession(ICallStream stream, IVoiceSpeech speech, IVoiceBrain brain, VoiceSessionOptions opt, IVoiceSessionHost host, TimeProvider? clock = null)
    {
        _stream = stream; _speech = speech; _brain = brain; _opt = opt; _host = host; _clock = clock ?? TimeProvider.System;
        _player = new SpeechPlayer((f, ct) => _stream.SendAudioAsync(f, ct), _clock);
        _vad = new VoiceActivityDetector { EndSilenceFrames = opt.EndSilenceFrames };
    }

    public VoiceCallState State { get { lock (_lock) return _state; } }

    /// <summary>Chamada atendida (webhook CallAccept ou primeiro quadro de áudio recebido).</summary>
    public void NotifyAnswered() => MarkConnected();

    /// <summary>Encerramento imediato pedido de fora (botão, kill switch, limite). Idempotente.</summary>
    public void RequestEnd(string reason) => End(reason);

    public async Task<VoiceSessionResult> RunAsync(VoiceCallState initial, CancellationToken external = default)
    {
        _startedAt = _clock.GetUtcNow(); _lastActivity = _startedAt;
        SetState(initial == VoiceCallState.Connected ? VoiceCallState.Connected : initial);
        using var reg = external.Register(() => End("shutdown"));
        var reader = Task.Run(ReadLoopAsync);
        var ticker = Task.Run(TickLoopAsync);
        if (initial == VoiceCallState.Connected) { _connectedAt = _clock.GetUtcNow(); StartOpener(); }
        await _done.Task;

        _cts.Cancel(); lock (_lock) _turnCts.Cancel();
        try { await Task.WhenAll(reader, ticker, _turnTask); } catch { /* cancelamentos esperados */ }
        BrainSummary? summary = null;
        try
        {
            using var sc = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            summary = await _brain.FinishAsync(_transcript.ToArray(), _endReason!, sc.Token);
        }
        catch (Exception ex) { _host.Event("summary_failed", ex.Message); }
        try { await _stream.DisposeAsync(); } catch { }
        var failed = _endReason is "ws_disconnected" or "stt_failed" or "tts_failed" or "llm_timeout" or "not_connected" or "error";
        SetState(failed ? VoiceCallState.Failed : VoiceCallState.Completed, force: true);
        var end = _clock.GetUtcNow();
        lock (_lock)
            return new VoiceSessionResult
            {
                EndReason = _endReason!, FinalState = _state, ConnectedAt = _connectedAt,
                DurationSeconds = _connectedAt == null ? 0 : (int)Math.Round((end - _connectedAt.Value).TotalSeconds),
                Turns = _turns, BargeIns = _bargeIns, SttSeconds = _sttSeconds, TtsCharacters = _ttsChars, LlmCalls = _llmCalls,
                Latencies = _latencies.ToList(), Transcript = _transcript.ToList(), Summary = summary
            };
    }

    // ---------- estado ----------
    private bool SetState(VoiceCallState next, bool force = false, int? turn = null)
    {
        lock (_lock)
        {
            if (turn.HasValue && turn.Value != _turnId) return false;
            if (_state == next) return true;
            if (!force && !VoiceStateMachine.CanMove(_state, next)) return false;
            if (VoiceStateMachine.IsTerminal(_state)) return false;
            _state = next;
        }
        _host.StateChanged(next);
        return true;
    }

    private void End(string reason)
    {
        lock (_lock)
        {
            if (_endReason != null) return;
            _endReason = reason;
            _turnCts.Cancel();
        }
        SetState(VoiceCallState.Ending, force: true);
        _host.Event("ending", reason);
        _done.TrySetResult();
    }

    private bool Ending { get { lock (_lock) return _endReason != null; } }

    private void MarkConnected()
    {
        lock (_lock)
        {
            if (_endReason != null || _connectedAt != null && _state != VoiceCallState.Dialing && _state != VoiceCallState.Ringing) return;
            if (_state is not (VoiceCallState.Dialing or VoiceCallState.Ringing or VoiceCallState.Idle)) return;
            _connectedAt = _clock.GetUtcNow(); _lastActivity = _connectedAt.Value;
        }
        SetState(VoiceCallState.Connected);
        _host.Event("connected");
        StartOpener();
    }

    // ---------- áudio recebido ----------
    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var ev in _stream.ReadAsync(_cts.Token))
            {
                if (ev.Kind == CallStreamEventKind.Media && ev.Pcm != null) OnPcm(ev.Pcm);
                else if (ev.Kind == CallStreamEventKind.Stopped)
                {
                    var connected = _connectedAt != null;
                    End(ev.Reason == "ws_disconnected" ? "ws_disconnected" : connected ? "remote_hangup" : "not_connected");
                    return;
                }
            }
            if (!_cts.IsCancellationRequested) End("ws_disconnected");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _host.Event("read_error", ex.Message); End("ws_disconnected"); }
    }

    private void OnPcm(byte[] pcm)
    {
        _inBuf.AddRange(pcm);
        while (_inBuf.Count >= VoiceAudio.FrameBytes)
        {
            var frame = _inBuf.GetRange(0, VoiceAudio.FrameBytes).ToArray();
            _inBuf.RemoveRange(0, VoiceAudio.FrameBytes);
            OnFrame(frame);
        }
    }

    private void OnFrame(byte[] frame)
    {
        if (Ending) return;
        if (State is VoiceCallState.Dialing or VoiceCallState.Ringing) MarkConnected();
        if (State is VoiceCallState.Idle or VoiceCallState.Ending or VoiceCallState.Completed or VoiceCallState.Failed) return;

        var speaking = State == VoiceCallState.Speaking;
        _vad.MinSpeechFrames = speaking ? _opt.BargeInFrames : 2;
        _vad.ThresholdScale = speaking ? 2.0 : 1.0; // eco da própria voz da Roberta
        if (!_vad.InSpeech) { _preroll.Enqueue(frame); while (_preroll.Count > 5) _preroll.Dequeue(); }

        var ev = _vad.Process(frame);
        var now = _clock.GetUtcNow();
        if (_vad.LastFrameVoiced) { _lastVoicedAt = now; _lastActivity = now; }

        if (ev == VadEvent.SpeechStart) BeginUtterance();
        else if (_utt != null) _utt.Write(frame);
        if (ev == VadEvent.SpeechEnd) EndUtterance(now);
    }

    // ---------- enunciado do cliente ----------
    private sealed class Utterance
    {
        public readonly Channel<(byte[] Pcm, bool Commit)> Ch = Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
        public readonly MemoryStream All = new();
        public int Frames;
        public Task<string?> Text = Task.FromResult<string?>(null);
        public readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Write(byte[] f) { All.Write(f); Frames++; Ch.Writer.TryWrite((f, false)); }
        public void Commit() { Ch.Writer.TryWrite((Pcm.Silence(), true)); Ch.Writer.TryComplete(); Committed.TrySetResult(); }
    }

    private void BeginUtterance()
    {
        lock (_lock)
        {
            if (_state == VoiceCallState.Speaking)
            {
                _turnCts.Cancel(); _turnId++; _bargeIns++;
                _transcript.Add(new("Roberta", "(interrompida pelo cliente)", Sec()));
                _host.Event("barge_in");
                SetStateLocked(VoiceCallState.Interrupted);
            }
            else if (_state == VoiceCallState.Thinking)
            {
                _turnCts.Cancel(); _turnId++;
                _host.Event("turn_superseded");
                SetStateLocked(VoiceCallState.Interrupted);
            }
            SetStateLocked(VoiceCallState.Listening);
            _silencePrompts = 0;
        }
        var u = new Utterance();
        foreach (var f in _preroll) u.Write(f);
        _utt = u;
        u.Text = Task.Run(() => TranscribeAsync(u, _cts.Token));
    }

    private void SetStateLocked(VoiceCallState next)
    {
        if (_state == next || VoiceStateMachine.IsTerminal(_state) || !VoiceStateMachine.CanMove(_state, next)) return;
        _state = next;
        _host.StateChanged(next);
    }

    private void EndUtterance(DateTimeOffset now)
    {
        var u = _utt; _utt = null;
        if (u == null) return;
        _speechEndAt = now;
        u.Commit();
        var endpointAt = _lastVoicedAt;
        int id; CancellationToken tok;
        lock (_lock)
        {
            if (_endReason != null) return;
            _turnCts.Cancel();
            _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            id = ++_turnId; tok = _turnCts.Token;
            SetStateLocked(VoiceCallState.Thinking);
            _turnTask = Task.Run(() => TurnAsync(u, id, tok, endpointAt, now));
        }
    }

    private async Task<string?> TranscribeAsync(Utterance u, CancellationToken ct)
    {
        if (!_opt.BatchOnly)
        {
            try { return await RealtimeAsync(u, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _host.Event("stt_realtime_failed", ex.Message); }
        }
        await u.Committed.Task.WaitAsync(ct);
        try { return await _speech.TranscribeBatchAsync(u.All.ToArray(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new SttFailedException(ex.Message, ex); }
    }

    private async Task<string?> RealtimeAsync(Utterance u, CancellationToken ct)
    {
        await using var stt = await _speech.OpenSttAsync(ct);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rx = Task.Run(async () =>
        {
            try
            {
                await foreach (var r in stt.ResultsAsync(ct)) if (r.Final) { tcs.TrySetResult(r.Text); return; }
                tcs.TrySetException(new IntegrationException("STT fechou sem transcrição final."));
            }
            catch (Exception e) { tcs.TrySetException(e); }
        }, ct);
        await foreach (var (pcm, commit) in u.Ch.Reader.ReadAllAsync(ct)) await stt.SendAsync(pcm, commit, ct);
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMilliseconds(_opt.SttCommitTimeoutMs), _clock, ct));
        if (winner != tcs.Task) throw new TimeoutException("STT não devolveu a transcrição final a tempo.");
        return await tcs.Task;
    }

    // ---------- turno: transcrição → cérebro → voz ----------
    private async Task TurnAsync(Utterance u, int id, CancellationToken ct, DateTimeOffset endpointAt, DateTimeOffset speechEndAt)
    {
        try
        {
            string? text;
            try { text = await u.Text.WaitAsync(ct); }
            catch (SttFailedException ex)
            {
                _host.Event("stt_failed", ex.Message);
                if (++_sttFailures >= 2) { End("stt_failed"); return; }
                await SpeakAsync(_opt.NotUnderstoodText, id, ct);
                return;
            }
            _sttFailures = 0;
            lock (_lock) _sttSeconds += u.Frames * VoiceAudio.FrameMs / 1000.0;
            text = text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) { SetState(VoiceCallState.Listening, turn: id); return; }
            var sttDone = _clock.GetUtcNow();
            var full = string.IsNullOrEmpty(_carry) ? text : _carry + " " + text;
            lock (_lock) _transcript.Add(new("Cliente", text, Sec()));
            _carry = full; // se este turno for superado por nova fala, a próxima transcrição se junta a esta

            using var brainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var brainTask = _brain.RespondAsync(full, brainCts.Token);
            DateTimeOffset? firstAudio = null; var filler = false;
            var first = await Task.WhenAny(brainTask, Task.Delay(TimeSpan.FromMilliseconds(_opt.FillerAfterMs), _clock, ct));
            if (first != brainTask)
            {
                filler = true; _host.Event("filler");
                if (!await SpeakAsync(_opt.FillerText, id, ct, record: false)) return;
                firstAudio = _player.FirstFrameAt;
                var rest = Math.Max(1, _opt.BrainTimeoutMs - _opt.FillerAfterMs);
                var second = await Task.WhenAny(brainTask, Task.Delay(TimeSpan.FromMilliseconds(rest), _clock, ct));
                if (second != brainTask)
                {
                    brainCts.Cancel();
                    _host.Event("brain_timeout");
                    if (++_brainTimeouts >= 2) { End("llm_timeout"); return; }
                    await SpeakAsync(_opt.BrainSlowText, id, ct);
                    return;
                }
            }
            BrainTurn turn;
            try { turn = await brainTask; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _host.Event("brain_error", ex.Message);
                if (++_brainTimeouts >= 2) { End("llm_timeout"); return; }
                await SpeakAsync(_opt.BrainSlowText, id, ct);
                return;
            }
            _brainTimeouts = 0;
            var brainDone = _clock.GetUtcNow();
            lock (_lock) { _turns++; if (turn.LlmUsed) _llmCalls++; }
            _carry = "";
            _host.Event("turn", $"{turn.Action}{(turn.Outcome != null ? " → " + turn.Outcome : "")}");

            var ok = await SpeakAsync(turn.Reply, id, ct);
            firstAudio ??= _player.FirstFrameAt;
            if (firstAudio != null)
            {
                var s = new LatencySample(Ms(speechEndAt - endpointAt), Ms(sttDone - speechEndAt), Ms(brainDone - sttDone),
                    Ms(firstAudio.Value - brainDone), Ms(firstAudio.Value - endpointAt), filler);
                lock (_latencies) _latencies.Add(s);
            }
            if (ok && turn.EndCall) End(turn.OptOut ? "opt_out" : turn.Handoff ? "handoff" : "completed");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!Ending)
        {
            _host.Event("turn_error", ex.Message);
            End("error");
        }
    }

    private static int Ms(TimeSpan t) => (int)Math.Max(0, t.TotalMilliseconds);
    private int Sec() => _connectedAt == null ? 0 : (int)(_clock.GetUtcNow() - _connectedAt.Value).TotalSeconds;

    /// <summary>Sintetiza por frases (a próxima é gerada enquanto a anterior toca) e reproduz. Devolve false se cancelado ou se o TTS falhou.</summary>
    private async Task<bool> SpeakAsync(string rawText, int id, CancellationToken ct, bool record = true)
    {
        var text = VoiceText.ForSpeech(rawText);
        if (text.Length == 0) return true;
        if (!SetState(VoiceCallState.Speaking, turn: id)) return false;
        var ch = Channel.CreateBounded<byte[]>(64);
        using var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var s in VoiceText.Sentences(text))
                {
                    await foreach (var b in _speech.StreamTtsAsync(s, pcts.Token)) await ch.Writer.WriteAsync(b, pcts.Token);
                    lock (_lock) _ttsChars += s.Length;
                }
                ch.Writer.TryComplete();
            }
            catch (Exception ex) { ch.Writer.TryComplete(ex); }
        });
        bool completed;
        try { completed = await _player.PlayAsync(ch.Reader.ReadAllAsync(pcts.Token), pcts.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pcts.Cancel();
            _host.Event("tts_failed", ex.Message);
            End("tts_failed");
            return false;
        }
        finally { pcts.Cancel(); try { await producer; } catch { } }
        if (record) lock (_lock) _transcript.Add(new("Roberta", completed ? text : text + " (interrompida)", Sec()));
        if (completed) { _lastActivity = _clock.GetUtcNow(); SetState(VoiceCallState.Listening, turn: id); }
        return completed;
    }

    // ---------- falas iniciadas pela Roberta (abertura, silêncio, limite) ----------
    private void RunSystemTurn(Func<int, CancellationToken, Task> body)
    {
        int id; CancellationToken tok;
        lock (_lock)
        {
            if (_endReason != null) return;
            _turnCts.Cancel();
            _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            id = ++_turnId; tok = _turnCts.Token;
            _turnTask = Task.Run(async () => { try { await body(id, tok); } catch (OperationCanceledException) { } catch (Exception ex) when (!Ending) { _host.Event("turn_error", ex.Message); End("error"); } });
        }
    }

    private void StartOpener()
    {
        if (_opened) return; _opened = true;
        RunSystemTurn(async (id, ct) =>
        {
            var text = await _brain.OpenerAsync(ct);
            await SpeakAsync(text, id, ct);
        });
    }

    private async Task TickLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, _cts.Token);
                var now = _clock.GetUtcNow();
                var st = State;
                if (st is VoiceCallState.Dialing or VoiceCallState.Ringing)
                {
                    if ((now - _startedAt).TotalSeconds >= _opt.RingSeconds) End("no_answer");
                    continue;
                }
                if (_connectedAt == null) continue;
                var elapsed = (now - _connectedAt.Value).TotalSeconds;
                if (elapsed >= _opt.MaxSeconds + 20) { End("max_duration"); continue; }
                if (elapsed >= _opt.MaxSeconds && !_wrapUp)
                {
                    _wrapUp = true; _host.Event("max_duration_wrapup");
                    RunSystemTurn(async (id, ct) => { await SpeakAsync(_opt.MaxDurationText, id, ct); End("max_duration"); });
                    continue;
                }
                if (st == VoiceCallState.Listening && !_vad.InSpeech && !_wrapUp && (now - _lastActivity).TotalSeconds >= _opt.SilenceSeconds)
                {
                    _lastActivity = now;
                    if (_silencePrompts < _opt.MaxSilencePrompts)
                    {
                        _silencePrompts++; _host.Event("silence_prompt", _silencePrompts.ToString());
                        RunSystemTurn((id, ct) => SpeakAsync(_opt.SilencePromptText, id, ct));
                    }
                    else RunSystemTurn(async (id, ct) => { await SpeakAsync(_opt.SilenceByeText, id, ct); End("silence"); });
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
