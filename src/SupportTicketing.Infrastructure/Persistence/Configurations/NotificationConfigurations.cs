using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Notifications;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.Property(n => n.Title).HasMaxLength(200).IsRequired();
        builder.Property(n => n.Body).HasMaxLength(2000).IsRequired();
        builder.Property(n => n.Link).HasMaxLength(500);
        builder.Property(n => n.TicketNumber).HasMaxLength(32);
        builder.Property(n => n.DeduplicationKey).HasMaxLength(200).IsRequired();

        builder.Ignore(n => n.IsRead);

        // The idempotency guarantee for notifications. A background job that runs every
        // minute must not tell the same person the same thing sixty times an hour.
        builder.HasIndex(n => new { n.RecipientUserId, n.DeduplicationKey })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Notifications_Recipient_DedupKey");

        // Drives the bell: unread first, newest first.
        builder.HasIndex(n => new { n.RecipientUserId, n.ReadAtUtc, n.CreatedAtUtc })
            .HasDatabaseName("IX_Notifications_Recipient_Read_Created");

        builder.HasOne(n => n.Recipient).WithMany()
            .HasForeignKey(n => n.RecipientUserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(n => n.Deliveries).WithOne(d => d.Notification!)
            .HasForeignKey(d => d.NotificationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("NotificationDeliveries", t =>
            t.HasCheckConstraint("CK_NotificationDeliveries_Attempts", "[AttemptCount] >= 0"));

        builder.Property(d => d.FailureReason).HasMaxLength(500);

        builder.HasIndex(d => new { d.NotificationId, d.Channel })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_NotificationDeliveries_Notification_Channel");

        // The retry sweep: pending work whose next attempt is due.
        builder.HasIndex(d => new { d.State, d.NextAttemptAtUtc })
            .HasDatabaseName("IX_NotificationDeliveries_State_NextAttempt");
    }
}

public sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences");

        // Absence of a row means the channel default applies, so a new event type
        // reaches people without every user having to opt in first.
        builder.HasIndex(p => new { p.UserId, p.EventType, p.Channel })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_NotificationPreferences_User_Event_Channel");

        builder.HasOne(p => p.User).WithMany()
            .HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
