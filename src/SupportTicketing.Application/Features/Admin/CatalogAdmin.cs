using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

// Aliased rather than imported wholesale: the domain has its own PriorityMatrixCell
// and importing the namespace would make every mention here ambiguous with the contract.
using PriorityCalculator = SupportTicketing.Domain.Tickets.PriorityCalculator;

namespace SupportTicketing.Application.Features.Admin;

// ----------------------------------------------------------------- categories

public sealed record ListAdminCategoriesQuery : IQuery<IReadOnlyList<AdminCategoryResponse>>;

public sealed class ListAdminCategoriesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListAdminCategoriesQuery, IReadOnlyList<AdminCategoryResponse>>
{
    public async Task<IReadOnlyList<AdminCategoryResponse>> HandleAsync(
        ListAdminCategoriesQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        // Ticket counts are shown beside each category because they are what makes
        // "can I delete this?" answerable without guessing.
        var ticketCounts = await db.Tickets.AsNoTracking()
            .Where(t => t.CategoryId != null)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count, cancellationToken);

        var categories = await db.Categories.AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.Code, c.Description,
                c.DefaultTeamId,
                DefaultTeamName = c.DefaultTeam == null ? null : c.DefaultTeam.Name,
                c.SlaPolicyId,
                c.DisplayOrder, c.IsActive, c.IsInternalOnly,
                Subcategories = c.Subcategories
                    .OrderBy(sub => sub.DisplayOrder).ThenBy(sub => sub.Name)
                    .Select(sub => new AdminSubcategoryResponse
                    {
                        Id = sub.Id,
                        CategoryId = sub.CategoryId,
                        Name = sub.Name,
                        Code = sub.Code,
                        DefaultTeamId = sub.DefaultTeamId,
                        DefaultImpact = sub.DefaultImpact == null ? null : sub.DefaultImpact.ToString(),
                        DisplayOrder = sub.DisplayOrder,
                        IsActive = sub.IsActive,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        var policyNames = await db.SlaPolicies.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        return
        [
            .. categories.Select(c => new AdminCategoryResponse
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code,
                Description = c.Description,
                DefaultTeamId = c.DefaultTeamId,
                DefaultTeamName = c.DefaultTeamName,
                SlaPolicyId = c.SlaPolicyId,
                SlaPolicyName = c.SlaPolicyId is { } id ? policyNames.GetValueOrDefault(id) : null,
                DisplayOrder = c.DisplayOrder,
                IsActive = c.IsActive,
                IsInternalOnly = c.IsInternalOnly,
                TicketCount = ticketCounts.GetValueOrDefault(c.Id),
                Subcategories = c.Subcategories,
            })
        ];
    }
}

public sealed record SaveCategoryCommand(Guid? Id, SaveCategoryRequest Request)
    : ICommand<AdminCategoryResponse>;

public sealed class SaveCategoryCommandValidator : AbstractValidator<SaveCategoryCommand>
{
    public SaveCategoryCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Code).NotEmpty().MaximumLength(20);
    }
}

public sealed class SaveCategoryCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveCategoryCommand, AdminCategoryResponse>
{
    public async Task<AdminCategoryResponse> HandleAsync(
        SaveCategoryCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;
        var code = r.Code.Trim().ToUpperInvariant();

        var clash = await db.Categories.AsNoTracking()
            .AnyAsync(c => c.Code == code && (command.Id == null || c.Id != command.Id), cancellationToken);

        if (clash)
        {
            throw new ConflictException("category_code_taken", $"Another category uses the code '{code}'.");
        }

        Category category;

        if (command.Id is { } id)
        {
            category = await db.Categories.AsTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(Category), id);
        }
        else
        {
            category = new Category { OrganizationId = organizationId, Name = r.Name, Code = code };
            db.Categories.Add(category);
        }

        category.Name = r.Name.Trim();
        category.Code = code;
        category.Description = r.Description?.Trim();
        category.DefaultTeamId = r.DefaultTeamId;
        category.SlaPolicyId = r.SlaPolicyId;
        category.DisplayOrder = r.DisplayOrder;
        category.IsActive = r.IsActive;
        category.IsInternalOnly = r.IsInternalOnly;

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(Category), category.Id, category.Name,
            changes: new { category.Name, category.Code, category.IsActive, category.DefaultTeamId },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var categories = await dispatcher.QueryAsync(new ListAdminCategoriesQuery(), cancellationToken);
        return categories.First(c => c.Id == category.Id);
    }
}

