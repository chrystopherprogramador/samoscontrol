using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

public class ContractTemplate : BaseEntity
{
    [Required, MaxLength(150)] public string Name { get; set; } = "";
    /// <summary>Plain text. Lines starting with "# " are headings. Supports {{PLACEHOLDERS}}.</summary>
    [Required] public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public enum ContractStatus { Rascunho, AguardandoAssinatura, Assinado, Cancelado }

public class Contract : BaseEntity
{
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public int? TemplateId { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    [MaxLength(200)] public string? ServiceName { get; set; }
    public decimal Value { get; set; }
    public DateTime? DueDate { get; set; }

    // Commercial terms: implementation fee (optionally split) + monthly fee. They feed the document, its hash and the billing plan.
    public decimal ImplementationValue { get; set; }
    public decimal DiscountValue { get; set; }
    public int ImplementationInstallments { get; set; } = 1;
    public DateTime? ImplementationFirstDue { get; set; }
    public decimal MonthlyValue { get; set; }
    /// <summary>Due date of the first monthly charge; later ones keep the same day of month.</summary>
    public DateTime? MonthlyStart { get; set; }
    /// <summary>Number of monthly charges; null = indefinite term.</summary>
    public int? MonthlyMonths { get; set; }
    public BillingType BillingType { get; set; } = BillingType.Pix;
    /// <summary>True when no more charges must be generated (plan complete or contract ended).</summary>
    public bool BillingClosed { get; set; }
    /// <summary>Public code printed on the PDF and used on /verify.</summary>
    [MaxLength(32)] public string? VerificationCode { get; set; }

    /// <summary>SERVICO (client contract) or PARCERIA (seller partnership contract; no charges).</summary>
    [MaxLength(20)] public string Kind { get; set; } = "SERVICO";
    /// <summary>Seller (carteira) credited with commissions; rates are frozen at creation so the contract text matches what is paid.</summary>
    public int? SellerId { get; set; }
    public decimal ImplementationCommissionPercent { get; set; }
    public decimal MonthlyCommissionPercent { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Rascunho;
    public string RenderedText { get; set; } = "";
    [MaxLength(64)] public string? DocumentHash { get; set; }
    [MaxLength(300)] public string? FinalPdfPath { get; set; }
    [MaxLength(64)] public string? FinalPdfHash { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? SignedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public List<ContractParty> Parties { get; set; } = new();
    public List<ContractEvent> Events { get; set; } = new();
}

public class ContractParty : BaseEntity
{
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [MaxLength(50)] public string Role { get; set; } = "CONTRATANTE";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? Document { get; set; }
    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    /// <summary>Random, unguessable token used in the public signing link.</summary>
    [MaxLength(64)] public string Token { get; set; } = "";
    public ContractSignature? Signature { get; set; }
    /// <summary>SHA-256 of the one-time code sent over WhatsApp (never stored in clear).</summary>
    [MaxLength(64)] public string? OtpHash { get; set; }
    public DateTime? OtpExpiresAt { get; set; }
    public int OtpAttempts { get; set; }
    public DateTime? OtpSentAt { get; set; }
}

public class ContractSignature
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public int PartyId { get; set; }
    public ContractParty? Party { get; set; }
    public DateTime SignedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? Document { get; set; }
    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(64)] public string? Ip { get; set; }
    [MaxLength(500)] public string? UserAgent { get; set; }
    [MaxLength(64)] public string DocumentHash { get; set; } = "";
    [MaxLength(64)] public string SignatureHash { get; set; } = "";
    public bool AcceptedTerms { get; set; }
    public string AcceptedTermsText { get; set; } = "";
    /// <summary>E.g. WHATSAPP_OTP when the signer proved control of the phone with a one-time code.</summary>
    [MaxLength(30)] public string? VerificationMethod { get; set; }
    [MaxLength(40)] public string? VerifiedPhone { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

public class ContractEvent
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public int? PartyId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(50)] public string Type { get; set; } = "";
    [MaxLength(64)] public string? Ip { get; set; }
    [MaxLength(500)] public string? UserAgent { get; set; }
    public string? Details { get; set; }
}
