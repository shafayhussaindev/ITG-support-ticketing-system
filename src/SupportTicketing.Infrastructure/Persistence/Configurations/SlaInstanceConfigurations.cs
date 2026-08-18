using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Escalations;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Sla;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class TicketSlaInstanceConfiguration : IEntityTypeConfiguration<TicketSlaInstance>
{
    public void Configure(EntityTypeBuilder<TicketSlaInstance> builder)
    {
        builder.ToTable("TicketSlaInstances", t =>
            t.HasCheckConstraint("CK_TicketSlaInstances_Paused", "[TotalPausedMinutes] >= 0"));

        builder.Ignore(i => i.IsPaused);
        builder.Ignore(i => i.IsResolutionSettled);

        // One live clock per ticket. Two would mean two different promises.
        builder.HasIndex(i => i.TicketId)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_TicketSlaInstances_Ticket");

        // The sweep query: unsettled clocks ordered by how close they are to breaching.
        builder.HasIndex(i => new { i.OrganizationId, i.ResolutionState, i.ResolutionDueAtUtc })
            .HasDatabaseName("IX_TicketSlaInstances_Org_State_Due");

        builder.HasIndex(i => new { i.ResponseState, i.ResponseDueAtUtc })
            .HasDatabaseName("IX_TicketSlaInstances_ResponseState_Due");

        builder.HasOne(i => i.Policy).WithMany()
            .HasForeignKey(i => i.PolicyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SlaEventConfiguration : IEntityTypeConfiguration<SlaEvent>
{
    public void Configure(EntityTypeBuilder<SlaEvent> builder)
    {
        builder.ToTable("SlaEvents");

        builder.Property(e => e.Detail).HasMaxLength(1000);

        // This index is the idempotency mechanism, not merely an optimisation. The
        // background worker attempts to insert an event and relies on the unique
        // violation to tell it the work was already done by an earlier pass.
        builder.HasIndex(e => new { e.SlaInstanceId, e.EventType, e.Level })
            .IsUnique()
            .HasDatabaseName("UX_SlaEvents_Instance_Type_Level");

        builder.HasIndex(e => new { e.TicketId, e.OccurredAtUtc })
            .HasDatabaseName("IX_SlaEvents_Ticket_OccurredAt");

        builder.HasOne(e => e.SlaInstance).WithMany()
            .HasForeignKey(e => e.SlaInstanceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EscalationPolicyConfiguration : IEntityTypeConfiguration<EscalationPolicy>
{
    public void Configure(EntityTypeBuilder<EscalationPolicy> builder)
    {
        builder.ToTable("EscalationPolicies");

        builder.Property(p => p.Name).HasMaxLength(150).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(1000);

        builder.Ignore(p => p.Precedence);

        builder.HasIndex(p => p.OrganizationId)
            .IsUnique()
            .HasFilter("[IsDefault] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("UX_EscalationPolicies_Org_SingleDefault");

        builder.HasMany(p => p.Steps).WithOne(s => s.Policy!)
            .HasForeignKey(s => s.PolicyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EscalationStepConfiguration : IEntityTypeConfiguration<EscalationStep>
{
    public void Configure(EntityTypeBuilder<EscalationStep> builder)
    {
        builder.ToTable("EscalationSteps", t =>
        {
            t.HasCheckConstraint("CK_EscalationSteps_Level", "[Level] > 0");
            // Above 100 is deliberate: a rung at 120 chases an already-breached ticket.
            t.HasCheckConstraint("CK_EscalationSteps_Threshold", "[ThresholdPercent] BETWEEN 1 AND 1000");
        });

        builder.Property(s => s.MessageTemplate).HasMaxLength(1000);

        builder.HasIndex(s => new { s.PolicyId, s.Level })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_EscalationSteps_Policy_Level");

        builder.HasOne(s => s.RecipientUser).WithMany()
            .HasForeignKey(s => s.RecipientUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.RecipientTeam).WithMany()
            .HasForeignKey(s => s.RecipientTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EscalationHistoryConfiguration : IEntityTypeConfiguration<EscalationHistory>
{
    public void Configure(EntityTypeBuilder<EscalationHistory> builder)
    {
        builder.ToTable("EscalationHistory");

        builder.Property(h => h.Reason).HasMaxLength(1000);

        // Same idempotency contract as SLA events: one escalation per ticket per rung.
        builder.HasIndex(h => new { h.TicketId, h.Level })
            .IsUnique()
            .HasDatabaseName("UX_EscalationHistory_Ticket_Level");

        builder.HasIndex(h => new { h.OrganizationId, h.State, h.RaisedAtUtc })
            .HasDatabaseName("IX_EscalationHistory_Org_State_Raised");

        builder.HasIndex(h => new { h.RecipientUserId, h.State })
            .HasDatabaseName("IX_EscalationHistory_Recipient_State");
    }
}
