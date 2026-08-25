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

        // A deleted account keeps its row so tickets stay attributable, but it is not
        // a person any more and has no business in a list of people.
        var users = db.Users.AsNoTracking().Where(u => !u.IsAnonymised);

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
        var openByStaff = await db.Tickets.AsNoTracking()
            .Where(t => t.AssignedStaffId != null
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Cancelled)
            .GroupBy(t => t.AssignedStaffId!.Value)
            .Select(g => new { StaffId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StaffId, x => x.Count, cancellationToken);

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
                    OpenTickets = openByStaff.GetValueOrDefault(u.Id),
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

public sealed record DeleteUserCommand(Guid Id) : ICommand<DeleteUserResult>;

public sealed record DeleteUserResult(bool RowRemoved, string Message);

/// <summary>
/// Deletes an account, for a Super Admin.
/// </summary>
/// <remarks>
/// <para>
/// Two outcomes, decided by whether the account left anything behind.
/// </para>
/// <para>
/// An account that owns nothing — a mistyped address, a duplicate, somebody
/// provisioned for a role they never took up — is removed outright, row and all.
/// </para>
/// <para>
/// An account that raised tickets, was assigned them, or wrote comments is
/// <em>anonymised</em>: the name becomes "Deleted user", the email an unroutable
/// placeholder, the password hash a random value, and the row is left in place. It has
/// to be. Every ticket points at it, and a system whose whole claim is that changes are
/// attributable cannot answer "who raised this?" with a dangling identifier. What a
/// reader sees on the ticket is that the person is gone, which is the honest answer —
/// and what is actually destroyed is the identity, which is the part a deletion is
/// really about.
/// </para>
/// <para>
/// Anonymised accounts are hidden from every list where somebody picks a person, so
/// they do not accumulate in the interface. Audit rows are untouched: they hold the
/// name and email as a snapshot rather than a foreign key, which is what lets an
/// investigation reconstruct who did what before the account was removed.
/// </para>
/// </remarks>
public sealed class DeleteUserCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IPasswordHasher hasher, IAuditWriter audit, IClock clock)
    : ICommandHandler<DeleteUserCommand, DeleteUserResult>
{
    public async Task<DeleteUserResult> HandleAsync(
        DeleteUserCommand command, CancellationToken cancellationToken)
    {
        // Not users.manage. Deleting is a different act from administering, and the one
        // person who should be able to do it is the one who answers for the tenant.
        currentUser.Require(Permissions.Administration.ManageOrganizations);

        if (command.Id == currentUser.UserId)
        {
            throw new ConflictException(
                "cannot_delete_self", "You cannot delete your own account.");
        }

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Id);

        if (user.IsAnonymised)
        {
            throw new ConflictException(
                "already_deleted", "That account has already been deleted.");
        }

        var previousEmail = user.Email;
        var previousName = user.FullName;
        var owned = await CountOwnedWorkAsync(db, user.Id, cancellationToken);

        // Membership, sessions and preferences describe the account rather than its
        // work, and mean nothing once it is gone. Removed in both cases.
        await db.UserRoles.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.UserSkills.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.UserPermissionOverrides.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.RefreshTokens.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.TeamMembers.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.NotificationPreferences.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Notifications.Where(r => r.RecipientUserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        // A team pointing at a departed lead would fail to load. Cleared rather than
        // reassigned, because who leads it next is not this command's decision.
        await db.Teams.Where(t => t.TeamLeadId == user.Id)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.TeamLeadId, (Guid?)null), cancellationToken);

        // Written before the change, so the entry carries the identity that is about to
        // stop existing.
        await audit.WriteAsync(
            AuditAction.Deleted, nameof(User), user.Id, previousEmail,
            changes: new
            {
                Email = previousEmail,
                Name = previousName,
                Anonymised = owned.Total > 0,
                Retained = owned.Describe(),
            },
            cancellationToken: cancellationToken);

        if (owned.Total == 0)
        {
            db.Users.Remove(user);
            await db.SaveChangesAsync(cancellationToken);

            return new DeleteUserResult(
                true, $"{previousEmail} has been deleted. The account owned no work.");
        }

        Anonymise(user, hasher, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        return new DeleteUserResult(
            false,
            $"{previousEmail} has been deleted. {owned.Describe()} remain and now show the "
            + "person as \"Deleted user\", because that history has to stay attributable.");
    }

    /// <summary>
    /// Strips everything that identifies the person, leaving the row references depend on.
    /// </summary>
    private static void Anonymise(User user, IPasswordHasher hasher, DateTime now)
    {
        // Reads naturally wherever a name is displayed: "Deleted user" appears in a
        // ticket's requester field exactly where the person's name used to, so none of
        // the forty-odd places that project a name needed changing.
        user.FirstName = "Deleted";
        user.LastName = "user";

        // .invalid is reserved by RFC 2606 and can never resolve, so this cannot
        // accidentally become a routable address. The identifier keeps it unique.
        user.Email = $"deleted-{user.Id:N}@deleted.invalid";
        user.NormalizedEmail = user.Email.ToUpperInvariant();

        user.PhoneNumber = null;
        user.JobTitle = null;
        user.AvatarUrl = null;
        user.TwoFactorSecret = null;
        user.TwoFactorEnabled = false;

        // A fresh random hash rather than a blank one: the credential is destroyed
        // rather than disabled, so flipping the flag back could not restore access.
        user.PasswordHash = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        user.IsActive = false;
        user.IsAnonymised = true;
        user.AnonymisedAtUtc = now;
        user.MustChangePassword = false;
        user.LockoutEndUtc = null;
        user.AccessFailedCount = 0;
        user.IsAvailableForAssignment = false;
    }

    private sealed record OwnedWork(int Raised, int Assigned, int Comments, int Articles)
    {
        internal int Total => Raised + Assigned + Comments + Articles;

        /// <summary>
        /// Names what is being kept, in words.
        /// </summary>
        /// <remarks>
        /// Counted rather than merely detected, so the confirmation can say "4 tickets"
        /// instead of "some work" — the difference between an administrator
        /// understanding what just happened and guessing at it.
        /// </remarks>
        internal string Describe()
        {
            var parts = new List<string>();

            if (Raised > 0) parts.Add($"{Raised} raised {Plural(Raised, "ticket")}");
            if (Assigned > 0) parts.Add($"{Assigned} assigned {Plural(Assigned, "ticket")}");
            if (Comments > 0) parts.Add($"{Comments} {Plural(Comments, "comment")}");
            if (Articles > 0) parts.Add($"{Articles} knowledge {Plural(Articles, "article")}");

            return parts.Count == 0 ? "Nothing" : string.Join(", ", parts);
        }

        private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";
    }

    private static async Task<OwnedWork> CountOwnedWorkAsync(
        IAppDbContext db, Guid userId, CancellationToken cancellationToken) => new(
        await db.Tickets.IgnoreQueryFilters().CountAsync(t => t.RequesterId == userId, cancellationToken),
        await db.Tickets.IgnoreQueryFilters().CountAsync(t => t.AssignedStaffId == userId, cancellationToken),
        await db.TicketComments.IgnoreQueryFilters().CountAsync(c => c.AuthorId == userId, cancellationToken),
        await db.KnowledgeArticles.IgnoreQueryFilters().CountAsync(a => a.AuthorId == userId, cancellationToken));
}
