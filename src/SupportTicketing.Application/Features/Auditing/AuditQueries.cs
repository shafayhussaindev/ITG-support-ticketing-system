using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auditing;
using SupportTicketing.Domain.Auditing;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auditing;

public sealed record ListAuditLogQuery(AuditLogQueryParameters Parameters)
    : IQuery<PagedResult<AuditLogResponse>>;

/// <summary>
/// Reads the append-only audit log.
/// </summary>
/// <remarks>
/// <para>
/// The log is organization-wide by design: it exists to answer "who did this, and
/// when", which is not a question that survives being filtered down to the reader's
/// own tickets. Access is therefore governed by a single permission —
/// <c>audit.view</c> — rather than by data scope, and that permission is deliberately
/// narrow. The organization boundary still applies, because it is enforced by the
/// DbContext's global filter rather than by anything written here.
/// </para>
/// <para>
/// Filter values that name an enum member are parsed and rejected when unknown. An
/// ignored filter is worse than a rejected one: the caller gets a full result set,
/// believes it is narrow, and draws a conclusion from it.
/// </para>
/// </remarks>
public sealed class ListAuditLogQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListAuditLogQuery, PagedResult<AuditLogResponse>>
{
    public async Task<PagedResult<AuditLogResponse>> HandleAsync(
        ListAuditLogQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ViewAudit);

        var p = query.Parameters;
        var page = p.Page < 1 ? 1 : p.Page;
        var pageSize = Math.Clamp(p.PageSize, 1, PagedQuery.MaxPageSize);

        var logs = db.AuditLogs.AsNoTracking();

        if (p.FromUtc is { } from)
        {
            logs = logs.Where(a => a.OccurredAtUtc >= from);
        }

        if (p.ToUtc is { } to)
        {
            logs = logs.Where(a => a.OccurredAtUtc <= to);
        }

        if (!string.IsNullOrWhiteSpace(p.Action))
        {
            if (!Enum.TryParse<AuditAction>(p.Action, ignoreCase: true, out var action))
            {
                throw new ValidationException(
                    [new ValidationFailure("action", $"'{p.Action}' is not a known audit action.")]);
            }

            logs = logs.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(p.Source))
        {
            if (!Enum.TryParse<DecisionSource>(p.Source, ignoreCase: true, out var source))
            {
                throw new ValidationException(
                    [new ValidationFailure("source", $"'{p.Source}' is not a known decision source.")]);
            }

            logs = logs.Where(a => a.Source == source);
        }

        if (!string.IsNullOrWhiteSpace(p.EntityType))
        {
            logs = logs.Where(a => a.EntityType == p.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(p.EntityReference))
        {
            logs = logs.Where(a => a.EntityReference != null && a.EntityReference.Contains(p.EntityReference));
        }

        if (p.EntityId is { } entityId)
        {
            logs = logs.Where(a => a.EntityId == entityId);
        }

        if (p.ActorId is { } actorId)
        {
            logs = logs.Where(a => a.ActorId == actorId);
        }

        if (p.CorrelationId is { } correlationId)
        {
            logs = logs.Where(a => a.CorrelationId == correlationId);
        }

        if (p.FailuresOnly == true)
        {
            logs = logs.Where(a => a.IsFailure);
        }

        if (!string.IsNullOrWhiteSpace(p.Search))
        {
            var term = p.Search.Trim();

            logs = logs.Where(a =>
                (a.EntityReference != null && a.EntityReference.Contains(term))
                || (a.ActorName != null && a.ActorName.Contains(term))
                || (a.ActorEmail != null && a.ActorEmail.Contains(term))
                || (a.Reason != null && a.Reason.Contains(term)));
        }

        var total = await logs.CountAsync(cancellationToken);

        if (total == 0)
        {
            return PagedResult<AuditLogResponse>.Empty(page, pageSize);
        }

        // Newest first, then by identifier. Time alone is not a total order here:
        // several rows written inside one transaction share a timestamp to the tick,
        // and without the tiebreaker they can swap places between pages and be
        // skipped or shown twice.
        var rows = await logs
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogResponse>
        {
            Items = [.. rows.Select(Project)],
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    internal static AuditLogResponse Project(AuditLog a) => new()
    {
        Id = a.Id,
        Action = a.Action.ToString(),
        EntityType = a.EntityType,
        EntityId = a.EntityId,
        EntityReference = a.EntityReference,
        ActorId = a.ActorId,
        ActorName = a.ActorName,
        ActorEmail = a.ActorEmail,
        Source = a.Source.ToString(),
        OccurredAtUtc = a.OccurredAtUtc,
        Changes = FlattenChanges(a.ChangesJson),
        Reason = a.Reason,
        IpAddress = a.IpAddress,
        CorrelationId = a.CorrelationId,
        IsFailure = a.IsFailure,
        FailureReason = a.FailureReason,
    };

    /// <summary>
    /// Turns the stored JSON object into field/value pairs for display.
    /// </summary>
    /// <remarks>
    /// Writers serialise anonymous objects, so the shape is a flat object in practice
    /// — but the column is free-form and rows written years ago cannot be migrated,
    /// because the table is append-only. Malformed or unexpected JSON therefore yields
    /// the raw text under a single field rather than an exception: an audit row that
    /// cannot be rendered must still be visible.
    /// </remarks>
    internal static IReadOnlyList<AuditFieldChange> FlattenChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [new AuditFieldChange("value", document.RootElement.ToString())];
            }

            return
            [
                .. document.RootElement.EnumerateObject().Select(property => new AuditFieldChange(
                    property.Name,
                    property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Object or JsonValueKind.Array => property.Value.GetRawText(),
                        _ => property.Value.ToString(),
                    }))
            ];
        }
        catch (JsonException)
        {
            return [new AuditFieldChange("raw", json)];
        }
    }
}

