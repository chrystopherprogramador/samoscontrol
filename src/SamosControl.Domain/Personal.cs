using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

/// <summary>Organização pessoal do usuário (tarefas, notas, gastos) operada pelo assistente via WhatsApp ou painel.</summary>
public class PersonalTask : BaseEntity
{
    public int UserId { get; set; }
    [Required, MaxLength(300)] public string Title { get; set; } = "";
    [MaxLength(2000)] public string? Notes { get; set; }
    [MaxLength(60)] public string? Category { get; set; }
    /// <summary>0 = normal, 1 = alta, 2 = urgente.</summary>
    public int Priority { get; set; }
    public DateTime? DueAt { get; set; }
    /// <summary>Quando avisar por WhatsApp/notificação. Nulo = não avisa.</summary>
    public DateTime? RemindAt { get; set; }
    /// <summary>Repete a cada N minutos (1440 = diário, 10080 = semanal) após o aviso.</summary>
    public int? RepeatMinutes { get; set; }
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
}

public class PersonalNote : BaseEntity
{
    public int UserId { get; set; }
    [MaxLength(200)] public string? Title { get; set; }
    [Required, MaxLength(4000)] public string Text { get; set; } = "";
    [MaxLength(60)] public string? Tag { get; set; }
}

public class PersonalExpense : BaseEntity
{
    public int UserId { get; set; }
    public decimal Amount { get; set; }
    public bool IsIncome { get; set; }
    [Required, MaxLength(300)] public string Description { get; set; } = "";
    [MaxLength(60)] public string? Category { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
}
