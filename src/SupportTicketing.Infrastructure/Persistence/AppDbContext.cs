using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Auditing;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Teams;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Infrastructure.Persistence;

public class AppDbContext : DbContext, IAppDbContext
{
    private readonly Guid? _tenantId;

    /// <summary>
    /// Disables the organization filter. Only <see cref="IgnoreTenantFilter{TEntity}"/>,
    /// the development seeder and design-time migrations set this.
    /// </summary>
    private readonly bool _bypassTenantFilter;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        _tenantId = currentUser.OrganizationId;
        _bypassTenantFilter = false;
    }

    /// <summary>
    /// Constructor for the seeder, background jobs and design-time tooling, where no
    /// HTTP principal exists. Callers are responsible for scoping their own writes.
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
        _tenantId = null;
        _bypassTenantFilter = true;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Office> Offices => Set<Office>();
    public DbSet<Department> Departments => Set<Department>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<UserSkill> UserSkills => Set<UserSkill>();

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Subcategory> Subcategories => Set<Subcategory>();
    public DbSet<BusinessApplication> Applications => Set<BusinessApplication>();
    public DbSet<ApplicationModule> ApplicationModules => Set<ApplicationModule>();
    public DbSet<PriorityMatrixEntry> PriorityMatrixEntries => Set<PriorityMatrixEntry>();
    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketCommentMention> TicketCommentMentions => Set<TicketCommentMention>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<TicketAssignment> TicketAssignments => Set<TicketAssignment>();
    public DbSet<TicketStatusHistory> TicketStatusHistory => Set<TicketStatusHistory>();
    public DbSet<TicketPriorityHistory> TicketPriorityHistory => Set<TicketPriorityHistory>();
    public DbSet<WorkLog> WorkLogs => Set<WorkLog>();
    public DbSet<TicketRelatedRecord> TicketRelatedRecords => Set<TicketRelatedRecord>();
    public DbSet<TicketTag> TicketTags => Set<TicketTag>();
    public DbSet<TicketNumberSequence> TicketNumberSequences => Set<TicketNumberSequence>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public IQueryable<TEntity> IgnoreTenantFilter<TEntity>() where TEntity : class =>
        Set<TEntity>().IgnoreQueryFilters();

    /// <summary>
    /// Overrides the ambient tenant. EF reads <see cref="TenantIdForFilter"/> when it
    /// materialises the filter's parameter for each query, so changing it here affects
    /// queries executed inside the scope and nothing else.
    /// </summary>
    public IDisposable BeginTenantScope(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A tenant scope requires a real organization id.", nameof(organizationId));
        }

        var previous = _tenantOverride;
        _tenantOverride = organizationId;
        return new TenantScope(this, previous);
    }

    private Guid? _tenantOverride;

    private sealed class TenantScope(AppDbContext context, Guid? previous) : IDisposable
    {
        public void Dispose() => context._tenantOverride = previous;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        ApplyGlobalFilters(modelBuilder);
        ApplyConventions(modelBuilder);
    }

    /// <summary>
    /// Attaches the tenant and soft-delete filters to every entity that declares the
    /// corresponding interface, so a new entity is protected the moment it is added
    /// to the model rather than when someone remembers to write a Where clause.
    /// </summary>
    private void ApplyGlobalFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            var isTenantOwned = typeof(ITenantOwned).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);

            if (!isTenantOwned && !isSoftDeletable)
            {
                continue;
            }

            var parameter = Expression.Parameter(clrType, "e");
            Expression? filter = null;

            if (isTenantOwned)
            {
                // e.OrganizationId == _tenantId  OR  the bypass flag is set.
                // With no principal and no bypass, _tenantId is null and the comparison
                // matches nothing — the filter fails closed rather than open.
                var organizationId = Expression.Property(parameter, nameof(ITenantOwned.OrganizationId));
                var tenantValue = Expression.Property(
                    Expression.Constant(this), nameof(TenantIdForFilter));
                var bypassValue = Expression.Property(
                    Expression.Constant(this), nameof(BypassTenantFilterForFilter));

                var matchesTenant = Expression.Equal(
                    Expression.Convert(organizationId, typeof(Guid?)),
                    tenantValue);

                filter = Expression.OrElse(bypassValue, matchesTenant);
            }

            if (isSoftDeletable)
            {
                var isDeleted = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                var notDeleted = Expression.Not(isDeleted);
                filter = filter is null ? notDeleted : Expression.AndAlso(filter, notDeleted);
            }

            modelBuilder.Entity(clrType).HasQueryFilter(Expression.Lambda(filter!, parameter));
        }
    }

    /// <summary>
    /// Exposed for the query-filter expression; EF turns this into a query parameter.
    /// An active <see cref="BeginTenantScope"/> takes precedence over the principal's
    /// organization, which is what makes the sign-in flow work before a claim exists.
    /// </summary>
    public Guid? TenantIdForFilter => _tenantOverride ?? _tenantId;

    /// <summary>Exposed for the query-filter expression; EF turns this into a query parameter.</summary>
    public bool BypassTenantFilterForFilter => _bypassTenantFilter;

    /// <summary>
    /// Writes the value unchanged and stamps <see cref="DateTimeKind.Utc"/> on read.
    /// Everything in this system is stored UTC; this makes that explicit to callers.
    /// </summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter =
        new(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcDateTimeConverter =
        new(
            value => value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

    /// <summary>
    /// Model-wide conventions: UTC datetimes, sensible string defaults, and
    /// <c>rowversion</c> concurrency tokens.
    /// </summary>
    private static void ApplyConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                // Store every DateTime as datetime2 to avoid the precision loss and
                // range limits of the legacy datetime type.
                //
                // SQL Server's datetime2 carries no offset, so values come back with
                // DateTimeKind.Unspecified. System.Text.Json then writes them without a
                // trailing "Z", and a browser parsing an ISO string that has no zone
                // treats it as *local* time — so every timestamp rendered in the UI was
                // wrong by the reader's UTC offset, and a ticket raised seconds ago
                // showed as "5 hours ago" in Karachi. Re-stamping the Kind on read makes
                // the serialised value explicitly UTC, which is what the client expects.
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetColumnType("datetime2(3)");
                    property.SetValueConverter(UtcDateTimeConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("datetime2(3)");
                    property.SetValueConverter(NullableUtcDateTimeConverter);
                }

                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                {
                    property.SetColumnType("decimal(18,4)");
                }

                // An unbounded nvarchar(max) cannot be indexed and encourages unbounded
                // input. Anything that genuinely needs max opts in explicitly in its
                // configuration class.
                if (property.ClrType == typeof(string) && property.GetMaxLength() is null)
                {
                    property.SetMaxLength(256);
                }
            }

            if (typeof(IHasRowVersion).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .Property(nameof(IHasRowVersion.RowVersion))
                    .IsRowVersion();
            }
        }
    }
}
