using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Sla;

namespace SupportTicketing.Application.Features.Admin;

// ------------------------------------------------------------------ policies

public sealed record ListSlaPoliciesQuery : IQuery<IReadOnlyList<SlaPolicyResponse>>;

public sealed class ListSlaPoliciesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListSlaPoliciesQuery, IReadOnlyList<SlaPolicyResponse>>
{
    public async Task<IReadOnlyList<SlaPolicyResponse>> HandleAsync(
        ListSlaPoliciesQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Sla.Manage);

        // Running clocks per policy: editing targets does not move an existing
        // deadline, and an administrator deciding whether to edit or supersede needs
        // to see how much work is currently measured against this policy.
        var activeClocks = await db.TicketSlaInstances.AsNoTracking()
            .Where(i => i.PolicyId != null && i.ResolutionState == SlaTimerState.Running)
            .GroupBy(i => i.PolicyId!.Value)
            .Select(g => new { PolicyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PolicyId, x => x.Count, cancellationToken);

        var policies = await db.SlaPolicies.AsNoTracking()
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Name, p.Description,
                p.BusinessCalendarId,
                BusinessCalendarName = p.BusinessCalendar == null ? null : p.BusinessCalendar.Name,
                p.CategoryId,
                p.TicketType,
                p.IsDefault, p.IsActive, p.PauseWhenWaitingOnOthers,
                Targets = p.Targets
                    .OrderByDescending(t => t.Priority)
                    .Select(t => new SlaTargetResponse
                    {
                        Priority = t.Priority.ToString(),
                        ResponseMinutes = t.ResponseMinutes,
                        ResolutionMinutes = t.ResolutionMinutes,
                        WarningThresholdPercent = t.WarningThresholdPercent,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        var categoryNames = await db.Categories.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        return
        [
            .. policies.Select(p => new SlaPolicyResponse
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                BusinessCalendarId = p.BusinessCalendarId,
                BusinessCalendarName = p.BusinessCalendarName,
                CategoryId = p.CategoryId,
                CategoryName = p.CategoryId is { } id ? categoryNames.GetValueOrDefault(id) : null,
                TicketType = p.TicketType?.ToString(),
                IsDefault = p.IsDefault,
                IsActive = p.IsActive,
                PauseWhenWaitingOnOthers = p.PauseWhenWaitingOnOthers,
                Targets = p.Targets,
                ActiveClocks = activeClocks.GetValueOrDefault(p.Id),
            })
        ];
    }
}

public sealed record SaveSlaPolicyCommand(Guid? Id, SaveSlaPolicyRequest Request)
    : ICommand<SlaPolicyResponse>;

public sealed class SaveSlaPolicyCommandValidator : AbstractValidator<SaveSlaPolicyCommand>
{
    public SaveSlaPolicyCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Targets).NotEmpty().WithMessage("A policy needs at least one target.");

        RuleForEach(c => c.Request.Targets).ChildRules(target =>
        {
            target.RuleFor(t => t.ResponseMinutes).InclusiveBetween(1, 525_600);
            target.RuleFor(t => t.ResolutionMinutes).InclusiveBetween(1, 525_600);
            target.RuleFor(t => t.WarningThresholdPercent).InclusiveBetween(1, 99);

            // A resolution target inside the response target is not a stricter policy,
            // it is an unsatisfiable one: the clock would breach resolution before a
            // reply was even due.
            target.RuleFor(t => t)
                .Must(t => t.ResolutionMinutes >= t.ResponseMinutes)
                .WithMessage("Resolution cannot be sooner than first response.");
        });
    }
}

/// <summary>
/// Creates or edits a policy and its per-priority targets.
/// </summary>
/// <remarks>
/// New targets apply to clocks started from now on. Running clocks keep the deadline
/// they were given, because a deadline that moves after the fact makes "did we meet
/// it?" unanswerable, and every SLA report built on this data would quietly change
/// its own history.
/// </remarks>
public sealed class SaveSlaPolicyCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveSlaPolicyCommand, SlaPolicyResponse>
{
    public async Task<SlaPolicyResponse> HandleAsync(
        SaveSlaPolicyCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Sla.Manage);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;

