using System.ComponentModel.DataAnnotations;

namespace SupportTicketing.Workers;

public sealed class SlaMonitorOptions
{
    public const string SectionName = "SlaMonitor";

    /// <summary>Disables the sweep entirely, for environments that should not act on data.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to sweep. A minute is fine: the work is a single indexed query and
    /// every action it takes is idempotent, so a missed or repeated tick is harmless.
    /// </summary>
    [Range(10, 3600)]
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum SLA instances examined per pass. Bounds the transaction and stops one
    /// enormous backlog from starving the rest of the system.
    /// </summary>
    [Range(10, 5000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>Delay before the first sweep, giving the host time to finish starting.</summary>
    [Range(0, 600)]
    public int StartupDelaySeconds { get; set; } = 15;
}
