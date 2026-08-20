using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Application.Features.Sla;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

public sealed record CreateTicketCommand(CreateTicketRequest Request, TicketSource Source)
    : ICommand<TicketDetailResponse>;

public sealed class CreateTicketCommandValidator : AbstractValidator<CreateTicketCommand>
{
    public CreateTicketCommandValidator()
    {
        RuleFor(x => x.Request.Subject).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Request.Description).NotEmpty().MaximumLength(20_000);
        RuleFor(x => x.Request.Impact).NotEmpty().Must(BeImpact)
            .WithMessage("Impact must be Low, Medium, High or Critical.");
        RuleFor(x => x.Request.Urgency).NotEmpty().Must(BeUrgency)
            .WithMessage("Urgency must be Low, Medium, High or Critical.");
        RuleFor(x => x.Request.Type).NotEmpty().Must(BeType)
            .WithMessage("That is not a recognised ticket type.");
        RuleFor(x => x.Request.ContactEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Request.ContactEmail));
        RuleFor(x => x.Request.Tags).Must(t => t is null || t.Count <= 20)
            .WithMessage("A ticket may carry at most 20 tags.");
    }

    private static bool BeImpact(string value) => Enum.TryParse<ImpactLevel>(value, true, out _);
    private static bool BeUrgency(string value) => Enum.TryParse<UrgencyLevel>(value, true, out _);
    private static bool BeType(string value) => Enum.TryParse<TicketType>(value, true, out _);
}

