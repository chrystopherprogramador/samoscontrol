using System.ComponentModel.DataAnnotations;

namespace SamosControl.Domain;

// ---------- Billing ----------
public enum ChargeStatus { Pendente, Pago, Vencido, Cancelado, Estornado }
public enum BillingType { Pix, Boleto }

public class Charge : BaseEntity
{
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? ContractId { get; set; }
    [MaxLength(200)] public string? ServiceName { get; set; }
    public decimal Amount { get; set; }
    public DateTime DueDate { get; set; }
    public ChargeStatus Status { get; set; } = ChargeStatus.Pendente;
    public BillingType BillingType { get; set; } = BillingType.Pix;
    [MaxLength(60)] public string? AsaasId { get; set; }
    /// <summary>Gateway that issued the charge (ASAAS or EFI). AsaasId holds the gateway's external id (Efí: txid for Pix, charge_id for boleto).</summary>
    [MaxLength(10)] public string Provider { get; set; } = "ASAAS";
    [MaxLength(500)] public string? InvoiceUrl { get; set; }
    public string? PixPayload { get; set; }
    public string? PixQrCodeBase64 { get; set; }
    public DateTime? PaidAt { get; set; }
    public decimal? PaidAmount { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public int? CreatedByUserId { get; set; }
    /// <summary>AVULSA (manual), IMPLANTACAO or MENSALIDADE. With ContractId + Sequence it makes contract billing idempotent.</summary>
    [MaxLength(20)] public string Kind { get; set; } = "AVULSA";
    public int Sequence { get; set; }
}

// ---------- WhatsApp ----------
public class WhatsAppConversation : BaseEntity
{
    /// <summary>Digits only (E.164 without +), e.g. 5562999999999.</summary>
    [Required, MaxLength(30)] public string Phone { get; set; } = "";
    [MaxLength(200)] public string? ContactName { get; set; }
    public int? LeadId { get; set; }
    public int? CustomerId { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    /// <summary>null = the company number; otherwise the seller whose own WhatsApp holds this chat.</summary>
    public int? SellerId { get; set; }
    [MaxLength(200)] public string? LastMessagePreview { get; set; }
    [MaxLength(600)] public string? PictureUrl { get; set; }
    public DateTime? PictureCheckedAt { get; set; }
    public List<WhatsAppMessage> Messages { get; set; } = new();
}

/// <summary>Quick-reply template. SellerId null = shared by everyone.</summary>
public class WhatsAppTemplate : BaseEntity
{
    public int? SellerId { get; set; }
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    [Required, MaxLength(2000)] public string Body { get; set; } = "";
}

public enum MessageDirection { In, Out }

public class WhatsAppMessage : BaseEntity
{
    public int ConversationId { get; set; }
    public WhatsAppConversation? Conversation { get; set; }
    [MaxLength(150)] public string? ExternalId { get; set; }
    public MessageDirection Direction { get; set; }
    [MaxLength(30)] public string Type { get; set; } = "text";
    public string? Body { get; set; }
    [MaxLength(30)] public string Status { get; set; } = "RECEBIDA";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int? SentByUserId { get; set; }
    public bool FromAgent { get; set; }
}

// ---------- AI ----------
public enum AiActionStatus { Executada, AguardandoConfirmacao, Confirmada, Cancelada, Expirada, Negada, Falhou }

/// <summary>Audit of every tool invocation by SamosAgent (also acts as pending-confirmation store).</summary>
public class AiAction : BaseEntity
{
    public int? UserId { get; set; }
    [MaxLength(20)] public string Channel { get; set; } = "WEB";
    [MaxLength(30)] public string? Phone { get; set; }
    public string UserMessage { get; set; } = "";
    [MaxLength(80)] public string Tool { get; set; } = "";
    public string? ArgsJson { get; set; }
    public AiActionStatus Status { get; set; }
    public bool RequiresConfirmation { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Result { get; set; }
}

public class AiMessage : BaseEntity
{
    public int? UserId { get; set; }
    [MaxLength(20)] public string Channel { get; set; } = "WEB";
    [MaxLength(10)] public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

// ---------- Automation ----------
public enum JobType { SyncLeads, SendWhatsApp, CheckServices, GenerateReport, Reminder, CheckBilling }
public enum ExecutionStatus { Running, Success, Failed }

public class AutomationJob : BaseEntity
{
    [Required, MaxLength(150)] public string Name { get; set; } = "";
    public JobType Type { get; set; }
    /// <summary>Type-specific JSON configuration.</summary>
    public string? ConfigJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>Recurrence in minutes; null = run once.</summary>
    public int? RecurrenceMinutes { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public ExecutionStatus? LastStatus { get; set; }
    public int? CreatedByUserId { get; set; }
    public bool IsSystem { get; set; }
}

public class AutomationExecution
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public AutomationJob? Job { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Running;
    [MaxLength(20)] public string Trigger { get; set; } = "SCHEDULE";
    public string? Result { get; set; }
    public List<AutomationLog> Logs { get; set; } = new();
}

public class AutomationLog
{
    public long Id { get; set; }
    public int ExecutionId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(10)] public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
}

// ---------- Monitoring ----------
public enum ServiceKind { Http, Https, Tcp }
public enum ServiceStatus { Unknown, Online, Degraded, Offline }

public class Server : BaseEntity
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Required, MaxLength(200)] public string Host { get; set; } = "";
    [MaxLength(50)] public string? Type { get; set; }
    public int? Port { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public List<MonitoredService> Services { get; set; } = new();
}

public class MonitoredService : BaseEntity
{
    public int ServerId { get; set; }
    public Server? Server { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public ServiceKind Kind { get; set; } = ServiceKind.Https;
    /// <summary>Full URL for HTTP/HTTPS; ignored for TCP.</summary>
    [MaxLength(500)] public string? Url { get; set; }
    public int? Port { get; set; }
    public int ExpectedStatus { get; set; } = 200;
    public int TimeoutMs { get; set; } = 10000;
    public int DegradedLatencyMs { get; set; } = 2000;
    public int IntervalSeconds { get; set; } = 60;
    public int FailureThreshold { get; set; } = 2;
    public bool IsEnabled { get; set; } = true;
    public ServiceStatus Status { get; set; } = ServiceStatus.Unknown;
    public DateTime? LastCheckAt { get; set; }
    public int? LastLatencyMs { get; set; }
    public DateTime? SslExpiresAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastAlertAt { get; set; }
}

public class ServiceCheck
{
    public long Id { get; set; }
    public int ServiceId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public ServiceStatus Status { get; set; }
    public int? StatusCode { get; set; }
    public int? LatencyMs { get; set; }
    public DateTime? SslExpiresAt { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
}

public class Incident
{
    public int Id { get; set; }
    public int ServiceId { get; set; }
    public MonitoredService? Service { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public DateTime? AlertSentAt { get; set; }
    public DateTime? RecoveryAlertSentAt { get; set; }
}
