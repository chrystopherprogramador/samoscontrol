using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SamosControl.Application.Voice;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Integrations.Voice;
using SamosControl.Web.Controllers;

namespace SamosControl.Tests;

// ============================================================================================
// ATENÇÃO: tudo aqui é SIMULADO (fakes de rede, STT, TTS e relógio). Nada disto prova o funcionamento
// na rede do WhatsApp nem a qualidade de áudio real. Veja docs/sales-ai/ROBERTA_VOICE.md.
// ============================================================================================

internal sealed class FakeStream : ICallStream
{
    public readonly Channel<CallStreamEvent> In = Channel.CreateUnbounded<CallStreamEvent>();
    private readonly List<byte[]> _sent = new();
    public int SentCount { get { lock (_sent) return _sent.Count; } }
    public bool Closed { get; private set; }

    public async IAsyncEnumerable<CallStreamEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in In.Reader.ReadAllAsync(ct)) yield return e;
    }
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) { lock (_sent) _sent.Add(pcm.ToArray()); return ValueTask.CompletedTask; }
    public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    public void Push(byte[] pcm) => In.Writer.TryWrite(new CallStreamEvent(CallStreamEventKind.Media, pcm));
    public void Stop(string? reason = null) => In.Writer.TryWrite(new CallStreamEvent(CallStreamEventKind.Stopped, Reason: reason));
    public void Disconnect() => In.Writer.TryComplete();
}

internal sealed class FakeSpeech : IVoiceSpeech
{
    public bool IsConfigured { get; set; } = true;
    public int FramesPerSentence { get; set; } = 3;
    public bool FailTts { get; set; }
    public bool RealtimeFails { get; set; }
    public bool BatchFails { get; set; }
    public readonly Queue<string> Transcripts = new();
    public readonly List<string> TtsTexts = new();

