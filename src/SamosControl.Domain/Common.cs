namespace SamosControl.Domain;

public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum NotificationType { Info, Warning, Error, Success }
public enum NotificationSource { Crm, Contract, Billing, Monitoring, Ai, Automation, System }

public class Notification : BaseEntity
{
    public NotificationType Type { get; set; }
    public NotificationSource Source { get; set; }
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public string? Link { get; set; }
    /// <summary>Null = visible to all users with access.</summary>
    public int? UserId { get; set; }
}

public class NotificationRead
{
    public int NotificationId { get; set; }
    public int UserId { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public string? UserName { get; set; }
    public string Module { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? OldData { get; set; }
    public string? NewData { get; set; }
    public string? Ip { get; set; }
}

public class AppSetting
{
    public string Key { get; set; } = "";
    /// <summary>Plain text, or DataProtection-encrypted when <see cref="IsSecret"/>.</summary>
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum WebhookStatus { Received, Processed, Ignored, Failed }

/// <summary>Raw webhook storage; unique (Provider, ExternalId) gives idempotency.</summary>
public class WebhookEvent : BaseEntity
{
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public WebhookStatus Status { get; set; } = WebhookStatus.Received;
    public string? Error { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
