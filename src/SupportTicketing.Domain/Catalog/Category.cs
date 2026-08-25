using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Sla;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Domain.Catalog;

public class Category : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }

    /// <summary>Default team for tickets in this category when no routing rule matches.</summary>
    public Guid? DefaultTeamId { get; set; }
    public Team? DefaultTeam { get; set; }

    /// <summary>Optional SLA policy override for this category.</summary>
    public Guid? SlaPolicyId { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Hidden from the requester portal; staff may still select it.</summary>
    public bool IsInternalOnly { get; set; }

    public ICollection<Subcategory> Subcategories { get; set; } = [];
}

public class Subcategory : TenantEntity
{
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }

    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }

    public Guid? DefaultTeamId { get; set; }
    public Guid? SlaPolicyId { get; set; }

    /// <summary>Skill required to work tickets in this subcategory, used by skill-based routing.</summary>
    public Guid? RequiredSkillId { get; set; }

    /// <summary>Impact suggested in the create-ticket wizard. The requester can change it.</summary>
    public ImpactLevel? DefaultImpact { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A business application that tickets can be raised against, for example the ERP,
/// the QA portal, or a shipment tracking system.
/// </summary>
public class BusinessApplication : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }
    public string? Vendor { get; set; }
    public string? Version { get; set; }

    /// <summary>Team that owns support for this application.</summary>
    public Guid? OwningTeamId { get; set; }
    public Team? OwningTeam { get; set; }

    /// <summary>Raises the calculated priority floor for tickets against critical systems.</summary>
    public bool IsBusinessCritical { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<ApplicationModule> Modules { get; set; } = [];
}

public class ApplicationModule : TenantEntity
{
    public Guid ApplicationId { get; set; }
    public BusinessApplication? Application { get; set; }

    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }

    public Guid? OwningTeamId { get; set; }
    public Guid? RequiredSkillId { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One cell of the configurable impact × urgency priority matrix. Administrators
/// edit these rows; the priority calculator reads them and never hardcodes a result.
/// </summary>
public class PriorityMatrixEntry : TenantEntity
{
    /// <summary>
    /// The SLA policy this cell belongs to, or null for the organization's own matrix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is the default and the fallback. An organization has one matrix that applies
    /// everywhere; a policy may then override it, because what counts as Critical is not
    /// the same question for a production line as for an internal reporting request.
    /// </para>
    /// <para>
    /// Resolution is per cell, not per matrix: a policy that overrides only the cells it
    /// cares about inherits the rest, so an administrator editing the organization
    /// matrix does not silently leave a policy behind.
    /// </para>
    /// </remarks>
    public Guid? SlaPolicyId { get; set; }

    public SlaPolicy? SlaPolicy { get; set; }

    public ImpactLevel Impact { get; set; }
    public UrgencyLevel Urgency { get; set; }
    public PriorityLevel Priority { get; set; }
}

/// <summary>A free-form label applied to tickets for reporting and search.</summary>
public class Tag : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>Hex colour used by the UI badge, for example <c>#0F6E77</c>.</summary>
    public string? Color { get; set; }

    public bool IsActive { get; set; } = true;
}
