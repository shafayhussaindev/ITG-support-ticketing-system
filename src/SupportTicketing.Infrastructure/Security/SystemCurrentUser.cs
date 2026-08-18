using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Infrastructure.Security;

/// <summary>
/// The principal used by background workers, the development seeder and design-time
/// tooling, where no HTTP request exists.
/// </summary>
/// <remarks>
/// Actions attributed to this principal are audited with
/// <see cref="DecisionSource.System"/>, which is how the audit trail distinguishes
/// "the auto-close job closed this ticket" from "a person closed this ticket".
/// </remarks>
public sealed class SystemCurrentUser : ICurrentUser
{
    public SystemCurrentUser(Guid? organizationId = null)
    {
        OrganizationId = organizationId;
        CorrelationId = Guid.CreateVersion7();
    }

    public Guid? UserId => null;
    public Guid? OrganizationId { get; }
    public string? Email => "system@internal";
    public string? FullName => "System";
    public bool IsAuthenticated => true;

    /// <summary>
    /// Background jobs act through the same application commands as people, so they
    /// need the full permission set. What keeps this safe is that the workers project
    /// only ever dispatches a fixed, reviewed list of commands, and every one of them
    /// is audited as a system action.
    /// </summary>
    public IReadOnlySet<string> Permissions { get; } = Domain.Identity.Permissions.All.ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<Guid> TeamIds => [];
    public Guid? DepartmentId => null;
    public Guid? OfficeId => null;
    public DataScope Scope => DataScope.All;
    public Guid CorrelationId { get; }
    public string? IpAddress => null;
    public string? UserAgent => "SupportTicketing.Workers";

    public bool Has(string permission) => true;

    public void Require(string permission)
    {
        // A system principal always satisfies the check; the method exists so worker
        // code can share handlers with request-scoped code without branching.
    }
}