    public async IAsyncEnumerable<byte[]> StreamTtsAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (TtsTexts) TtsTexts.Add(text);
        if (FailTts) throw new IntegrationException("ElevenLabs simulado indisponível");
        for (var i = 0; i < FramesPerSentence; i++) { ct.ThrowIfCancellationRequested(); yield return Pcm.Tone(1000); await Task.Yield(); }
    }

    public Task<ISttSession> OpenSttAsync(CancellationToken ct)
    {
        if (RealtimeFails) throw new IntegrationException("STT realtime simulado fora do ar");
        return Task.FromResult<ISttSession>(new Stt(this));
    }

    public Task<string?> TranscribeBatchAsync(byte[] pcm16k, CancellationToken ct)
    {
        if (BatchFails) throw new IntegrationException("STT em lote simulado fora do ar");
        return Task.FromResult<string?>(Next());
    }

    public bool SpokeText(string text) { lock (TtsTexts) return TtsTexts.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)); }
    public int SpokeCount(string text) { lock (TtsTexts) return TtsTexts.Count(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)); }
    private string? Next() { lock (Transcripts) return Transcripts.Count > 0 ? Transcripts.Dequeue() : null; }

    private sealed class Stt : ISttSession
    {
        private readonly FakeSpeech _o; private readonly Channel<SttResult> _r = Channel.CreateUnbounded<SttResult>();
        public Stt(FakeSpeech o) { _o = o; }
        public Task SendAsync(ReadOnlyMemory<byte> pcm, bool commit, CancellationToken ct)
        {
            if (commit) { _r.Writer.TryWrite(new SttResult(_o.Next() ?? "", true)); _r.Writer.TryComplete(); }
            return Task.CompletedTask;
        }
        public IAsyncEnumerable<SttResult> ResultsAsync(CancellationToken ct) => _r.Reader.ReadAllAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrain : IVoiceBrain
{
    public Func<string, CancellationToken, Task<BrainTurn>> Respond { get; set; } = (_, _) => Task.FromResult(new BrainTurn("Entendi, me conta um pouco mais.", false, null, "DiscoverNeed", true));
    public string Opener { get; set; } = "Oi, aqui é a Roberta, assistente virtual de vendas da Samos.";
    public readonly List<string> Inputs = new();
    public string? Outcome { get; set; } = "indefinido";
    public Task<string> OpenerAsync(CancellationToken ct) => Task.FromResult(Opener);
    public Task<BrainTurn> RespondAsync(string userText, CancellationToken ct) { lock (Inputs) Inputs.Add(userText); return Respond(userText, ct); }
    public Task<BrainSummary> FinishAsync(IReadOnlyList<TranscriptLine> t, string endReason, CancellationToken ct) => Task.FromResult(new BrainSummary(Outcome ?? "indefinido", $"fim:{endReason}"));
    public int Calls { get { lock (Inputs) return Inputs.Count; } }
}

internal sealed class FakeHost : IVoiceSessionHost
{
    public readonly List<VoiceCallState> States = new();
    public readonly List<string> Events = new();
    public void StateChanged(VoiceCallState s) { lock (States) States.Add(s); }
    public void Event(string type, string? detail = null) { lock (Events) Events.Add(type); }
    public bool Saw(VoiceCallState s) { lock (States) return States.Contains(s); }
    public int Count(string ev) { lock (Events) return Events.Count(e => e == ev); }
}

/// <summary>Sessão completa com relógio falso: o tempo só anda quando o teste o avança.</summary>
internal sealed class Sim
{
    public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
    public readonly FakeStream Stream = new();
    public readonly FakeSpeech Speech = new();
    public readonly FakeBrain Brain = new();
    public readonly FakeHost Host = new();
    public readonly VoiceSessionOptions Opt = new() { EndSilenceFrames = 5, FillerAfterMs = 2500, BrainTimeoutMs = 20000, SilenceSeconds = 30 };
    public VoiceSession Session = null!;
    public Task<VoiceSessionResult> Run = null!;

    public void Start(VoiceCallState initial = VoiceCallState.Connected)
    {
        Session = new VoiceSession(Stream, Speech, Brain, Opt, Host, Clock);
        Run = Task.Run(() => Session.RunAsync(initial));
    }

    public async Task Step(int ms)
    {
        Clock.Advance(TimeSpan.FromMilliseconds(ms));
        await Task.Delay(1);
    }

    public async Task<bool> Until(Func<bool> cond, int maxMs = 60000)
    {
        for (var t = 0; t < maxMs && !cond(); t += 60) await Step(60);
        return cond();
    }

    public async Task Talk(int frames, double amp = 6000) { for (var i = 0; i < frames; i++) { Stream.Push(Pcm.Tone(amp)); await Step(60); } }
    public async Task Quiet(int frames) { for (var i = 0; i < frames; i++) { Stream.Push(Pcm.Silence()); await Step(60); } }

    public async Task<VoiceSessionResult> Finish(int maxMs = 120000)
    {
        Assert.True(await Until(() => Run.IsCompleted, maxMs), "a sessão deveria ter terminado");
        return await Run;
    }

    /// <summary>Espera a abertura da Roberta terminar de falar e o estado voltar a Listening.</summary>
    public async Task WaitOpenerDone()
    {
        Assert.True(await Until(() => Speech.TtsTexts.Count > 0 && Session.State == VoiceCallState.Listening), "abertura não concluída");
    }

    /// <summary>Cliente fala uma frase (STT simulado devolve <paramref name="said"/>) e a sessão processa o turno.</summary>
    public async Task SayAndWaitBrain(string said, int expectCalls = 1)
    {
        Speech.Transcripts.Enqueue(said);
        await Talk(7); await Quiet(Opt.EndSilenceFrames + 2);
        Assert.True(await Until(() => Brain.Calls >= expectCalls), "o cérebro deveria ter sido chamado");
    }
}

public class VoiceUnitTests
{
    [Fact]
    public void Vad_DetectsSpeechStartAndEnd()
    {
        var vad = new VoiceActivityDetector { EndSilenceFrames = 4 };
        for (var i = 0; i < 10; i++) Assert.Equal(VadEvent.None, vad.Process(Pcm.Silence()));
        Assert.Equal(VadEvent.None, vad.Process(Pcm.Tone(6000)));
        Assert.Equal(VadEvent.SpeechStart, vad.Process(Pcm.Tone(6000)));
        for (var i = 0; i < 3; i++) Assert.Equal(VadEvent.None, vad.Process(Pcm.Silence()));
        Assert.Equal(VadEvent.SpeechEnd, vad.Process(Pcm.Silence()));
    }

    [Fact]
    public void Vad_IgnoresLowNoise_AndScaleRaisesThreshold()
    {
        var vad = new VoiceActivityDetector();
        for (var i = 0; i < 20; i++) Assert.Equal(VadEvent.None, vad.Process(Pcm.Tone(200)));
        vad.ThresholdScale = 2.0;
        for (var i = 0; i < 5; i++) Assert.Equal(VadEvent.None, vad.Process(Pcm.Tone(500)));
        vad.ThresholdScale = 1.0; vad.MinSpeechFrames = 2;
        vad.Process(Pcm.Tone(5000));
        Assert.Equal(VadEvent.SpeechStart, vad.Process(Pcm.Tone(5000)));
    }

    [Theory]
    [InlineData(VoiceCallState.Idle, VoiceCallState.Speaking, false)]
    [InlineData(VoiceCallState.Ringing, VoiceCallState.Connected, true)]
    [InlineData(VoiceCallState.Speaking, VoiceCallState.Interrupted, true)]
    [InlineData(VoiceCallState.Interrupted, VoiceCallState.Listening, true)]
    [InlineData(VoiceCallState.Completed, VoiceCallState.Listening, false)]
    [InlineData(VoiceCallState.Failed, VoiceCallState.Ringing, false)]
    [InlineData(VoiceCallState.Ending, VoiceCallState.Completed, true)]
    public void StateMachine_Transitions(VoiceCallState from, VoiceCallState to, bool ok) => Assert.Equal(ok, VoiceStateMachine.CanMove(from, to));

    [Fact]
    public void StateMachine_TerminalAndActive()
    {
        Assert.True(VoiceStateMachine.IsTerminal(VoiceCallState.Failed));
        Assert.False(VoiceStateMachine.IsActive(VoiceCallState.Completed));
        Assert.True(VoiceStateMachine.IsActive(VoiceCallState.Speaking));
    }

    private static async IAsyncEnumerable<byte[]> Chunks(int frames, int chunkBytes = 700)
    {
        var all = new byte[frames * VoiceAudio.FrameBytes];
        for (var o = 0; o < all.Length; o += chunkBytes) { yield return all[o..Math.Min(all.Length, o + chunkBytes)]; await Task.Yield(); }
    }

    [Fact]
    public async Task SpeechPlayer_SendsFixedFrames_PacedInRealTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var sent = new List<(int Len, DateTimeOffset At)>();
        var player = new SpeechPlayer((f, _) => { sent.Add((f.Length, clock.GetUtcNow())); return ValueTask.CompletedTask; }, clock);
        var play = player.PlayAsync(Chunks(10), CancellationToken.None);
        while (!play.IsCompleted) { clock.Advance(TimeSpan.FromMilliseconds(20)); await Task.Delay(1); }
        Assert.True(await play);
        Assert.Equal(10, sent.Count);
        Assert.All(sent, s => Assert.Equal(VoiceAudio.FrameBytes, s.Len));
        // com antecedência máxima de 2 quadros, o último sai ~(10-1-2)*60 ms após o início, nunca em rajada
        Assert.True((sent[^1].At - sent[0].At).TotalMilliseconds >= 6 * VoiceAudio.FrameMs);
    }

    [Fact]
    public async Task SpeechPlayer_CancelStopsSending()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var count = 0;
        var player = new SpeechPlayer((_, _) => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; }, clock);
        using var cts = new CancellationTokenSource();
        var play = player.PlayAsync(Chunks(200), cts.Token);
        for (var i = 0; i < 10; i++) { clock.Advance(TimeSpan.FromMilliseconds(60)); await Task.Delay(1); }
        cts.Cancel();
        Assert.False(await play);
        var at = count;
        clock.Advance(TimeSpan.FromSeconds(5)); await Task.Delay(20);
        Assert.Equal(at, count);
        Assert.True(at < 20);
    }

    [Fact]
    public void VoiceText_RemovesWhatIsNotSpoken()
    {
        Assert.Equal("Olá tudo bem? Veja", VoiceText.ForSpeech("**Olá** 😀 tudo bem?\n- Veja https://x.com/a"));
        var s = VoiceText.Sentences("Oi, aqui é a Roberta da Samos. Como posso ajudar você hoje? Fico no aguardo.");
        Assert.True(s.Count >= 2);
        Assert.Equal("Oi, aqui é a Roberta da Samos. Como posso ajudar você hoje? Fico no aguardo.", string.Join(" ", s));
    }

    [Fact]
    public void CallStreamProtocol_ParsesDocumentedMessages()
    {
        var pcm = Pcm.Tone(1000);
        var media = CallStreamProtocol.Parse($"{{\"event\":\"media\",\"track\":\"inbound\",\"payload\":\"{Convert.ToBase64String(pcm)}\"}}");
        Assert.Equal(CallStreamEventKind.Media, media!.Kind);
        Assert.Equal(pcm, media.Pcm);
        Assert.Null(CallStreamProtocol.Parse("{\"event\":\"media\",\"track\":\"outbound\",\"payload\":\"AAAA\"}"));
        Assert.Equal(CallStreamEventKind.Started, CallStreamProtocol.Parse("{\"event\":\"start\",\"callId\":\"c1\",\"sampleRate\":16000}")!.Kind);
        Assert.Equal("hangup", CallStreamProtocol.Parse("{\"event\":\"stop\",\"reason\":\"hangup\"}")!.Reason);
        Assert.Null(CallStreamProtocol.Parse("não é json"));
        Assert.Contains("\"track\":\"outbound\"", CallStreamProtocol.MediaMessage(pcm));
    }

    [Fact]
    public void CallStreamProtocol_SignatureIsHmacSha256Hex()
    {
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("inst|call1|1700000000"))).ToLowerInvariant();
        Assert.Equal(expected, CallStreamProtocol.Sign("k", "inst", "call1", 1700000000));
    }

    [Fact]
    public void RealtimeSttProtocol_ParsesAndRaisesErrors()
    {
        Assert.False(RealtimeSttProtocol.Parse("{\"message_type\":\"partial_transcript\",\"text\":\"ol\"}")!.Final);
        var f = RealtimeSttProtocol.Parse("{\"message_type\":\"committed_transcript\",\"text\":\"olá\"}")!;
        Assert.True(f.Final); Assert.Equal("olá", f.Text);
        Assert.Null(RealtimeSttProtocol.Parse("{\"message_type\":\"session_started\"}"));
        Assert.Throws<IntegrationException>(() => RealtimeSttProtocol.Parse("{\"message_type\":\"quota_exceeded\",\"error\":\"x\"}"));
        Assert.Contains("\"commit\":true", RealtimeSttProtocol.Chunk(new byte[4], true));
    }

    [Theory]
    [InlineData("{\"event\":\"CallOffer\",\"data\":{\"callId\":\"abc\",\"callCreator\":\"5511999990001@s.whatsapp.net\"}}", "CallOffer", "abc", "5511999990001@s.whatsapp.net")]
    [InlineData("{\"type\":\"CallTerminate\",\"CallID\":\"z9\"}", "CallTerminate", "z9", null)]
    public void WebhookParser_IsTolerant(string json, string ev, string? id, string? from)
    {
        var r = VoiceWebhookController.Parse(json);
        Assert.Equal((ev, id, from), (r.Event, r.CallId, r.From));
        Assert.Null(VoiceWebhookController.Parse("lixo").Event);
    }
}

