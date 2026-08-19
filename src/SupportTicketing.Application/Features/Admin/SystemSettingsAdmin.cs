using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Auditing;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Admin;

public sealed record ListSystemSettingsQuery : IQuery<IReadOnlyList<SystemSettingResponse>>;

/// <summary>
/// Runtime configuration an administrator can change without a deployment.
/// </summary>
/// <remarks>
/// <para>
/// Settings marked sensitive are returned masked. This endpoint exists to let an
/// administrator confirm that an integration credential is <em>set</em>, not to hand
/// it back to a browser — once a secret is rendered on a screen it is in a screenshot,
/// a support chat and a browser cache.
/// </para>
/// <para>
/// A null organization means a global default. Rows for this tenant shadow the global
/// row of the same key, and both are listed so it is visible which is in force.
/// </para>
/// </remarks>
public sealed class ListSystemSettingsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListSystemSettingsQuery, IReadOnlyList<SystemSettingResponse>>
{
    internal const string Mask = "••••••••";

    public async Task<IReadOnlyList<SystemSettingResponse>> HandleAsync(
        ListSystemSettingsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ConfigureSystem);

        var organizationId = currentUser.OrganizationId;

        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => s.OrganizationId == null || s.OrganizationId == organizationId)
            .OrderBy(s => s.Category).ThenBy(s => s.Key)
            .Select(s => new
            {
                s.Id, s.Key, s.Value, s.ValueType, s.Description, s.Category,
                s.IsSensitive, s.IsSystemManaged, s.OrganizationId, s.UpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        // Where both a global default and a tenant override exist, only the override
        // is shown: listing both invites editing the one that is not in force.
        var overridden = rows
            .Where(r => r.OrganizationId != null)
            .Select(r => r.Key)
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. rows
                .Where(r => r.OrganizationId != null || !overridden.Contains(r.Key))
                .Select(r => new SystemSettingResponse
                {
                    Id = r.Id,
                    Key = r.Key,
                    Value = r.IsSensitive ? Mask : r.Value,
                    ValueType = r.ValueType,
                    Description = r.Description,
                    Category = r.Category,
                    IsSensitive = r.IsSensitive,
                    IsSystemManaged = r.IsSystemManaged,
                    IsOrganizationOverride = r.OrganizationId != null,
                    UpdatedAtUtc = r.UpdatedAtUtc,
                })
        ];
    }
}

public sealed record SaveSystemSettingCommand(SaveSystemSettingRequest Request)
    : ICommand<SystemSettingResponse>;

public sealed class SaveSystemSettingCommandValidator : AbstractValidator<SaveSystemSettingCommand>
{
    private static readonly string[] ValueTypes = ["string", "int", "bool", "decimal", "json"];

    public SaveSystemSettingCommandValidator()
    {
        RuleFor(c => c.Request.Key)
            .NotEmpty()
            .MaximumLength(150)
            .Matches("^[A-Za-z][A-Za-z0-9_.:-]*$")
            .WithMessage("A key may contain letters, digits and . : _ - and must start with a letter.");

        RuleFor(c => c.Request.Value).NotNull().MaximumLength(4000);

        RuleFor(c => c.Request.ValueType)
            .Must(t => string.IsNullOrWhiteSpace(t) || ValueTypes.Contains(t.ToLowerInvariant()))
            .WithMessage($"Value type must be one of: {string.Join(", ", ValueTypes)}.");
    }
}

/// <summary>
/// Writes a per-organization setting, creating the override if it does not exist.
/// </summary>
/// <remarks>
/// Global rows are never edited here. Changing a global default from a tenant's
/// administration screen would silently alter every other tenant, so a save always
/// produces or updates a row owned by the caller's organization instead.
/// </remarks>
public sealed class SaveSystemSettingCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<SaveSystemSettingCommand, SystemSettingResponse>
{
    public async Task<SystemSettingResponse> HandleAsync(
        SaveSystemSettingCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ConfigureSystem);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;
        var key = r.Key.Trim();

