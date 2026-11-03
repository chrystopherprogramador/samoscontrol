using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

/// <summary>Shadow = generates and records, never sends. Review = a human approves each reply. Auto = sends (only with explicit owner authorization).</summary>
public enum SdrMode { Shadow, Review, Auto }

public enum ConvStage { Descoberta, Qualificacao, Apresentacao, Demo, Handoff, Encerrada }

/// <summary>Structured, persisted conversation memory (one per lead) so the AI never depends only on text history.</summary>
public class ConversationState : BaseEntity
{
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    public ConvStage Stage { get; set; } = ConvStage.Descoberta;
    /// <summary>0 unknown, 1 low, 2 medium, 3 high.</summary>
    public int InterestLevel { get; set; }
    public int? StoreCount { get; set; }
    [MaxLength(120)] public string? CurrentErp { get; set; }
    [MaxLength(120)] public string? Segment { get; set; }
    [MaxLength(1000)] public string? PainPoints { get; set; }
    [MaxLength(1000)] public string? Objections { get; set; }
    /// <summary>Comma separated topics: erp,stores,pain.</summary>
    [MaxLength(300)] public string QuestionsAsked { get; set; } = "";
    [MaxLength(300)] public string QuestionsAnswered { get; set; } = "";
    public bool PriceDiscussed { get; set; }
    public bool DemoOffered { get; set; }
    public bool DemoAccepted { get; set; }
    public bool HumanRequested { get; set; }
    public bool OptOut { get; set; }
    public bool SatisfiedWithCurrent { get; set; }
    public int NegativeStreak { get; set; }
    public int TurnCount { get; set; }
    [MaxLength(40)] public string? LastAction { get; set; }
    [MaxLength(40)] public string? NextAction { get; set; }
    [MaxLength(1500)] public string? Summary { get; set; }
    public DateTime? LastContactAt { get; set; }

    // ---- Feeling comercial (Roberta 3.0) ----
    /// <summary>0-100, computed deterministically from the lead's signals.</summary>
    public int Temperature { get; set; }
    /// <summary>baixa | media | alta.</summary>
    [MaxLength(10)] public string BuyingIntent { get; set; } = "baixa";
    /// <summary>baixo | medio | alto.</summary>
    [MaxLength(10)] public string Engagement { get; set; } = "baixo";
    /// <summary>Pain category code (e.g. estoque_divergencia, fiscal, sistema_atual).</summary>
    [MaxLength(40)] public string? MainPain { get; set; }
    /// <summary>0 none, 1 mentioned, 2 strong/recurrent.</summary>
    public int PainIntensity { get; set; }
    public int PainDepth { get; set; }
    public int PitchCount { get; set; }
    [MaxLength(40)] public string? CurrentGoal { get; set; }
    /// <summary>Comma separated tags: objetivo, comunicativo, informal, tecnico, apressado.</summary>
    [MaxLength(80)] public string? ClientStyle { get; set; }
    [MaxLength(20)] public string? LastFormat { get; set; }
    public int AudioSentCount { get; set; }
    public bool ClientSentAudio { get; set; }
    [MaxLength(300)] public string? FeaturesPresented { get; set; }
    public bool ReadyForHandoff { get; set; }
    [MaxLength(40)] public string? HandoffKind { get; set; }
    [MaxLength(300)] public string? LastQuestion { get; set; }
    public int FollowUpCount { get; set; }
    public int InboundWords { get; set; }
    public DateTime? LastInboundAt { get; set; }

