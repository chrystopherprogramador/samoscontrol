using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

public enum LeadStatus { Novo, Contato, Qualificado, Proposta, Negociacao, Ganho, Perdido }

public class LeadSource : BaseEntity
{
    [Required, MaxLength(80)] public string Name { get; set; } = "";
}

public class Tag : BaseEntity
{
    [Required, MaxLength(50)] public string Name { get; set; } = "";
    [MaxLength(20)] public string Color { get; set; } = "#6c757d";
}

public class LeadTag
{
    public int LeadId { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}

public class Lead : BaseEntity
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? CompanyName { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(40)] public string? WhatsApp { get; set; }
    [MaxLength(200), EmailAddress] public string? Email { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(2)] public string? State { get; set; }
    [MaxLength(100)] public string? Segment { get; set; }
    [MaxLength(300)] public string? Website { get; set; }
    [MaxLength(200)] public string? GooglePlaceId { get; set; }
    public int? SourceId { get; set; }
    public LeadSource? Source { get; set; }
    public LeadStatus Status { get; set; } = LeadStatus.Novo;
    public int? AssignedUserId { get; set; }
    public User? AssignedUser { get; set; }
    /// <summary>Seller currently working this lead over WhatsApp; others need admin approval to take it.</summary>
    public int? SellerId { get; set; }
    [MaxLength(4000)] public string? Notes { get; set; }
    public DateTime? LastInteractionAt { get; set; }
    public int? CustomerId { get; set; }

    // Normalized keys used for duplicate detection.
    [MaxLength(20)] public string? PhoneKey { get; set; }
    [MaxLength(200)] public string? WebsiteKey { get; set; }
    [MaxLength(64)] public string? NameAddressKey { get; set; }

    public List<LeadTag> Tags { get; set; } = new();
    public List<Interaction> Interactions { get; set; } = new();
}

public class Company : BaseEntity
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? Document { get; set; }
    [MaxLength(300)] public string? Website { get; set; }
}

public class Customer : BaseEntity
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? Document { get; set; }
    [MaxLength(200), EmailAddress] public string? Email { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(40)] public string? WhatsApp { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(2)] public string? State { get; set; }
    [MaxLength(10)] public string? PostalCode { get; set; }
    [MaxLength(4000)] public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? LeadId { get; set; }
    /// <summary>Seller whose carteira this client belongs to.</summary>
    public int? SellerId { get; set; }
    [MaxLength(60)] public string? AsaasCustomerId { get; set; }
    public List<Contact> Contacts { get; set; } = new();
}

public class Contact : BaseEntity
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(100)] public string? Role { get; set; }
    [MaxLength(200), EmailAddress] public string? Email { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    public int? CustomerId { get; set; }
    public int? LeadId { get; set; }
}

public enum InteractionType { Nota, Ligacao, WhatsApp, Email, Reuniao, Sistema }

public class Interaction : BaseEntity
{
    public int? LeadId { get; set; }
    public int? CustomerId { get; set; }
    public InteractionType Type { get; set; }
    [Required, MaxLength(4000)] public string Description { get; set; } = "";
    public int? UserId { get; set; }
    public string? UserName { get; set; }
}

/// <summary>Observação livre (histórico de anotações) de Lead ou Cliente.</summary>
public class Note : BaseEntity
{
    public int? LeadId { get; set; }
    public int? CustomerId { get; set; }
    [Required, MaxLength(4000)] public string Text { get; set; } = "";
    public int? UserId { get; set; }
}

public class LeadSyncRun
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "RUNNING";
    public int Found { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Errors { get; set; }
    public string? Message { get; set; }
}

public class ScraperSearch : BaseEntity
{
    [Required, MaxLength(200)] public string Query { get; set; } = "";
    [MaxLength(200)] public string? Location { get; set; }
    [MaxLength(100)] public string? Segment { get; set; }
    [MaxLength(100)] public string? ExternalId { get; set; }
    public string Status { get; set; } = "PENDENTE";
    public DateTime? LastSyncAt { get; set; }
    public int ImportedCount { get; set; }
    public string? LastError { get; set; }
}
