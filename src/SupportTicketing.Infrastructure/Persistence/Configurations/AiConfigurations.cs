using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Ai;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class AiConfigurationEntityConfiguration : IEntityTypeConfiguration<AiConfiguration>
{
    public void Configure(EntityTypeBuilder<AiConfiguration> builder)
    {
        builder.ToTable("AiConfigurations", t =>
        {
            t.HasCheckConstraint("CK_AiConfigurations_Threshold",
                "[AutoApplyConfidenceThreshold] BETWEEN 0 AND 1");
            t.HasCheckConstraint("CK_AiConfigurations_Tokens", "[MaxTokensPerRequest] > 0");
        });

        builder.Property(c => c.ModelIdentifier).HasMaxLength(100).IsRequired();

        // One configuration per organization. Two rows would make "is AI on?" ambiguous.
        builder.HasIndex(c => c.OrganizationId)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_AiConfigurations_Org");
    }
}

public sealed class AiPromptTemplateConfiguration : IEntityTypeConfiguration<AiPromptTemplate>
{
    public void Configure(EntityTypeBuilder<AiPromptTemplate> builder)
    {
        builder.ToTable("AiPromptTemplates");

        builder.Property(t => t.Version).HasMaxLength(40).IsRequired();
        builder.Property(t => t.SystemPrompt).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(t => t.ResponseSchema).HasColumnType("nvarchar(max)");

        builder.HasIndex(t => new { t.OrganizationId, t.RecommendationType, t.Version })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_AiPromptTemplates_Org_Type_Version");
    }
}

public sealed class AiRecommendationConfiguration : IEntityTypeConfiguration<AiRecommendation>
{
    public void Configure(EntityTypeBuilder<AiRecommendation> builder)
    {
        builder.ToTable("AiRecommendations", t =>
            t.HasCheckConstraint("CK_AiRecommendations_Confidence", "[Confidence] BETWEEN 0 AND 1"));

        builder.Property(r => r.SuggestedValueJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(r => r.Explanation).HasMaxLength(2000);
        builder.Property(r => r.DeterministicValue).HasMaxLength(200);
        builder.Property(r => r.ModelIdentifier).HasMaxLength(100).IsRequired();
        builder.Property(r => r.PromptVersion).HasMaxLength(40).IsRequired();
        builder.Property(r => r.InputHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.OverrideReason).HasMaxLength(1000);
        builder.Property(r => r.EstimatedCostUsd).HasColumnType("decimal(12,6)");

        builder.Ignore(r => r.IsPending);

        builder.HasIndex(r => new { r.TicketId, r.RecommendationType })
            .HasDatabaseName("IX_AiRecommendations_Ticket_Type");

        // Lets an identical question reuse an existing answer instead of paying twice.
        builder.HasIndex(r => new { r.OrganizationId, r.InputHash, r.RecommendationType })
            .HasDatabaseName("IX_AiRecommendations_Org_InputHash_Type");

        builder.HasOne(r => r.AcceptedBy).WithMany()
            .HasForeignKey(r => r.AcceptedById).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AiUsageRecordConfiguration : IEntityTypeConfiguration<AiUsageRecord>
{
    public void Configure(EntityTypeBuilder<AiUsageRecord> builder)
    {
        builder.ToTable("AiUsageRecords");

        builder.Property(u => u.ModelIdentifier).HasMaxLength(100).IsRequired();
        builder.Property(u => u.FailureReason).HasMaxLength(200);
        builder.Property(u => u.EstimatedCostUsd).HasColumnType("decimal(12,6)");

        builder.Ignore(u => u.TotalTokens);

        // Powers the spend and reliability rollups an administrator needs.
        builder.HasIndex(u => new { u.OrganizationId, u.OccurredAtUtc })
            .HasDatabaseName("IX_AiUsageRecords_Org_OccurredAt");

        builder.HasIndex(u => new { u.OrganizationId, u.Succeeded })
            .HasDatabaseName("IX_AiUsageRecords_Org_Succeeded");
    }
}
