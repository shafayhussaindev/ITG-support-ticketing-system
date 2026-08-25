using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Admin;

public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleResponse>>;

public sealed class ListRolesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleResponse>>
{
    public async Task<IReadOnlyList<RoleResponse>> HandleAsync(
        ListRolesQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        var roles = await db.Roles.AsNoTracking()
            .OrderByDescending(r => r.Rank)
            .ThenBy(r => r.Name)
            .Select(r => new RoleResponse
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                DefaultScope = r.DefaultScope.ToString(),
                Rank = r.Rank,
                IsSystemRole = r.IsSystemRole,
                UserCount = r.UserRoles.Count,
                Permissions = r.RolePermissions.Select(rp => rp.Permission!.Key).ToList(),
            })
            .ToListAsync(cancellationToken);

        return roles;
    }
}

public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionResponse>>;

/// <summary>
/// The permission catalogue, grouped by area.
/// </summary>
/// <remarks>
/// Read from the table rather than from <see cref="Permissions.All"/>, so a key that
/// was added to the constant list but never seeded — and therefore cannot be granted
/// — does not appear as an option that silently does nothing.
/// </remarks>
public sealed class ListPermissionsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionResponse>>
{
    public async Task<IReadOnlyList<PermissionResponse>> HandleAsync(
        ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        return await db.Permissions.AsNoTracking()
            .OrderBy(p => p.Category).ThenBy(p => p.Key)
            .Select(p => new PermissionResponse
            {
                Key = p.Key,
                Name = p.Name,
                Category = p.Category,
                Description = p.Description,
            })
            .ToListAsync(cancellationToken);
    }
}

// --------------------------------------------------------------------- create

public sealed record CreateRoleCommand(CreateRoleRequest Request) : ICommand<RoleResponse>;

public sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.DefaultScope)
            .Must(scope => Enum.TryParse<DataScope>(scope, ignoreCase: true, out _))
            .WithMessage("Scope must be one of Own, Assigned, Team, Department, Organization or All.");
        RuleFor(c => c.Request.Rank).InclusiveBetween(0, 1000);
    }
}

public sealed class CreateRoleCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<CreateRoleCommand, RoleResponse>
{
    public async Task<RoleResponse> HandleAsync(
        CreateRoleCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var request = command.Request;
        var name = request.Name.Trim();

        if (await db.Roles.AsNoTracking().AnyAsync(r => r.Name == name, cancellationToken))
        {
            throw new ConflictException("role_exists", $"A role named '{name}' already exists.");
        }

        var role = new Role
        {
            OrganizationId = organizationId,
            Name = name,
            Description = request.Description?.Trim(),
            DefaultScope = Enum.Parse<DataScope>(request.DefaultScope, ignoreCase: true),
            Rank = request.Rank,

            // Only the seeder creates system roles. One made here is an ordinary role
            // and stays deletable, which is what an administrator expects of something
            // they just invented.
            IsSystemRole = false,
        };

        db.Roles.Add(role);

        await ApplyPermissionsAsync(db, role, request.PermissionKeys ?? [], cancellationToken);

        await audit.WriteAsync(
            AuditAction.Created, nameof(Role), role.Id, role.Name,
            changes: new { role.Name, Scope = role.DefaultScope.ToString(), role.Rank },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await RoleRanking.NormaliseAsync(db, cancellationToken);

        var roles = await dispatcher.QueryAsync(new ListRolesQuery(), cancellationToken);
        return roles.First(r => r.Id == role.Id);
    }

    /// <summary>
    /// Replaces a role's permission rows from a set of keys.
    /// </summary>
    /// <remarks>
    /// Keys that do not exist in the catalogue are rejected rather than skipped. A
    /// silently dropped key produces a role that looks correct in the request and is
    /// missing a permission in practice, which surfaces later as an unexplained 403.
    /// </remarks>
    internal static async Task ApplyPermissionsAsync(
        IAppDbContext db, Role role, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();

        var permissions = await db.Permissions.AsNoTracking()
            .Where(p => wanted.Contains(p.Key))
            .Select(p => new { p.Id, p.Key })
            .ToListAsync(cancellationToken);

        if (permissions.Count != wanted.Count)
        {
            var known = permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var unknown = wanted.Where(k => !known.Contains(k));

            throw new ValidationException(
                $"Unknown permission keys: {string.Join(", ", unknown)}.");
        }

        var existing = await db.RolePermissions.AsTracking()
            .Where(rp => rp.RoleId == role.Id)
            .ToListAsync(cancellationToken);

        var wantedIds = permissions.Select(p => p.Id).ToHashSet();

        foreach (var removed in existing.Where(rp => !wantedIds.Contains(rp.PermissionId)))
        {
            db.RolePermissions.Remove(removed);
        }

        var currentIds = existing.Select(rp => rp.PermissionId).ToHashSet();

        foreach (var added in wantedIds.Where(id => !currentIds.Contains(id)))
        {
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = added });
        }
    }
}