public sealed record SaveSubcategoryCommand(Guid? Id, SaveSubcategoryRequest Request)
    : ICommand<AdminCategoryResponse>;

public sealed class SaveSubcategoryCommandValidator : AbstractValidator<SaveSubcategoryCommand>
{
    public SaveSubcategoryCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Code).NotEmpty().MaximumLength(20);
    }
}

public sealed class SaveSubcategoryCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveSubcategoryCommand, AdminCategoryResponse>
{
    public async Task<AdminCategoryResponse> HandleAsync(
        SaveSubcategoryCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;

        var parent = await db.Categories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == r.CategoryId, cancellationToken)
            ?? throw new NotFoundException(nameof(Category), r.CategoryId);

        Subcategory subcategory;

        if (command.Id is { } id)
        {
            subcategory = await db.Subcategories.AsTracking()
                .FirstOrDefaultAsync(sub => sub.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(Subcategory), id);
        }
        else
        {
            subcategory = new Subcategory
            {
                OrganizationId = organizationId,
                CategoryId = parent.Id,
                Name = r.Name,
                Code = r.Code,
            };

            db.Subcategories.Add(subcategory);
        }

        subcategory.CategoryId = parent.Id;
        subcategory.Name = r.Name.Trim();
        subcategory.Code = r.Code.Trim().ToUpperInvariant();
        subcategory.Description = r.Description?.Trim();
        subcategory.DefaultTeamId = r.DefaultTeamId;
        subcategory.DefaultImpact = Enum.TryParse<ImpactLevel>(r.DefaultImpact, ignoreCase: true, out var impact)
            ? impact
            : null;
        subcategory.DisplayOrder = r.DisplayOrder;
        subcategory.IsActive = r.IsActive;

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(Subcategory), subcategory.Id, $"{parent.Name} / {subcategory.Name}",
            changes: new { subcategory.Name, subcategory.Code, subcategory.IsActive },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var categories = await dispatcher.QueryAsync(new ListAdminCategoriesQuery(), cancellationToken);
        return categories.First(c => c.Id == parent.Id);
    }
}

/// <summary>
/// Archives a category.
/// </summary>
/// <remarks>
/// Categories in use are deactivated rather than removed. A ticket raised last year
/// under "Network" must still say "Network" — rewriting or nulling it to permit a
/// tidy-up would silently falsify the history the reporting screens read from.
/// </remarks>
public sealed record DeleteCategoryCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteCategoryCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<DeleteCategoryCommand, bool>
{
    public async Task<bool> HandleAsync(
        DeleteCategoryCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var category = await db.Categories.AsTracking()
            .FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(Category), command.Id);

        var inUse = await db.Tickets.AsNoTracking()
            .AnyAsync(t => t.CategoryId == command.Id, cancellationToken);

        if (inUse)
        {
            throw new ConflictException(
                "category_in_use",
                "Tickets are filed under this category, so it cannot be removed. Deactivate "
                + "it instead: it disappears from the raise-a-ticket form while the existing "
                + "tickets keep saying what they always said.");
        }

        // Soft delete — the interceptor rewrites this as an archive.
        db.Categories.Remove(category);