        SlaPolicy policy;

        if (command.Id is { } id)
        {
            policy = await db.SlaPolicies.AsTracking()
                .Include(p => p.Targets)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(SlaPolicy), id);
        }
        else
        {
            policy = new SlaPolicy { OrganizationId = organizationId, Name = r.Name };
            db.SlaPolicies.Add(policy);
        }

        policy.Name = r.Name.Trim();
        policy.Description = r.Description?.Trim();
        policy.BusinessCalendarId = r.BusinessCalendarId;
        policy.CategoryId = r.CategoryId;
        policy.TicketType = Enum.TryParse<TicketType>(r.TicketType, ignoreCase: true, out var type)
            ? type
            : null;
        policy.IsActive = r.IsActive;
        policy.PauseWhenWaitingOnOthers = r.PauseWhenWaitingOnOthers;

        if (r.IsDefault && !policy.IsDefault)
        {
            // Exactly one default. Two would make which policy a ticket receives
            // depend on row order, which is not a decision anyone made.
            var others = await db.SlaPolicies.AsTracking()
                .Where(p => p.IsDefault && p.Id != policy.Id)
                .ToListAsync(cancellationToken);

            foreach (var other in others)
            {
                other.IsDefault = false;
            }
        }

        policy.IsDefault = r.IsDefault;

        await ApplyTargetsAsync(db, policy, organizationId, r.Targets, cancellationToken);

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(SlaPolicy), policy.Id, policy.Name,
            changes: new
            {
                policy.Name,
                policy.IsDefault,
                policy.IsActive,
                Targets = string.Join("; ", r.Targets.Select(t =>
                    $"{t.Priority} {t.ResponseMinutes}/{t.ResolutionMinutes}m")),
            },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var policies = await dispatcher.QueryAsync(new ListSlaPoliciesQuery(), cancellationToken);
        return policies.First(p => p.Id == policy.Id);
    }

    private static async Task ApplyTargetsAsync(
        IAppDbContext db, SlaPolicy policy, Guid organizationId,
        IReadOnlyList<SlaTargetResponse> targets, CancellationToken cancellationToken)
    {
        var existing = await db.SlaTargets.AsTracking()
            .Where(t => t.PolicyId == policy.Id)
            .ToListAsync(cancellationToken);

        var wanted = new HashSet<PriorityLevel>();

        foreach (var target in targets)
        {
            if (!Enum.TryParse<PriorityLevel>(target.Priority, ignoreCase: true, out var priority))
            {
                throw new ValidationException($"'{target.Priority}' is not a known priority.");
            }

            wanted.Add(priority);

            var row = existing.FirstOrDefault(t => t.Priority == priority);

            if (row is null)
            {
                db.SlaTargets.Add(new SlaTarget
                {
                    OrganizationId = organizationId,
                    PolicyId = policy.Id,
                    Priority = priority,
                    ResponseMinutes = target.ResponseMinutes,
                    ResolutionMinutes = target.ResolutionMinutes,
                    WarningThresholdPercent = target.WarningThresholdPercent,
                });
            }
            else
            {
                row.ResponseMinutes = target.ResponseMinutes;
                row.ResolutionMinutes = target.ResolutionMinutes;
                row.WarningThresholdPercent = target.WarningThresholdPercent;
            }
        }

        foreach (var removed in existing.Where(t => !wanted.Contains(t.Priority)))
        {
            db.SlaTargets.Remove(removed);
        }
    }
}

// ----------------------------------------------------------------- calendars

public sealed record ListBusinessCalendarsQuery : IQuery<IReadOnlyList<BusinessCalendarResponse>>;

public sealed class ListBusinessCalendarsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListBusinessCalendarsQuery, IReadOnlyList<BusinessCalendarResponse>>
{
    public async Task<IReadOnlyList<BusinessCalendarResponse>> HandleAsync(
        ListBusinessCalendarsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCalendars);