    // ---- Memória comercial (Roberta 4.0) ----
    /// <summary>Optimistic concurrency token, bumped on every state update so simultaneous messages cannot silently overwrite each other.</summary>
    [ConcurrencyCheck] public int StateVersion { get; set; }
    /// <summary>Unconfirmed guesses about the lead (never stated to the client as fact).</summary>
    [MaxLength(600)] public string? Hypotheses { get; set; }
    /// <summary>What the lead said about the impact/frequency of the problem (confirmed by the lead).</summary>
    [MaxLength(500)] public string? ImpactNotes { get; set; }
    public bool ImpactKnown { get; set; }
    /// <summary>Commitments made in the conversation (e.g. "enviar material", "retomar amanhã à tarde").</summary>
    [MaxLength(500)] public string? Commitments { get; set; }
    /// <summary>texto | audio | null (when the lead expressed a channel preference).</summary>
    [MaxLength(10)] public string? PreferredChannel { get; set; }
    [MaxLength(80)] public string? PreferredTime { get; set; }
    /// <summary>descoberta | aprofundamento | apresentacao | demo | transferido | pausado | encerrado.</summary>
    [MaxLength(20)] public string OpportunityStage { get; set; } = "descoberta";
    /// <summary>Consecutive disengagement signals (monosyllables, ignored questions).</summary>
    public int LossSignals { get; set; }
    /// <summary>Last actions taken, newest last (csv), used to avoid repeating what did not work.</summary>
    [MaxLength(300)] public string? ActionHistory { get; set; }
    /// <summary>Every objection occurrence (csv of objection codes), used to count repeats.</summary>
    [MaxLength(300)] public string? ObjectionLog { get; set; }
    public bool AiDisclosed { get; set; }
    [MaxLength(40)] public string? LastInboundHash { get; set; }
    [MaxLength(60)] public string? Strategy { get; set; }
    /// <summary>Distinct pain categories voiced by the lead (csv), so a second pain is never lost.</summary>
    [MaxLength(120)] public string? PainCategories { get; set; }
    /// <summary>True only while processing a message that arrived after a long silence (not persisted).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public bool Returned { get; set; }
}

/// <summary>Conversation state used by Shadow mode so that simulations never change the real commercial memory of a lead.</summary>
public class ShadowConversationState : BaseEntity
{
    public int LeadId { get; set; }
    public string Json { get; set; } = "{}";
}

public class PromptVersion : BaseEntity
{
    [Required, MaxLength(60)] public string Name { get; set; } = "roberta";
    public int Version { get; set; }
    public string Content { get; set; } = "";
    [MaxLength(120)] public string? CreatedBy { get; set; }
    public bool IsActive { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    /// <summary>Regression pass rate (0-100) at activation time.</summary>
    public int? PassRate { get; set; }
}

public enum SuggestionStatus { Shadow, Pendente, Aprovada, Editada, Rejeitada, Enviada }

/// <summary>A reply Roberta proposed (shadow/review) with the context that produced it.</summary>
public class SdrSuggestion : BaseEntity
{
    public int ContactId { get; set; }
    public SdrContact? Contact { get; set; }
    public int LeadId { get; set; }
    [MaxLength(2000)] public string InboundText { get; set; } = "";
    [MaxLength(40)] public string Action { get; set; } = "";
    [MaxLength(2000)] public string? Reply { get; set; }
    [MaxLength(2000)] public string? EditedReply { get; set; }
    /// <summary>text | audio | audio_text (decided by the format advisor).</summary>
    [MaxLength(20)] public string? Format { get; set; }
    /// <summary>Selected commercial strategy (snake_case) and the objection handled, for audit and metrics.</summary>
    [MaxLength(60)] public string? Strategy { get; set; }
    [MaxLength(40)] public string? ObjectionKind { get; set; }
    public bool ValidatorOk { get; set; }
    [MaxLength(1000)] public string? ValidatorIssues { get; set; }
    public string? ContextJson { get; set; }
    [MaxLength(1000)] public string? ScoresJson { get; set; }
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Shadow;
    [MaxLength(120)] public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public int? PromptVersion { get; set; }
}

public class LlmExecution : BaseEntity
{
    public int? LeadId { get; set; }
    [MaxLength(60)] public string Task { get; set; } = "";
    [MaxLength(60)] public string? PromptName { get; set; }
    public int? PromptVersion { get; set; }
    [MaxLength(80)] public string? Model { get; set; }
    [MaxLength(40)] public string? Provider { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
    public int LatencyMs { get; set; }
    [MaxLength(40)] public string? Action { get; set; }
    [MaxLength(300)] public string? ValidatorResult { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
}
