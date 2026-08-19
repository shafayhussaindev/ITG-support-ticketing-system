using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Teams;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Admin;

// ----------------------------------------------------------------------- list

public sealed record ListUsersQuery(UserListQueryParameters Parameters)
    : IQuery<PagedResult<UserListItemResponse>>;

public sealed class ListUsersQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListUsersQuery, PagedResult<UserListItemResponse>>
{
    public async Task<PagedResult<UserListItemResponse>> HandleAsync(
        ListUsersQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        var p = query.Parameters;
        var page = p.Page < 1 ? 1 : p.Page;
        var pageSize = Math.Clamp(p.PageSize, 1, PagedQuery.MaxPageSize);

        var users = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(p.Search))
        {
            var term = p.Search.Trim();
            users = users.Where(u =>
                u.Email.Contains(term)
                || u.FirstName.Contains(term)
                || u.LastName.Contains(term)
                || (u.JobTitle != null && u.JobTitle.Contains(term)));
        }

        if (p.RoleId is { } roleId)
        {
            users = users.Where(u => u.UserRoles.Any(r => r.RoleId == roleId));
        }

        if (p.TeamId is { } teamId)
        {
            users = users.Where(u => u.TeamMemberships.Any(m => m.TeamId == teamId && m.IsActive));
        }

        if (p.DepartmentId is { } departmentId)
        {
            users = users.Where(u => u.DepartmentId == departmentId);
        }

        if (p.ActiveOnly == true)
        {
            users = users.Where(u => u.IsActive);
        }

        var total = await users.CountAsync(cancellationToken);

        if (total == 0)
        {
            return PagedResult<UserListItemResponse>.Empty(page, pageSize);
        }

        // Open-ticket counts are computed in one grouped query rather than a
        // correlated subquery per row: the list is the screen an administrator uses
        // to decide who is overloaded, and N+1 here is felt immediately.
        var openByAgent = await db.Tickets.AsNoTracking()
            .Where(t => t.AssignedAgentId != null
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Cancelled)
            .GroupBy(t => t.AssignedAgentId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, cancellationToken);

        var rows = await users
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.JobTitle,
                DepartmentName = u.Department == null ? null : u.Department.Name,
                OfficeName = u.Office == null ? null : u.Office.Name,
                Roles = u.UserRoles.Select(r => r.Role!.Name).ToList(),
                Teams = u.TeamMemberships.Where(m => m.IsActive).Select(m => m.Team!.Name).ToList(),
                u.IsActive,
                u.LockoutEndUtc,
                u.LastLoginAtUtc,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<UserListItemResponse>
        {
            Items =
            [
                .. rows.Select(u => new UserListItemResponse
                {
                    Id = u.Id,
                    Email = u.Email,
                    FullName = $"{u.FirstName} {u.LastName}",
                    JobTitle = u.JobTitle,
                    DepartmentName = u.DepartmentName,
                    OfficeName = u.OfficeName,
                    Roles = u.Roles,
                    Teams = u.Teams,
                    IsActive = u.IsActive,
                    LockoutEndUtc = u.LockoutEndUtc,
                    LastLoginAtUtc = u.LastLoginAtUtc,
                    OpenTickets = openByAgent.GetValueOrDefault(u.Id),
                })
            ],
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }
}

// --------------------------------------------------------------------- detail

public sealed record GetUserQuery(Guid Id) : IQuery<UserDetailResponse>;

