using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Teams;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Admin;

public sealed record ListTeamsQuery : IQuery<IReadOnlyList<TeamResponse>>;

public sealed class ListTeamsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListTeamsQuery, IReadOnlyList<TeamResponse>>
{
    public async Task<IReadOnlyList<TeamResponse>> HandleAsync(
        ListTeamsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageTeams);

        var open = db.Tickets.AsNoTracking()
            .Where(t => t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled);

        var openByTeam = await open
            .Where(t => t.AssignedTeamId != null)
            .GroupBy(t => t.AssignedTeamId!.Value)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, cancellationToken);

        // Counted per member *per team*, not per person. A member row sits inside a
        // team, so an administrator reading "Ayesha: 5 open" there reasonably takes it
        // to mean five on this team — and the guard that refuses to remove somebody
        // holding open work counts it exactly that way. Grouping by agent alone made
        // the number on the screen and the rule behind the button disagree.
        var openByAgentAndTeam = await open
            .Where(t => t.AssignedAgentId != null && t.AssignedTeamId != null)
            .GroupBy(t => new { AgentId = t.AssignedAgentId!.Value, TeamId = t.AssignedTeamId!.Value })
            .Select(g => new { g.Key.AgentId, g.Key.TeamId, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var openByMember = openByAgentAndTeam
            .ToDictionary(x => (x.AgentId, x.TeamId), x => x.Count);

        var teamNames = await db.Teams.AsNoTracking()
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var teams = await db.Teams.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Code,
                t.Description,
                t.DepartmentId,
                DepartmentName = t.Department == null ? null : t.Department.Name,
                t.TeamLeadId,
                TeamLeadName = t.TeamLead == null ? null : t.TeamLead.FirstName + " " + t.TeamLead.LastName,
                t.EscalationTeamId,
                t.AcceptanceTimeoutMinutes,
                t.IsActive,
                Members = t.Members
                    .Where(m => m.IsActive)
                    .Select(m => new
                    {
                        m.UserId,
                        FullName = m.User!.FirstName + " " + m.User.LastName,
                        m.User.Email,
                        m.RoleInTeam,
                        m.CapacityWeight,
                        UserActive = m.User.IsActive,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. teams.Select(t => new TeamResponse
            {
                Id = t.Id,
                Name = t.Name,
                Code = t.Code,
                Description = t.Description,
                DepartmentId = t.DepartmentId,
                DepartmentName = t.DepartmentName,
                TeamLeadId = t.TeamLeadId,
                TeamLeadName = t.TeamLeadName,
                EscalationTeamId = t.EscalationTeamId,
                EscalationTeamName = t.EscalationTeamId is { } id ? teamNames.GetValueOrDefault(id) : null,
                AcceptanceTimeoutMinutes = t.AcceptanceTimeoutMinutes,
                IsActive = t.IsActive,
                OpenTickets = openByTeam.GetValueOrDefault(t.Id),
                Members =
                [
                    .. t.Members
                        .OrderByDescending(m => m.RoleInTeam)
                        .ThenBy(m => m.FullName)
                        .Select(m => new TeamMemberResponse
                        {
                            UserId = m.UserId,
                            FullName = m.FullName,
                            Email = m.Email,
                            RoleInTeam = m.RoleInTeam.ToString(),
                            CapacityWeight = m.CapacityWeight,
                            IsActive = m.UserActive,
                            OpenTickets = openByMember.GetValueOrDefault((m.UserId, t.Id)),
                        })
                ],
            })
        ];
    }
}

// ------------------------------------------------------------ create / update

public sealed record SaveTeamCommand(Guid? Id, SaveTeamRequest Request) : ICommand<TeamResponse>;

public sealed class SaveTeamCommandValidator : AbstractValidator<SaveTeamCommand>
{
    public SaveTeamCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Code).NotEmpty().MaximumLength(20);
        RuleFor(c => c.Request.AcceptanceTimeoutMinutes).InclusiveBetween(1, 10_080);
    }
}

public sealed class SaveTeamCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveTeamCommand, TeamResponse>
{
    public async Task<TeamResponse> HandleAsync(
        SaveTeamCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageTeams);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;
        var code = r.Code.Trim().ToUpperInvariant();

        var clash = await db.Teams.AsNoTracking()
            .AnyAsync(t => t.Code == code && (command.Id == null || t.Id != command.Id), cancellationToken);

        if (clash)
        {
            throw new ConflictException("team_code_taken", $"Another team already uses the code '{code}'.");
        }

        Team team;

        if (command.Id is { } id)
        {
            team = await db.Teams.AsTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(Team), id);

