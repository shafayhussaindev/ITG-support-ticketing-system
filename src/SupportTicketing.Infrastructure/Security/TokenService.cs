using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Infrastructure.Security;

/// <summary>
/// Issues signed access tokens and opaque refresh tokens.
/// </summary>
/// <remarks>
/// <para>
/// Permissions are embedded as claims so authorization needs no database round trip
/// per request. The cost is that a permission revoked mid-session stays effective
/// until the access token expires, which is why the lifetime defaults to fifteen
/// minutes. For immediate revocation an administrator deactivates the user or
/// revokes their refresh-token families, which blocks the next refresh.
/// </para>
/// <para>
/// Refresh tokens are opaque 256-bit random values, never JWTs. Only their SHA-256
/// hash is persisted, so a database disclosure does not yield usable tokens.
/// </para>
/// </remarks>
public sealed class TokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public string CreateAccessToken(
        User user,
        IReadOnlyCollection<string> permissions,
        DataScope scope,
        out DateTime expiresAtUtc)
    {
        var now = clock.UtcNow;
        expiresAtUtc = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(AppClaims.UserId, user.Id.ToString()),
            new(AppClaims.OrganizationId, user.OrganizationId.ToString()),
            new(AppClaims.FullName, user.FullName),
            new(AppClaims.Scope, ((int)scope).ToString())
        };

        if (user.DepartmentId is { } departmentId)
        {
            claims.Add(new Claim(AppClaims.DepartmentId, departmentId.ToString()));
        }

        if (user.OfficeId is { } officeId)
        {
            claims.Add(new Claim(AppClaims.OfficeId, officeId.ToString()));
        }

        foreach (var membership in user.TeamMemberships.Where(m => m.IsActive))
        {
            claims.Add(new Claim(AppClaims.TeamId, membership.TeamId.ToString()));
        }

        foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
        {
            claims.Add(new Claim(AppClaims.Permission, permission));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string Token, string Hash) CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes);
        return (token, HashRefreshToken(token));
    }

    public string HashRefreshToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
