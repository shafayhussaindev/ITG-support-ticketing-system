using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Sla;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class BusinessCalendarConfiguration : IEntityTypeConfiguration<BusinessCalendar>
{
    public void Configure(EntityTypeBuilder<BusinessCalendar> builder)
    {
        builder.ToTable("BusinessCalendars");

        builder.Property(c => c.Name).HasMaxLength(150).IsRequired();
        builder.Property(c => c.Code).HasMaxLength(30).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(500);
        builder.Property(c => c.TimeZoneId).HasMaxLength(100).IsRequired();

        builder.HasIndex(c => new { c.OrganizationId, c.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_BusinessCalendars_Org_Code");

        builder.HasMany(c => c.Hours).WithOne(h => h.Calendar!)
            .HasForeignKey(h => h.CalendarId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(c => c.Holidays).WithOne(h => h.Calendar!)
            .HasForeignKey(h => h.CalendarId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BusinessHourConfiguration : IEntityTypeConfiguration<BusinessHour>
{
    public void Configure(EntityTypeBuilder<BusinessHour> builder)
    {
        // A window that ends before it starts is not a short day, it is corrupt data
        // that would silently subtract working time. Rejected at the database.
        builder.ToTable("BusinessHours", t => t.HasCheckConstraint(
            "CK_BusinessHours_Window",
            "[StartMinute] >= 0 AND [EndMinute] <= 1440 AND [EndMinute] > [StartMinute]"));

        builder.HasIndex(h => new { h.CalendarId, h.DayOfWeek })
            .HasDatabaseName("IX_BusinessHours_Calendar_Day");
    }
}

public sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.ToTable("Holidays");

        builder.Property(h => h.Name).HasMaxLength(150).IsRequired();

        builder.HasIndex(h => new { h.CalendarId, h.DateUtc })
            .HasDatabaseName("IX_Holidays_Calendar_Date");
    }
}

public sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.ToTable("SlaPolicies");

        builder.Property(p => p.Name).HasMaxLength(150).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(1000);

        builder.Ignore(p => p.Precedence);

        // At most one default per organization, or policy selection becomes arbitrary.
        builder.HasIndex(p => p.OrganizationId)
            .IsUnique()
            .HasFilter("[IsDefault] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("UX_SlaPolicies_Org_SingleDefault");

        builder.HasIndex(p => new { p.OrganizationId, p.IsActive })
            .HasDatabaseName("IX_SlaPolicies_Org_Active");

        builder.HasOne(p => p.BusinessCalendar).WithMany()
            .HasForeignKey(p => p.BusinessCalendarId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Targets).WithOne(t => t.Policy!)
            .HasForeignKey(t => t.PolicyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SlaTargetConfiguration : IEntityTypeConfiguration<SlaTarget>
{
    public void Configure(EntityTypeBuilder<SlaTarget> builder)
    {
        builder.ToTable("SlaTargets", t =>
        {
            t.HasCheckConstraint("CK_SlaTargets_Response", "[ResponseMinutes] > 0");
            t.HasCheckConstraint("CK_SlaTargets_Resolution", "[ResolutionMinutes] > 0");
            t.HasCheckConstraint("CK_SlaTargets_Warning", "[WarningThresholdPercent] BETWEEN 1 AND 100");
        });

        // One target per priority per policy, so the engine cannot find two answers.
        builder.HasIndex(t => new { t.PolicyId, t.Priority })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_SlaTargets_Policy_Priority");
    }
}
