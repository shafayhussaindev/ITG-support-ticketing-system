using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("Teams", t =>
            t.HasCheckConstraint("CK_Teams_AcceptanceTimeout", "[AcceptanceTimeoutMinutes] > 0"));

        builder.Property(t => t.Name).HasMaxLength(150).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(20).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(1000);

        builder.HasIndex(t => new { t.OrganizationId, t.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Teams_Org_Code");

        builder.HasOne(t => t.Department)
            .WithMany()
            .HasForeignKey(t => t.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.TeamLead)
            .WithMany()
            .HasForeignKey(t => t.TeamLeadId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("TeamMembers", t =>
            t.HasCheckConstraint("CK_TeamMembers_CapacityWeight", "[CapacityWeight] >= 0 AND [CapacityWeight] <= 10"));

        builder.HasIndex(m => new { m.TeamId, m.UserId })
            .IsUnique()
            .HasDatabaseName("UX_TeamMembers_Team_User");

        // Resolving "which teams is this user in" runs on every authenticated request.
        builder.HasIndex(m => new { m.UserId, m.IsActive })
            .HasDatabaseName("IX_TeamMembers_User_IsActive");

        builder.HasOne(m => m.Team)
            .WithMany(t => t.Members)
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany(u => u.TeamMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("Skills");

        builder.Property(s => s.Name).HasMaxLength(150).IsRequired();
        builder.Property(s => s.Code).HasMaxLength(30).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500);

        builder.HasIndex(s => new { s.OrganizationId, s.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Skills_Org_Code");
    }
}

public sealed class UserSkillConfiguration : IEntityTypeConfiguration<UserSkill>
{
    public void Configure(EntityTypeBuilder<UserSkill> builder)
    {
        builder.ToTable("UserSkills", t =>
            t.HasCheckConstraint("CK_UserSkills_Proficiency", "[Proficiency] BETWEEN 1 AND 5"));

        builder.HasIndex(s => new { s.UserId, s.SkillId })
            .IsUnique()
            .HasDatabaseName("UX_UserSkills_User_Skill");

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Skill)
            .WithMany(s => s.UserSkills)
            .HasForeignKey(s => s.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