// ---------------------------------------------------------------- facets

public sealed record GetAuditFilterOptionsQuery : IQuery<AuditFilterOptions>;

/// <summary>
/// The values that actually occur in this organization's log.
/// </summary>
/// <remarks>
/// Built from the data rather than from the enum, so the filter offers what can be
/// found. Listing every <see cref="AuditAction"/> member would advertise actions the
/// deployment has never performed and send people hunting for nothing.
/// </remarks>
public sealed class GetAuditFilterOptionsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetAuditFilterOptionsQuery, AuditFilterOptions>
{
    private const int MaxActors = 50;

    public async Task<AuditFilterOptions> HandleAsync(
        GetAuditFilterOptionsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ViewAudit);

        var logs = db.AuditLogs.AsNoTracking();

        var actions = await logs
            .Select(a => a.Action)
            .Distinct()
            .ToListAsync(cancellationToken);

        var entityTypes = await logs
            .Select(a => a.EntityType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);

        // Busiest actors first: a list of fifty names ordered alphabetically is a
        // list nobody reads, whereas the people generating the most activity are
        // exactly who an administrator came here to look at.
        var actors = await logs
            .Where(a => a.ActorId != null && a.ActorName != null)
            .GroupBy(a => new { a.ActorId, a.ActorName })
            .Select(g => new { g.Key.ActorId, g.Key.ActorName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(MaxActors)
            .ToListAsync(cancellationToken);

        var earliest = await logs
            .OrderBy(a => a.OccurredAtUtc)
            .Select(a => (DateTime?)a.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var total = await logs.CountAsync(cancellationToken);

        return new AuditFilterOptions
        {
            Actions = [.. actions.Select(a => a.ToString()).OrderBy(a => a)],
            EntityTypes = entityTypes,
            Actors = [.. actors.Select(a => new AuditActorOption(a.ActorId!.Value, a.ActorName!, a.Count))],
            EarliestEntryUtc = earliest,
            TotalEntries = total,
        };
    }
}

// ------------------------------------------------------- one entity's history

public sealed record GetEntityAuditTrailQuery(Guid EntityId) : IQuery<IReadOnlyList<AuditLogResponse>>;

/// <summary>
/// Everything the log holds about one entity, oldest first.
/// </summary>
/// <remarks>
/// Ordered forwards because this view is read as a narrative — what happened, then
/// what happened next — unlike the main log, which is read as "what happened
/// recently" and so runs backwards.
/// </remarks>
public sealed class GetEntityAuditTrailQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetEntityAuditTrailQuery, IReadOnlyList<AuditLogResponse>>
{
    private const int MaxEntries = 500;

    public async Task<IReadOnlyList<AuditLogResponse>> HandleAsync(
        GetEntityAuditTrailQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ViewAudit);

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == query.EntityId)
            .OrderBy(a => a.OccurredAtUtc)
            .ThenBy(a => a.Id)
            .Take(MaxEntries)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ListAuditLogQueryHandler.Project)];
    }
}
