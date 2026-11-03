using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

public enum VoiceDirection { Outbound, Inbound }

/// <summary>Estados da sessão de ligação (máquina de estados validada em VoiceStateMachine).</summary>
public enum VoiceCallState { Idle, Dialing, Ringing, Connected, Listening, Thinking, Speaking, Interrupted, Ending, Completed, Failed }

/// <summary>Ligação de voz da Roberta pelo WhatsApp (prova de conceito, só números autorizados).</summary>
public class VoiceCall : BaseEntity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    /// <summary>Chave de idempotência do pedido de início (evita ligação duplicada por duplo clique/retry).</summary>
    [MaxLength(64)] public string? IdempotencyKey { get; set; }
    public VoiceDirection Direction { get; set; }
    public VoiceCallState State { get; set; } = VoiceCallState.Idle;
    [MaxLength(30)] public string Phone { get; set; } = "";
    [MaxLength(20)] public string PhoneKey { get; set; } = "";
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    [MaxLength(120)] public string? ContactName { get; set; }
    /// <summary>callId devolvido pelo Evolution Go Calls.</summary>
    [MaxLength(120)] public string? ExternalCallId { get; set; }
    public int? StartedByUserId { get; set; }
    [MaxLength(120)] public string? StartedByName { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    /// <summary>completed, no_answer, rejected, max_duration, kill_switch, user_hangup, remote_hangup, ws_disconnected, stt_failed, tts_failed, not_allowed, error...</summary>
    [MaxLength(40)] public string? EndReason { get; set; }
    [MaxLength(600)] public string? FailureDetail { get; set; }

    // Resultado comercial
    /// <summary>demo_oferecida, transferir_humano, opt_out, sem_interesse, qualificado, indefinido...</summary>
    [MaxLength(40)] public string? Outcome { get; set; }
    [MaxLength(2000)] public string? Summary { get; set; }
    /// <summary>Transcrição (só gravada se VOICE_CALLS_STORE_TRANSCRIPT=true). Áudio nunca é gravado.</summary>
    public string? Transcript { get; set; }

    // Telemetria real (medida, nunca estimada)
    public int Turns { get; set; }
    public int BargeIns { get; set; }
    public int AvgResponseLatencyMs { get; set; }
    public int MaxResponseLatencyMs { get; set; }
    public int TtsCharacters { get; set; }
    public int SttSeconds { get; set; }
    public int LlmCalls { get; set; }
    /// <summary>Só calculado quando as tarifas estão configuradas nas Configurações.</summary>
    public decimal? EstimatedCost { get; set; }
}

public class VoiceCallEvent
{
    public long Id { get; set; }
    public int VoiceCallId { get; set; }
    public VoiceCall? VoiceCall { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(40)] public string Type { get; set; } = "";
    [MaxLength(1000)] public string? Detail { get; set; }
}

/// <summary>Números autorizados a receber/fazer chamadas na fase de prova de conceito.</summary>
public class VoiceAllowedNumber : BaseEntity
{
    [MaxLength(30)] public string Phone { get; set; } = "";
    [MaxLength(20)] public string PhoneKey { get; set; } = "";
    [MaxLength(120)] public string Label { get; set; } = "";
    public bool Active { get; set; } = true;
    /// <summary>Quem confirmou que o titular do número concordou com os testes (ligação automatizada e transcrição).</summary>
    [MaxLength(120)] public string? ConsentBy { get; set; }
    public DateTime? ConsentAt { get; set; }
}