public sealed class GetUserQueryHandler(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetUserQuery, UserDetailResponse>
{
    public async Task<UserDetailResponse> HandleAsync(
        GetUserQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        var user = await db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role!).ThenInclude(r => r.RolePermissions)
            .Include(u => u.TeamMemberships).ThenInclude(m => m.Team)
            .Include(u => u.PermissionOverrides).ThenInclude(o => o.Permission)
            .FirstOrDefaultAsync(u => u.Id == query.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), query.Id);

        var now = clock.UtcNow;

        var sessions = await db.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.UserId == user.Id && t.RevokedAtUtc == null && t.ExpiresAtUtc > now,
                cancellationToken);

        var permissions = await ResolvePermissionsAsync(db, user, now, cancellationToken);

        return new UserDetailResponse
        {
            Id = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            JobTitle = user.JobTitle,
            PhoneNumber = user.PhoneNumber,
            TimeZoneId = user.TimeZoneId,
            DepartmentId = user.DepartmentId,
            OfficeId = user.OfficeId,
            IsActive = user.IsActive,
            MustChangePassword = user.MustChangePassword,
            IsAvailableForAssignment = user.IsAvailableForAssignment,
            MaxConcurrentTickets = user.MaxConcurrentTickets,
            LockoutEndUtc = user.LockoutEndUtc,
            LastLoginAtUtc = user.LastLoginAtUtc,
            RoleIds = [.. user.UserRoles.Select(r => r.RoleId)],
            Teams =
            [
                .. user.TeamMemberships.Where(m => m.IsActive).Select(m => new TeamMembershipResponse
                {
                    TeamId = m.TeamId,
                    TeamName = m.Team?.Name ?? "—",
                    RoleInTeam = m.RoleInTeam.ToString(),
                    CapacityWeight = m.CapacityWeight,
                })
            ],
            EffectivePermissions = permissions,
            ActiveSessions = sessions,
        };
    }

    /// <summary>
    /// The union of the user's roles, then overrides applied — denies last.
    /// </summary>
    /// <remarks>
    /// Shown read-only so an administrator can answer "why can this person do that?"
    /// without reasoning across three tables in their head. A deny beats every role
    /// grant, which is exactly the case that is hard to see from the role list alone.
    /// </remarks>
    internal static async Task<IReadOnlyList<string>> ResolvePermissionsAsync(
        IAppDbContext db, User user, DateTime now, CancellationToken cancellationToken)
    {
        var roleIds = user.UserRoles.Select(r => r.RoleId).ToList();

        var granted = await db.RolePermissions.AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission!.Key)
            .Distinct()
            .ToListAsync(cancellationToken);

        var effective = granted.ToHashSet(StringComparer.Ordinal);

        foreach (var over in user.PermissionOverrides
                     .Where(o => o.ExpiresAtUtc is null || o.ExpiresAtUtc > now))
        {
            var key = over.Permission?.Key;

            if (key is null)
            {
                continue;
            }

            if (over.IsGranted)
            {
                effective.Add(key);
            }
            else
            {
                effective.Remove(key);
            }
        }

        return [.. effective.OrderBy(k => k, StringComparer.Ordinal)];
    }
}

// --------------------------------------------------------------------- create

public sealed record CreateUserCommand(CreateUserRequest Request)
    : ICommand<TemporaryPasswordResponse>;

public sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(c => c.Request.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Request.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.LastName).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.JobTitle).MaximumLength(150);
    }
}

/// <summary>
/// Creates an account with a generated one-time password.
/// </summary>
/// <remarks>
/// The administrator never chooses the password. Letting them set one produces
/// predictable values across an organization — every new starter given the same
/// string — and leaves the administrator holding a credential they had no need to
/// know. A random one, shown once, flagged for change at first sign-in, avoids both.
/// </remarks>
public sealed class CreateUserCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IPasswordHasher hasher, IAuditWriter audit, IClock clock)
    : ICommandHandler<CreateUserCommand, TemporaryPasswordResponse>
{
    public async Task<TemporaryPasswordResponse> HandleAsync(
        CreateUserCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var request = command.Request;
        var email = request.Email.Trim();
        var normalized = email.ToUpperInvariant();

        var exists = await db.Users.AsNoTracking()
            .AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (exists)
        {
            throw new ConflictException("user_exists", "An account with that email address already exists.");
        }

        var temporary = TemporaryPassword.Generate();
        var now = clock.UtcNow;

        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            NormalizedEmail = normalized,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            JobTitle = request.JobTitle?.Trim(),
            PhoneNumber = request.PhoneNumber?.Trim(),
            TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "UTC" : request.TimeZoneId,
            DepartmentId = request.DepartmentId,
            OfficeId = request.OfficeId,
            PasswordHash = hasher.Hash(temporary),
            MustChangePassword = true,
            IsActive = true,
            PasswordChangedAtUtc = now,
        };

        db.Users.Add(user);

        foreach (var roleId in (request.RoleIds ?? []).Distinct())
        {
            db.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = roleId,
                GrantedAtUtc = now,
                GrantedBy = currentUser.UserId,
            });
        }

        // The email is recorded; the password is not, in any form.
        await audit.WriteAsync(
            AuditAction.Created, nameof(User), user.Id, user.Email,
            changes: new { user.Email, user.FirstName, user.LastName, Roles = request.RoleIds?.Count ?? 0 },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new TemporaryPasswordResponse
        {
            TemporaryPassword = temporary,
            Notice =
                "Shown once and never stored in readable form. Pass it to the user by a "
                + "separate channel; they must change it at first sign-in.",
        };
    }
}