// --------------------------------------------------------------------- update

public sealed record UpdateRoleCommand(Guid Id, UpdateRoleRequest Request) : ICommand<RoleResponse>;

public sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator()
    {
        RuleFor(c => c.Request.DefaultScope)
            .Must(scope => Enum.TryParse<DataScope>(scope, ignoreCase: true, out _))
            .WithMessage("Scope must be one of Own, Assigned, Team, Department, Organization or All.");
        RuleFor(c => c.Request.Rank).InclusiveBetween(0, 1000);
    }
}

public sealed class UpdateRoleCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<UpdateRoleCommand, RoleResponse>
{
    public async Task<RoleResponse> HandleAsync(
        UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        var role = await db.Roles.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(Role), command.Id);

        var scope = Enum.Parse<DataScope>(command.Request.DefaultScope, ignoreCase: true);

        role.Description = command.Request.Description?.Trim();
        role.DefaultScope = scope;
        role.Rank = command.Request.Rank;

        await audit.WriteAsync(
            AuditAction.Updated, nameof(Role), role.Id, role.Name,
            changes: new { Scope = scope.ToString(), role.Rank, role.Description },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await RoleRanking.NormaliseAsync(db, cancellationToken);

        var roles = await dispatcher.QueryAsync(new ListRolesQuery(), cancellationToken);
        return roles.First(r => r.Id == role.Id);
    }
}

// ---------------------------------------------------------------- permissions

public sealed record SetRolePermissionsCommand(Guid Id, SetRolePermissionsRequest Request)
    : ICommand<RoleResponse>;

/// <summary>
/// Replaces the permissions a role carries.
/// </summary>
/// <remarks>
/// <para>
/// Permitted on system roles too. The seeded roles are a starting point, not a
/// contract — an organization that wants its staff to change priority should be able
/// to say so without a deployment. What system roles cannot do is be renamed or
/// deleted, because code and seed data refer to them by name.
/// </para>
/// <para>
/// The change lands in the database immediately but not in anyone's session:
/// permissions ride in the access token and are re-read at refresh, so an existing
/// token keeps its old set for up to its remaining lifetime. That is a deliberate
/// trade for a stateless check on every request, and it is why removing a permission
/// is not a containment measure.
/// </para>
/// </remarks>
public sealed class SetRolePermissionsCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SetRolePermissionsCommand, RoleResponse>
{
    public async Task<RoleResponse> HandleAsync(
        SetRolePermissionsCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        var role = await db.Roles.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(Role), command.Id);

        var keys = command.Request.PermissionKeys;

