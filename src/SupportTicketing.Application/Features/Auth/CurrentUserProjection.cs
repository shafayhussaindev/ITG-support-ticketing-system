using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auth;

/// <summary>
/// Public entry point to <see cref="ProfileBuilder"/> for callers outside this
/// assembly, such as the <c>GET /auth/me</c> endpoint.
/// </summary>
public static class CurrentUserProjection
{
    public static Task<CurrentUserResponse> BuildAsync(
        IAppDbContext db,
        User user,
        ResolvedAccess access,
        CancellationToken cancellationToken) =>
        ProfileBuilder.BuildAsync(db, user, access, cancellationToken);
}