        var policyCounts = await db.SlaPolicies.AsNoTracking()
            .Where(p => p.BusinessCalendarId != null)
            .GroupBy(p => p.BusinessCalendarId!.Value)
            .Select(g => new { CalendarId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CalendarId, x => x.Count, cancellationToken);

        var calendars = await db.BusinessCalendars.AsNoTracking()
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.Code, c.TimeZoneId, c.IsDefault, c.IsActive,
                Hours = c.Hours
                    .OrderBy(h => h.DayOfWeek).ThenBy(h => h.StartMinute)
                    .Select(h => new BusinessHourResponse
                    {
                        DayOfWeek = h.DayOfWeek.ToString(),
                        StartMinute = h.StartMinute,
                        EndMinute = h.EndMinute,
                    })
                    .ToList(),
                Holidays = c.Holidays
                    .OrderBy(h => h.DateUtc)
                    .Select(h => new HolidayResponse
                    {
                        Id = h.Id,
                        Name = h.Name,
                        DateUtc = h.DateUtc,
                        IsRecurring = h.IsRecurring,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. calendars.Select(c => new BusinessCalendarResponse
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code,
                TimeZoneId = c.TimeZoneId,
                IsDefault = c.IsDefault,
                IsActive = c.IsActive,
                Hours = c.Hours,
                Holidays = c.Holidays,
                PoliciesUsing = policyCounts.GetValueOrDefault(c.Id),
            })
        ];
    }
}

public sealed record SaveBusinessCalendarCommand(Guid? Id, SaveBusinessCalendarRequest Request)
    : ICommand<BusinessCalendarResponse>;

public sealed class SaveBusinessCalendarCommandValidator : AbstractValidator<SaveBusinessCalendarCommand>
{
    public SaveBusinessCalendarCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Request.Code).NotEmpty().MaximumLength(20);

        RuleFor(c => c.Request.TimeZoneId)
            .Must(BeAKnownTimeZone)
            .WithMessage("That time zone is not installed on the server.");

        RuleForEach(c => c.Request.Hours).ChildRules(hour =>
        {
            hour.RuleFor(h => h.StartMinute).InclusiveBetween(0, 1440);
            hour.RuleFor(h => h.EndMinute).InclusiveBetween(0, 1440);
            hour.RuleFor(h => h)
                .Must(h => h.EndMinute > h.StartMinute)
                .WithMessage("A working window must end after it starts.");
        });
    }

    /// <summary>
    /// Rejected here rather than at first use.
    /// </summary>
    /// <remarks>
    /// An unknown time zone identifier saved now surfaces later inside the SLA sweep,
    /// as a background job throwing on a ticket nobody is watching. Failing at the
    /// point of configuration puts the error in front of the person who caused it.
    /// </remarks>
    private static bool BeAKnownTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public sealed class SaveBusinessCalendarCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<SaveBusinessCalendarCommand, BusinessCalendarResponse>
{
    public async Task<BusinessCalendarResponse> HandleAsync(
        SaveBusinessCalendarCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCalendars);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var r = command.Request;
        var code = r.Code.Trim().ToUpperInvariant();

        BusinessCalendar calendar;

        if (command.Id is { } id)
        {
            calendar = await db.BusinessCalendars.AsTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(BusinessCalendar), id);
        }
        else
        {
            calendar = new BusinessCalendar { OrganizationId = organizationId, Name = r.Name, Code = code };
            db.BusinessCalendars.Add(calendar);
        }

        calendar.Name = r.Name.Trim();
        calendar.Code = code;
        calendar.Description = r.Description?.Trim();
        calendar.TimeZoneId = r.TimeZoneId;
        calendar.IsActive = r.IsActive;

        if (r.IsDefault && !calendar.IsDefault)
        {
            var others = await db.BusinessCalendars.AsTracking()
                .Where(c => c.IsDefault && c.Id != calendar.Id)
                .ToListAsync(cancellationToken);

            foreach (var other in others)
            {
                other.IsDefault = false;
            }
        }

        calendar.IsDefault = r.IsDefault;

        // Hours are replaced wholesale. An administrator editing a weekly grid is
        // describing the whole week, and merging per-row would make removing Saturday
        // a different operation from shortening it.
        var existingHours = await db.BusinessHours.AsTracking()
            .Where(h => h.CalendarId == calendar.Id)
            .ToListAsync(cancellationToken);

        foreach (var hour in existingHours)
        {
            db.BusinessHours.Remove(hour);
        }

        foreach (var hour in r.Hours)
        {
            if (!Enum.TryParse<DayOfWeek>(hour.DayOfWeek, ignoreCase: true, out var day))
            {
                throw new ValidationException($"'{hour.DayOfWeek}' is not a day of the week.");
            }

            db.BusinessHours.Add(new BusinessHour
            {
                OrganizationId = organizationId,
                CalendarId = calendar.Id,
                DayOfWeek = day,
                StartMinute = hour.StartMinute,
                EndMinute = hour.EndMinute,
            });
        }

        await audit.WriteAsync(
            command.Id is null ? AuditAction.Created : AuditAction.Updated,
            nameof(BusinessCalendar), calendar.Id, calendar.Name,
            changes: new { calendar.Name, calendar.TimeZoneId, calendar.IsDefault, Windows = r.Hours.Count },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var calendars = await dispatcher.QueryAsync(new ListBusinessCalendarsQuery(), cancellationToken);
        return calendars.First(c => c.Id == calendar.Id);
    }
}

