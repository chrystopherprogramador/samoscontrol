using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using SamosControl.Integrations.Voice;

namespace SamosControl.Application.Voice;

public record VoiceStartResult(bool Ok, int? CallId, string? Error, bool Duplicate = false);

public record VoiceLimits(bool Enabled, bool KillSwitch, int MaxConcurrent, int MaxSeconds, int MaxAttemptsPerDay, int RingSeconds, bool StoreTranscript, bool InboundEnabled)
{
    public static VoiceLimits From(ISettingsReader s)
    {
        static int I(string? v, int d, int min, int max) => int.TryParse(v, out var n) ? Math.Clamp(n, min, max) : d;
        static bool B(string? v) => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return new(B(s.Get(SettingKeys.VoiceEnabled)), B(s.Get(SettingKeys.VoiceKillSwitch)),
            I(s.Get(SettingKeys.VoiceMaxConcurrent), 1, 1, 3), I(s.Get(SettingKeys.VoiceMaxSeconds), 300, 30, 900),
            I(s.Get(SettingKeys.VoiceMaxAttemptsDay), 0, 0, 10000), I(s.Get(SettingKeys.VoiceRingSeconds), 40, 10, 90),
            B(s.Get(SettingKeys.VoiceStoreTranscript)), B(s.Get(SettingKeys.VoiceInboundEnabled)));
    }
}

/// <summary>
/// Orquestra as ligações: kill switch, lista de autorizados, limites, idempotência, discagem, sessão, encerramento e gravação do resultado.
/// Singleton (as sessões vivem em memória; o banco guarda o histórico).
/// </summary>
public class VoiceCallManager
{
    private sealed record Active(VoiceSession Session, CallHost Host);
    private readonly ConcurrentDictionary<int, Active> _active = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ISettingsReader _s;
    private readonly ICallGateway _gateway;
    private readonly IVoiceSpeech _speech;
    private readonly ILogger<VoiceCallManager> _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public VoiceCallManager(IServiceScopeFactory scopes, ISettingsReader s, ICallGateway gateway, IVoiceSpeech speech, ILogger<VoiceCallManager> log, TimeProvider? clock = null)
    { _scopes = scopes; _s = s; _gateway = gateway; _speech = speech; _log = log; _clock = clock ?? TimeProvider.System; }

    public int ActiveCount => _active.Count;
    public bool IsActive(int callId) => _active.ContainsKey(callId);
    public VoiceLimits Limits => VoiceLimits.From(_s);

    /// <summary>Motivo pelo qual NÃO se pode ligar agora (null = pode). Usado pela tela e pelo início da chamada.</summary>
    public string? Readiness()
    {
        var l = Limits;
        if (l.KillSwitch) return "Kill switch global ativo: todas as ligações estão bloqueadas.";
        if (!l.Enabled) return "Ligações de voz desativadas (VOICE_CALLS_ENABLED).";
        if (!_gateway.IsConfigured) return "Evolution Go Calls não configurado (URL e chave).";
        if (!_speech.IsConfigured) return "ElevenLabs (chave e voz) não configurado.";
        return null;
    }

    public async Task<VoiceStartResult> StartTestCallAsync(string phone, int? userId, string? userName, string idempotencyKey, CancellationToken ct = default)
    {
        var norm = PhoneUtil.Normalize(phone); var key = PhoneUtil.Key(phone);
        if (norm == null || key == null) return new(false, null, "Telefone inválido.");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) idempotencyKey = Guid.NewGuid().ToString("N");
        idempotencyKey = idempotencyKey.Length > 64 ? idempotencyKey[..64] : idempotencyKey;

        await _startGate.WaitAsync(ct); // serializa os pedidos de início: limites e duplicidade ficam consistentes
        VoiceCall call; VoiceAllowedNumber allowed; Lead lead; SdrContact contact;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.VoiceCalls.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (existing != null) return new(true, existing.Id, null, true);

            var why = Readiness();
            if (why != null) return new(false, null, why);
            var l = Limits;
            var allow = await db.VoiceAllowedNumbers.AsNoTracking().FirstOrDefaultAsync(x => x.PhoneKey == key && x.Active, ct);
            if (allow == null || allow.ConsentAt == null) return new(false, null, "Número fora da lista de autorizados (ou sem consentimento registrado).");
            allowed = allow;

