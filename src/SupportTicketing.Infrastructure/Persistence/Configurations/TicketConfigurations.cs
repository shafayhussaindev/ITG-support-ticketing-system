using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

/*
  Delete behaviour across this file is Restrict everywhere.

  Nothing is physically deleted — archivable entities are soft-deleted and history
  tables are append-only — so cascade paths would never fire in normal operation.
  Declaring them anyway would produce SQL Server's "multiple cascade paths" error,
  because a ticket reaches its attachments both directly and through its comments.
  Restrict states the real intent: ticket history is not removable.
*/

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets", t =>
        {
            t.HasCheckConstraint("CK_Tickets_ReopenCount", "[ReopenCount] >= 0");
            t.HasCheckConstraint("CK_Tickets_Impact", "[Impact] BETWEEN 1 AND 4");
            t.HasCheckConstraint("CK_Tickets_Urgency", "[Urgency] BETWEEN 1 AND 4");
            t.HasCheckConstraint("CK_Tickets_Priority", "[Priority] BETWEEN 1 AND 4");
        });

        builder.Property(t => t.TicketNumber).HasMaxLength(32).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(300).IsRequired();
        builder.Property(t => t.Description).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(t => t.ContactEmail).HasMaxLength(256);
        builder.Property(t => t.ContactPhone).HasMaxLength(50);
        builder.Property(t => t.PriorityOverrideReason).HasMaxLength(1000);
        builder.Property(t => t.RootCause).HasColumnType("nvarchar(max)");
        builder.Property(t => t.ResolutionSummary).HasColumnType("nvarchar(max)");
        builder.Property(t => t.WorkPerformed).HasColumnType("nvarchar(max)");
        builder.Property(t => t.CancellationReason).HasMaxLength(1000);

        builder.Ignore(t => t.IsClosed);
        builder.Ignore(t => t.HasFirstResponse);

        // The human-facing identifier must be unique within a tenant. This index is
        // also the backstop that turns a numbering race into a retryable duplicate-key
        // error rather than two tickets sharing a number.
        builder.HasIndex(t => new { t.OrganizationId, t.TicketNumber })
            .IsUnique()
            .HasDatabaseName("UX_Tickets_Org_Number");

        // ---- indexes shaped by the actual list queries ----------------------
        builder.HasIndex(t => new { t.OrganizationId, t.Status, t.CreatedAtUtc })
            .HasDatabaseName("IX_Tickets_Org_Status_Created");

        // The agent queue: "my open work, worst first".
        builder.HasIndex(t => new { t.OrganizationId, t.AssignedAgentId, t.Status, t.Priority })
            .HasDatabaseName("IX_Tickets_Org_Agent_Status_Priority");

        // The team queue, including the unassigned pool.
        builder.HasIndex(t => new { t.OrganizationId, t.AssignedTeamId, t.Status })
            .HasDatabaseName("IX_Tickets_Org_Team_Status");

        // The requester portal.
        builder.HasIndex(t => new { t.OrganizationId, t.RequesterId, t.CreatedAtUtc })
            .HasDatabaseName("IX_Tickets_Org_Requester_Created");

        builder.HasIndex(t => new { t.OrganizationId, t.DepartmentId, t.Status })
            .HasDatabaseName("IX_Tickets_Org_Department_Status");

        builder.HasIndex(t => new { t.OrganizationId, t.CategoryId })
            .HasDatabaseName("IX_Tickets_Org_Category");

        builder.HasOne(t => t.Requester).WithMany()
            .HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.AssignedAgent).WithMany()
            .HasForeignKey(t => t.AssignedAgentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.AssignedTeam).WithMany()
            .HasForeignKey(t => t.AssignedTeamId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Office).WithMany()
            .HasForeignKey(t => t.OfficeId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Department).WithMany()
            .HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Category).WithMany()
            .HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Subcategory).WithMany()
            .HasForeignKey(t => t.SubcategoryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Application).WithMany()
            .HasForeignKey(t => t.ApplicationId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.ApplicationModule).WithMany()
            .HasForeignKey(t => t.ApplicationModuleId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Comments).WithOne(c => c.Ticket!)
            .HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Attachments).WithOne(a => a.Ticket!)
            .HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Assignments).WithOne(a => a.Ticket!)
            .HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.StatusHistory).WithOne(h => h.Ticket!)
            .HasForeignKey(h => h.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.PriorityHistory).WithOne(h => h.Ticket!)
            .HasForeignKey(h => h.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.WorkLogs).WithOne(w => w.Ticket!)
            .HasForeignKey(w => w.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.RelatedRecords).WithOne(r => r.Ticket!)
            .HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Tags).WithOne(t2 => t2.Ticket!)
            .HasForeignKey(t2 => t2.TicketId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("TicketComments");

        builder.Property(c => c.Body).HasColumnType("nvarchar(max)").IsRequired();

        // The conversation view reads a ticket's comments in time order, and the
        // requester-facing variant filters to public replies. Leading with TicketId
        // and including Type lets both use one index.
        builder.HasIndex(c => new { c.TicketId, c.Type, c.CreatedAtUtc })
            .HasDatabaseName("IX_TicketComments_Ticket_Type_Created");

        builder.HasOne(c => c.Author).WithMany()
            .HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.Attachments).WithOne(a => a.Comment!)
            .HasForeignKey(a => a.CommentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.Mentions).WithOne(m => m.Comment!)
            .HasForeignKey(m => m.CommentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketCommentMentionConfiguration : IEntityTypeConfiguration<TicketCommentMention>
{
    public void Configure(EntityTypeBuilder<TicketCommentMention> builder)
    {
        builder.ToTable("TicketCommentMentions");

        builder.HasIndex(m => new { m.CommentId, m.MentionedUserId })
            .IsUnique()
            .HasDatabaseName("UX_TicketCommentMentions_Comment_User");

        // Powers "comments that mention me".
        builder.HasIndex(m => m.MentionedUserId).HasDatabaseName("IX_TicketCommentMentions_User");

        builder.HasOne(m => m.MentionedUser).WithMany()
            .HasForeignKey(m => m.MentionedUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        builder.ToTable("TicketAttachments", t =>
            t.HasCheckConstraint("CK_TicketAttachments_Size", "[SizeBytes] > 0"));

        builder.Property(a => a.OriginalFileName).HasMaxLength(260).IsRequired();
        builder.Property(a => a.StoredFileName).HasMaxLength(120).IsRequired();
        builder.Property(a => a.StoragePath).HasMaxLength(400).IsRequired();
        builder.Property(a => a.DeclaredContentType).HasMaxLength(150);
        builder.Property(a => a.ContentType).HasMaxLength(150).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ScanDetail).HasMaxLength(500);

        builder.Ignore(a => a.IsDownloadable);

        builder.HasIndex(a => a.TicketId).HasDatabaseName("IX_TicketAttachments_Ticket");
        builder.HasIndex(a => a.Sha256).HasDatabaseName("IX_TicketAttachments_Sha256");

        builder.HasOne(a => a.UploadedBy).WithMany()
            .HasForeignKey(a => a.UploadedById).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketAssignmentConfiguration : IEntityTypeConfiguration<TicketAssignment>
{
    public void Configure(EntityTypeBuilder<TicketAssignment> builder)
    {
        builder.ToTable("TicketAssignments");

        builder.Property(a => a.Reason).HasMaxLength(1000);

        builder.HasIndex(a => new { a.TicketId, a.AssignedAtUtc })
            .HasDatabaseName("IX_TicketAssignments_Ticket_AssignedAt");

        builder.HasOne(a => a.NewAgent).WithMany()
            .HasForeignKey(a => a.NewAgentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.NewTeam).WithMany()
            .HasForeignKey(a => a.NewTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketStatusHistoryConfiguration : IEntityTypeConfiguration<TicketStatusHistory>
{
    public void Configure(EntityTypeBuilder<TicketStatusHistory> builder)
    {
        builder.ToTable("TicketStatusHistory");

        builder.Property(h => h.Reason).HasMaxLength(1000);

        builder.HasIndex(h => new { h.TicketId, h.ChangedAtUtc })
            .HasDatabaseName("IX_TicketStatusHistory_Ticket_ChangedAt");
    }
}

public sealed class TicketPriorityHistoryConfiguration : IEntityTypeConfiguration<TicketPriorityHistory>
{
    public void Configure(EntityTypeBuilder<TicketPriorityHistory> builder)
    {
        builder.ToTable("TicketPriorityHistory");

        builder.Property(h => h.Reason).HasMaxLength(1000);

        builder.HasIndex(h => new { h.TicketId, h.ChangedAtUtc })
            .HasDatabaseName("IX_TicketPriorityHistory_Ticket_ChangedAt");
    }
}

public sealed class WorkLogConfiguration : IEntityTypeConfiguration<WorkLog>
{
    public void Configure(EntityTypeBuilder<WorkLog> builder)
    {
        builder.ToTable("WorkLogs", t =>
            t.HasCheckConstraint("CK_WorkLogs_Minutes", "[MinutesSpent] > 0 AND [MinutesSpent] <= 1440"));

        builder.Property(w => w.Description).HasMaxLength(2000).IsRequired();

        builder.HasIndex(w => new { w.TicketId, w.WorkDateUtc })
            .HasDatabaseName("IX_WorkLogs_Ticket_WorkDate");

        builder.HasIndex(w => new { w.UserId, w.WorkDateUtc })
            .HasDatabaseName("IX_WorkLogs_User_WorkDate");

        builder.HasOne(w => w.User).WithMany()
            .HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketRelatedRecordConfiguration : IEntityTypeConfiguration<TicketRelatedRecord>
{
    public void Configure(EntityTypeBuilder<TicketRelatedRecord> builder)
    {
        builder.ToTable("TicketRelatedRecords");

        builder.Property(r => r.RecordReference).HasMaxLength(120).IsRequired();
        builder.Property(r => r.RecordLabel).HasMaxLength(300);
        builder.Property(r => r.RecordUrl).HasMaxLength(1000);
        builder.Property(r => r.SourceSystem).HasMaxLength(60);
        builder.Property(r => r.Notes).HasMaxLength(1000);

        builder.HasIndex(r => r.TicketId).HasDatabaseName("IX_TicketRelatedRecords_Ticket");

        // Answers the operational question "which tickets touch this purchase order?".
        builder.HasIndex(r => new { r.OrganizationId, r.RecordType, r.RecordReference })
            .HasDatabaseName("IX_TicketRelatedRecords_Org_Type_Reference");
    }
}

public sealed class TicketTagConfiguration : IEntityTypeConfiguration<TicketTag>
{
    public void Configure(EntityTypeBuilder<TicketTag> builder)
    {
        builder.ToTable("TicketTags");

        builder.HasIndex(t => new { t.TicketId, t.TagId })
            .IsUnique()
            .HasDatabaseName("UX_TicketTags_Ticket_Tag");

        builder.HasOne(t => t.Tag).WithMany()
            .HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketNumberSequenceConfiguration : IEntityTypeConfiguration<TicketNumberSequence>
{
    public void Configure(EntityTypeBuilder<TicketNumberSequence> builder)
    {
        builder.ToTable("TicketNumberSequences", t =>
            t.HasCheckConstraint("CK_TicketNumberSequences_LastValue", "[LastValue] >= 0"));

        builder.Property(s => s.Prefix).HasMaxLength(10).IsRequired();

        // One counter per tenant, prefix and year. Uniqueness matters because a second
        // row for the same key would let two tickets receive the same number.
        builder.HasIndex(s => new { s.OrganizationId, s.Prefix, s.Year })
            .IsUnique()
            .HasDatabaseName("UX_TicketNumberSequences_Org_Prefix_Year");
    }
}
