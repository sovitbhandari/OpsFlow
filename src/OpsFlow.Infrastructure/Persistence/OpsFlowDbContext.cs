using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.Persistence;

public sealed class OpsFlowDbContext(DbContextOptions<OpsFlowDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> Memberships => Set<OrganizationMembership>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderComment> Comments => Set<WorkOrderComment>();
    public DbSet<WorkOrderActivity> Activities => Set<WorkOrderActivity>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<SlaEscalation> SlaEscalations => Set<SlaEscalation>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(256);
            entity.Property(u => u.UserName).HasMaxLength(256);
        });

        builder.Entity<Organization>(entity =>
        {
            entity.ToTable("Organizations");
            entity.Property(o => o.Name).HasMaxLength(200).IsRequired();
            entity.Property(o => o.Slug).HasMaxLength(80).IsRequired();
            entity.HasIndex(o => o.Slug).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_Organizations_Name", "LEN([Name]) > 0"));
        });

        builder.Entity<OrganizationMembership>(entity =>
        {
            entity.ToTable("OrganizationMemberships");
            entity.Property(m => m.Role).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique();
            entity.HasAlternateKey(m => new { m.OrganizationId, m.UserId });
            entity.HasOne(m => m.Organization)
                .WithMany()
                .HasForeignKey(m => m.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_Memberships_Role",
                "[Role] IN (N'Admin', N'Dispatcher', N'Technician', N'Requester')"));
        });

        builder.Entity<SlaPolicy>(entity =>
        {
            entity.ToTable("SlaPolicies");
            entity.Property(p => p.Priority).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(p => new { p.OrganizationId, p.Priority }).IsUnique();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(p => p.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t => t.HasCheckConstraint("CK_SlaPolicies_Duration", "[DurationSeconds] BETWEEN 900 AND 7776000"));
        });

        builder.Entity<WorkOrder>(entity =>
        {
            entity.ToTable("WorkOrders");
            entity.Property(w => w.Title).HasMaxLength(200).IsRequired();
            entity.Property(w => w.Description).HasMaxLength(4000).IsRequired();
            entity.Property(w => w.Category).HasMaxLength(100).IsRequired();
            entity.Property(w => w.Priority).HasConversion<string>().HasMaxLength(32);
            entity.Property(w => w.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(w => w.StatusBeforeHold).HasConversion<string>().HasMaxLength(32);
            entity.Property(w => w.RowVersion).IsRowVersion();
            entity.HasIndex(w => new { w.OrganizationId, w.Number }).IsUnique();
            entity.HasAlternateKey(w => new { w.Id, w.OrganizationId });
            entity.HasIndex(w => new { w.OrganizationId, w.Status, w.CreatedAtUtc, w.Number })
                .HasDatabaseName("IX_WorkOrders_Org_Status_Created_Number")
                .IncludeProperties(w => new { w.Title, w.Priority, w.Category, w.AssignedTechnicianUserId, w.DueAtUtc, w.RequesterUserId });
            entity.HasIndex(w => new { w.OrganizationId, w.AssignedTechnicianUserId, w.Status });
            entity.HasIndex(w => new { w.OrganizationId, w.DueAtUtc });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(w => w.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationMembership>()
                .WithMany()
                .HasForeignKey(w => new { w.OrganizationId, w.RequesterUserId })
                .HasPrincipalKey(m => new { m.OrganizationId, m.UserId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationMembership>()
                .WithMany()
                .HasForeignKey(w => new { w.OrganizationId, w.AssignedTechnicianUserId })
                .HasPrincipalKey(m => new { m.OrganizationId, m.UserId })
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_WorkOrders_Status", "[Status] IN (N'Open', N'Assigned', N'InProgress', N'OnHold', N'Resolved', N'Closed', N'Cancelled')");
                t.HasCheckConstraint("CK_WorkOrders_Priority", "[Priority] IN (N'Low', N'Medium', N'High', N'Critical')");
                t.HasCheckConstraint("CK_WorkOrders_Title", "LEN([Title]) > 0");
            });
        });

        builder.Entity<WorkOrderComment>(entity =>
        {
            entity.ToTable("WorkOrderComments");
            entity.Property(c => c.Body).HasMaxLength(2000).IsRequired();
            entity.HasIndex(c => new { c.OrganizationId, c.WorkOrderId, c.CreatedAtUtc });
            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(c => new { c.WorkOrderId, c.OrganizationId })
                .HasPrincipalKey(w => new { w.Id, w.OrganizationId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WorkOrderActivity>(entity =>
        {
            entity.ToTable("WorkOrderActivities");
            entity.Property(a => a.ActivityType).HasMaxLength(64).IsRequired();
            entity.Property(a => a.Message).HasMaxLength(500).IsRequired();
            entity.HasIndex(a => new { a.OrganizationId, a.WorkOrderId, a.CreatedAtUtc });
            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(a => new { a.WorkOrderId, a.OrganizationId })
                .HasPrincipalKey(w => new { w.Id, w.OrganizationId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Attachment>(entity =>
        {
            entity.ToTable("Attachments");
            entity.Property(a => a.FileName).HasMaxLength(120).IsRequired();
            entity.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(a => a.StorageKey).HasMaxLength(300).IsRequired();
            entity.HasIndex(a => a.StorageKey).IsUnique();
            entity.HasIndex(a => new { a.OrganizationId, a.WorkOrderId });
            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(a => new { a.WorkOrderId, a.OrganizationId })
                .HasPrincipalKey(w => new { w.Id, w.OrganizationId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.Property(a => a.Action).HasMaxLength(80).IsRequired();
            entity.Property(a => a.EntityType).HasMaxLength(80).IsRequired();
            entity.Property(a => a.EntityId).HasMaxLength(64).IsRequired();
            entity.Property(a => a.Summary).HasMaxLength(500).IsRequired();
            entity.HasIndex(a => new { a.OrganizationId, a.CreatedAtUtc });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(a => a.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.Property(m => m.Type).HasMaxLength(80).IsRequired();
            entity.Property(m => m.Payload).HasMaxLength(4000).IsRequired();
            entity.Property(m => m.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(m => m.LastError).HasMaxLength(1000);
            entity.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(m => m.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SlaEscalation>(entity =>
        {
            entity.ToTable("SlaEscalations");
            entity.HasIndex(e => e.WorkOrderId).IsUnique();
            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(e => new { e.WorkOrderId, e.OrganizationId })
                .HasPrincipalKey(w => new { w.Id, w.OrganizationId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("IdempotencyRecords");
            entity.Property(r => r.Operation).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Key).HasMaxLength(128).IsRequired();
            entity.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(r => r.ResponseBody).HasMaxLength(16000);
            entity.HasIndex(r => new { r.OrganizationId, r.UserId, r.Operation, r.Key }).IsUnique();
            entity.HasIndex(r => r.ExpiresAtUtc);
        });
    }
}
