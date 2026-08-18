namespace SupportTicketing.Domain.Sla;

/// <summary>One working window on one weekday, in calendar-local minutes past midnight.</summary>
public readonly record struct BusinessWindow(DayOfWeek Day, int StartMinute, int EndMinute)
{
    public int Length => EndMinute - StartMinute;
}

/// <summary>
/// An immutable snapshot of a working calendar, in the shape the calculator needs.
/// </summary>
/// <remarks>
/// Deliberately a plain value object rather than the EF entity, so the calculator can
/// be exercised from a unit test with a hand-built calendar and no database.
/// </remarks>
public sealed class WorkingCalendar
{
    public WorkingCalendar(
        TimeZoneInfo timeZone,
        IEnumerable<BusinessWindow>? windows = null,
        IEnumerable<DateOnly>? holidays = null,
        IEnumerable<(int Month, int Day)>? recurringHolidays = null)
    {
        TimeZone = timeZone;

        Windows = (windows ?? [])
            .Where(w => w.EndMinute > w.StartMinute)
            .OrderBy(w => w.Day)
            .ThenBy(w => w.StartMinute)
            .ToList();

        Holidays = (holidays ?? []).ToHashSet();
        RecurringHolidays = (recurringHolidays ?? []).ToHashSet();
    }

    public TimeZoneInfo TimeZone { get; }
    public IReadOnlyList<BusinessWindow> Windows { get; }
    public IReadOnlySet<DateOnly> Holidays { get; }
    public IReadOnlySet<(int Month, int Day)> RecurringHolidays { get; }

    /// <summary>
    /// A calendar with no windows means round-the-clock cover.
    /// </summary>
    /// <remarks>
    /// This is the safe reading. Treating "no hours configured" as "no working time"
    /// would make every deadline unreachable, and the calculator would walk years of
    /// days looking for a minute of business time that never arrives.
    /// </remarks>
    public bool IsContinuous => Windows.Count == 0;

    public bool IsHoliday(DateOnly date) =>
        Holidays.Contains(date) || RecurringHolidays.Contains((date.Month, date.Day));

    /// <summary>Round-the-clock calendar, used when an organization has configured nothing.</summary>
    public static WorkingCalendar Continuous(TimeZoneInfo? timeZone = null) =>
        new(timeZone ?? TimeZoneInfo.Utc);

    /// <summary>Monday to Friday, nine to five. The conventional default.</summary>
    public static WorkingCalendar StandardWeek(TimeZoneInfo? timeZone = null, int startHour = 9, int endHour = 17)
    {
        var windows = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday,
        }.Select(day => new BusinessWindow(day, startHour * 60, endHour * 60));

        return new WorkingCalendar(timeZone ?? TimeZoneInfo.Utc, windows);
    }
}

/// <summary>
/// Converts durations expressed in business minutes into absolute instants.
/// </summary>
/// <remarks>
/// <para>
/// This is why an SLA cannot be a timestamp plus a TimeSpan. A four-hour resolution
/// target on a ticket raised at 16:00 on a Friday is not due at 20:00 that evening;
/// under Monday-to-Friday nine-to-five cover it is due at 12:00 on Monday. Without
/// this, every out-of-hours ticket breaches before anyone has seen it.
/// </para>
/// <para>
/// Every method is pure and takes its clock as an argument, so the class is fully
/// testable without a database, a timer, or a particular machine time zone.
/// </para>
/// </remarks>
public static class BusinessHoursCalculator
{
    /// <summary>
    /// Bounds the search so a pathological calendar cannot spin forever. Ten years of
    /// days is far beyond any real SLA and still cheap to walk.
    /// </summary>
    private const int MaxDaysToScan = 3_650;