/// <summary>
/// Creates a ticket, calculates its priority, records its opening history and routes
/// it to a default team.
/// </summary>
/// <remarks>
/// The whole thing runs in the transaction opened by the pipeline, so the number
/// allocation, the ticket row, its history rows and its audit entry either all commit
/// or none do. A partially created ticket with no status history would be
/// unreconstructable later.
/// </remarks>
public sealed class CreateTicketCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITicketNumberGenerator numberGenerator,
    ISlaEngine slaEngine,
    IPriorityMatrixResolver priorityMatrix,
    ISeverityPolicy severityPolicy,
    IAuditWriter audit,
    IClock clock)
    : ICommandHandler<CreateTicketCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        CreateTicketCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.Create);

        var request = command.Request;
        var now = clock.UtcNow;
        var organizationId = currentUser.OrganizationId
            ?? throw new ForbiddenException("No organization is associated with your account.");

        var requesterId = await ResolveRequesterAsync(request.RequesterId, cancellationToken);
        var requester = await db.Users
            .Where(u => u.Id == requesterId)
            .Select(u => new { u.Id, u.Email, u.PhoneNumber, u.DepartmentId, u.OfficeId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("User", requesterId);

        await ValidateCatalogAsync(request, cancellationToken);

        var impact = Enum.Parse<ImpactLevel>(request.Impact, true);
        var urgency = Enum.Parse<UrgencyLevel>(request.Urgency, true);

        // The matrix is the authority. The requester supplied impact and urgency, both
        // of which they can judge; they were never asked to pick a priority.
        // Capped before the matrix sees it. A requester who marks everything Critical
        // is not lying so much as advocating, and the cap turns advocacy back into a
        // description without discarding what they said.
        var claim = await severityPolicy.ApplyAsync(impact, urgency, cancellationToken);
        impact = claim.Impact;
        urgency = claim.Urgency;

        var ticketType = Enum.Parse<TicketType>(request.Type, true);

        // Resolved through the SLA policy that will apply to this ticket, so a policy
        // with its own matrix prices its own work. Selection reads category, department
        // and type — never priority — so there is nothing circular in asking now.
        var matrix = await priorityMatrix.ForTicketShapeAsync(
            request.CategoryId, request.DepartmentId, ticketType, cancellationToken);

        var priority = PriorityCalculator.Calculate(impact, urgency, matrix);

        var organization = await db.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.TicketPrefix })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Organization", organizationId);

        var ticketNumber = await numberGenerator.NextAsync(
            organizationId, organization.TicketPrefix, cancellationToken);

        var defaultTeamId = await ResolveDefaultTeamAsync(request, cancellationToken);

        var ticket = new Ticket
        {
            OrganizationId = organizationId,
            TicketNumber = ticketNumber,
            Subject = request.Subject.Trim(),
            Description = request.Description.Trim(),
            RequesterId = requesterId,
            // Falls back to the requester's own posting so reporting by department
            // works even when the form leaves it blank.
            DepartmentId = request.DepartmentId ?? requester.DepartmentId,
            OfficeId = request.OfficeId ?? requester.OfficeId,
            ContactEmail = request.ContactEmail ?? requester.Email,
            ContactPhone = request.ContactPhone ?? requester.PhoneNumber,
            CategoryId = request.CategoryId,
            SubcategoryId = request.SubcategoryId,
            ApplicationId = request.ApplicationId,
            ApplicationModuleId = request.ApplicationModuleId,
            Type = ticketType,
            Impact = impact,
            Urgency = urgency,
            ClaimedImpact = claim.ClaimedImpact,
            ClaimedUrgency = claim.ClaimedUrgency,
            Priority = priority.Priority,
            SuggestedPriority = priority.Priority,
            PriorityDecisionSource = DecisionSource.Rule,
            Severity = MapSeverity(priority.Priority),
            Status = TicketStatus.New,
            Source = command.Source,
            AssignedTeamId = defaultTeamId,
        };

        db.Tickets.Add(ticket);

        db.TicketStatusHistory.Add(new TicketStatusHistory
        {
            OrganizationId = organizationId,
            TicketId = ticket.Id,
            FromStatus = null,
            ToStatus = TicketStatus.New,
            ChangedById = currentUser.UserId,
            ChangedAtUtc = now,
            Reason = "Ticket raised.",
            Source = DecisionSource.Human,
            CorrelationId = currentUser.CorrelationId,
        });

        db.TicketPriorityHistory.Add(new TicketPriorityHistory
        {
            OrganizationId = organizationId,
            TicketId = ticket.Id,
            FromPriority = null,
            ToPriority = priority.Priority,
            Impact = impact,
            Urgency = urgency,
            MatrixPriority = priority.Priority,
            ChangedById = currentUser.UserId,
            ChangedAtUtc = now,
            Reason = priority.Explanation,
            Source = DecisionSource.Rule,
            CorrelationId = currentUser.CorrelationId,
        });

        // The clock starts inside the same transaction as the ticket. Starting it
        // afterwards would leave a window where a ticket exists with no promise
        // attached, and a crash in that window would lose the SLA entirely.
        await slaEngine.StartAsync(ticket, cancellationToken);

        await AttachTagsAsync(ticket, request.Tags, organizationId, now, cancellationToken);
        AttachRelatedRecords(ticket, request.RelatedRecords, organizationId);

        await audit.WriteAsync(
            AuditAction.Created, nameof(Ticket), ticket.Id, ticketNumber,
            changes: new
            {
                ticket.Subject,
                Impact = impact.ToString(),
                Urgency = urgency.ToString(),
                Priority = priority.Priority.ToString(),
                PriorityFromMatrix = priority.FromConfiguredMatrix,
                Source = command.Source.ToString(),
            },
            reason: priority.Explanation,
            source: DecisionSource.Human,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }

    /// <summary>
    /// A caller may raise a ticket for someone else only if they can act beyond their
    /// own queue. Without this check any requester could file tickets under a
    /// colleague's name.
    /// </summary>
    private async Task<Guid> ResolveRequesterAsync(Guid? requestedId, CancellationToken cancellationToken)
    {
        var self = currentUser.UserId ?? throw new ForbiddenException();

        if (requestedId is null || requestedId == self)
        {
            return self;
        }

        if (!currentUser.Has(Permissions.Tickets.Edit))
        {
            throw new ForbiddenException("You can only raise tickets on your own behalf.");
        }

        // The tenant filter means a requester from another organization simply is not
        // found, so this doubles as the cross-tenant guard.
        var exists = await db.Users.AnyAsync(u => u.Id == requestedId && u.IsActive, cancellationToken);

        return exists ? requestedId.Value : throw new NotFoundException("User", requestedId.Value);
    }

    /// <summary>
    /// Confirms every referenced catalogue entry belongs to the caller's organization.
    /// The tenant filter already scopes these queries, so an identifier from another
    /// tenant is reported as not found rather than silently accepted.
    /// </summary>
    private async Task ValidateCatalogAsync(CreateTicketRequest request, CancellationToken cancellationToken)
    {
        if (request.CategoryId is { } categoryId
            && !await db.Categories.AnyAsync(c => c.Id == categoryId && c.IsActive, cancellationToken))
        {
            throw new NotFoundException("Category", categoryId);
        }

        if (request.SubcategoryId is { } subcategoryId)
        {
            var subcategory = await db.Subcategories
                .Where(sc => sc.Id == subcategoryId && sc.IsActive)
                .Select(sc => new { sc.CategoryId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Subcategory", subcategoryId);

            if (request.CategoryId is not null && subcategory.CategoryId != request.CategoryId)
            {
                throw new BusinessRuleException(
                    "ticket.subcategory_mismatch",
                    "The selected subcategory does not belong to the selected category.");
            }
        }

        if (request.ApplicationId is { } applicationId
            && !await db.Applications.AnyAsync(a => a.Id == applicationId && a.IsActive, cancellationToken))
        {
            throw new NotFoundException("Application", applicationId);
        }

        if (request.ApplicationModuleId is { } moduleId)
        {
            var module = await db.ApplicationModules
                .Where(m => m.Id == moduleId && m.IsActive)
                .Select(m => new { m.ApplicationId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("ApplicationModule", moduleId);

            if (request.ApplicationId is not null && module.ApplicationId != request.ApplicationId)
            {
                throw new BusinessRuleException(
                    "ticket.module_mismatch",
                    "The selected module does not belong to the selected application.");
            }
        }
    }

    /// <summary>
    /// Initial routing: the subcategory's team wins over the category's, and the
    /// owning team of the affected application is the last resort. Leaving a ticket
    /// with no team at all is the one outcome to avoid, because nobody owns it.
    /// </summary>
    private async Task<Guid?> ResolveDefaultTeamAsync(
        CreateTicketRequest request, CancellationToken cancellationToken)
    {
        if (request.SubcategoryId is { } subcategoryId)
        {
            var teamId = await db.Subcategories
                .Where(sc => sc.Id == subcategoryId)
                .Select(sc => sc.DefaultTeamId)
                .FirstOrDefaultAsync(cancellationToken);

            if (teamId is not null)
            {
                return teamId;
            }
        }

        if (request.CategoryId is { } categoryId)
        {
            var teamId = await db.Categories
                .Where(c => c.Id == categoryId)
                .Select(c => c.DefaultTeamId)
                .FirstOrDefaultAsync(cancellationToken);

            if (teamId is not null)
            {
                return teamId;
            }
        }

        if (request.ApplicationId is { } applicationId)
        {
            return await db.Applications
                .Where(a => a.Id == applicationId)
                .Select(a => a.OwningTeamId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private async Task AttachTagsAsync(
        Ticket ticket,
        IReadOnlyList<string>? names,
        Guid organizationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (names is null || names.Count == 0)
        {
            return;
        }

        var wanted = names
            .Select(n => n.Trim())
            .Where(n => n.Length is > 0 and <= 60)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = await db.Tags
            .Where(t => wanted.Contains(t.Name))
            .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var name in wanted)
        {
            if (!existing.TryGetValue(name, out var tag))
            {
                tag = new Tag { OrganizationId = organizationId, Name = name, CreatedAtUtc = now };
                db.Tags.Add(tag);
            }

            db.TicketTags.Add(new TicketTag
            {
                OrganizationId = organizationId,
                TicketId = ticket.Id,
                TagId = tag.Id,
            });
        }
    }

    private void AttachRelatedRecords(
        Ticket ticket, IReadOnlyList<RelatedRecordRequest>? records, Guid organizationId)
    {
        if (records is null)
        {
            return;
        }

        foreach (var record in records)
        {
            if (!Enum.TryParse<RelatedRecordType>(record.RecordType, true, out var type))
            {
                type = RelatedRecordType.Other;
            }

            db.TicketRelatedRecords.Add(new TicketRelatedRecord
            {
                OrganizationId = organizationId,
                TicketId = ticket.Id,
                RecordType = type,
                RecordReference = record.RecordReference.Trim(),
                RecordLabel = record.RecordLabel,
                RecordUrl = record.RecordUrl,
                SourceSystem = record.SourceSystem,
                Notes = record.Notes,
            });
        }
    }

    /// <summary>
    /// Severity starts aligned with priority and can be adjusted independently later.
    /// They are different things — a cosmetic defect on a critical system is high
    /// priority but minor severity — but one sensible default beats an empty field.
    /// </summary>
    private static SeverityLevel MapSeverity(PriorityLevel priority) => priority switch
    {
        PriorityLevel.Critical => SeverityLevel.Critical,
        PriorityLevel.High => SeverityLevel.Major,
        PriorityLevel.Medium => SeverityLevel.Moderate,
        _ => SeverityLevel.Minor,
    };
}
