using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Sla;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Sla;

/// <summary>
/// Works out which priority matrix applies to a ticket, and answers with its cells.
/// </summary>
/// <remarks>
/// <para>
/// An organization has one matrix. A policy may override it, because what counts as
/// Critical is not the same question for a stopped production line as for an internal
/// reporting request, and a single organization-wide grid forces one answer on both.
/// </para>
/// <para>
/// There is no circularity in consulting the policy before the priority exists. A
/// policy is selected by category, department and ticket type — never by priority —
/// so the applicable policy is known from the moment the ticket is described. Priority
/// then follows from that policy's matrix, and the SLA target follows from the
/// priority.
/// </para>
/// </remarks>
public interface IPriorityMatrixResolver
{
    /// <summary>The cells that apply to a ticket with this shape, policy overrides first.</summary>
    Task<IReadOnlyList<PriorityMatrixCell>> ForTicketShapeAsync(
        Guid? categoryId, Guid? departmentId, TicketType type, CancellationToken cancellationToken);

    /// <summary>The cells that apply to an existing ticket.</summary>
    Task<IReadOnlyList<PriorityMatrixCell>> ForTicketAsync(
        Ticket ticket, CancellationToken cancellationToken);
}

public sealed class PriorityMatrixResolver(IAppDbContext db) : IPriorityMatrixResolver
{
    public Task<IReadOnlyList<PriorityMatrixCell>> ForTicketAsync(
        Ticket ticket, CancellationToken cancellationToken) =>
        ForTicketShapeAsync(ticket.CategoryId, ticket.DepartmentId, ticket.Type, cancellationToken);

    public async Task<IReadOnlyList<PriorityMatrixCell>> ForTicketShapeAsync(
        Guid? categoryId, Guid? departmentId, TicketType type, CancellationToken cancellationToken)
    {
        var policyId = await SlaPolicySelection.SelectIdAsync(
            db, categoryId, departmentId, type, cancellationToken);

        // Both sets in one round trip. Fetching the policy's rows and then falling back
        // to a second query when a cell is missing would cost a query per hole.
        var rows = await db.PriorityMatrixEntries.AsNoTracking()
            .Where(e => e.SlaPolicyId == null || e.SlaPolicyId == policyId)
            .Select(e => new { e.SlaPolicyId, e.Impact, e.Urgency, e.Priority })
            .ToListAsync(cancellationToken);

        // Per cell, not per matrix. A policy that overrides two cells inherits the other
        // fourteen, so editing the organization matrix still reaches every policy that
        // has not deliberately spoken for itself.
        var merged = new Dictionary<(ImpactLevel, UrgencyLevel), PriorityLevel>();

        foreach (var row in rows.Where(r => r.SlaPolicyId is null))
        {
            merged[(row.Impact, row.Urgency)] = row.Priority;
        }

        if (policyId is not null)
        {
            foreach (var row in rows.Where(r => r.SlaPolicyId == policyId))
            {
                merged[(row.Impact, row.Urgency)] = row.Priority;
            }
        }

        return [.. merged.Select(kv => new PriorityMatrixCell(kv.Key.Item1, kv.Key.Item2, kv.Value))];
    }
}

/// <summary>
/// Which SLA policy applies to a ticket of a given shape.
/// </summary>
/// <remarks>
/// Extracted so the SLA engine and the priority matrix cannot drift apart. Two copies
/// of this rule would eventually disagree, and the symptom — a ticket priced by one
/// policy's matrix and clocked against another's targets — would be very hard to see.
/// </remarks>
public static class SlaPolicySelection
{
    public static async Task<Guid?> SelectIdAsync(
        IAppDbContext db,
        Guid? categoryId,
        Guid? departmentId,
        TicketType type,
        CancellationToken cancellationToken)
    {
        var candidates = await db.SlaPolicies.AsNoTracking()
            .Where(p => p.IsActive
                && (p.CategoryId == null || p.CategoryId == categoryId)
                && (p.DepartmentId == null || p.DepartmentId == departmentId)
                && (p.TicketType == null || p.TicketType == type))
            .Select(p => new { p.Id, p.CategoryId, p.DepartmentId, p.TicketType, p.IsDefault })
            .ToListAsync(cancellationToken);

        // Precedence is computed in memory because it is a derived property on the
        // entity and has no column to order by in SQL.
        return candidates
            .OrderByDescending(p =>
                (p.CategoryId is not null ? 4 : 0)
                + (p.TicketType is not null ? 2 : 0)
                + (p.DepartmentId is not null ? 1 : 0))
            .ThenByDescending(p => p.IsDefault)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefault();
    }
}