/// <summary>Os 20 cenários da especificação, executados SIMULADOS contra a sessão real (VAD, estados, barge-in, timers).</summary>
public class VoiceScenarioTests
{
    // 1 + 2
    [Fact]
    public async Task S01_02_Dial_ThenAnswer_RobertaOpensAndIdentifiesAsVirtualAssistant()
    {
        var s = new Sim(); s.Start(VoiceCallState.Ringing);
        await s.Step(120);
        Assert.Equal(VoiceCallState.Ringing, s.Session.State);
        s.Stream.Push(Pcm.Silence()); // primeiro quadro recebido = atendeu
        await s.WaitOpenerDone();
        Assert.True(s.Host.Saw(VoiceCallState.Connected));
        Assert.True(s.Host.Saw(VoiceCallState.Speaking));
        Assert.True(s.Stream.SentCount >= 3);
        Assert.Contains("assistente virtual", s.Speech.TtsTexts[0]);
        s.Session.RequestEnd("user_hangup");
        var r = await s.Finish();
        Assert.Equal(VoiceCallState.Completed, r.FinalState);
    }

    // 3
    [Fact]
    public async Task S03_NoAnswer_TimesOutWithoutAudio()
    {
        var s = new Sim(); s.Opt.RingSeconds = 5; s.Start(VoiceCallState.Ringing);
        var r = await s.Finish();
        Assert.Equal("no_answer", r.EndReason);
        Assert.Equal(0, s.Stream.SentCount);
        Assert.Empty(s.Speech.TtsTexts);
        Assert.Equal(0, r.DurationSeconds);
    }

