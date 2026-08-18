using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.Api.Security;

/// <summary>
/// Requires the principal to hold a specific permission key.
/// </summary>
/// <remarks>
/// Applied as <c>[HasPermission(Permissions.Tickets.Resolve)]</c>. Note that this
/// answers only "may this principal perform this verb". Which <em>rows</em> they may
/// act on is a separate question answered by the data-scope filter inside the
/// handler — conflating the two is how object-level authorization defects happen.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(string permission) => Policy = PolicyPrefix + permission;
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionAuthorizationHandler(ILogger<PermissionAuthorizationHandler> logger)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var granted = context.User
            .FindAll(AppClaims.Permission)
            .Any(c => string.Equals(c.Value, requirement.Permission, StringComparison.Ordinal));

        if (granted)
        {
            context.Succeed(requirement);
        }
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            logger.LogInformation(
                "Authorization denied: principal lacks permission {Permission}", requirement.Permission);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Materialises a policy for each permission key on first use, so adding a permission
/// never requires registering a policy by hand in startup.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var existing = await base.GetPolicyAsync(policyName);
        if (existing is not null)
        {
            return existing;
        }

        if (!policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
    }
}
