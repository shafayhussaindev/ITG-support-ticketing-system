using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Sla;
using PriorityCalculator = SupportTicketing.Domain.Tickets.PriorityCalculator;

namespace SupportTicketing.Application.Features.Admin;

/// <summary>Where a resolved cell's value came from.</summary>
internal static class MatrixSource
{
    internal const string Policy = "Policy";
    internal const string Organization = "Organization";
    internal const string BuiltIn = "BuiltIn";
}

// ------------------------------------------------------------------------ read

public sealed record GetPolicyPriorityMatrixQuery(Guid PolicyId) : IQuery<PolicyPriorityMatrixResponse>;

/// <summary>
/// A policy's grid, every cell resolved and labelled with where it came from.
/// </summary>
/// <remarks>
/// All sixteen combinations are returned whether or not the policy has said anything
/// about them, for the same reason the organization grid does: a grid with holes in it
/// invites the reader to think the missing cells are impossible rather than merely
/// inherited.
/// </remarks>
public sealed class GetPolicyPriorityMatrixQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetPolicyPriorityMatrixQuery, PolicyPriorityMatrixResponse>
{
    public async Task<PolicyPriorityMatrixResponse> HandleAsync(
        GetPolicyPriorityMatrixQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Sla.Manage);

        var policy = await db.SlaPolicies.AsNoTracking()
            .Where(p => p.Id == query.PolicyId)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(SlaPolicy), query.PolicyId);

        var rows = await db.PriorityMatrixEntries.AsNoTracking()
            .Where(e => e.SlaPolicyId == null || e.SlaPolicyId == query.PolicyId)
            .Select(e => new { e.SlaPolicyId, e.Impact, e.Urgency, e.Priority })
            .ToListAsync(cancellationToken);

        var organization = rows
            .Where(r => r.SlaPolicyId is null)
            .ToDictionary(r => (r.Impact, r.Urgency), r => r.Priority);

        var overrides = rows
            .Where(r => r.SlaPolicyId == query.PolicyId)
            .ToDictionary(r => (r.Impact, r.Urgency), r => r.Priority);

        var cells = new List<PolicyPriorityMatrixCell>(16);

        foreach (var impact in Enum.GetValues<ImpactLevel>())
        {
            foreach (var urgency in Enum.GetValues<UrgencyLevel>())
            {
                var key = (impact, urgency);

                var (priority, source) =
                    overrides.TryGetValue(key, out var own) ? (own, MatrixSource.Policy)
                    : organization.TryGetValue(key, out var org) ? (org, MatrixSource.Organization)
                    : (PriorityCalculator.DefaultFor(impact, urgency), MatrixSource.BuiltIn);

                cells.Add(new PolicyPriorityMatrixCell
                {
                    Impact = impact.ToString(),
                    Urgency = urgency.ToString(),
                    Priority = priority.ToString(),
                    Source = source,
                });
            }
        }

        return new PolicyPriorityMatrixResponse
        {
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            HasOverrides = overrides.Count > 0,
            OverriddenCells = overrides.Count,
            Cells = cells,
        };
    }
}

// ----------------------------------------------------------------------- write

public sealed record SavePolicyPriorityMatrixCommand(Guid PolicyId, SavePriorityMatrixRequest Request)
    : ICommand<PolicyPriorityMatrixResponse>;

public sealed class SavePolicyPriorityMatrixCommandValidator
    : AbstractValidator<SavePolicyPriorityMatrixCommand>
{
    public SavePolicyPriorityMatrixCommandValidator()
    {
        RuleFor(c => c.Request.Cells).NotEmpty()
            .WithMessage("Send the cells this policy should override, or clear the override instead.");
    }
}