    // 4
    [Fact]
    public async Task S04_Rejected_EndsAsNotConnected()
    {
        var s = new Sim(); s.Start(VoiceCallState.Ringing);
        await s.Step(120); s.Stream.Stop("rejected");
        var r = await s.Finish();
        Assert.Equal("not_connected", r.EndReason);
        Assert.Equal(VoiceCallState.Failed, r.FinalState);
        Assert.Empty(s.Speech.TtsTexts);
    }

    // 5
    [Fact]
    public async Task S05_InboundCall_StartsConnectedAndSpeaksFirst()
    {
        var s = new Sim(); s.Brain.Opener = "Oi! Aqui é a Roberta, assistente virtual de vendas da Samos. Obrigada por ligar."; s.Start(VoiceCallState.Connected);
        await s.WaitOpenerDone();
        Assert.Contains("Obrigada por ligar", s.Speech.TtsTexts.Last() + string.Join(" ", s.Speech.TtsTexts));
        s.Session.RequestEnd("user_hangup"); await s.Finish();
    }

    // 6
    [Fact]
    public async Task S06_BargeIn_StopsRobertaAndListens()
    {
        var s = new Sim(); s.Speech.FramesPerSentence = 60; s.Start();
        Assert.True(await s.Until(() => s.Session.State == VoiceCallState.Speaking && s.Stream.SentCount > 4));
        s.Speech.Transcripts.Enqueue("espera, deixa eu te perguntar uma coisa");
        await s.Talk(6, 9000);
        Assert.True(s.Host.Saw(VoiceCallState.Interrupted));
        var after = s.Stream.SentCount;
        await s.Quiet(8);
        Assert.True(await s.Until(() => s.Brain.Calls >= 1));
        Assert.True(s.Stream.SentCount - after < 70, "a fala da Roberta deveria ter sido cortada");
        s.Session.RequestEnd("user_hangup");
        var r = await s.Finish();
        Assert.Equal(1, r.BargeIns);
        Assert.Contains(r.Transcript, l => l.Text.Contains("interrompida"));
    }

    [Fact]
    public async Task S06b_EchoOfRobertasOwnVoice_DoesNotTriggerBargeIn()
    {
        var s = new Sim(); s.Speech.FramesPerSentence = 40; s.Start();
        Assert.True(await s.Until(() => s.Session.State == VoiceCallState.Speaking));
        await s.Talk(10, 300); // eco fraco da própria voz
        Assert.False(s.Host.Saw(VoiceCallState.Interrupted));
        s.Session.RequestEnd("user_hangup");
        Assert.Equal(0, (await s.Finish()).BargeIns);
    }