        var global = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == null && s.Key == key, cancellationToken);

        if (global?.IsSystemManaged == true && !currentUser.Has(Permissions.Administration.ManageOrganizations))
        {
            throw new ForbiddenException(
                $"'{key}' is system managed and can only be changed by a Super Admin.");
        }

        var setting = await db.SystemSettings.AsTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Key == key, cancellationToken);

        var isNew = setting is null;

        if (setting is null)
        {
            setting = new SystemSetting
            {
                OrganizationId = organizationId,
                Key = key,
                Value = r.Value,
                ValueType = string.IsNullOrWhiteSpace(r.ValueType) ? "string" : r.ValueType.ToLowerInvariant(),
            };

            db.SystemSettings.Add(setting);
        }

        // A masked value coming back unchanged means "leave it alone", not "set the
        // secret to a row of dots". Without this, opening the page and pressing save
        // destroys every credential on it.
        var sensitive = r.IsSensitive || setting.IsSensitive;

        if (!(sensitive && r.Value == ListSystemSettingsQueryHandler.Mask))
        {
            setting.Value = r.Value;
        }

        setting.ValueType = string.IsNullOrWhiteSpace(r.ValueType)
            ? setting.ValueType
            : r.ValueType.ToLowerInvariant();
        setting.Description = r.Description?.Trim() ?? global?.Description;
        setting.Category = r.Category?.Trim() ?? global?.Category;
        setting.IsSensitive = sensitive;

        await audit.WriteAsync(
            AuditAction.ConfigurationChanged, nameof(SystemSetting), setting.Id, key,
            // The value is recorded only when it is not a secret. An audit trail that
            // logs the credential it was protecting has defeated its own purpose.
            changes: new
            {
                Key = key,
                Value = setting.IsSensitive ? "(not recorded)" : setting.Value,
                setting.ValueType,
                Created = isNew,
            },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new SystemSettingResponse
        {
            Id = setting.Id,
            Key = setting.Key,
            Value = setting.IsSensitive ? ListSystemSettingsQueryHandler.Mask : setting.Value,
            ValueType = setting.ValueType,
            Description = setting.Description,
            Category = setting.Category,
            IsSensitive = setting.IsSensitive,
            IsSystemManaged = global?.IsSystemManaged ?? false,
            IsOrganizationOverride = true,
            UpdatedAtUtc = setting.UpdatedAtUtc,
        };
    }
}

public sealed record DeleteSystemSettingCommand(Guid Id) : ICommand<bool>;

/// <summary>
/// Removes an organization's override, restoring the global default.
/// </summary>
public sealed class DeleteSystemSettingCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<DeleteSystemSettingCommand, bool>
{
    public async Task<bool> HandleAsync(
        DeleteSystemSettingCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ConfigureSystem);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var setting = await db.SystemSettings.AsTracking()
            .FirstOrDefaultAsync(
                s => s.Id == command.Id && s.OrganizationId == organizationId, cancellationToken)
            ?? throw new NotFoundException(nameof(SystemSetting), command.Id);

        db.SystemSettings.Remove(setting);

        await audit.WriteAsync(
            AuditAction.ConfigurationChanged, nameof(SystemSetting), setting.Id, setting.Key,
            changes: new { Key = setting.Key, RevertedToGlobalDefault = true },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}

// ------------------------------------------------------------------ reference

public sealed record GetAdminReferenceDataQuery : IQuery<AdminReferenceData>;

public sealed class GetAdminReferenceDataQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetAdminReferenceDataQuery, AdminReferenceData>
{
    public async Task<AdminReferenceData> HandleAsync(
        GetAdminReferenceDataQuery query, CancellationToken cancellationToken)
    {
        // Any administration screen may ask for this, so it is gated on holding at
        // least one of the administration permissions rather than on a specific one.
        if (!currentUser.Permissions.Any(p => p.StartsWith("users.", StringComparison.Ordinal)
                                              || p.StartsWith("roles.", StringComparison.Ordinal)
                                              || p.StartsWith("teams.", StringComparison.Ordinal)
                                              || p.StartsWith("catalog.", StringComparison.Ordinal)
                                              || p.StartsWith("sla.", StringComparison.Ordinal)
                                              || p.StartsWith("calendars.", StringComparison.Ordinal)
                                              || p.StartsWith("system.", StringComparison.Ordinal)))
        {
            throw new ForbiddenException();
        }

        return new AdminReferenceData
        {
            Departments = await LookupAsync(db.Departments.Select(d => new LookupItem(d.Id, d.Name, d.IsActive))),
            Offices = await LookupAsync(db.Offices.Select(o => new LookupItem(o.Id, o.Name, o.IsActive))),
            Teams = await LookupAsync(db.Teams.Select(t => new LookupItem(t.Id, t.Name, t.IsActive))),
            Roles = await LookupAsync(db.Roles.Select(r => new LookupItem(r.Id, r.Name, true))),
            Categories = await LookupAsync(db.Categories.Select(c => new LookupItem(c.Id, c.Name, c.IsActive))),
            SlaPolicies = await LookupAsync(db.SlaPolicies.Select(p => new LookupItem(p.Id, p.Name, p.IsActive))),
            BusinessCalendars = await LookupAsync(
                db.BusinessCalendars.Select(c => new LookupItem(c.Id, c.Name, c.IsActive))),
            // Deleted accounts keep their row so tickets stay attributable, but they
            // are not people any more and must not appear in a picker.
            Users = await LookupAsync(db.Users
                .Where(u => !u.IsAnonymised)
                .Select(u => new LookupItem(u.Id, u.FirstName + " " + u.LastName, u.IsActive))),
        };

        // Ordered after materialising. Sorting by a property of a record the query
        // projects into is not translatable — the provider has no column to sort on —
        // and these lists are short enough that the ordering is free in memory.
        async Task<IReadOnlyList<LookupItem>> LookupAsync(IQueryable<LookupItem> source)
        {
            var items = await source.AsNoTracking().ToListAsync(cancellationToken);
            return [.. items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }
}
