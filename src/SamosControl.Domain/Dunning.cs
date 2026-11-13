using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

/// <summary>Aviso de cobrança enviado (ou tentado) ao cliente. (ChargeId, Rule, Channel) é único: garante que cada regra dispara uma vez por canal.</summary>
public class ChargeNotice : BaseEntity
{
    public int ChargeId { get; set; }
    public Charge? Charge { get; set; }
    /// <summary>Regra que disparou: before:N, due, after:N ou manual:yyyyMMddHHmm.</summary>
    [MaxLength(30)] public string Rule { get; set; } = "";
    /// <summary>WHATSAPP ou EMAIL.</summary>
    [MaxLength(10)] public string Channel { get; set; } = "";
    public bool Success { get; set; }
    public int Attempts { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    public DateTime? SentAt { get; set; }
}
