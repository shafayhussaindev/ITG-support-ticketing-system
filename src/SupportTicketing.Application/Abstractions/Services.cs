using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Abstractions;

/// <summary>
/// The authenticated principal for the current request.
/// </summary>
/// <remarks>
/// This is the single source of truth for identity and tenancy. Handlers must read
/// <see cref="OrganizationId"/> from here and never from a route value, query string
/// or request body — trusting a client-supplied organization identifier is the
/// classic multi-tenant data-leak defect.
/// </remarks>
public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? OrganizationId { get; }
    string? Email { get; }
    string? FullName { get; }
    bool IsAuthenticated { get; }

    IReadOnlySet<string> Permissions { get; }
    IReadOnlyList<Guid> TeamIds { get; }
    Guid? DepartmentId { get; }
    Guid? OfficeId { get; }
    DataScope Scope { get; }

    /// <summary>Ties every log line, audit row and downstream call for this request together.</summary>
    Guid CorrelationId { get; }

    string? IpAddress { get; }
    string? UserAgent { get; }

    bool Has(string permission);

    /// <summary>Throws <see cref="ForbiddenException"/> when the permission is absent.</summary>
    void Require(string permission);
}

/// <summary>
/// Abstracts the clock so time-dependent logic — SLA deadlines, business hours,
/// lockout windows — is deterministically testable.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Returns whether the password matched, and whether the stored hash should be upgraded.</summary>
    (bool Succeeded, bool NeedsRehash) Verify(string hash, string password);

    /// <summary>
    /// A real hash of a random value, used to burn the same CPU time when no account
    /// matches the supplied email. Without it, "unknown email" returns measurably
    /// faster than "known email, wrong password", which turns the sign-in endpoint
    /// into an account-enumeration oracle.
    /// </summary>
    string DummyHash { get; }
}

public sealed record TokenPair(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public interface ITokenService
{
    /// <summary>Issues a signed access token carrying identity, tenancy, scope and permission claims.</summary>
    string CreateAccessToken(User user, IReadOnlyCollection<string> permissions, DataScope scope, out DateTime expiresAtUtc);

    /// <summary>Returns the opaque token to hand the client, and the SHA-256 hash to persist.</summary>
    (string Token, string Hash) CreateRefreshToken();

    string HashRefreshToken(string token);
}

/// <summary>
/// Writes immutable audit rows. Injected into handlers rather than called from the
/// DbContext so that the reason and decision source are explicit at the call site.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(
        AuditAction action,
        string entityType,
        Guid? entityId,
        string? entityReference = null,
        object? changes = null,
        string? reason = null,
        DecisionSource source = DecisionSource.Human,
        bool isFailure = false,
        string? failureReason = null,
        Guid? organizationIdOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when the principal is authenticated but lacks the required permission or scope.</summary>
public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : Exception(message);

/// <summary>
/// Thrown when a resource does not exist, or exists but is outside the caller's
/// tenant or scope. Both cases return 404 deliberately: a 403 would confirm the
/// record exists and leak information to an attacker enumerating identifiers.
/// </summary>
public sealed class NotFoundException(string entity, object key)
    : Exception($"{entity} '{key}' was not found.")
{
    public string Entity { get; } = entity;
}

/// <summary>Thrown when a request conflicts with current state, such as a concurrency clash.</summary>
public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
