using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

public enum SdrStage { Fila, Enviado, Respondeu, Repassado, OptOut, Encerrado, SemWhatsApp, Erro }

/// <summary>Automatic first-contact campaign: the SDR messages leads until a seller takes over.</summary>
public class SdrCampaign : BaseEntity
{
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    /// <summary>Seller whose WhatsApp sends the messages; null = company number (hand-off is then distributed by region/round-robin).</summary>
    public int? SellerId { get; set; }
    public int DailyLimit { get; set; } = 20;
    public int StartHour { get; set; } = 9;
    public int EndHour { get; set; } = 18;
    public int MinDelaySec { get; set; } = 90;
    public int MaxDelaySec { get; set; } = 240;
    /// <summary>First-message variants separated by a line containing only ---. Placeholders: {{nome}} {{empresa}} {{cidade}} {{segmento}}.</summary>
    [MaxLength(6000)] public string Variants { get; set; } = "";
    [MaxLength(2000)] public string? FollowUpText { get; set; }
    public int FollowUpHours { get; set; } = 48;
    public int MaxFollowUps { get; set; } = 2;
    [MaxLength(2000)] public string? Pitch { get; set; }
    public bool AiReplies { get; set; } = true;
    public int HandoffAlertMinutes { get; set; } = 15;
    public LeadStatus? FilterStatus { get; set; } = LeadStatus.Novo;
    [MaxLength(2)] public string? FilterState { get; set; }
    [MaxLength(100)] public string? FilterSegment { get; set; }
    public DateTime? NextSendAt { get; set; }
}

public class SdrContact : BaseEntity
{
    public int CampaignId { get; set; }
    public SdrCampaign? Campaign { get; set; }
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    [MaxLength(30)] public string Phone { get; set; } = "";
    [MaxLength(20)] public string PhoneKey { get; set; } = "";
    public SdrStage Stage { get; set; } = SdrStage.Fila;
    public int Variant { get; set; }
    public int Attempts { get; set; }
    public int AiTurns { get; set; }
    public DateTime? LastSentAt { get; set; }
    public DateTime? NextActionAt { get; set; }
    public DateTime? RepliedAt { get; set; }
    public int Score { get; set; }
    [MaxLength(1500)] public string? Summary { get; set; }
    public int? HandoffSellerId { get; set; }
    public DateTime? HandoffAt { get; set; }
    public bool Alerted { get; set; }
    [MaxLength(300)] public string? Error { get; set; }
}

public enum KnowledgeStatus { Pendente, Respondida, Ignorada }

/// <summary>Question/answer pair the AI is allowed to use. The AI asks questions it needs answered (Pendente) and learns from the owner's answers.</summary>
public class AiKnowledge : BaseEntity
{
    [MaxLength(100)] public string Topic { get; set; } = "Geral";
    [Required, MaxLength(600)] public string Question { get; set; } = "";
    [MaxLength(4000)] public string? Answer { get; set; }
    public KnowledgeStatus Status { get; set; } = KnowledgeStatus.Pendente;
    /// <summary>Treinamento (asked by the AI), Manual (typed by the owner) or Lacuna (a lead asked something the AI could not answer).</summary>
    [MaxLength(20)] public string Origin { get; set; } = "Treinamento";
    public int? LeadId { get; set; }
}
