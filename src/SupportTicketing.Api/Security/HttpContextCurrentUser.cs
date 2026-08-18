using System.Security.Claims;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.Api.Security;

/// <summary>
/// Projects the authenticated principal out of the current HTTP request.
/// </summary>
/// <remarks>
/// Everything here comes from validated JWT claims. Nothing is read from the route,
/// query string, body or a custom header, because any of those would let a caller
/// nominate their own organization or permissions.
/// </remarks>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private readonly IHttpContextAccessor _accessor;

    // Cached against the principal instance rather than eagerly, so the cache is
    // invalidated automatically when authentication replaces the principal.
    private ClaimsPrincipal? _cachedFor;
    private IReadOnlySet<string>? _cachedPermissions;
    private IReadOnlyList<Guid>? _cachedTeamIds;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
        CorrelationId = ResolveCorrelationId(accessor.HttpContext);
    }

    private HttpContext? Context => _accessor.HttpContext;

    /// <summary>
    /// Read on every access rather than captured in the constructor.
    /// </summary>
    /// <remarks>
    /// This service is scoped, and the exception-handling middleware resolves it at
    /// the very top of the pipeline — before authentication has run. Capturing
    /// <c>HttpContext.User</c> in the constructor would freeze the anonymous
    /// principal for the entire request, and every authenticated endpoint would then
    /// see no user at all.
    /// </remarks>
    private ClaimsPrincipal? Principal => Context?.User;

    public Guid? UserId => ParseGuid(AppClaims.UserId);
    public Guid? OrganizationId => ParseGuid(AppClaims.OrganizationId);
    public string? Email => Principal?.FindFirst(ClaimTypes.Email)?.Value
                            ?? Principal?.FindFirst("email")?.Value;
    public string? FullName => Principal?.FindFirst(AppClaims.FullName)?.Value;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public IReadOnlySet<string> Permissions
    {
        get
        {
            RefreshCacheIfPrincipalChanged();
            return _cachedPermissions!;
        }
    }

    public IReadOnlyList<Guid> TeamIds
    {
        get
        {
            RefreshCacheIfPrincipalChanged();
            return _cachedTeamIds!;
        }
    }

    private void RefreshCacheIfPrincipalChanged()
    {
        var principal = Principal;

        if (_cachedPermissions is not null && ReferenceEquals(_cachedFor, principal))
        {
            return;
        }

        _cachedFor = principal;

        _cachedPermissions = principal?.FindAll(AppClaims.Permission)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        _cachedTeamIds = principal?.FindAll(AppClaims.TeamId)
            .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList()
            ?? [];
    }
    public Guid? DepartmentId => ParseGuid(AppClaims.DepartmentId);
    public Guid? OfficeId => ParseGuid(AppClaims.OfficeId);

    public DataScope Scope =>
        int.TryParse(Principal?.FindFirst(AppClaims.Scope)?.Value, out var value)
        && Enum.IsDefined(typeof(DataScope), value)
            ? (DataScope)value
            : DataScope.Own;

    public Guid CorrelationId { get; }

    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Context?.Request.Headers.UserAgent.ToString();

    public bool Has(string permission) => Permissions.Contains(permission);

    public void Require(string permission)
    {
        if (!Has(permission))
        {
            throw new ForbiddenException($"This action requires the '{permission}' permission.");
        }
    }

    private Guid? ParseGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirst(claimType)?.Value, out var value) ? value : null;

    /// <summary>
    /// Honours an inbound correlation id so a trace can span the frontend, the API and
    /// downstream services, and mints one when the caller did not supply a valid value.
    /// </summary>
    private static Guid ResolveCorrelationId(HttpContext? context)
    {
        if (context is not null
            && context.Request.Headers.TryGetValue(CorrelationHeader, out var header)
            && Guid.TryParse(header.ToString(), out var supplied))
        {
            return supplied;
        }

        return Guid.CreateVersion7();
    }
}