    // 7
    [Fact]
    public async Task S07_Silence_PromptsTwiceThenEnds()
    {
        var s = new Sim(); s.Opt.SilenceSeconds = 2; s.Start();
        var r = await s.Finish();
        Assert.Equal("silence", r.EndReason);
        Assert.Equal(2, s.Speech.SpokeCount(s.Opt.SilencePromptText));
        Assert.True(s.Speech.SpokeText("vou encerrar"));
    }

    // 8
    [Fact]
    public async Task S08_FastTalker_BackToBackUtterancesAreMergedIntoOneTurn()
    {
        var s = new Sim();
        s.Brain.Respond = async (_, ct) => { await Task.Delay(TimeSpan.FromMilliseconds(1500), s.Clock, ct); return new BrainTurn("Claro, já te explico.", false, null, "PitchFeature", true); };
        s.Start(); await s.WaitOpenerDone();
        s.Speech.Transcripts.Enqueue("preciso saber o preço rápido"); s.Speech.Transcripts.Enqueue("e se integra com nota fiscal");
        await s.Talk(4); await s.Quiet(7);
        await s.Talk(4); await s.Quiet(7);
        Assert.True(await s.Until(() => s.Speech.SpokeText("já te explico")));
        Assert.Contains("preço rápido", s.Brain.Inputs.Last());
        Assert.Contains("nota fiscal", s.Brain.Inputs.Last());
        Assert.Equal(1, s.Host.Count("turn"));
        s.Session.RequestEnd("user_hangup"); await s.Finish();
    }

    // 9 + 10 + 11
    [Fact]
    public async Task S09_RegionalExpression_ReachesBrainUnchanged_AndObjectionIsAnswered()
    {
        var s = new Sim();
        s.Brain.Respond = (txt, _) => Task.FromResult(new BrainTurn("Entendo, trocar de sistema dá trabalho mesmo. Posso te mostrar como é a migração?", false, null, "HandleObjection", true));
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("oxente, já tenho sistema, visse? bora deixar assim mesmo");
        Assert.Equal("oxente, já tenho sistema, visse? bora deixar assim mesmo", s.Brain.Inputs[0]);
        Assert.True(await s.Until(() => s.Speech.SpokeText("trocar de sistema")));
        s.Session.RequestEnd("user_hangup"); await s.Finish();
    }

    [Fact]
    public async Task S11_DemoRequest_SummaryCarriesOutcome()
    {
        var s = new Sim(); s.Brain.Outcome = "demo_aceita";
        s.Brain.Respond = (_, _) => Task.FromResult(new BrainTurn("Perfeito! Qual o melhor horário pra demonstração?", false, "demo_aceita", "OfferDemo", true));
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("quero ver uma demonstração do Axis");
        Assert.True(await s.Until(() => s.Speech.SpokeText("demonstração")));
        s.Session.RequestEnd("user_hangup");
        var r = await s.Finish();
        Assert.Equal("demo_aceita", r.Summary!.Outcome);
        Assert.Equal(1, r.Turns); Assert.Equal(1, r.LlmCalls);
        Assert.True(r.TtsCharacters > 0 && r.SttSeconds > 0);
    }

    // 12
    [Fact]
    public async Task S12_CustomerAsksToEnd_RobertaSaysGoodbyeThenHangsUp()
    {
        var s = new Sim();
        s.Brain.Respond = (_, _) => Task.FromResult(new BrainTurn("Tudo bem, obrigada pelo tempo! Até mais.", true, "encerrado", "ClosePolitely", false));
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("pode encerrar por favor");
        var r = await s.Finish();
        Assert.Equal("completed", r.EndReason);
        Assert.True(s.Speech.SpokeText("obrigada pelo tempo"), "deve se despedir antes de desligar");
    }

    // 13
    [Fact]
    public async Task S13_WebSocketDisconnect_FailsWithoutRetryLoop()
    {
        var s = new Sim(); s.Start(); await s.WaitOpenerDone();
        s.Stream.Disconnect();
        var r = await s.Finish();
        Assert.Equal("ws_disconnected", r.EndReason);
        Assert.Equal(VoiceCallState.Failed, r.FinalState);
    }

    // 14
    [Fact]
    public async Task S14_RealtimeSttFails_FallsBackToBatch()
    {
        var s = new Sim(); s.Speech.RealtimeFails = true; s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("quanto custa?");
        Assert.Equal("quanto custa?", s.Brain.Inputs[0]);
        Assert.Contains("stt_realtime_failed", s.Host.Events);
        s.Session.RequestEnd("user_hangup"); await s.Finish();
    }