// ------------------------------------------------------------------ holidays

public sealed record AddHolidayCommand(Guid CalendarId, SaveHolidayRequest Request)
    : ICommand<BusinessCalendarResponse>;

public sealed class AddHolidayCommandValidator : AbstractValidator<AddHolidayCommand>
{
    public AddHolidayCommandValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(150);
    }
}

public sealed class AddHolidayCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<AddHolidayCommand, BusinessCalendarResponse>
{
    public async Task<BusinessCalendarResponse> HandleAsync(
        AddHolidayCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCalendars);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var calendar = await db.BusinessCalendars.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == command.CalendarId, cancellationToken)
            ?? throw new NotFoundException(nameof(BusinessCalendar), command.CalendarId);

        // Stored at midnight: a holiday is a date, and a stray time component would
        // make the SLA calculator treat the morning of a public holiday as working.
        var date = command.Request.DateUtc.Date;

        var duplicate = await db.Holidays.AsNoTracking()
            .AnyAsync(h => h.CalendarId == calendar.Id && h.DateUtc == date, cancellationToken);

        if (duplicate)
        {
            throw new ConflictException(
                "holiday_exists", "That date is already a holiday on this calendar.");
        }

        db.Holidays.Add(new Holiday
        {
            OrganizationId = organizationId,
            CalendarId = calendar.Id,
            Name = command.Request.Name.Trim(),
            DateUtc = date,
            IsRecurring = command.Request.IsRecurring,
        });

        await audit.WriteAsync(
            AuditAction.Created, nameof(Holiday), calendar.Id,
            $"{calendar.Name} / {command.Request.Name}",
            changes: new { command.Request.Name, Date = date, command.Request.IsRecurring },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var calendars = await dispatcher.QueryAsync(new ListBusinessCalendarsQuery(), cancellationToken);
        return calendars.First(c => c.Id == calendar.Id);
    }
}

public sealed record RemoveHolidayCommand(Guid CalendarId, Guid HolidayId)
    : ICommand<BusinessCalendarResponse>;

public sealed class RemoveHolidayCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IDispatcher dispatcher, IAuditWriter audit)
    : ICommandHandler<RemoveHolidayCommand, BusinessCalendarResponse>
{
    public async Task<BusinessCalendarResponse> HandleAsync(
        RemoveHolidayCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Administration.ManageCalendars);

        var holiday = await db.Holidays.AsTracking()
            .FirstOrDefaultAsync(
                h => h.Id == command.HolidayId && h.CalendarId == command.CalendarId, cancellationToken)
            ?? throw new NotFoundException(nameof(Holiday), command.HolidayId);

        db.Holidays.Remove(holiday);

        await audit.WriteAsync(
            AuditAction.Deleted, nameof(Holiday), command.CalendarId, holiday.Name,
            changes: new { holiday.Name, holiday.DateUtc },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var calendars = await dispatcher.QueryAsync(new ListBusinessCalendarsQuery(), cancellationToken);
        return calendars.First(c => c.Id == command.CalendarId);
    }
}