// --------------------------------------------------------------------- update

public sealed record UpdateUserCommand(Guid Id, UpdateUserRequest Request) : ICommand<UserDetailResponse>;

public sealed class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(c => c.Request.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.LastName).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.MaxConcurrentTickets).InclusiveBetween(0, 500);
    }
}

public sealed class UpdateUserCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<UpdateUserCommand, UserDetailResponse>
{
    public async Task<UserDetailResponse> HandleAsync(
        UpdateUserCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        var r = command.Request;

        user.FirstName = r.FirstName.Trim();
        user.LastName = r.LastName.Trim();
        user.JobTitle = r.JobTitle?.Trim();
        user.PhoneNumber = r.PhoneNumber?.Trim();
        user.TimeZoneId = string.IsNullOrWhiteSpace(r.TimeZoneId) ? user.TimeZoneId : r.TimeZoneId;
        user.DepartmentId = r.DepartmentId;
        user.OfficeId = r.OfficeId;
        user.IsAvailableForAssignment = r.IsAvailableForAssignment;
        user.MaxConcurrentTickets = r.MaxConcurrentTickets;

        await audit.WriteAsync(
            AuditAction.Updated, nameof(User), user.Id, user.Email,
            changes: new
            {
                user.FirstName, user.LastName, user.JobTitle,
                user.DepartmentId, user.IsAvailableForAssignment, user.MaxConcurrentTickets,
            },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await dispatcher.QueryAsync(new GetUserQuery(command.Id), cancellationToken);
    }
}

// ---------------------------------------------------------------------- roles

public sealed record SetUserRolesCommand(Guid Id, SetUserRolesRequest Request)
    : ICommand<UserDetailResponse>;

/// <summary>
/// Replaces a user's roles wholesale.
/// </summary>
/// <remarks>
/// <para>
/// Set semantics rather than add/remove: an administrator looking at a checkbox list
/// is describing the end state, and a partial-update API invites a race where two
/// administrators each remove the role the other just added.
/// </para>
/// <para>
/// Permissions live in the access token, so a change here does not take effect for an
/// existing session until it expires — fifteen minutes by default. Removing every
/// role from a hostile account is therefore not the right containment tool;
/// deactivating them, which revokes their sessions, is.
/// </para>
/// </remarks>
public sealed class SetUserRolesCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit, IClock clock)
    : ICommandHandler<SetUserRolesCommand, UserDetailResponse>
{
    public async Task<UserDetailResponse> HandleAsync(
        SetUserRolesCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        var user = await db.Users.AsTracking()
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        var requested = command.Request.RoleIds.Distinct().ToList();

        // Roles are tenant-scoped, so this also rejects a role identifier copied from
        // another organization — the filter makes it simply not exist.
        var valid = await db.Roles.AsNoTracking()
            .Where(role => requested.Contains(role.Id))
            .Select(role => new { role.Id, role.Name })
            .ToListAsync(cancellationToken);

        if (valid.Count != requested.Count)
        {
            throw new ValidationException("One or more of those roles do not exist.");
        }

        var before = user.UserRoles.Select(r => r.RoleId).ToHashSet();
        var after = valid.Select(v => v.Id).ToHashSet();

        foreach (var removed in user.UserRoles.Where(r => !after.Contains(r.RoleId)).ToList())
        {
            db.UserRoles.Remove(removed);
        }

        foreach (var added in after.Where(id => !before.Contains(id)))
        {
            db.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = added,
                GrantedAtUtc = clock.UtcNow,
                GrantedBy = currentUser.UserId,
            });
        }

        await audit.WriteAsync(
            AuditAction.RoleChanged, nameof(User), user.Id, user.Email,
            changes: new { Roles = string.Join(", ", valid.Select(v => v.Name)) },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await dispatcher.QueryAsync(new GetUserQuery(command.Id), cancellationToken);
    }
}

// ----------------------------------------------------------- activate / lock

public sealed record SetUserActiveCommand(Guid Id, SetUserActiveRequest Request)
    : ICommand<UserDetailResponse>;