    [Fact]
    public async Task S14b_BothSttFail_AsksToRepeatThenEndsAsFailed()
    {
        var s = new Sim(); s.Speech.RealtimeFails = true; s.Speech.BatchFails = true; s.Start(); await s.WaitOpenerDone();
        await s.Talk(5); await s.Quiet(7);
        Assert.True(await s.Until(() => s.Speech.SpokeText("não consegui te ouvir")));
        await s.Talk(5); await s.Quiet(7);
        var r = await s.Finish();
        Assert.Equal("stt_failed", r.EndReason);
        Assert.Equal(VoiceCallState.Failed, r.FinalState);
        Assert.Equal(0, s.Brain.Calls);
    }

    // 15
    [Fact]
    public async Task S15_ElevenLabsFails_EndsOnceWithoutCallLoop()
    {
        var s = new Sim(); s.Speech.FailTts = true; s.Start();
        var r = await s.Finish();
        Assert.Equal("tts_failed", r.EndReason);
        Assert.Equal(VoiceCallState.Failed, r.FinalState);
        Assert.Single(s.Speech.TtsTexts);
    }

    // 16
    [Fact]
    public async Task S16_SlowLlm_PlaysFillerThenAnswers_AndLatencyIsMeasured()
    {
        var s = new Sim(); s.Opt.FillerAfterMs = 1000; s.Opt.BrainTimeoutMs = 10000;
        s.Brain.Respond = async (_, ct) => { await Task.Delay(TimeSpan.FromMilliseconds(4000), s.Clock, ct); return new BrainTurn("Prontinho, achei aqui.", false, null, "ShareInsight", true); };
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("como funciona o controle de estoque?");
        Assert.True(await s.Until(() => s.Speech.SpokeText("achei aqui")));
        Assert.True(s.Speech.SpokeText(s.Opt.FillerText));
        s.Session.RequestEnd("user_hangup");
        var r = await s.Finish();
        var lat = Assert.Single(r.Latencies);
        Assert.True(lat.Filler); Assert.True(lat.TotalMs > 0);
    }

    [Fact]
    public async Task S16b_LlmNeverAnswers_TwiceEndsAsLlmTimeout()
    {
        var s = new Sim(); s.Opt.FillerAfterMs = 500; s.Opt.BrainTimeoutMs = 2000;
        s.Brain.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("alô?");
        Assert.True(await s.Until(() => s.Speech.SpokeText("instabilidade")));
        await s.SayAndWaitBrain("e aí?", expectCalls: 2);
        var r = await s.Finish();
        Assert.Equal("llm_timeout", r.EndReason);
        Assert.Equal(VoiceCallState.Failed, r.FinalState);
    }

    // 18
    [Fact]
    public async Task S18_HumanRequested_HandsOffAndEnds()
    {
        var s = new Sim(); s.Brain.Outcome = "transferir_humano";
        s.Brain.Respond = (_, _) => Task.FromResult(new BrainTurn("Claro, vou pedir para alguém do time falar com você ainda hoje.", true, "transferir_humano", "TransferHuman", false, Handoff: true));
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("quero falar com uma pessoa");
        var r = await s.Finish();
        Assert.Equal("handoff", r.EndReason);
        Assert.Equal("transferir_humano", r.Summary!.Outcome);
    }

    [Fact]
    public async Task S12b_OptOut_EndsWithOptOutReason()
    {
        var s = new Sim();
        s.Brain.Respond = (_, _) => Task.FromResult(new BrainTurn("Sem problema, não vou mais te incomodar.", true, "opt_out", "ClosePolitely", false, OptOut: true));
        s.Start(); await s.WaitOpenerDone();
        await s.SayAndWaitBrain("não quero mais contato");
        Assert.Equal("opt_out", (await s.Finish()).EndReason);
    }

    // 19
    [Fact]
    public async Task S19_SimultaneousEvents_EndOnlyOnce()
    {
        var s = new Sim(); s.Start(VoiceCallState.Ringing); await s.Step(120);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => s.Session.NotifyAnswered())));
        Assert.Equal(1, s.Host.Count("connected"));
        await Task.WhenAll(Task.Run(() => s.Session.RequestEnd("user_hangup")), Task.Run(() => s.Session.RequestEnd("kill_switch")), Task.Run(() => s.Session.RequestEnd("remote_hangup")));
        var r = await s.Finish();
        Assert.Equal(1, s.Host.Count("ending"));
        Assert.Contains(r.EndReason, new[] { "user_hangup", "kill_switch", "remote_hangup" });
    }

    // 20
    [Fact]
    public async Task S20_MaxDuration_WrapsUpPolitelyThenEnds()
    {
        var s = new Sim(); s.Opt.MaxSeconds = 4; s.Start();
        var r = await s.Finish();
        Assert.Equal("max_duration", r.EndReason);
        Assert.True(s.Speech.SpokeText("tempo limite"));
    }
}

