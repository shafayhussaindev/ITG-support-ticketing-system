using SupportTicketing.Domain.Sla;

namespace SupportTicketing.UnitTests.Sla;

public class BusinessHoursCalculatorTests
{
    // A fixed zone keeps these tests independent of the machine they run on.
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static WorkingCalendar NineToFive(params DayOfWeek[] days)
    {
        var working = days.Length > 0
            ? days
            : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

        return new WorkingCalendar(Utc, working.Select(d => new BusinessWindow(d, 9 * 60, 17 * 60)));
    }

    private static DateTime At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ within a day

    [Fact]
    public void Time_inside_the_working_day_advances_normally()
    {
        // Wednesday 10:00 plus two hours is Wednesday 12:00.
        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 10), 120, NineToFive());

        result.ShouldBe(At(2026, 8, 19, 12));
    }

    [Fact]
    public void A_deadline_landing_exactly_on_the_close_stays_on_the_same_day()
    {
        // 15:00 plus two hours is exactly 17:00, the close. It should not roll over.
        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 15), 120, NineToFive());

        result.ShouldBe(At(2026, 8, 19, 17));
    }

    // ------------------------------------------------------------ overnight

    [Fact]
    public void Work_remaining_at_close_resumes_the_next_morning()
    {
        // Wednesday 16:00 plus four hours: one hour today, three tomorrow from 09:00.
        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 16), 240, NineToFive());

        result.ShouldBe(At(2026, 8, 20, 12));
    }

    [Fact]
    public void A_ticket_raised_before_opening_starts_counting_at_opening()
    {
        // 06:00 is outside hours; the clock starts at 09:00, so four hours is 13:00.
        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 6), 240, NineToFive());

        result.ShouldBe(At(2026, 8, 19, 13));
    }

    [Fact]
    public void A_ticket_raised_after_closing_starts_the_next_morning()
    {
        // 22:00 Wednesday: nothing counts tonight, so two hours lands 11:00 Thursday.
        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 22), 120, NineToFive());

        result.ShouldBe(At(2026, 8, 20, 11));
    }

    // ------------------------------------------------------------ weekends

    [Fact]
    public void A_friday_afternoon_ticket_is_due_on_monday()
    {
        // The canonical failure this class exists to prevent. Friday 16:00 plus four
        // business hours is 12:00 on Monday: one hour before Friday close, then three
        // from Monday opening. It is emphatically not 20:00 on Friday night.
        var friday = At(2026, 8, 21, 16);

        var result = BusinessHoursCalculator.AddBusinessMinutes(friday, 240, NineToFive());

        result.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        result.ShouldBe(At(2026, 8, 24, 12));
    }

    [Fact]
    public void The_weekend_contributes_no_business_time()
    {
        // Saturday 10:00 plus one hour: the whole weekend is skipped, so 10:00 Monday.
        var saturday = At(2026, 8, 22, 10);

        var result = BusinessHoursCalculator.AddBusinessMinutes(saturday, 60, NineToFive());

        result.ShouldBe(At(2026, 8, 24, 10));
    }

    // ------------------------------------------------------------ holidays

    [Fact]
    public void A_holiday_is_skipped_entirely()
    {
        // Thursday 20 August is a holiday, so Wednesday 16:00 plus four hours skips to Friday.
        var calendar = new WorkingCalendar(
            Utc,
            NineToFive().Windows,
            holidays: [new DateOnly(2026, 8, 20)]);

        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 16), 240, calendar);

        result.ShouldBe(At(2026, 8, 21, 12));
    }

    [Fact]
    public void A_recurring_holiday_applies_in_every_year()
    {
        var calendar = new WorkingCalendar(
            Utc,
            NineToFive().Windows,
            recurringHolidays: [(1, 1)]);

        calendar.IsHoliday(new DateOnly(2026, 1, 1)).ShouldBeTrue();
        calendar.IsHoliday(new DateOnly(2031, 1, 1)).ShouldBeTrue();
        calendar.IsHoliday(new DateOnly(2026, 1, 2)).ShouldBeFalse();
    }

    [Fact]
    public void Consecutive_holidays_are_all_skipped()
    {
        // A four-day shutdown must not silently consume the SLA budget.
        var calendar = new WorkingCalendar(
            Utc,
            NineToFive().Windows,
            holidays:
            [
                new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 21),
                new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25),
            ]);

        var result = BusinessHoursCalculator.AddBusinessMinutes(At(2026, 8, 19, 16), 240, calendar);

        result.ShouldBe(At(2026, 8, 26, 12));
    }
}
