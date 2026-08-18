using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SupportTicketing.Domain.Ai;
using SupportTicketing.Domain.Auditing;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Teams;
using SupportTicketing.Domain.Escalations;
using SupportTicketing.Domain.Feedback;
using SupportTicketing.Domain.Knowledge;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Sla;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Abstractions;

/// <summary>
/// The persistence surface available to the Application layer.
/// </summary>
/// <remarks>
/// Exposing an interface rather than the concrete DbContext keeps the Application
/// layer free of the SQL Server provider and makes handlers testable. Tenant and
/// soft-delete filtering is applied by global query filters inside the
/// implementation, so every query here is already scoped to the caller's
/// organization — there is no way for a handler to forget.
/// </remarks>
public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<Office> Offices { get; }
    DbSet<Department> Departments { get; }

    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserPermissionOverride> UserPermissionOverrides { get; }
    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<Team> Teams { get; }
    DbSet<TeamMember> TeamMembers { get; }
    DbSet<Skill> Skills { get; }
    DbSet<UserSkill> UserSkills { get; }

    DbSet<Category> Categories { get; }
    DbSet<Subcategory> Subcategories { get; }
    DbSet<BusinessApplication> Applications { get; }
    DbSet<ApplicationModule> ApplicationModules { get; }
    DbSet<PriorityMatrixEntry> PriorityMatrixEntries { get; }
    DbSet<Tag> Tags { get; }

    DbSet<Ticket> Tickets { get; }
    DbSet<TicketComment> TicketComments { get; }
    DbSet<TicketCommentMention> TicketCommentMentions { get; }
    DbSet<TicketAttachment> TicketAttachments { get; }
    DbSet<TicketAssignment> TicketAssignments { get; }
    DbSet<TicketStatusHistory> TicketStatusHistory { get; }
    DbSet<TicketPriorityHistory> TicketPriorityHistory { get; }
    DbSet<WorkLog> WorkLogs { get; }
    DbSet<TicketRelatedRecord> TicketRelatedRecords { get; }
    DbSet<TicketTag> TicketTags { get; }
    DbSet<TicketNumberSequence> TicketNumberSequences { get; }

    DbSet<BusinessCalendar> BusinessCalendars { get; }
    DbSet<BusinessHour> BusinessHours { get; }
    DbSet<Holiday> Holidays { get; }
    DbSet<SlaPolicy> SlaPolicies { get; }
    DbSet<SlaTarget> SlaTargets { get; }
    DbSet<TicketSlaInstance> TicketSlaInstances { get; }
    DbSet<SlaEvent> SlaEvents { get; }

    DbSet<EscalationPolicy> EscalationPolicies { get; }
    DbSet<EscalationStep> EscalationSteps { get; }
    DbSet<EscalationHistory> EscalationHistory { get; }

    DbSet<Notification> Notifications { get; }
    DbSet<NotificationDelivery> NotificationDeliveries { get; }
    DbSet<NotificationPreference> NotificationPreferences { get; }

    DbSet<KnowledgeArticle> KnowledgeArticles { get; }
    DbSet<KnowledgeArticleVersion> KnowledgeArticleVersions { get; }
    DbSet<KnowledgeFeedback> KnowledgeFeedback { get; }
    DbSet<SatisfactionRating> SatisfactionRatings { get; }

    DbSet<AiConfiguration> AiConfigurations { get; }
    DbSet<AiPromptTemplate> AiPromptTemplates { get; }
    DbSet<AiRecommendation> AiRecommendations { get; }
    DbSet<AiUsageRecord> AiUsageRecords { get; }

    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SystemSetting> SystemSettings { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a query with the organization filter disabled. Reserved for
    /// authentication (which must find a user before their tenant is known) and for
    /// audited Super Admin break-glass reads. Architecture tests restrict its callers.
    /// </summary>
    IQueryable<TEntity> IgnoreTenantFilter<TEntity>() where TEntity : class;

    /// <summary>
    /// Pins the organization filter to a specific tenant for the lifetime of the
    /// returned scope.
    /// </summary>
    /// <remarks>
    /// This exists for the authentication flow. Sign-in and refresh run without a
    /// principal, so the ambient tenant is null and every filtered query would match
    /// nothing — silently, because an over-restrictive filter returns an empty set
    /// rather than an error. Once the handler has identified the user from their
    /// credentials it opens a scope for that user's organization, and the rest of the
    /// flow runs against correctly filtered data instead of reaching for
    /// <see cref="IgnoreTenantFilter{TEntity}"/> repeatedly.
    /// <para>
    /// Prefer this over disabling the filter: it keeps isolation enforced, just
    /// against a tenant established by verified credentials rather than by a claim.
    /// </para>
    /// </remarks>
    IDisposable BeginTenantScope(Guid organizationId);
}
