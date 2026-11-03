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