    /// <summary>
    /// Returns the instant at which the given number of business minutes will have
    /// elapsed, counting from the supplied start.
    /// </summary>
    public static DateTime AddBusinessMinutes(DateTime startUtc, int businessMinutes, WorkingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (businessMinutes <= 0)
        {
            return startUtc;
        }

        if (calendar.IsContinuous)
        {
            return startUtc.AddMinutes(businessMinutes);
        }

        var local = ToLocal(startUtc, calendar.TimeZone);
        var remaining = businessMinutes;

        var date = DateOnly.FromDateTime(local);
        var cursorMinute = (int)local.TimeOfDay.TotalMinutes;

        for (var scanned = 0; scanned < MaxDaysToScan; scanned++)
        {
            if (!calendar.IsHoliday(date))
            {
                foreach (var window in WindowsOn(calendar, date.DayOfWeek))
                {
                    // A window already behind the cursor contributes nothing today.
                    var from = Math.Max(cursorMinute, window.StartMinute);

                    if (from >= window.EndMinute)
                    {
                        continue;
                    }

                    var available = window.EndMinute - from;

                    if (remaining <= available)
                    {
                        return ToUtc(date, from + remaining, calendar.TimeZone);
                    }

                    remaining -= available;
                }
            }

            date = date.AddDays(1);
            cursorMinute = 0;
        }

        throw new InvalidOperationException(
            "Could not place a deadline within " + MaxDaysToScan + " days of " + startUtc.ToString("o")
            + ". The calendar likely has no usable working hours.");
    }

    /// <summary>
    /// Counts the business minutes between two instants, ignoring time outside working
    /// hours, at a weekend, or on a holiday.
    /// </summary>
    public static int BusinessMinutesBetween(DateTime fromUtc, DateTime toUtc, WorkingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (toUtc <= fromUtc)
        {
            return 0;
        }

        if (calendar.IsContinuous)
        {
            return (int)Math.Round((toUtc - fromUtc).TotalMinutes);
        }

        var localFrom = ToLocal(fromUtc, calendar.TimeZone);
        var localTo = ToLocal(toUtc, calendar.TimeZone);

        var date = DateOnly.FromDateTime(localFrom);
        var endDate = DateOnly.FromDateTime(localTo);

        var cursorMinute = (int)localFrom.TimeOfDay.TotalMinutes;
        var endMinute = (int)localTo.TimeOfDay.TotalMinutes;

        var total = 0;

        for (var scanned = 0; scanned <= MaxDaysToScan && date <= endDate; scanned++)
        {
            if (!calendar.IsHoliday(date))
            {
                // On the final day, stop at the end instant rather than at the window close.
                var dayCeiling = date == endDate ? endMinute : 1440;

                foreach (var window in WindowsOn(calendar, date.DayOfWeek))
                {
                    var from = Math.Max(cursorMinute, window.StartMinute);
                    var to = Math.Min(dayCeiling, window.EndMinute);

                    if (to > from)
                    {
                        total += to - from;
                    }
                }
            }

            date = date.AddDays(1);
            cursorMinute = 0;
        }

        return total;
    }

    /// <summary>True when the instant falls inside a working window.</summary>
    public static bool IsWithinBusinessHours(DateTime instantUtc, WorkingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (calendar.IsContinuous)
        {
            return true;
        }

        var local = ToLocal(instantUtc, calendar.TimeZone);
        var date = DateOnly.FromDateTime(local);

        if (calendar.IsHoliday(date))
        {
            return false;
        }

        var minute = (int)local.TimeOfDay.TotalMinutes;

        return WindowsOn(calendar, date.DayOfWeek)
            .Any(w => minute >= w.StartMinute && minute < w.EndMinute);
    }

    private static IEnumerable<BusinessWindow> WindowsOn(WorkingCalendar calendar, DayOfWeek day) =>
        calendar.Windows.Where(w => w.Day == day);

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>
    /// Converts a calendar-local date and minute back to UTC, coping with both
    /// daylight-saving discontinuities.
    /// </summary>
    /// <remarks>
    /// On the spring-forward night a local time such as 02:30 does not exist, and
    /// ConvertTimeToUtc throws rather than guessing. On the autumn night the same local
    /// time occurs twice. Both are handled explicitly, so a deadline landing on a
    /// clock-change weekend yields a real instant instead of an exception or an
    /// hour-long error.
    /// </remarks>
    private static DateTime ToUtc(DateOnly date, int minuteOfDay, TimeZoneInfo zone)
    {
        // A window may legitimately end at midnight, which rolls into the next day.
        var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // The clock jumped forward over this instant; the next real one is an hour on.
            local = local.AddHours(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Two instants share this local time. Taking the larger offset yields the
            // earlier instant, which is the tighter deadline and the safer reading of
            // a commitment.
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            return DateTime.SpecifyKind(local - offsets.Max(), DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