        await audit.WriteAsync(
            AuditAction.Deleted, nameof(Category), category.Id, category.Name,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}

// --------------------------------------------------------------- applications

public sealed record ListAdminApplicationsQuery : IQuery<IReadOnlyList<AdminApplicationResponse>>;

public sealed class ListAdminApplicationsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListAdminApplicationsQuery, IReadOnlyList<AdminApplicationResponse>>
{
    public async Task<IReadOnlyList<AdminApplicationResponse>> HandleAsync(
        ListAdminApplicationsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        return await db.Applications.AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AdminApplicationResponse
            {
                Id = a.Id,
                Name = a.Name,
                Code = a.Code,
                Vendor = a.Vendor,
                Version = a.Version,
                OwningTeamId = a.OwningTeamId,
                OwningTeamName = a.OwningTeam == null ? null : a.OwningTeam.Name,
                IsBusinessCritical = a.IsBusinessCritical,
                IsActive = a.IsActive,
                Modules = a.Modules
                    .OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name)
                    .Select(m => new AdminModuleResponse
                    {
                        Id = m.Id,
                        ApplicationId = m.ApplicationId,
                        Name = m.Name,
                        Code = m.Code,
                        DisplayOrder = m.DisplayOrder,
                        IsActive = m.IsActive,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
    }
}

public sealed record SaveApplicationCommand(Guid? Id, SaveApplicationRequest Request)
    : ICommand<AdminApplicationResponse>;

public sealed class SaveApplicationCommandValidator : AbstractValidator<SaveApplicationCommand>
{
    public SaveApplicationCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Code).NotEmpty().MaximumLength(20);
    }
}

public sealed class SaveApplicationCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveApplicationCommand, AdminApplicationResponse>
{
    public async Task<AdminApplicationResponse> HandleAsync(
        SaveApplicationCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;
        var code = r.Code.Trim().ToUpperInvariant();

        BusinessApplication application;

        if (command.Id is { } id)
        {
            application = await db.Applications.AsTracking()
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(BusinessApplication), id);
        }
        else
        {
            application = new BusinessApplication
            {
                OrganizationId = organizationId,
                Name = r.Name,
                Code = code,
            };

            db.Applications.Add(application);
        }

        application.Name = r.Name.Trim();
        application.Code = code;
        application.Description = r.Description?.Trim();
        application.Vendor = r.Vendor?.Trim();
        application.Version = r.Version?.Trim();
        application.OwningTeamId = r.OwningTeamId;
        application.IsBusinessCritical = r.IsBusinessCritical;
        application.IsActive = r.IsActive;

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(BusinessApplication), application.Id, application.Name,
            changes: new { application.Name, application.Code, application.IsBusinessCritical },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var applications = await dispatcher.QueryAsync(new ListAdminApplicationsQuery(), cancellationToken);
        return applications.First(a => a.Id == application.Id);
    }
}

public sealed record SaveModuleCommand(Guid? Id, SaveModuleRequest Request)
    : ICommand<AdminApplicationResponse>;

public sealed class SaveModuleCommandValidator : AbstractValidator<SaveModuleCommand>
{
    public SaveModuleCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Code).NotEmpty().MaximumLength(20);
    }
}

public sealed class SaveModuleCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveModuleCommand, AdminApplicationResponse>
{
    public async Task<AdminApplicationResponse> HandleAsync(
        SaveModuleCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;

        var application = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == r.ApplicationId, cancellationToken)
            ?? throw new NotFoundException(nameof(BusinessApplication), r.ApplicationId);

        ApplicationModule module;

        if (command.Id is { } id)
        {
            module = await db.ApplicationModules.AsTracking()
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(ApplicationModule), id);
        }
        else
        {
            module = new ApplicationModule
            {
                OrganizationId = organizationId,
                ApplicationId = application.Id,
                Name = r.Name,
                Code = r.Code,
            };

            db.ApplicationModules.Add(module);
        }

        module.ApplicationId = application.Id;
        module.Name = r.Name.Trim();
        module.Code = r.Code.Trim().ToUpperInvariant();
        module.Description = r.Description?.Trim();
        module.OwningTeamId = r.OwningTeamId;
        module.DisplayOrder = r.DisplayOrder;
        module.IsActive = r.IsActive;

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(ApplicationModule), module.Id, $"{application.Name} / {module.Name}",
            changes: new { module.Name, module.Code, module.IsActive },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var applications = await dispatcher.QueryAsync(new ListAdminApplicationsQuery(), cancellationToken);
        return applications.First(a => a.Id == application.Id);
    }
}

// ------------------------------------------------------------ priority matrix

public sealed record GetPriorityMatrixQuery : IQuery<IReadOnlyList<PriorityMatrixCell>>;