/// <summary>Cenário 17 e regras do gerenciador (lista de autorizados, kill switch, limites, idempotência) contra EF InMemory.</summary>
public class VoiceManagerTests
{
    private sealed class FakeGateway : ICallGateway
    {
        public bool IsConfigured { get; set; } = true;
        public int Dials, Hangups; public string? LastNumber; public bool FailDial;
        public readonly List<FakeStream> Streams = new();
        public Task<string> DialAsync(string phone, CancellationToken ct) { if (FailDial) throw new IntegrationException("falha simulada"); Dials++; LastNumber = phone; return Task.FromResult("call-" + Dials); }
        public Task AnswerAsync(string c, string id, CancellationToken ct) => Task.CompletedTask;
        public Task RejectAsync(string c, string id, CancellationToken ct) => Task.CompletedTask;
        public Task HangupAsync(string id, CancellationToken ct) { Hangups++; return Task.CompletedTask; }
        public Task<ICallStream> OpenStreamAsync(string id, CancellationToken ct) { var s = new FakeStream(); Streams.Add(s); return Task.FromResult<ICallStream>(s); }
    }

    private static (TestHost Host, FakeGateway Gw, VoiceCallManager Mgr) Setup(Dictionary<string, string?>? cfg = null)
    {
        var gw = new FakeGateway();
        var c = new Dictionary<string, string?> { ["VOICE_CALLS_ENABLED"] = "true" };
        if (cfg != null) foreach (var kv in cfg) c[kv.Key] = kv.Value;
        var host = new TestHost(c, s => { s.AddSingleton<ICallGateway>(gw); s.AddSingleton<IVoiceSpeech>(new FakeSpeech()); });
        return (host, gw, host.Root.GetRequiredService<VoiceCallManager>());
    }

    private static async Task Allow(TestHost h, string phone, bool consent = true)
    {
        using var sc = h.Scope(); var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        db.VoiceAllowedNumbers.Add(new VoiceAllowedNumber { Phone = PhoneUtil.Normalize(phone)!, PhoneKey = PhoneUtil.Key(phone)!, Label = "Teste", Active = true, ConsentAt = consent ? DateTime.UtcNow : null, ConsentBy = "admin" });
        await db.SaveChangesAsync();
    }

    private const string Phone = "11999990001";

    [Fact]
    public async Task NotAllowlisted_IsRefused_AndNeverDials()
    {
        var (h, gw, m) = Setup(); using var _ = h;
        var r = await m.StartTestCallAsync(Phone, 1, "admin", "k1");
        Assert.False(r.Ok); Assert.Equal(0, gw.Dials);
        await Allow(h, "11988880000");
        Assert.False((await m.StartTestCallAsync(Phone, 1, "admin", "k2")).Ok);
        Assert.Equal(0, gw.Dials);
    }

    [Fact]
    public async Task AllowedWithoutConsent_IsRefused()
    {
        var (h, gw, m) = Setup(); using var _ = h;
        await Allow(h, Phone, consent: false);
        Assert.False((await m.StartTestCallAsync(Phone, 1, "admin", "k1")).Ok);
        Assert.Equal(0, gw.Dials);
    }

    [Fact]
    public async Task DisabledOrKillSwitch_BlocksCalls()
    {
        var (h, gw, m) = Setup(new() { ["VOICE_CALLS_ENABLED"] = "false" }); using var _ = h;
        await Allow(h, Phone);
        Assert.False((await m.StartTestCallAsync(Phone, 1, "admin", "k1")).Ok);
        var (h2, gw2, m2) = Setup(new() { ["VOICE_CALLS_KILL_SWITCH"] = "true" }); using var _2 = h2;
        await Allow(h2, Phone);
        var r = await m2.StartTestCallAsync(Phone, 1, "admin", "k1");
        Assert.False(r.Ok); Assert.Contains("Kill switch", r.Error);
        Assert.Equal(0, gw.Dials + gw2.Dials);
    }

    [Fact]
    public async Task Start_IsIdempotent_NoDuplicateCallToSameContact()
    {
        var (h, gw, m) = Setup(); using var _ = h;
        await Allow(h, Phone);
        var a = await m.StartTestCallAsync(Phone, 1, "admin", "same-key");
        var b = await m.StartTestCallAsync(Phone, 1, "admin", "same-key");     // clique duplo: mesma chave
        var c = await m.StartTestCallAsync(Phone, 1, "admin", "other-key");    // outra chave, mas já há ligação ativa
        Assert.True(a.Ok); Assert.True(b.Ok && b.Duplicate); Assert.Equal(a.CallId, b.CallId);
        Assert.False(c.Ok);
        Assert.Equal(1, gw.Dials);
        Assert.True(m.IsActive(a.CallId!.Value));
        Assert.True(await m.HangupAsync(a.CallId.Value));
        Assert.True(await WaitAsync(() => !m.IsActive(a.CallId.Value)));
        Assert.False(await m.HangupAsync(a.CallId.Value) && m.IsActive(a.CallId.Value)); // encerrar de novo é inofensivo
        Assert.True(gw.Hangups >= 1);
    }

