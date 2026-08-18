using SupportTicketing.Domain.Common;

namespace SupportTicketing.Domain.Sla;

/// <summary>
/// A working-hours definition: which hours count as business time, which days are
/// holidays, and which time zone those are expressed in.
/// </summary>
/// <remarks>
/// SLA durations are measured in business time, not wall-clock time. A four-hour
/// resolution target raised at 16:00 on a Friday is not due at 20:00 that evening —
/// it is due mid-morning on Monday. Getting that wrong makes every out-of-hours
/// ticket breach the moment it is raised.
/// </remarks>
public class BusinessCalendar : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }

    /// <summary>IANA identifier, for example Asia/Karachi. Business hours are local to this zone.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Used when an office or SLA policy names no calendar of its own.</summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<BusinessHour> Hours { get; set; } = [];
    public ICollection<Holiday> Holidays { get; set; } = [];
}

/// <summary>One working window on one weekday. A split shift is two rows for the same day.</summary>
public class BusinessHour : TenantEntity
{
    public Guid CalendarId { get; set; }
    public BusinessCalendar? Calendar { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>Local start time, stored as minutes past midnight so comparisons stay trivial in SQL.</summary>
    public int StartMinute { get; set; }

    /// <summary>Local end time as minutes past midnight. Must be greater than StartMinute.</summary>
    public int EndMinute { get; set; }
}

/// <summary>A non-working day. Recurring holidays repeat on the same month and day each year.</summary>
public class Holiday : TenantEntity
{
    public Guid CalendarId { get; set; }
    public BusinessCalendar? Calendar { get; set; }

    public required string Name { get; set; }

    /// <summary>The local date that is not worked. For a recurring holiday only month and day matter.</summary>
    public DateTime DateUtc { get; set; }

    /// <summary>True for fixed-date holidays such as 1 January that repeat every year.</summary>
    public bool IsRecurring { get; set; }
}