public sealed class GetPriorityMatrixQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetPriorityMatrixQuery, IReadOnlyList<PriorityMatrixCell>>
{
    public async Task<IReadOnlyList<PriorityMatrixCell>> HandleAsync(
        GetPriorityMatrixQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var cells = await db.PriorityMatrixEntries.AsNoTracking()
            .Select(e => new { e.Impact, e.Urgency, e.Priority })
            .ToListAsync(cancellationToken);

        var configured = cells.ToDictionary(c => (c.Impact, c.Urgency), c => c.Priority);
        var result = new List<PriorityMatrixCell>(16);

        // Every combination is returned, filled from the built-in default where the
        // organization has no row. A grid with holes in it invites the reader to
        // assume the missing cells are impossible rather than merely unconfigured.
        foreach (var impact in Enum.GetValues<ImpactLevel>())
        {
            foreach (var urgency in Enum.GetValues<UrgencyLevel>())
            {
                var priority = configured.TryGetValue((impact, urgency), out var found)
                    ? found
                    : PriorityCalculator.DefaultFor(impact, urgency);

                result.Add(new PriorityMatrixCell
                {
                    Impact = impact.ToString(),
                    Urgency = urgency.ToString(),
                    Priority = priority.ToString(),
                });
            }
        }

        return result;
    }
}

public sealed record SavePriorityMatrixCommand(SavePriorityMatrixRequest Request)
    : ICommand<IReadOnlyList<PriorityMatrixCell>>;

/// <summary>
/// Rewrites the impact-by-urgency grid.
/// </summary>
/// <remarks>
/// <para>
/// Applies to tickets raised from now on. Existing tickets keep the priority they
/// were given, because their SLA clocks were started against it and silently
/// re-grading history would move deadlines that have already been met or missed.
/// </para>
/// <para>
/// All sixteen cells are required. A partial update would leave the grid in a state
/// where some pairs come from configuration and others from the built-in default,
/// which is the kind of thing nobody discovers until a Critical ticket comes out
/// Medium.
/// </para>
/// </remarks>
public sealed class SavePriorityMatrixCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SavePriorityMatrixCommand, IReadOnlyList<PriorityMatrixCell>>
{
    public async Task<IReadOnlyList<PriorityMatrixCell>> HandleAsync(
        SavePriorityMatrixCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCatalog);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var impacts = Enum.GetValues<ImpactLevel>();
        var urgencies = Enum.GetValues<UrgencyLevel>();
        var expected = impacts.Length * urgencies.Length;

        var parsed = new Dictionary<(ImpactLevel, UrgencyLevel), PriorityLevel>();

        foreach (var cell in command.Request.Cells)
        {
            if (!Enum.TryParse<ImpactLevel>(cell.Impact, ignoreCase: true, out var impact)
                || !Enum.TryParse<UrgencyLevel>(cell.Urgency, ignoreCase: true, out var urgency)
                || !Enum.TryParse<PriorityLevel>(cell.Priority, ignoreCase: true, out var priority))
            {
                throw new ValidationException(
                    $"'{cell.Impact}' / '{cell.Urgency}' / '{cell.Priority}' is not a valid matrix cell.");
            }

            parsed[(impact, urgency)] = priority;
        }

        if (parsed.Count != expected)
        {
            throw new ValidationException(
                $"The matrix needs all {expected} impact-and-urgency combinations; "
                + $"{parsed.Count} were supplied.");
        }

        var existing = await db.PriorityMatrixEntries.AsTracking().ToListAsync(cancellationToken);
        var changed = new List<string>();

        foreach (var ((impact, urgency), priority) in parsed)
        {
            var row = existing.FirstOrDefault(e => e.Impact == impact && e.Urgency == urgency);

            if (row is null)
            {
                db.PriorityMatrixEntries.Add(new PriorityMatrixEntry
                {
                    OrganizationId = organizationId,
                    Impact = impact,
                    Urgency = urgency,
                    Priority = priority,
                });

                changed.Add($"{impact}/{urgency}→{priority}");
            }
            else if (row.Priority != priority)
            {
                changed.Add($"{impact}/{urgency}: {row.Priority}→{priority}");
                row.Priority = priority;
            }
        }

        await audit.WriteAsync(
            AuditAction.ConfigurationChanged, nameof(PriorityMatrixEntry), null, "Priority matrix",
            changes: new { Changed = changed.Count, Cells = string.Join("; ", changed) },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await dispatcher.QueryAsync(new GetPriorityMatrixQuery(), cancellationToken);
    }
}
