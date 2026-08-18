using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Auditing;
using SupportTicketing.Domain.Catalog;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.Property(c => c.Name).HasMaxLength(150).IsRequired();
        builder.Property(c => c.Code).HasMaxLength(30).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(1000);

        builder.HasIndex(c => new { c.OrganizationId, c.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Categories_Org_Code");

        builder.HasOne(c => c.DefaultTeam)
            .WithMany()
            .HasForeignKey(c => c.DefaultTeamId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.Subcategories)
            .WithOne(s => s.Category!)
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubcategoryConfiguration : IEntityTypeConfiguration<Subcategory>
{
    public void Configure(EntityTypeBuilder<Subcategory> builder)
    {
        builder.ToTable("Subcategories");

        builder.Property(s => s.Name).HasMaxLength(150).IsRequired();
        builder.Property(s => s.Code).HasMaxLength(30).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(1000);

        builder.HasIndex(s => new { s.CategoryId, s.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Subcategories_Category_Code");
    }
}

public sealed class BusinessApplicationConfiguration : IEntityTypeConfiguration<BusinessApplication>
{
    public void Configure(EntityTypeBuilder<BusinessApplication> builder)
    {
        builder.ToTable("Applications");

        builder.Property(a => a.Name).HasMaxLength(150).IsRequired();
        builder.Property(a => a.Code).HasMaxLength(30).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(1000);
        builder.Property(a => a.Vendor).HasMaxLength(150);
        builder.Property(a => a.Version).HasMaxLength(50);

        builder.HasIndex(a => new { a.OrganizationId, a.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Applications_Org_Code");

        builder.HasOne(a => a.OwningTeam)
            .WithMany()
            .HasForeignKey(a => a.OwningTeamId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.Modules)
            .WithOne(m => m.Application!)
            .HasForeignKey(m => m.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ApplicationModuleConfiguration : IEntityTypeConfiguration<ApplicationModule>
{
    public void Configure(EntityTypeBuilder<ApplicationModule> builder)
    {
        builder.ToTable("ApplicationModules");

        builder.Property(m => m.Name).HasMaxLength(150).IsRequired();
        builder.Property(m => m.Code).HasMaxLength(30).IsRequired();
        builder.Property(m => m.Description).HasMaxLength(1000);

        builder.HasIndex(m => new { m.ApplicationId, m.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_ApplicationModules_App_Code");
    }
}

public sealed class PriorityMatrixEntryConfiguration : IEntityTypeConfiguration<PriorityMatrixEntry>
{
    public void Configure(EntityTypeBuilder<PriorityMatrixEntry> builder)
    {
        builder.ToTable("PriorityMatrixEntries");

        // Exactly one priority per (impact, urgency) pair per organization, so the
        // calculator can never find two conflicting answers.
        builder.HasIndex(p => new { p.OrganizationId, p.Impact, p.Urgency })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_PriorityMatrix_Org_Impact_Urgency");
    }
}

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");

        builder.Property(t => t.Name).HasMaxLength(60).IsRequired();
        builder.Property(t => t.Color).HasMaxLength(9);

        builder.HasIndex(t => new { t.OrganizationId, t.Name })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Tags_Org_Name");
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityReference).HasMaxLength(100);
        builder.Property(a => a.ActorName).HasMaxLength(200);
        builder.Property(a => a.ActorEmail).HasMaxLength(256);
        builder.Property(a => a.Reason).HasMaxLength(1000);
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(512);
        builder.Property(a => a.FailureReason).HasMaxLength(500);

        // The change set is the one genuinely unbounded column here.
        builder.Property(a => a.ChangesJson).HasColumnType("nvarchar(max)");

        // The audit viewer filters by organization and time, so lead with those.
        builder.HasIndex(a => new { a.OrganizationId, a.OccurredAtUtc })
            .HasDatabaseName("IX_AuditLogs_Org_OccurredAt");

        // Reconstructing one ticket's lifecycle.
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAtUtc })
            .HasDatabaseName("IX_AuditLogs_Entity_OccurredAt");

        builder.HasIndex(a => new { a.ActorId, a.OccurredAtUtc })
            .HasDatabaseName("IX_AuditLogs_Actor_OccurredAt");

        builder.HasIndex(a => a.CorrelationId).HasDatabaseName("IX_AuditLogs_CorrelationId");
    }
}

public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("SystemSettings");

        builder.Property(s => s.Key).HasMaxLength(150).IsRequired();
        builder.Property(s => s.Value).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(s => s.ValueType).HasMaxLength(30).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500);
        builder.Property(s => s.Category).HasMaxLength(60);

        // One row per key per organization; the global default has a NULL organization.
        // HasFilter(null) removes the "[OrganizationId] IS NOT NULL" predicate EF adds
        // by default for nullable index columns. Without this override the global rows
        // fall outside the index and duplicate global keys become possible. SQL Server
        // treats NULLs as equal for uniqueness, so the unfiltered index covers them.
        builder.HasIndex(s => new { s.OrganizationId, s.Key })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UX_SystemSettings_Org_Key");
    }
}