            // A team escalating to itself is an infinite ladder. The engine would
            // notice eventually; refusing the configuration is cheaper than debugging
            // a loop at three in the morning.
            if (r.EscalationTeamId == id)
            {
                throw new ValidationException("A team cannot escalate to itself.");
            }
        }
        else
        {
            team = new Team { OrganizationId = organizationId, Name = r.Name, Code = code };
            db.Teams.Add(team);
        }

        team.Name = r.Name.Trim();
        team.Code = code;
        team.Description = r.Description?.Trim();
        team.DepartmentId = r.DepartmentId;
        team.TeamLeadId = r.TeamLeadId;
        team.EscalationTeamId = r.EscalationTeamId;
        team.AcceptanceTimeoutMinutes = r.AcceptanceTimeoutMinutes;
        team.IsActive = r.IsActive;

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(Team), team.Id, team.Name,
            changes: new { team.Name, team.Code, team.IsActive, team.AcceptanceTimeoutMinutes },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var teams = await dispatcher.QueryAsync(new ListTeamsQuery(), cancellationToken);
        return teams.First(t => t.Id == team.Id);
    }
}

// -------------------------------------------------------------------- members

public sealed record SaveTeamMemberCommand(Guid TeamId, SaveTeamMemberRequest Request)
    : ICommand<TeamResponse>;

public sealed class SaveTeamMemberCommandValidator : AbstractValidator<SaveTeamMemberCommand>
{
    public SaveTeamMemberCommandValidator()
    {
        // Zero is meaningful — a member who is on the team but not in the routing
        // rotation — so the floor is zero rather than a fraction.
        RuleFor(c => c.Request.CapacityWeight).InclusiveBetween(0m, 10m);
    }
}

public sealed class SaveTeamMemberCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveTeamMemberCommand, TeamResponse>
{
    public async Task<TeamResponse> HandleAsync(
        SaveTeamMemberCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageTeams);

        var team = await db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == command.TeamId, cancellationToken)
            ?? throw new NotFoundException(nameof(Team), command.TeamId);

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Request.UserId, cancellationToken)
            ?? throw new NotFoundException(nameof(User), command.Request.UserId);

        // A deleted account is not a person who can be given work.
        if (user.IsAnonymised)
        {
            throw new ConflictException(
                "user_deleted", "That account has been deleted and cannot join a team.");
        }

        var membership = await db.TeamMembers.AsTracking()
            .FirstOrDefaultAsync(m => m.TeamId == team.Id && m.UserId == user.Id, cancellationToken);

        var role = Enum.TryParse<TeamRole>(command.Request.RoleInTeam, ignoreCase: true, out var parsed)
            ? parsed
            : TeamRole.Member;

        if (membership is null)
        {
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = team.Id,
                UserId = user.Id,
                RoleInTeam = role,
                CapacityWeight = command.Request.CapacityWeight,
                IsActive = true,
            });
        }
        else
        {
            // Reactivated rather than duplicated: someone who left and came back keeps
            // one membership row, so their history does not fork.
            membership.RoleInTeam = role;
            membership.CapacityWeight = command.Request.CapacityWeight;
            membership.IsActive = true;
        }

        await audit.WriteAsync(
            AuditAction.Updated, nameof(TeamMember), team.Id, $"{team.Name} / {user.Email}",
            changes: new { Member = user.Email, Role = role.ToString(), command.Request.CapacityWeight },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var teams = await dispatcher.QueryAsync(new ListTeamsQuery(), cancellationToken);
        return teams.First(t => t.Id == team.Id);
    }
}

public sealed record RemoveTeamMemberCommand(Guid TeamId, Guid UserId) : ICommand<TeamResponse>;

/// <summary>
/// Takes someone off a team.
/// </summary>
/// <remarks>
/// The membership row is deactivated, not deleted. Tickets routed to that person
/// while they were on the team remain explicable, and re-adding them later restores
/// one row rather than creating a second.
/// </remarks>
public sealed class RemoveTeamMemberCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<RemoveTeamMemberCommand, TeamResponse>
{
    public async Task<TeamResponse> HandleAsync(
        RemoveTeamMemberCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageTeams);

        var membership = await db.TeamMembers.AsTracking()
            .FirstOrDefaultAsync(
                m => m.TeamId == command.TeamId && m.UserId == command.UserId, cancellationToken)
            ?? throw new NotFoundException(nameof(TeamMember), command.UserId);

        var openTickets = await db.Tickets.AsNoTracking()
            .CountAsync(t => t.AssignedAgentId == command.UserId
                             && t.AssignedTeamId == command.TeamId
                             && t.Status != TicketStatus.Closed
                             && t.Status != TicketStatus.Cancelled,
                cancellationToken);

        if (openTickets > 0)
        {
            throw new ConflictException(
                "member_has_open_tickets",
                $"That person still owns {openTickets} open "
                + $"{(openTickets == 1 ? "ticket" : "tickets")} for this team. Reassign them "
                + "first, or the work becomes invisible to the team's queue.");
        }

        membership.IsActive = false;

        await audit.WriteAsync(
            AuditAction.Updated, nameof(TeamMember), command.TeamId, null,
            changes: new { Removed = command.UserId },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var teams = await dispatcher.QueryAsync(new ListTeamsQuery(), cancellationToken);
        return teams.First(t => t.Id == command.TeamId);
    }
}