            var activeStates = new[] { VoiceCallState.Dialing, VoiceCallState.Ringing, VoiceCallState.Connected, VoiceCallState.Listening, VoiceCallState.Thinking, VoiceCallState.Speaking, VoiceCallState.Interrupted, VoiceCallState.Ending };
            var running = await db.VoiceCalls.Where(x => activeStates.Contains(x.State)).Select(x => new { x.Id, x.PhoneKey }).ToListAsync(ct);
            var same = running.FirstOrDefault(x => x.PhoneKey == key);
            if (same != null) return new(false, same.Id, "Já existe uma ligação em andamento para este número.", true);
            if (running.Count >= l.MaxConcurrent) return new(false, null, $"Limite de ligações simultâneas atingido ({l.MaxConcurrent}).");
            var since = _clock.GetUtcNow().UtcDateTime.AddHours(-24);
            var attempts = l.MaxAttemptsPerDay > 0 ? await db.VoiceCalls.CountAsync(x => x.PhoneKey == key && x.CreatedAt >= since, ct) : 0;
            if (l.MaxAttemptsPerDay > 0 && attempts >= l.MaxAttemptsPerDay) return new(false, null, $"Limite de tentativas nas últimas 24 h atingido ({l.MaxAttemptsPerDay}).");

            (lead, contact) = await VoiceContactResolver.EnsureAsync(db, norm, allowed.Label, ct);
            call = new VoiceCall
            {
                Direction = VoiceDirection.Outbound, State = VoiceCallState.Dialing, Phone = norm, PhoneKey = key, LeadId = lead.Id, ContactName = allowed.Label,
                IdempotencyKey = idempotencyKey, StartedByUserId = userId, StartedByName = userName
            };
            db.VoiceCalls.Add(call);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                var dup = await db.VoiceCalls.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
                if (dup != null) return new(true, dup.Id, null, true);
                throw;
            }
            db.VoiceCallEvents.Add(new VoiceCallEvent { VoiceCallId = call.Id, Type = "created", Detail = $"Início solicitado por {userName ?? "sistema"}" });
            await db.SaveChangesAsync(ct);
        }
        finally { _startGate.Release(); }

        // Discagem e sessão fora do gate: não bloqueiam outros pedidos.
        string externalId; ICallStream stream;
        try
        {
            externalId = await _gateway.DialAsync(norm, ct);
            stream = await _gateway.OpenStreamAsync(externalId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Voz: falha ao discar/abrir stream (ligação {Id})", call.Id);
            await FailAsync(call.Id, "dial_failed", Short(ex.Message));
            return new(false, call.Id, "Falha ao iniciar a chamada: " + Short(ex.Message));
        }
        await UpdateAsync(call.Id, c => c.ExternalCallId = externalId);
        _ = RunAsync(call.Id, externalId, stream, lead.Id, contact.Id, inbound: false);
        return new(true, call.Id, null);
    }

    private async Task RunAsync(int callId, string externalId, ICallStream stream, int leadId, int contactId, bool inbound)
    {
        var l = Limits;
        var host = new CallHost(callId, _scopes, _log);
        var brain = new RobertaVoiceBrain(_scopes, leadId, contactId, inbound, l.StoreTranscript, $"/Voice/Details/{callId}", _log);
        var opt = new VoiceSessionOptions
        {
            MaxSeconds = l.MaxSeconds, RingSeconds = l.RingSeconds,
            BatchOnly = string.Equals(_s.Get(SettingKeys.VoiceSttMode), "batch", StringComparison.OrdinalIgnoreCase)
        };
        var session = new VoiceSession(stream, _speech, brain, opt, host, _clock);
        _active[callId] = new Active(session, host);
        VoiceSessionResult? res = null;
        try { res = await session.RunAsync(inbound ? VoiceCallState.Connected : VoiceCallState.Ringing); }
        catch (Exception ex) { _log.LogError(ex, "Voz: sessão {Id} terminou com erro", callId); }
        finally
        {
            _active.TryRemove(callId, out _);
            await host.CompleteAsync();
            try { await _gateway.HangupAsync(externalId, CancellationToken.None); } catch { /* já desligada */ }
        }
        await FinalizeAsync(callId, res);
    }

    private async Task FinalizeAsync(int callId, VoiceSessionResult? r)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var c = await db.VoiceCalls.FirstAsync(x => x.Id == callId);
            c.EndedAt = DateTime.UtcNow;
            if (r == null) { c.State = VoiceCallState.Failed; c.EndReason = "error"; }
            else
            {
                c.State = r.FinalState; c.EndReason = r.EndReason; c.DurationSeconds = r.DurationSeconds;
                c.ConnectedAt = r.ConnectedAt?.UtcDateTime;
                c.Turns = r.Turns; c.BargeIns = r.BargeIns; c.LlmCalls = r.LlmCalls; c.TtsCharacters = r.TtsCharacters; c.SttSeconds = (int)Math.Round(r.SttSeconds);
                if (r.Latencies.Count > 0) { c.AvgResponseLatencyMs = (int)r.Latencies.Average(x => x.TotalMs); c.MaxResponseLatencyMs = r.Latencies.Max(x => x.TotalMs); }
                c.Summary = r.Summary?.Summary; c.Outcome = r.Summary?.Outcome;
                if (Limits.StoreTranscript) c.Transcript = string.Join("\n", r.Transcript.Select(t => $"[{t.AtSecond}s] {t.Role}: {t.Text}"));
                c.EstimatedCost = EstimateCost(c);
                foreach (var lat in r.Latencies)
                    db.VoiceCallEvents.Add(new VoiceCallEvent { VoiceCallId = callId, Type = "latency", Detail = $"total={lat.TotalMs}ms endpoint={lat.EndpointMs} stt={lat.SttMs} brain={lat.BrainMs} tts_first_byte={lat.TtsFirstByteMs}{(lat.Filler ? " (com frase de espera)" : "")}" });
            }
            if (c.State == VoiceCallState.Failed)
            {
                c.FailureDetail ??= c.EndReason;
                await scope.ServiceProvider.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Warning, NotificationSource.System, "Ligação da Roberta falhou", $"{c.ContactName ?? c.Phone}: {c.EndReason}", $"/Voice/Details/{callId}");
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { _log.LogError(ex, "Voz: falha ao finalizar ligação {Id}", callId); }
    }

    private decimal? EstimateCost(VoiceCall c)
    {
        static decimal? D(string? v) => decimal.TryParse(v?.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        var tts = D(_s.Get(SettingKeys.VoiceCostTtsPer1kChars)); var stt = D(_s.Get(SettingKeys.VoiceCostSttPerMinute)); var llm = D(_s.Get(SettingKeys.VoiceCostLlmPerCall));
        if (tts == null && stt == null && llm == null) return null; // sem tarifas configuradas não se inventa custo
        return Math.Round((tts ?? 0) * c.TtsCharacters / 1000m + (stt ?? 0) * c.SttSeconds / 60m + (llm ?? 0) * c.LlmCalls, 4);
    }

    public async Task<bool> HangupAsync(int callId, string reason = "user_hangup")
    {
        if (_active.TryGetValue(callId, out var a)) { a.Session.RequestEnd(reason); return true; }
        // Sem sessão em memória (ex.: reinício): fecha o registro e tenta desligar no provedor, de forma idempotente.
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = await db.VoiceCalls.FirstOrDefaultAsync(x => x.Id == callId);
        if (c == null || VoiceStateMachine.IsTerminal(c.State)) return false;
        if (c.ExternalCallId != null) try { await _gateway.HangupAsync(c.ExternalCallId, CancellationToken.None); } catch { }
        c.State = VoiceCallState.Failed; c.EndReason = "orphaned"; c.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public void KillAll(string reason = "kill_switch") { foreach (var a in _active.Values) a.Session.RequestEnd(reason); }

    /// <summary>Webhook do Evolution Go Calls. Nomes dos eventos documentados: CallOffer, CallAccept, CallTerminate.</summary>
    public async Task<string> HandleWebhookAsync(string eventName, string? externalCallId, string? from, CancellationToken ct = default, string? rawBody = null)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        VoiceCall? c = null;
        if (!string.IsNullOrWhiteSpace(externalCallId)) c = await db.VoiceCalls.FirstOrDefaultAsync(x => x.ExternalCallId == externalCallId, ct);
        if (c != null && rawBody != null)
        {
            // diagnóstico: sequência real de eventos do WhatsApp (Offer/Accept/RelayLatency/Terminate)
            db.VoiceCallEvents.Add(new VoiceCallEvent { VoiceCallId = c.Id, Type = ("wh:" + eventName).Length > 40 ? ("wh:" + eventName)[..40] : "wh:" + eventName, Detail = rawBody.Length > 900 ? rawBody[..900] : rawBody });
            await db.SaveChangesAsync(ct);
        }
        switch (eventName)
        {
            case "CallAccept":
                if (c != null && _active.TryGetValue(c.Id, out var a)) { a.Session.NotifyAnswered(); return "accepted"; }
                return "ignored";
            case "CallTerminate":
                if (c != null && _active.TryGetValue(c.Id, out var t)) { t.Session.RequestEnd(t.Session.State is VoiceCallState.Dialing or VoiceCallState.Ringing ? "rejected" : "remote_hangup"); return "terminated"; }
                return "ignored";
            case "CallOffer":
                if (c != null && c.Direction == VoiceDirection.Outbound)
                {
                    // Eco da oferta que nós mesmos discamos. Se o criador é o próprio número destino, o WhatsApp remetente está ligando para si mesmo e recusa.
                    var creatorKey = rawBody == null ? null : CreatorPhoneKey(rawBody);
                    if (creatorKey != null && creatorKey == c.PhoneKey && _active.TryGetValue(c.Id, out var self))
                    {
                        await UpdateAsync(c.Id, x => x.FailureDetail = "O número de destino é o do próprio WhatsApp que faz as ligações (a instância do Evolution Go Calls). Cadastre outro número.");
                        self.Session.RequestEnd("self_call");
                        return "self_call";
                    }
                    return "outbound_echo";
                }
                return await HandleInboundOfferAsync(db, externalCallId, from, ct);
            default: return "ignored";
        }
    }

    private static string? CreatorPhoneKey(string rawBody)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rawBody, "\"(?:CallCreatorAlt|caller_pn)\"\\s*:\\s*\"(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? PhoneUtil.Key(m.Groups[1].Value) : null;
    }

    private async Task<string> HandleInboundOfferAsync(AppDbContext db, string? externalCallId, string? from, CancellationToken ct)
    {
        var l = Limits;
        if (string.IsNullOrWhiteSpace(externalCallId) || string.IsNullOrWhiteSpace(from)) return "invalid";
        if (!l.InboundEnabled || Readiness() != null) return "inbound_disabled"; // sem atender: o WhatsApp do cliente continua tocando/recusa normalmente
        var key = PhoneUtil.Key(from);
        var allow = key == null ? null : await db.VoiceAllowedNumbers.AsNoTracking().FirstOrDefaultAsync(x => x.PhoneKey == key && x.Active && x.ConsentAt != null, ct);
        if (allow == null) return "not_allowed";
        if (await db.VoiceCalls.AnyAsync(x => x.ExternalCallId == externalCallId, ct)) return "duplicate";
        if (_active.Count >= l.MaxConcurrent) return "busy";
        var (lead, contact) = await VoiceContactResolver.EnsureAsync(db, from, allow.Label, ct);
        var call = new VoiceCall { Direction = VoiceDirection.Inbound, State = VoiceCallState.Ringing, Phone = PhoneUtil.Normalize(from)!, PhoneKey = key!, LeadId = lead.Id, ContactName = allow.Label, ExternalCallId = externalCallId, IdempotencyKey = "in:" + externalCallId };
        db.VoiceCalls.Add(call);
        await db.SaveChangesAsync(ct);
        try
        {
            // callCreator = JID de quem ligou, como recebido no evento (formato exato a confirmar com um webhook real).
            await _gateway.AnswerAsync(from, externalCallId, ct);
            var stream = await _gateway.OpenStreamAsync(externalCallId, ct);
            _ = RunAsync(call.Id, externalCallId, stream, lead.Id, contact.Id, inbound: true);
            return "answered";
        }
        catch (Exception ex)
        {
            await FailAsync(call.Id, "answer_failed", Short(ex.Message));
            return "answer_failed";
        }
    }

    public async Task RecoverOrphansAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var live = new[] { VoiceCallState.Dialing, VoiceCallState.Ringing, VoiceCallState.Connected, VoiceCallState.Listening, VoiceCallState.Thinking, VoiceCallState.Speaking, VoiceCallState.Interrupted, VoiceCallState.Ending };
        foreach (var c in await db.VoiceCalls.Where(x => live.Contains(x.State)).ToListAsync())
        {
            if (c.ExternalCallId != null && _gateway.IsConfigured) try { await _gateway.HangupAsync(c.ExternalCallId, CancellationToken.None); } catch { }
            c.State = VoiceCallState.Failed; c.EndReason = "restart"; c.EndedAt = DateTime.UtcNow; c.FailureDetail = "Aplicação reiniciada durante a ligação.";
        }
        await db.SaveChangesAsync();
    }

    private async Task FailAsync(int id, string reason, string? detail)
    {
        await UpdateAsync(id, c => { c.State = VoiceCallState.Failed; c.EndReason = reason; c.FailureDetail = detail; c.EndedAt = DateTime.UtcNow; });
    }

    private async Task UpdateAsync(int id, Action<VoiceCall> change)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = await db.VoiceCalls.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return;
        change(c);
        await db.SaveChangesAsync();
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>Grava estado/eventos da sessão em ordem, sem bloquear o áudio.</summary>
    private sealed class CallHost : IVoiceSessionHost
    {
        private readonly int _id; private readonly IServiceScopeFactory _scopes; private readonly ILogger _log;
        private readonly Channel<Func<AppDbContext, Task>> _q = Channel.CreateUnbounded<Func<AppDbContext, Task>>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _worker;
        public CallHost(int id, IServiceScopeFactory scopes, ILogger log) { _id = id; _scopes = scopes; _log = log; _worker = Task.Run(Loop); }

        private async Task Loop()
        {
            await foreach (var work in _q.Reader.ReadAllAsync())
            {
                try { using var scope = _scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); await work(db); }
                catch (Exception ex) { _log.LogWarning(ex, "Voz: falha ao gravar evento da ligação {Id}", _id); }
            }
        }

        public void StateChanged(VoiceCallState s)
        {
            _q.Writer.TryWrite(async db =>
            {
                var c = await db.VoiceCalls.FirstOrDefaultAsync(x => x.Id == _id);
                if (c == null || VoiceStateMachine.IsTerminal(c.State)) return;
                c.State = s;
                if (s == VoiceCallState.Connected && c.ConnectedAt == null) c.ConnectedAt = DateTime.UtcNow;
                db.VoiceCallEvents.Add(new VoiceCallEvent { VoiceCallId = _id, Type = "state", Detail = s.ToString() });
                await db.SaveChangesAsync();
            });
        }

        public void Event(string type, string? detail = null)
        {
            _log.LogInformation("Voz {Id}: {Type} {Detail}", _id, type, detail);
            _q.Writer.TryWrite(async db => { db.VoiceCallEvents.Add(new VoiceCallEvent { VoiceCallId = _id, Type = type.Length > 40 ? type[..40] : type, Detail = detail is { Length: > 1000 } ? detail[..1000] : detail }); await db.SaveChangesAsync(); });
        }

        public async Task CompleteAsync() { _q.Writer.TryComplete(); await _worker; }
    }
}

/// <summary>Reinicia com segurança (fecha ligações órfãs) e aplica o kill switch em até ~2 s sobre ligações em andamento.</summary>
public class VoiceWatchdog : BackgroundService
{
    private readonly VoiceCallManager _m;
    private readonly ILogger<VoiceWatchdog> _log;
    public VoiceWatchdog(VoiceCallManager m, ILogger<VoiceWatchdog> log) { _m = m; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await _m.RecoverOrphansAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Voz: não foi possível recuperar ligações órfãs (tabelas ainda não migradas?)"); }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
            var l = _m.Limits;
            if ((l.KillSwitch || !l.Enabled) && _m.ActiveCount > 0) { _log.LogWarning("Voz: kill switch/desativado — encerrando {N} ligação(ões)", _m.ActiveCount); _m.KillAll(); }
        }
    }
}
