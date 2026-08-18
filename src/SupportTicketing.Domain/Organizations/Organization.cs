using SupportTicketing.Domain.Common;

namespace SupportTicketing.Domain.Organizations;

/// <summary>
/// The tenant boundary. Every <see cref="ITenantOwned"/> entity is filtered by
/// organization using the authenticated principal's claim.
/// </summary>
public class Organization : SoftDeletableEntity, IHasRowVersion
{
    public required string Name { get; set; }

    /// <summary>Short stable identifier used in ticket numbers, for example <c>ITG</c>.</summary>
    public required string Code { get; set; }

    public string? Description { get; set; }
    public string? LogoUrl { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }

    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;

    /// <summary>Prefix for generated ticket numbers, for example <c>TKT</c> in <c>TKT-2026-000001</c>.</summary>
    public string TicketPrefix { get; set; } = "TKT";

    public byte[]? RowVersion { get; set; }

    public ICollection<Office> Offices { get; set; } = [];
    public ICollection<Department> Departments { get; set; } = [];
}

public class Office : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>Drives SLA business-hours calculation for tickets raised from this office.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public Guid? BusinessCalendarId { get; set; }

    public bool IsActive { get; set; } = true;

    public Organization? Organization { get; set; }
}

public class Department : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }

    /// <summary>Self-reference forming a hierarchy. Department scope includes descendants.</summary>
    public Guid? ParentDepartmentId { get; set; }
    public Department? ParentDepartment { get; set; }
    public ICollection<Department> ChildDepartments { get; set; } = [];

    public Guid? OfficeId { get; set; }
    public Office? Office { get; set; }

    public Guid? ManagerId { get; set; }

    public bool IsActive { get; set; } = true;

    public Organization? Organization { get; set; }
}