/// <summary>
/// Replaces a policy's overrides.
/// </summary>
/// <remarks>
/// <para>
/// Only the cells sent are stored, and any the policy previously overrode but no longer
/// sends are removed rather than left behind. Sending a subset is therefore a complete
/// statement of what this policy wants to decide for itself; everything else goes back
/// to being inherited.
/// </para>
/// <para>
/// A cell whose value already matches what the policy would inherit is not stored. An
/// override that agrees with its parent is invisible in the interface but silently
/// pins the value, so a later change to the organization matrix would skip this policy
/// for no reason anybody could see.
/// </para>
/// <para>
/// Existing tickets are untouched. Their priority was decided when they were raised and
/// their SLA clock started against it; recalculating now would move a deadline after the
/// fact and make "did we meet it?" unanswerable.
/// </para>
/// </remarks>
public sealed class SavePolicyPriorityMatrixCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SavePolicyPriorityMatrixCommand, PolicyPriorityMatrixResponse>
{
    public async Task<PolicyPriorityMatrixResponse> HandleAsync(
        SavePolicyPriorityMatrixCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Sla.Manage);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var policy = await db.SlaPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == command.PolicyId, cancellationToken)
            ?? throw new NotFoundException(nameof(SlaPolicy), command.PolicyId);

        var requested = new Dictionary<(ImpactLevel, UrgencyLevel), PriorityLevel>();

        foreach (var cell in command.Request.Cells)
        {
            if (!Enum.TryParse<ImpactLevel>(cell.Impact, ignoreCase: true, out var impact)
                || !Enum.TryParse<UrgencyLevel>(cell.Urgency, ignoreCase: true, out var urgency)
                || !Enum.TryParse<PriorityLevel>(cell.Priority, ignoreCase: true, out var priority))
            {
                throw new ValidationException(
                    $"'{cell.Impact}' / '{cell.Urgency}' / '{cell.Priority}' is not a valid matrix cell.");
            }

            requested[(impact, urgency)] = priority;
        }

        // What the policy would see with no overrides at all, so a cell that merely
        // agrees with the parent can be dropped rather than pinned.
        var inherited = await db.PriorityMatrixEntries.AsNoTracking()
            .Where(e => e.SlaPolicyId == null)
            .Select(e => new { e.Impact, e.Urgency, e.Priority })
            .ToListAsync(cancellationToken);

        var parent = inherited.ToDictionary(r => (r.Impact, r.Urgency), r => r.Priority);

        PriorityLevel Inherited(ImpactLevel i, UrgencyLevel u) =>
            parent.TryGetValue((i, u), out var found) ? found : PriorityCalculator.DefaultFor(i, u);

        var genuine = requested
            .Where(kv => kv.Value != Inherited(kv.Key.Item1, kv.Key.Item2))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var existing = await db.PriorityMatrixEntries.AsTracking()
            .Where(e => e.SlaPolicyId == command.PolicyId)
            .ToListAsync(cancellationToken);

        var changed = new List<string>();

        foreach (var row in existing)
        {
            var key = (row.Impact, row.Urgency);

            if (!genuine.TryGetValue(key, out var wanted))
            {
                changed.Add($"{row.Impact}/{row.Urgency}: override removed");
                db.PriorityMatrixEntries.Remove(row);
            }
            else if (row.Priority != wanted)
            {
                changed.Add($"{row.Impact}/{row.Urgency}: {row.Priority}→{wanted}");
                row.Priority = wanted;
            }
        }

        foreach (var ((impact, urgency), priority) in genuine)
        {
            if (existing.Any(e => e.Impact == impact && e.Urgency == urgency))
            {
                continue;
            }

            db.PriorityMatrixEntries.Add(new PriorityMatrixEntry
            {
                OrganizationId = organizationId,
                SlaPolicyId = command.PolicyId,
                Impact = impact,
                Urgency = urgency,
                Priority = priority,
            });

            changed.Add($"{impact}/{urgency}→{priority}");
        }

        await audit.WriteAsync(
            AuditAction.ConfigurationChanged, nameof(PriorityMatrixEntry), policy.Id,
            $"Priority matrix for {policy.Name}",
            changes: new { Policy = policy.Name, Overrides = genuine.Count, Cells = string.Join("; ", changed) },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await dispatcher.QueryAsync(
            new GetPolicyPriorityMatrixQuery(command.PolicyId), cancellationToken);
    }
}

// ----------------------------------------------------------------------- clear

public sealed record ClearPolicyPriorityMatrixCommand(Guid PolicyId)
    : ICommand<PolicyPriorityMatrixResponse>;

/// <summary>Drops every override, returning the policy to the organization's matrix.</summary>
public sealed class ClearPolicyPriorityMatrixCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<ClearPolicyPriorityMatrixCommand, PolicyPriorityMatrixResponse>
{
    public async Task<PolicyPriorityMatrixResponse> HandleAsync(
        ClearPolicyPriorityMatrixCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Sla.Manage);

        var policy = await db.SlaPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == command.PolicyId, cancellationToken)
            ?? throw new NotFoundException(nameof(SlaPolicy), command.PolicyId);

        var removed = await db.PriorityMatrixEntries
            .Where(e => e.SlaPolicyId == command.PolicyId)
            .ExecuteDeleteAsync(cancellationToken);

        await audit.WriteAsync(
            AuditAction.ConfigurationChanged, nameof(PriorityMatrixEntry), policy.Id,
            $"Priority matrix for {policy.Name}",
            changes: new { Policy = policy.Name, Cleared = removed },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await dispatcher.QueryAsync(
            new GetPolicyPriorityMatrixQuery(command.PolicyId), cancellationToken);
    }
}