/// <summary>
/// Deactivates or restores an account.
/// </summary>
/// <remarks>
/// Deactivation revokes every refresh token the user holds, which is what actually
/// ends their access — clearing the flag alone would leave a live session running
/// until its access token expired. Accounts are never deleted: their name is attached
/// to tickets, comments and audit rows that must stay attributable.
/// </remarks>
public sealed class SetUserActiveCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit, IClock clock)
    : ICommandHandler<SetUserActiveCommand, UserDetailResponse>
{
    public async Task<UserDetailResponse> HandleAsync(
        SetUserActiveCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        if (command.Id == currentUser.UserId && !command.Request.IsActive)
        {
            throw new ConflictException(
                "cannot_deactivate_self",
                "You cannot deactivate your own account. Ask another administrator.");
        }

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        var now = clock.UtcNow;
        user.IsActive = command.Request.IsActive;

        if (!command.Request.IsActive)
        {
            await RevokeSessionsAsync(db, user.Id, now, "Account deactivated", cancellationToken);
        }
        else
        {
            // Restoring an account also clears a lockout: an administrator turning it
            // back on has made a decision that outranks a failed-password counter.
            user.LockoutEndUtc = null;
            user.AccessFailedCount = 0;
        }

        await audit.WriteAsync(
            command.Request.IsActive ? AuditAction.Updated : AuditAction.Deleted,
            nameof(User), user.Id, user.Email,
            changes: new { user.IsActive },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await dispatcher.QueryAsync(new GetUserQuery(command.Id), cancellationToken);
    }

    internal static async Task RevokeSessionsAsync(
        IAppDbContext db, Guid userId, DateTime now, string reason, CancellationToken cancellationToken)
    {
        var tokens = await db.RefreshTokens.AsTracking()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = reason;
        }
    }
}

// ------------------------------------------------------------- reset password

public sealed record ResetUserPasswordCommand(Guid Id) : ICommand<TemporaryPasswordResponse>;

public sealed class ResetUserPasswordCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IPasswordHasher hasher, IAuditWriter audit, IClock clock)
    : ICommandHandler<ResetUserPasswordCommand, TemporaryPasswordResponse>
{
    public async Task<TemporaryPasswordResponse> HandleAsync(
        ResetUserPasswordCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        var temporary = TemporaryPassword.Generate();
        var now = clock.UtcNow;

        user.PasswordHash = hasher.Hash(temporary);
        user.MustChangePassword = true;
        user.PasswordChangedAtUtc = now;
        user.LockoutEndUtc = null;
        user.AccessFailedCount = 0;

        // Every existing session dies with the old password. A reset that left them
        // running would not actually remove access from whoever prompted the reset.
        await SetUserActiveCommandHandler.RevokeSessionsAsync(
            db, user.Id, now, "Password reset by an administrator", cancellationToken);

        await audit.WriteAsync(
            AuditAction.PasswordChanged, nameof(User), user.Id, user.Email,
            changes: new { ResetByAdministrator = true, SessionsRevoked = true },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new TemporaryPasswordResponse
        {
            TemporaryPassword = temporary,
            Notice =
                "Every session for this account has been signed out. The password is shown "
                + "once; the user must change it at their next sign-in.",
        };
    }
}

/// <summary>
/// Generates the one-time passwords issued when an account is created or reset.
/// </summary>
/// <remarks>
/// Sixteen characters from a 31-symbol alphabet — about 79 bits. The alphabet omits
/// glyphs that are misread when a password is dictated or copied off a screen (no
/// 0/O, no 1/l/I): this password exists to survive one sign-in, and one that has to
/// be typed three times because of an ambiguous character tends to get written down
/// instead. The hyphen guarantees a non-alphanumeric for any policy that demands one,
/// without weakening the random portion.
/// </remarks>
internal static class TemporaryPassword
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    internal static string Generate()
    {
        var characters = new char[16];

        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{new string(characters[..8])}-{new string(characters[8..])}";
    }
}

// ------------------------------------------------------------ revoke sessions

public sealed record RevokeUserSessionsCommand(Guid Id) : ICommand<int>;

public sealed class RevokeUserSessionsCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<RevokeUserSessionsCommand, int>
{
    public async Task<int> HandleAsync(
        RevokeUserSessionsCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageUsers);

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        var now = clock.UtcNow;

