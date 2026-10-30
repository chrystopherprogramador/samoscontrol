using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public static class DependencyInjection
{
    public static string ResolveConnectionString(IConfiguration cfg)
    {
        var raw = FirstNonEmpty(cfg["DATABASE_CONNECTION_STRING"], cfg["DATABASE_URL"], cfg.GetConnectionString("Default"));
        if (raw == null)
            throw new InvalidOperationException("DATABASE_CONNECTION_STRING não configurada. Defina-a no serviço (ex.: Host=<host-do-postgres>;Port=5432;Database=samos;Username=samos;Password=...) ou use uma URL postgres://usuario:senha@host:5432/banco.");
        raw = raw.Trim().Trim('"');
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(raw);
            var info = u.UserInfo.Split(':', 2);
            return new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = u.Host, Port = u.Port > 0 ? u.Port : 5432,
                Database = u.AbsolutePath.Trim('/'),
                Username = Uri.UnescapeDataString(info[0]),
                Password = info.Length > 1 ? Uri.UnescapeDataString(info[1]) : null
            }.ConnectionString;
        }
        return raw;
    }

    private static string? FirstNonEmpty(params string?[] v) => v.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    public static IServiceCollection AddSamosApplication(this IServiceCollection s, IConfiguration cfg, bool runWorkers, bool configureDb = true)
    {
        AppTime.Configure(cfg["TIMEZONE"] ?? "America/Sao_Paulo");
        if (configureDb)
            s.AddDbContext<AppDbContext>(o => o.UseNpgsql(ResolveConnectionString(cfg)));

        s.AddMemoryCache();
        s.AddHttpClient();
        s.AddHttpClient("monitor");
        s.TryAddScoped<ICurrentUser, SystemUser>();
        s.TryAddSingleton<INotificationPublisher, NullNotificationPublisher>();

        s.AddSingleton<SettingsService>();
        s.AddSingleton<ISettingsReader>(sp => sp.GetRequiredService<SettingsService>());
        s.AddSingleton<FileStorage>();
        s.AddSingleton<PermissionService>();

        s.AddSingleton<IGoogleMapsScraperIntegration, GoogleMapsScraperIntegration>();
        s.AddSingleton<IAsaasIntegration, AsaasIntegration>();
        s.AddSingleton<IEfiIntegration, EfiIntegration>();
        s.AddSingleton<IEvolutionApiIntegration, EvolutionApiIntegration>();
        s.AddSingleton<IEvolutionInstances, EvolutionInstances>();
        s.AddSingleton<IAiProvider, OllamaProvider>();

        s.AddScoped<AuditService>();
        s.AddScoped<NotificationService>();
        s.AddScoped<AuthService>();
        s.AddScoped<LeadService>();
        s.AddScoped<LeadSyncService>();
        s.AddScoped<ContractBillingService>();
        s.AddScoped<ContractService>();
        s.AddScoped<CommissionService>();
        s.AddScoped<BillingService>();
        s.AddScoped<WhatsAppService>();
        s.AddScoped<MonitoringService>();
        s.AddScoped<DashboardService>();
        s.AddScoped<AutomationService>();
        s.AddScoped<SamosAgent>();
        s.AddScoped<PersonalService>();
        s.AddScoped<MapService>();
        s.AddScoped<KnowledgeService>();
        s.AddScoped<SdrService>();
        foreach (var t in new[]
        {
            typeof(QueryLeadsTool), typeof(QueryCustomersTool), typeof(QueryChargesTool), typeof(QueryContractsTool), typeof(QueryServersTool),
            typeof(QueryServicesTool), typeof(CompanySummaryTool), typeof(CreateJobTool), typeof(SyncLeadsTool), typeof(CreateLeadSearchTool),
            typeof(CreateChargeTool), typeof(SendWhatsAppTool),
            typeof(AddTaskTool), typeof(ListTasksTool), typeof(CompleteTaskTool), typeof(RescheduleTaskTool), typeof(DeleteTaskTool),
            typeof(AddNoteTool), typeof(SearchNotesTool), typeof(DeleteNoteTool), typeof(AddExpenseTool), typeof(ExpenseSummaryTool)
        }) s.AddScoped(typeof(IAgentTool), t);

        if (runWorkers)
        {
            s.AddHostedService<SchedulerWorker>();
            s.AddHostedService<MonitoringWorker>();
        }
        return s;
    }

    /// <summary>Persist DataProtection keys (used to encrypt secrets in the database) in STORAGE_PATH.</summary>
    public static IServiceCollection AddSamosDataProtection(this IServiceCollection s, IConfiguration cfg)
    {
        var root = cfg["STORAGE_PATH"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "storage");
        s.AddDataProtection().SetApplicationName("SamosControl").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
        return s;
    }
}
