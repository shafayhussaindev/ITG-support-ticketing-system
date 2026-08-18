using SupportTicketing.Domain.Sla;

namespace SupportTicketing.UnitTests.Sla;

/// <summary>
/// The awkward cases: split shifts, time zones, daylight saving, and the degenerate
/// calendars that would otherwise hang the calculator.
/// </summary>
public class BusinessHoursCalculatorEdgeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTime At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static WorkingCalendar Weekday(int startHour, int endHour) =>
        new(Utc, new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday,
        }.Select(d => new BusinessWindow(d, startHour * 60, endHour * 60)));

    /// <summary>Resolves a zone by IANA name, falling back to the Windows name on older hosts.</summary>
    private static TimeZoneInfo FindZone(string iana, string windows)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windows);
        }
    }

    [Fact]
    public void A_calendar_with_no_windows_means_round_the_clock()
    {
        // Treating "nothing configured" as "no working time" would make every deadline
        // unreachable, so an empty calendar is continuous rather than empty.
        var calendar = WorkingCalendar.Continuous();

        calendar.IsContinuous.ShouldBeTrue();

        BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 22, 23), 120, calendar)
            .ShouldBe(At(2026, 8, 23, 1));
    }

    [Fact]
    public void Continuous_cover_ignores_weekends_and_holidays()
    {
        var calendar = new WorkingCalendar(Utc, windows: null, holidays: [new DateOnly(2026, 8, 22)]);

        BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 22, 10), 60, calendar)
            .ShouldBe(At(2026, 8, 22, 11));
    }

    [Fact]
    public void A_lunch_break_is_not_counted_as_working_time()
    {
        var calendar = new WorkingCalendar(Utc,
        [
            new BusinessWindow(DayOfWeek.Wednesday, 9 * 60, 13 * 60),
            new BusinessWindow(DayOfWeek.Wednesday, 14 * 60, 18 * 60),
        ]);

        // 12:30 plus one hour: 30 minutes before lunch, 30 minutes after it reopens.
        BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 12, 30), 60, calendar)
            .ShouldBe(At(2026, 8, 19, 14, 30));
    }

    [Fact]
    public void An_instant_inside_the_lunch_break_is_outside_business_hours()
    {
        var calendar = new WorkingCalendar(Utc,
        [
            new BusinessWindow(DayOfWeek.Wednesday, 9 * 60, 13 * 60),
            new BusinessWindow(DayOfWeek.Wednesday, 14 * 60, 18 * 60),
        ]);

        BusinessHoursCalculator.IsWithinBusinessHours(At(2026, 8, 19, 13, 30), calendar).ShouldBeFalse();
        BusinessHoursCalculator.IsWithinBusinessHours(At(2026, 8, 19, 12, 30), calendar).ShouldBeTrue();
        BusinessHoursCalculator.IsWithinBusinessHours(At(2026, 8, 19, 15, 0), calendar).ShouldBeTrue();
    }

    [Fact]
    public void Business_hours_are_local_to_the_calendar_not_to_utc()
    {
        // Karachi is UTC+5 with no daylight saving, so a 09:00-17:00 local day is
        // 04:00-12:00 UTC. 05:00 UTC is inside hours; 13:00 UTC is not.
        var karachi = FindZone("Asia/Karachi", "Pakistan Standard Time");

        var calendar = new WorkingCalendar(karachi, new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday,
        }.Select(d => new BusinessWindow(d, 9 * 60, 17 * 60)));

        BusinessHoursCalculator.IsWithinBusinessHours(At(2026, 8, 19, 5), calendar).ShouldBeTrue();
        BusinessHoursCalculator.IsWithinBusinessHours(At(2026, 8, 19, 13), calendar).ShouldBeFalse();

        // 11:00 UTC is 16:00 local. Two business hours run to 17:00 local, then resume
        // at 09:00 local next day, which is 04:00 UTC.
        BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 11), 120, calendar)
            .ShouldBe(At(2026, 8, 20, 5));
    }

    [Fact]
    public void A_deadline_crossing_a_daylight_saving_change_still_produces_a_real_instant()
    {
        // London moves to summer time at 01:00 UTC on 29 March 2026, so 01:30 local does
        // not exist that night. The calculator must neither throw nor drift an hour.
        var london = FindZone("Europe/London", "GMT Standard Time");

        var calendar = new WorkingCalendar(london, Enum.GetValues<DayOfWeek>()
            .Select(d => new BusinessWindow(d, 0, 24 * 60)));

        var before = new DateTime(2026, 3, 28, 20, 0, 0, DateTimeKind.Utc);

        var result = Should.NotThrow(
            () => BusinessHoursCalculator.AddBusinessMinutes(before, 12 * 60, calendar));

        result.Kind.ShouldBe(DateTimeKind.Utc);
        result.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void Elapsed_time_excludes_evenings_and_weekends()
    {
        // Friday 16:00 to Monday 10:00 is one hour on Friday plus one on Monday.
        BusinessHoursCalculator
            .BusinessMinutesBetween(At(2026, 8, 21, 16), At(2026, 8, 24, 10), Weekday(9, 17))
            .ShouldBe(120);
    }

    [Fact]
    public void Elapsed_time_within_one_day_is_the_plain_difference()
    {
        BusinessHoursCalculator
            .BusinessMinutesBetween(At(2026, 8, 19, 10), At(2026, 8, 19, 12, 30), Weekday(9, 17))
            .ShouldBe(150);
    }

    [Fact]
    public void Elapsed_time_is_never_negative_when_the_arguments_are_reversed()
    {
        BusinessHoursCalculator
            .BusinessMinutesBetween(At(2026, 8, 19, 12), At(2026, 8, 19, 10), Weekday(9, 17))
            .ShouldBe(0);
    }

    [Fact]
    public void Adding_then_measuring_round_trips()
    {
        // The two directions must agree, or consumption percentages drift away from the
        // deadline they are meant to describe.
        var calendar = Weekday(9, 17);
        var start = At(2026, 8, 19, 14);

        foreach (var minutes in new[] { 15, 60, 240, 480, 1_200, 3_000 })
        {
            var deadline = BusinessHoursCalculator.AddBusinessMinutes(start, minutes, calendar);

            BusinessHoursCalculator.BusinessMinutesBetween(start, deadline, calendar)
                .ShouldBe(minutes, "round trip failed for " + minutes + " minutes");
        }
    }

    [Fact]
    public void A_zero_or_negative_duration_returns_the_start_unchanged()
    {
        var start = At(2026, 8, 19, 10);

        BusinessHoursCalculator.AddBusinessMinutes(start, 0, Weekday(9, 17)).ShouldBe(start);
        BusinessHoursCalculator.AddBusinessMinutes(start, -30, Weekday(9, 17)).ShouldBe(start);
    }

    [Fact]
    public void Windows_that_end_before_they_start_are_discarded()
    {
        // Nonsense configuration must not create negative working time. Discarding
        // leaves no windows, which reads as continuous cover rather than a hang.
        var calendar = new WorkingCalendar(Utc, [new BusinessWindow(DayOfWeek.Monday, 17 * 60, 9 * 60)]);

        calendar.Windows.ShouldBeEmpty();
        calendar.IsContinuous.ShouldBeTrue();

        Should.NotThrow(() => BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 10), 60, calendar));
    }

    [Fact]
    public void A_calendar_with_no_reachable_working_time_throws_instead_of_looping()
    {
        // Guards the scan bound. A clear exception beats a request that never returns.
        var everyDayIsAHoliday = Enumerable.Range(1, 12)
            .SelectMany(month => Enumerable.Range(1, 31).Select(day => (month, day)))
            .ToList();

        var calendar = new WorkingCalendar(Utc, Weekday(9, 17).Windows, recurringHolidays: everyDayIsAHoliday);

        Should.Throw<InvalidOperationException>(
            () => BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 10), 60, calendar));
    }
}
