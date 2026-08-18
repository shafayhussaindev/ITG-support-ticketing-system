namespace SupportTicketing.Domain.Common;

/// <summary>
/// Marks an entity as belonging to exactly one organization. Every such entity is
/// filtered by a global query filter driven by the authenticated principal's
/// organization claim — never by a value supplied in a request.
/// </summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; }
}

/// <summary>Populated automatically by the auditing SaveChanges interceptor.</summary>
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    Guid? CreatedBy { get; set; }
    DateTime? UpdatedAtUtc { get; set; }
    Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Operational records are archived, never physically deleted. A global query
/// filter hides archived rows; history and audit tables do not implement this
/// interface because they are never removed from view at all.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAtUtc { get; set; }
    Guid? DeletedBy { get; set; }
}

/// <summary>Optimistic concurrency token, mapped to SQL Server <c>rowversion</c>.</summary>
public interface IHasRowVersion
{
    byte[]? RowVersion { get; set; }
}

/// <summary>
/// Append-only marker. The persistence interceptor throws if an entity implementing
/// this interface is ever modified or deleted, so history cannot be rewritten even
/// by a bug in application code.
/// </summary>
public interface IAppendOnly;

public interface IDomainEvent
{
    DateTime OccurredAtUtc { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}
