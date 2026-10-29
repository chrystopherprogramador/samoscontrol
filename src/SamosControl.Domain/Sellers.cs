using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

/// <summary>Sales representative. Clients closed by the seller form their wallet (carteira) and generate commissions when they pay.</summary>
public class Seller : BaseEntity
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? Document { get; set; }
    [MaxLength(200)] public string? Email { get; set; }
    /// <summary>WhatsApp used to send the partnership contract.</summary>
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    /// <summary>Optional login: leads assigned to this user are linked to the seller automatically.</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }
    [MaxLength(200)] public string? PixKey { get; set; }
    [MaxLength(20)] public string? PixKeyType { get; set; }
    /// <summary>Percentage of the implementation amount actually received (0-100).</summary>
    public decimal ImplementationPercent { get; set; }
    /// <summary>Percentage of each monthly amount actually received (0-100).</summary>
    public decimal MonthlyPercent { get; set; }
    /// <summary>Monthly commissions stop after this many paid months per client; null = while the contract lasts.</summary>
    public int? MonthlyCommissionMonths { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(2000)] public string? Notes { get; set; }
    public int? PartnerContractId { get; set; }

    /// <summary>Evolution instance dedicated to this seller's own WhatsApp number (created automatically).</summary>
    [MaxLength(80)] public string? WaInstance { get; set; }
    /// <summary>DESCONECTADO, CONECTANDO or CONECTADO.</summary>
    [MaxLength(20)] public string WaStatus { get; set; } = "DESCONECTADO";
    [MaxLength(30)] public string? WaNumber { get; set; }
    [MaxLength(200)] public string? WaProfileName { get; set; }
    public DateTime? WaConnectedAt { get; set; }
}

public enum CommissionStatus { Pendente, EmProcessamento, Paga, Erro, Cancelada }

public class Commission : BaseEntity
{
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ContractId { get; set; }
    /// <summary>One commission per paid charge (unique) so processing is idempotent.</summary>
    public int ChargeId { get; set; }
    [MaxLength(20)] public string Kind { get; set; } = "";
    public int Sequence { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal Percent { get; set; }
    public decimal Amount { get; set; }
    public CommissionStatus Status { get; set; } = CommissionStatus.Pendente;
    [MaxLength(200)] public string? PixKey { get; set; }
    /// <summary>Idempotency id sent to the bank (Efí idEnvio).</summary>
    [MaxLength(40)] public string? PayoutId { get; set; }
    [MaxLength(60)] public string? PayoutReference { get; set; }
    public int Attempts { get; set; }
    public DateTime? PaidAt { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
}