    [Fact]
    public async Task AttemptLimitPerDay_IsEnforced()
    {
        var (h, gw, m) = Setup(new() { ["VOICE_CALLS_MAX_ATTEMPTS_DAY"] = "2" }); using var _ = h;
        await Allow(h, Phone);
        for (var i = 0; i < 2; i++)
        {
            var r = await m.StartTestCallAsync(Phone, 1, "admin", "k" + i);
            Assert.True(r.Ok);
            await m.HangupAsync(r.CallId!.Value);
            Assert.True(await WaitAsync(() => !m.IsActive(r.CallId.Value)));
        }
        var third = await m.StartTestCallAsync(Phone, 1, "admin", "k3");
        Assert.False(third.Ok); Assert.Contains("tentativas", third.Error);
        Assert.Equal(2, gw.Dials);
    }

    [Fact]
    public async Task ConcurrencyLimit_IsEnforced()
    {
        var (h, gw, m) = Setup(); using var _ = h;
        await Allow(h, Phone); await Allow(h, "11999990002");
        var a = await m.StartTestCallAsync(Phone, 1, "admin", "a");
        var b = await m.StartTestCallAsync("11999990002", 1, "admin", "b");
        Assert.True(a.Ok); Assert.False(b.Ok); Assert.Contains("simultâneas", b.Error);
        m.KillAll();
        Assert.True(await WaitAsync(() => m.ActiveCount == 0));
    }

    [Fact]
    public async Task DialFailure_MarksCallFailed_AndDoesNotRetry()
    {
        var (h, gw, m) = Setup(); using var _ = h; gw.FailDial = true;
        await Allow(h, Phone);
        var r = await m.StartTestCallAsync(Phone, 1, "admin", "k");
        Assert.False(r.Ok);
        using var sc = h.Scope(); var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = await db.VoiceCalls.SingleAsync();
        Assert.Equal(VoiceCallState.Failed, c.State); Assert.Equal("dial_failed", c.EndReason);
        Assert.Equal(0, gw.Dials);
    }

    [Fact]
    public async Task CompletedCall_PersistsResult_WithoutTranscriptByDefault_AndWritesCrmInteraction()
    {
        var (h, gw, m) = Setup(); using var _ = h;
        await Allow(h, Phone);
        var r = await m.StartTestCallAsync(Phone, 1, "admin", "k");
        Assert.True(r.Ok);
        gw.Streams[0].Push(Pcm.Silence()); // atendeu
        Assert.True(await WaitAsync(() => { using var sc = h.Scope(); return sc.ServiceProvider.GetRequiredService<AppDbContext>().VoiceCalls.Any(x => x.State == VoiceCallState.Speaking || x.State == VoiceCallState.Listening); }));
        await m.HangupAsync(r.CallId!.Value);
        Assert.True(await WaitAsync(() => { using var sc = h.Scope(); return sc.ServiceProvider.GetRequiredService<AppDbContext>().VoiceCalls.Any(x => x.State == VoiceCallState.Completed); }));
        using var s2 = h.Scope(); var db = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = await db.VoiceCalls.SingleAsync();
        Assert.Equal("user_hangup", c.EndReason);
        Assert.Null(c.Transcript);
        Assert.Null(c.EstimatedCost); // sem tarifas configuradas não se inventa custo
        Assert.NotNull(c.Summary);
        Assert.True(await db.Interactions.AnyAsync(i => i.LeadId == c.LeadId && i.Type == InteractionType.Ligacao));
    }

    // 17: a mesma memória (contato/lead) é usada pelo WhatsApp e pela ligação
    [Fact]
    public async Task S17_WhatsAppAndCall_ShareTheSameLeadAndContact()
    {
        using var h = new TestHost();
        using var sc = h.Scope(); var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var (lead1, c1) = await VoiceContactResolver.EnsureAsync(db, "5511999990001", "Maria", default);
        var (lead2, c2) = await VoiceContactResolver.EnsureAsync(db, "(11) 99999-0001", "Maria", default);
        Assert.Equal(lead1.Id, lead2.Id); Assert.Equal(c1.Id, c2.Id);
        Assert.Equal(1, await db.Leads.CountAsync());
        Assert.Equal(1, await db.SdrContacts.CountAsync());
        var camp = await db.SdrCampaigns.SingleAsync(x => x.Id == c1.CampaignId);
        Assert.False(camp.IsActive); // nunca entra nos disparos automáticos
    }

    [Fact]
    public async Task Webhook_UnknownCall_IsIgnored_AndInboundFromStranger_IsNotAnswered()
    {
        var (h, gw, m) = Setup(new() { ["VOICE_CALLS_INBOUND_ENABLED"] = "true" }); using var _ = h;
        Assert.Equal("ignored", await m.HandleWebhookAsync("CallAccept", "nope", null));
        Assert.Equal("not_allowed", await m.HandleWebhookAsync("CallOffer", "in-1", "5511977770000@s.whatsapp.net"));
        Assert.Equal(0, m.ActiveCount);
    }

    private static async Task<bool> WaitAsync(Func<bool> cond, int ms = 8000)
    {
        for (var t = 0; t < ms && !cond(); t += 25) await Task.Delay(25);
        return cond();
    }
}
