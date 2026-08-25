using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Feedback;
using SupportTicketing.Domain.Knowledge;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class KnowledgeArticleConfiguration : IEntityTypeConfiguration<KnowledgeArticle>
{
    public void Configure(EntityTypeBuilder<KnowledgeArticle> builder)
    {
        builder.ToTable("KnowledgeArticles", t =>
        {
            t.HasCheckConstraint("CK_KnowledgeArticles_Counts",
                "[ViewCount] >= 0 AND [HelpfulCount] >= 0 AND [NotHelpfulCount] >= 0");
            t.HasCheckConstraint("CK_KnowledgeArticles_Version", "[CurrentVersion] > 0");
        });

        builder.Property(a => a.Title).HasMaxLength(250).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(600).IsRequired();
        builder.Property(a => a.Content).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(a => a.Slug).HasMaxLength(160).IsRequired();
        builder.Property(a => a.Tags).HasMaxLength(500);

        builder.Ignore(a => a.IsReadable);
        builder.Ignore(a => a.HelpfulRatio);

        builder.HasIndex(a => new { a.OrganizationId, a.Slug })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_KnowledgeArticles_Org_Slug");

        // The browse and search path: published articles of a visibility the caller
        // may see, newest first.
        builder.HasIndex(a => new { a.OrganizationId, a.Status, a.Visibility })
            .HasDatabaseName("IX_KnowledgeArticles_Org_Status_Visibility");

        builder.HasIndex(a => new { a.OrganizationId, a.CategoryId })
            .HasDatabaseName("IX_KnowledgeArticles_Org_Category");

        builder.HasOne(a => a.Author).WithMany()
            .HasForeignKey(a => a.AuthorId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Category).WithMany()
            .HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.Versions).WithOne(v => v.Article!)
            .HasForeignKey(v => v.ArticleId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.Feedback).WithOne(f => f.Article!)
            .HasForeignKey(f => f.ArticleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class KnowledgeArticleVersionConfiguration : IEntityTypeConfiguration<KnowledgeArticleVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeArticleVersion> builder)
    {
        builder.ToTable("KnowledgeArticleVersions");

        builder.Property(v => v.Title).HasMaxLength(250).IsRequired();
        builder.Property(v => v.Summary).HasMaxLength(600).IsRequired();
        builder.Property(v => v.Content).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(v => v.ChangeNote).HasMaxLength(1000);

        builder.HasIndex(v => new { v.ArticleId, v.Version })
            .IsUnique()
            .HasDatabaseName("UX_KnowledgeArticleVersions_Article_Version");
    }
}

public sealed class KnowledgeFeedbackConfiguration : IEntityTypeConfiguration<KnowledgeFeedback>
{
    public void Configure(EntityTypeBuilder<KnowledgeFeedback> builder)
    {
        builder.ToTable("KnowledgeFeedback");

        builder.Property(f => f.Comment).HasMaxLength(1000);

        // One verdict per reader per article, so the helpful counters cannot be
        // inflated by clicking twice.
        builder.HasIndex(f => new { f.ArticleId, f.UserId })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_KnowledgeFeedback_Article_User");

        builder.HasOne(f => f.User).WithMany()
            .HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SatisfactionRatingConfiguration : IEntityTypeConfiguration<SatisfactionRating>
{
    public void Configure(EntityTypeBuilder<SatisfactionRating> builder)
    {
        builder.ToTable("SatisfactionRatings", t =>
        {
            t.HasCheckConstraint("CK_SatisfactionRatings_Rating", "[Rating] BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_SatisfactionRatings_Resolution",
                "[ResolutionRating] IS NULL OR [ResolutionRating] BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_SatisfactionRatings_Staff",
                "[StaffRating] IS NULL OR [StaffRating] BETWEEN 1 AND 5");
        });

        builder.Property(r => r.Comment).HasMaxLength(2000);

        builder.Ignore(r => r.IsDetractor);

        // One rating per ticket. Re-rating after a disagreement would let a score be
        // lobbied upward, so the database refuses a second submission outright.
        builder.HasIndex(r => r.TicketId)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_SatisfactionRatings_Ticket");

        // Staff and team performance reporting.
        builder.HasIndex(r => new { r.OrganizationId, r.RatedStaffId, r.SubmittedAtUtc })
            .HasDatabaseName("IX_SatisfactionRatings_Org_Staff_Submitted");

        builder.HasIndex(r => new { r.OrganizationId, r.SubmittedAtUtc })
            .HasDatabaseName("IX_SatisfactionRatings_Org_Submitted");

        builder.HasOne(r => r.RatedBy).WithMany()
            .HasForeignKey(r => r.RatedById).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Ticket).WithMany()
            .HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.Restrict);
    }
}
