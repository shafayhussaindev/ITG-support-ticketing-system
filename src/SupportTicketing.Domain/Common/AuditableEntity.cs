namespace SupportTicketing.Domain.Common;

/// <summary>Entity with creation and modification tracking.</summary>
public abstract class AuditableEntity : Entity, IAuditable
{
    public DateTime CreatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Auditable entity that is archived rather than deleted.</summary>
public abstract class SoftDeletableEntity : AuditableEntity, ISoftDeletable
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

/// <summary>Tenant-scoped, auditable, archivable entity — the default for business data.</summary>
public abstract class TenantEntity : SoftDeletableEntity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
}
