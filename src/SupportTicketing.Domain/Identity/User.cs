using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Domain.Identity;

public class User : TenantEntity, IHasRowVersion
{
    public required string Email { get; set; }

    /// <summary>Upper-invariant form of <see cref="Email"/>, used for the unique index and lookups.</summary>
    public required string NormalizedEmail { get; set; }

    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>PBKDF2 hash produced by ASP.NET Core Identity's password hasher. Never logged, never returned.</summary>
    public required string PasswordHash { get; set; }

    public string? PhoneNumber { get; set; }
    public string? JobTitle { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>IANA identifier, for example <c>Asia/Karachi</c>. Timestamps are stored UTC and rendered in this zone.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public string Locale { get; set; } = "en-US";

    public Guid? OfficeId { get; set; }
    public Office? Office { get; set; }

    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Forces the change-password flow before any other API access is granted.</summary>
    public bool MustChangePassword { get; set; }

    // ----- Multi-factor authentication -------------------------------------
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Base32 TOTP secret. Encrypted at rest; never returned by any endpoint.</summary>
    public string? TwoFactorSecret { get; set; }

    // ----- Lockout ----------------------------------------------------------
    public int AccessFailedCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? PasswordChangedAtUtc { get; set; }

    /// <summary>True while a lockout window is active.</summary>
    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc.HasValue && LockoutEndUtc.Value > nowUtc;

    /// <summary>
    /// Availability flag used by the assignment engine. An unavailable agent is
    /// skipped by automatic routing but can still be assigned manually.
    /// </summary>
    public bool IsAvailableForAssignment { get; set; } = true;

    /// <summary>Soft cap used by workload balancing. Zero means "no explicit cap".</summary>
    public int MaxConcurrentTickets { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<TeamMember> TeamMemberships { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserPermissionOverride> PermissionOverrides { get; set; } = [];
}

public class Role : TenantEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// System roles are seeded and cannot be deleted, though their permission set
    /// remains fully editable by an administrator.
    /// </summary>
    public bool IsSystemRole { get; set; }

    /// <summary>Default data scope granted to holders of this role.</summary>
    public DataScope DefaultScope { get; set; } = DataScope.Own;

    /// <summary>Ordering hint for UI lists; higher means broader authority.</summary>
    public int Rank { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}

public class Permission : Entity
{
    public required string Key { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}

public class UserRole : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    public DateTime GrantedAtUtc { get; set; }
    public Guid? GrantedBy { get; set; }
}

public class RolePermission : Entity
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    public Guid PermissionId { get; set; }
    public Permission? Permission { get; set; }
}

/// <summary>
/// Per-user grant or deny that overrides the union of their roles.
/// A deny always wins, regardless of role membership.
/// </summary>
public class UserPermissionOverride : AuditableEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid PermissionId { get; set; }
    public Permission? Permission { get; set; }

    /// <summary>True grants the permission; false denies it and beats every role grant.</summary>
    public bool IsGranted { get; set; }

    public string? Reason { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// A refresh token in a rotation family. Tokens are stored hashed. Presenting a
/// token that has already been rotated is treated as theft: the whole family is
/// revoked and the event is audited.
/// </summary>
public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 of the token. The plaintext is returned to the client once and never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Groups every token derived from one login, so theft can revoke the whole chain.</summary>
    public Guid FamilyId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>Set when this token is rotated, forming the chain used for reuse detection.</summary>
    public Guid? ReplacedByTokenId { get; set; }

    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}