        // Guards the one edit nobody recovers from without database access: an
        // administrator removing the ability to administer, from the role that is the
        // only way back in. Checked against the caller's own roles specifically.
        if (!keys.Contains(Permissions.Administration.ManageRoles, StringComparer.Ordinal))
        {
            var callerHoldsThisRole = await db.UserRoles.AsNoTracking()
                .AnyAsync(ur => ur.RoleId == role.Id && ur.UserId == currentUser.UserId, cancellationToken);

            var othersCanStillManage = await db.RolePermissions.AsNoTracking()
                .AnyAsync(rp => rp.RoleId != role.Id
                                && rp.Permission!.Key == Permissions.Administration.ManageRoles
                                && rp.Role!.UserRoles.Any(),
                    cancellationToken);

            if (callerHoldsThisRole && !othersCanStillManage)
            {
                throw new ConflictException(
                    "last_role_manager",
                    "Removing roles.manage from this role would leave nobody able to manage "
                    + "roles. Grant it to another role that has members first.");
            }
        }

        await CreateRoleCommandHandler.ApplyPermissionsAsync(db, role, keys, cancellationToken);

        await audit.WriteAsync(
            AuditAction.PermissionChanged, nameof(Role), role.Id, role.Name,
            changes: new { PermissionCount = keys.Count, Permissions = string.Join(",", keys.Order()) },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var roles = await dispatcher.QueryAsync(new ListRolesQuery(), cancellationToken);
        return roles.First(r => r.Id == role.Id);
    }
}

// --------------------------------------------------------------------- delete

public sealed record DeleteRoleCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteRoleCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<DeleteRoleCommand, bool>
{
    public async Task<bool> HandleAsync(
        DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageRoles);

        var role = await db.Roles.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(Role), command.Id);

        if (role.IsSystemRole)
        {
            throw new ConflictException(
                "system_role",
                $"'{role.Name}' is a system role. Its permissions can be edited, but it "
                + "cannot be removed — seed data and documentation refer to it by name.");
        }

        var members = await db.UserRoles.AsNoTracking()
            .CountAsync(ur => ur.RoleId == role.Id, cancellationToken);

        if (members > 0)
        {
            throw new ConflictException(
                "role_in_use",
                $"{members} {(members == 1 ? "person holds" : "people hold")} this role. "
                + "Move them to another role first — deleting it here would silently strip "
                + "their permissions.");
        }

        db.Roles.Remove(role);

        await audit.WriteAsync(
            AuditAction.Deleted, nameof(Role), role.Id, role.Name,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}

/// <summary>
/// Keeps role ranks evenly spaced.
/// </summary>
internal static class RoleRanking
{
    /// <summary>The gap left between neighbours, and the rank of the lowest role.</summary>
    internal const int Step = 10;

    /// <summary>
    /// Re-spaces every role to 10, 20, 30 … in its existing order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rank is an ordering hint and nothing else — no authorization check reads it — so
    /// renumbering costs nothing and changes no behaviour. What it buys is the guarantee
    /// that two neighbouring roles always have integers between them.
    /// </para>
    /// <para>
    /// That guarantee is what lets an administrator say "put this one between Manager
    /// and Team Lead" instead of inventing a number. The position is turned into a rank
    /// half way between its neighbours, and without re-spacing the gap halves on every
    /// insertion: 10, 5, 2, 1, and then there is nowhere left to put anything and two
    /// roles collide. Re-spacing afterwards means the next insertion always has room.
    /// </para>
    /// <para>
    /// Ties are broken by name so the order is deterministic. Two roles that somehow
    /// arrived at the same rank would otherwise be re-spaced differently on each call,
    /// and the ladder would appear to shuffle itself.
    /// </para>
    /// </remarks>
    internal static async Task NormaliseAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsTracking()
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var changed = false;

        for (var i = 0; i < roles.Count; i++)
        {
            var rank = (i + 1) * Step;

            if (roles[i].Rank == rank)
            {
                continue;
            }

            roles[i].Rank = rank;
            changed = true;
        }

        // Saving unconditionally would write an audit-free update on every role edit,
        // including the overwhelmingly common case where nothing moved.
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