        var count = await db.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.UserId == user.Id && t.RevokedAtUtc == null, cancellationToken);

        await SetUserActiveCommandHandler.RevokeSessionsAsync(
            db, user.Id, now, "Signed out by an administrator", cancellationToken);

        await audit.WriteAsync(
            AuditAction.LoggedOut, nameof(User), user.Id, user.Email,
            changes: new { SessionsRevoked = count, ByAdministrator = true },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return count;
    }
}

// --------------------------------------------------------------------- delete

public sealed record DeleteUserCommand(Guid Id) : ICommand<bool>;

/// <summary>
/// Removes an account outright, for a Super Admin.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. An account that has raised a ticket, been assigned one, or
/// written a comment cannot be deleted, because the rows referencing it are the work
/// itself — removing the account would either destroy that work or leave a ticket with
/// no requester. Deactivation is the answer there, and the refusal says so.
/// </para>
/// <para>
/// What this is for is the account that should not exist: a typo in an email address,
/// a duplicate, someone provisioned for a role they never took up. Those accumulate,
/// and deactivating them leaves a user list nobody can read.
/// </para>
/// <para>
/// Audit rows survive. They hold the actor's name and email as a snapshot rather than
/// a foreign key, precisely so history stays readable when the account behind it is
/// gone.
/// </para>
/// </remarks>
public sealed class DeleteUserCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<DeleteUserCommand, bool>
{
    public async Task<bool> HandleAsync(
        DeleteUserCommand command, CancellationToken cancellationToken)
    {
        // Not users.manage. Deleting is a different act from administering, and the
        // one person who should be able to do it is the one who answers for the tenant.
        currentUser.Require(Permissions.Administration.ManageOrganizations);

        if (command.Id == currentUser.UserId)
        {
            throw new ConflictException(
                "cannot_delete_self", "You cannot delete your own account.");
        }

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        var blockers = await DescribeWorkAsync(db, user.Id, cancellationToken);

        if (blockers.Count > 0)
        {
            throw new ConflictException(
                "user_owns_work",
                $"{user.Email} cannot be deleted because the account owns "
                + $"{string.Join(", ", blockers)}. Removing it would leave that work "
                + "without an owner, or destroy it. Deactivate the account instead — "
                + "that revokes every session and keeps the history readable.");
        }

        // Rows that exist only to describe the account, and mean nothing once it is
        // gone. Removed explicitly rather than by cascade, so what is destroyed is
        // written down here rather than inferred from the schema.
        await db.UserRoles.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.UserSkills.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.UserPermissionOverrides.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.RefreshTokens.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.TeamMembers.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.NotificationPreferences.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Notifications.Where(r => r.RecipientUserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        // Written before the row goes, so the audit entry carries the name and email
        // of an account that will not be there to look up afterwards.
        await audit.WriteAsync(
            AuditAction.Deleted, nameof(User), user.Id, user.Email,
            changes: new { user.Email, Name = user.FullName, Permanent = true },
            cancellationToken: cancellationToken);

        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Describes, in words, what the account owns — for the refusal message.
    /// </summary>
    /// <remarks>
    /// Counting rather than existence-checking so the message can say "four tickets"
    /// instead of "some work", which is the difference between an administrator
    /// knowing what to reassign and having to go and find out.
    /// </remarks>
    private static async Task<List<string>> DescribeWorkAsync(
        IAppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        var raised = await db.Tickets.IgnoreQueryFilters()
            .CountAsync(t => t.RequesterId == userId, cancellationToken);

        if (raised > 0) blockers.Add($"{raised} raised {Plural(raised, "ticket")}");

        var assigned = await db.Tickets.IgnoreQueryFilters()
            .CountAsync(t => t.AssignedAgentId == userId, cancellationToken);

        if (assigned > 0) blockers.Add($"{assigned} assigned {Plural(assigned, "ticket")}");

        var comments = await db.TicketComments.IgnoreQueryFilters()
            .CountAsync(c => c.AuthorId == userId, cancellationToken);

        if (comments > 0) blockers.Add($"{comments} {Plural(comments, "comment")}");

        var articles = await db.KnowledgeArticles.IgnoreQueryFilters()
            .CountAsync(a => a.AuthorId == userId, cancellationToken);

        if (articles > 0) blockers.Add($"{articles} knowledge {Plural(articles, "article")}");

        var leads = await db.Teams.IgnoreQueryFilters()
            .CountAsync(t => t.TeamLeadId == userId, cancellationToken);

        if (leads > 0) blockers.Add($"the lead role on {leads} {Plural(leads, "team")}");

        return blockers;
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";
}
