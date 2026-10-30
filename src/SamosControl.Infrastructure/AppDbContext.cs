using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;

namespace SamosControl.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadSource> LeadSources => Set<LeadSource>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<LeadTag> LeadTags => Set<LeadTag>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<LeadSyncRun> LeadSyncRuns => Set<LeadSyncRun>();
    public DbSet<ScraperSearch> ScraperSearches => Set<ScraperSearch>();
    public DbSet<ContractTemplate> ContractTemplates => Set<ContractTemplate>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractParty> ContractParties => Set<ContractParty>();
    public DbSet<ContractSignature> ContractSignatures => Set<ContractSignature>();
    public DbSet<ContractEvent> ContractEvents => Set<ContractEvent>();
    public DbSet<Charge> Charges => Set<Charge>();
    public DbSet<Seller> Sellers => Set<Seller>();
    public DbSet<Commission> Commissions => Set<Commission>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<WhatsAppConversation> WhatsAppConversations => Set<WhatsAppConversation>();
    public DbSet<WhatsAppMessage> WhatsAppMessages => Set<WhatsAppMessage>();
    public DbSet<WhatsAppTemplate> WhatsAppTemplates => Set<WhatsAppTemplate>();
    public DbSet<AiAction> AiActions => Set<AiAction>();
    public DbSet<AiMessage> AiMessages => Set<AiMessage>();
    public DbSet<PersonalTask> PersonalTasks => Set<PersonalTask>();
    public DbSet<PersonalNote> PersonalNotes => Set<PersonalNote>();
    public DbSet<PersonalExpense> PersonalExpenses => Set<PersonalExpense>();
    public DbSet<SdrCampaign> SdrCampaigns => Set<SdrCampaign>();
    public DbSet<SdrContact> SdrContacts => Set<SdrContact>();
    public DbSet<AiKnowledge> AiKnowledge => Set<AiKnowledge>();
    public DbSet<AutomationJob> AutomationJobs => Set<AutomationJob>();
    public DbSet<AutomationExecution> AutomationExecutions => Set<AutomationExecution>();
    public DbSet<AutomationLog> AutomationLogs => Set<AutomationLog>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<MonitoredService> Services => Set<MonitoredService>();
    public DbSet<ServiceCheck> ServiceChecks => Set<ServiceCheck>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationRead> NotificationReads => Set<NotificationRead>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(80);
        });

        b.Entity<Lead>(e =>
        {
            e.HasIndex(x => x.GooglePlaceId).IsUnique().HasFilter("\"GooglePlaceId\" IS NOT NULL");
            e.HasIndex(x => x.PhoneKey);
            e.HasIndex(x => x.WebsiteKey);
            e.HasIndex(x => x.NameAddressKey);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Interactions).WithOne().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<LeadSource>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        b.Entity<LeadTag>(e =>
        {
            e.HasKey(x => new { x.LeadId, x.TagId });
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Customer>(e =>
        {
            e.HasIndex(x => x.Document);
            e.HasIndex(x => x.AsaasCustomerId);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Contacts).WithOne().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Interaction>(e =>
        {
            e.HasIndex(x => x.LeadId);
            e.HasIndex(x => x.CustomerId);
        });
        b.Entity<Note>().HasIndex(x => x.LeadId);

        b.Entity<Contract>(e =>
        {
            e.HasIndex(x => x.Status);
            e.Property(x => x.Value).HasPrecision(14, 2);
            e.Property(x => x.ImplementationValue).HasPrecision(14, 2);
            e.Property(x => x.DiscountValue).HasPrecision(14, 2);
            e.Property(x => x.MonthlyValue).HasPrecision(14, 2);
            e.HasIndex(x => x.VerificationCode).IsUnique().HasFilter("\"VerificationCode\" IS NOT NULL");
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Parties).WithOne(x => x.Contract).HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ContractParty>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.Signature).WithOne(x => x.Party).HasForeignKey<ContractSignature>(x => x.PartyId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ContractSignature>().HasIndex(x => x.PartyId).IsUnique();
        b.Entity<ContractEvent>().HasIndex(x => x.ContractId);

        b.Entity<Charge>(e =>
        {
            e.HasIndex(x => x.AsaasId).IsUnique().HasFilter("\"AsaasId\" IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.DueDate });
            e.HasIndex(x => new { x.ContractId, x.Kind, x.Sequence }).IsUnique().HasFilter("\"Kind\" <> 'AVULSA'");
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.PaidAmount).HasPrecision(14, 2);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Seller>(e =>
        {
            e.Property(x => x.ImplementationPercent).HasPrecision(5, 2);
            e.Property(x => x.MonthlyPercent).HasPrecision(5, 2);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.WaInstance).IsUnique().HasFilter("\"WaInstance\" IS NOT NULL");
        });
        b.Entity<Commission>(e =>
        {
            e.HasIndex(x => x.ChargeId).IsUnique();
            e.HasIndex(x => new { x.SellerId, x.Status });
            e.Property(x => x.BaseAmount).HasPrecision(14, 2);
            e.Property(x => x.Percent).HasPrecision(5, 2);
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.HasOne(x => x.Seller).WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Contract>().Property(x => x.ImplementationCommissionPercent).HasPrecision(5, 2);
        b.Entity<Contract>().Property(x => x.MonthlyCommissionPercent).HasPrecision(5, 2);
        b.Entity<WebhookEvent>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(30);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.EventType).HasMaxLength(100);
        });
        b.Entity<WhatsAppConversation>(e =>
        {
            e.HasIndex(x => x.Phone).IsUnique().HasFilter("\"SellerId\" IS NULL");
            e.HasIndex(x => new { x.SellerId, x.Phone }).IsUnique().HasFilter("\"SellerId\" IS NOT NULL");
            e.HasMany(x => x.Messages).WithOne(x => x.Conversation).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WhatsAppMessage>(e =>
        {
            e.HasIndex(x => x.ExternalId);
            e.HasIndex(x => new { x.ConversationId, x.ExternalId }).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
            e.HasIndex(x => new { x.ConversationId, x.Timestamp });
        });
        b.Entity<AiAction>(e => { e.HasIndex(x => x.CreatedAt); e.HasIndex(x => new { x.Phone, x.Status }); });
        b.Entity<AiMessage>().HasIndex(x => new { x.UserId, x.Channel, x.CreatedAt });
        b.Entity<PersonalTask>().HasIndex(x => new { x.UserId, x.Done });
        b.Entity<PersonalTask>().HasIndex(x => x.RemindAt);
        b.Entity<PersonalNote>().HasIndex(x => x.UserId);
        b.Entity<PersonalExpense>().HasIndex(x => new { x.UserId, x.Date });
        b.Entity<PersonalExpense>().Property(x => x.Amount).HasPrecision(14, 2);
        b.Entity<SdrContact>().HasIndex(x => new { x.CampaignId, x.LeadId }).IsUnique();
        b.Entity<SdrContact>().HasIndex(x => x.PhoneKey);
        b.Entity<SdrContact>().HasIndex(x => new { x.CampaignId, x.Stage, x.NextActionAt });
        b.Entity<AiKnowledge>().HasIndex(x => x.Status);

        b.Entity<AutomationJob>(e => { e.HasIndex(x => new { x.IsEnabled, x.NextRunAt }); });
        b.Entity<AutomationExecution>(e =>
        {
            e.HasIndex(x => x.JobId);
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Logs).WithOne().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MonitoredService>(e =>
        {
            e.ToTable("MonitoredServices");
            e.HasOne(x => x.Server).WithMany(x => x.Services).HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ServiceCheck>(e => { e.HasIndex(x => new { x.ServiceId, x.At }); });
        b.Entity<Incident>(e =>
        {
            e.HasIndex(x => new { x.ServiceId, x.ResolvedAt });
            e.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Notification>(e => e.HasIndex(x => x.CreatedAt));
        b.Entity<NotificationRead>(e => e.HasKey(x => new { x.NotificationId, x.UserId }));
        b.Entity<AuditLog>(e => { e.HasIndex(x => x.At); e.HasIndex(x => x.Module); });
        b.Entity<AppSetting>(e => { e.HasKey(x => x.Key); e.Property(x => x.Key).HasMaxLength(100); });

        // Store enums as strings for readable data and safe reordering.
        foreach (var et in b.Model.GetEntityTypes())
            foreach (var p in et.ClrType.GetProperties().Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).IsEnum))
                b.Entity(et.ClrType).Property(p.Name).HasConversion<string>().HasMaxLength(40);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Touch();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Touch();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Touch()
    {
        foreach (var e in ChangeTracker.Entries<BaseEntity>().Where(e => e.State == EntityState.Modified))
            e.Entity.UpdatedAt = DateTime.UtcNow;
    }
}
