namespace SupportTicketing.Domain.Common;

/// <summary>
/// Base type for every persisted entity.
/// </summary>
/// <remarks>
/// Identifiers are UUID v7: time-ordered, so they cluster well in a SQL Server
/// clustered index the way an IDENTITY column would, while remaining
/// unguessable. Guessable sequential integer keys are the primary enabler of
/// horizontal privilege escalation, which is the highest-severity threat in a
/// multi-tenant support system.
/// </remarks>
public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    /// <summary>Domain events raised by this entity, dispatched after a successful commit.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj) =>
        obj is Entity other && GetType() == other.GetType() && Id.Equals(other.Id) && Id != Guid.Empty;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
