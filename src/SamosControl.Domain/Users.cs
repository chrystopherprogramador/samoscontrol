namespace SamosControl.Domain;

public class User : BaseEntity
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetExpires { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }
}

public class Role : BaseEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = new();
}

public class RolePermission
{
    public int RoleId { get; set; }
    public string Permission { get; set; } = "";
}

public static class Perm
{
    public const string DashboardView = "dashboard.view";
    public const string CrmView = "crm.view";
    public const string CrmEdit = "crm.edit";
    public const string CrmDelete = "crm.delete";
    public const string ContractsView = "contracts.view";
    public const string ContractsCreate = "contracts.create";
    public const string ContractsSign = "contracts.sign";
    public const string ContractsTemplates = "contracts.templates";
    public const string BillingView = "billing.view";
    public const string BillingCreate = "billing.create";
    public const string BillingCancel = "billing.cancel";
    public const string WhatsAppView = "whatsapp.view";
    public const string WhatsAppSend = "whatsapp.send";
    public const string WhatsAppAll = "whatsapp.all";
    public const string WhatsAppManage = "whatsapp.manage";
    public const string AiUse = "ai.use";
    public const string AiHistory = "ai.history";
    public const string AutomationView = "automation.view";
    public const string AutomationManage = "automation.manage";
    public const string MonitoringView = "monitoring.view";
    public const string MonitoringManage = "monitoring.manage";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string AuditView = "audit.view";
    public const string SettingsManage = "settings.manage";
    public const string SellersView = "sellers.view";
    public const string SellersManage = "sellers.manage";
    public const string CommissionsPay = "commissions.pay";

    public static readonly string[] All =
    {
        DashboardView, CrmView, CrmEdit, CrmDelete, ContractsView, ContractsCreate, ContractsSign, ContractsTemplates,
        BillingView, BillingCreate, BillingCancel, WhatsAppView, WhatsAppSend, WhatsAppAll, WhatsAppManage, AiUse, AiHistory,
        AutomationView, AutomationManage, MonitoringView, MonitoringManage,
        UsersManage, RolesManage, AuditView, SettingsManage, SellersView, SellersManage, CommissionsPay
    };

    public static Dictionary<string, string[]> DefaultRoles => new()
    {
        ["ADMIN"] = All,
        ["GESTOR"] = All.Where(p => p is not (UsersManage or RolesManage or SettingsManage)).ToArray(),
        ["COMERCIAL"] = new[] { DashboardView, CrmView, CrmEdit, ContractsView, ContractsCreate, ContractsSign, WhatsAppView, WhatsAppSend, AiUse, SellersView },
        ["FINANCEIRO"] = new[] { DashboardView, CrmView, ContractsView, BillingView, BillingCreate, BillingCancel, AiUse, SellersView, CommissionsPay },
        ["SUPORTE"] = new[] { DashboardView, CrmView, CrmEdit, WhatsAppView, WhatsAppSend, MonitoringView },
        ["OPERADOR"] = new[] { DashboardView, CrmView, AutomationView, AutomationManage, MonitoringView, MonitoringManage },
        ["VISUALIZADOR"] = new[] { DashboardView, CrmView, ContractsView, BillingView, MonitoringView, AutomationView },
    };
}
