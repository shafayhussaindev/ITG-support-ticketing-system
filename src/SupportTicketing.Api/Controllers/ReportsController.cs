using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Reporting;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1/reports")]
[Produces("application/json")]
public sealed class ReportsController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Response and resolution performance against target.</summary>
    [HttpGet("sla-compliance")]
    [HasPermission(Permissions.Reports.View)]
    [SwaggerOperation(Summary = "SLA compliance", Description =
        "Broken down by priority, team and category. Only settled clocks count towards "
        + "compliance — a running clock has not yet failed, and counting it either way "
        + "would misrepresent the month. Tickets with no SLA policy are excluded rather "
        + "than treated as compliant.")]
    [ProducesResponseType<SlaComplianceReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SlaComplianceReport>> SlaCompliance(
        [FromQuery] ReportQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetSlaComplianceReportQuery(parameters), cancellationToken));

    /// <summary>Throughput and quality per agent.</summary>
    [HttpGet("staff-performance")]
    [HasPermission(Permissions.Reports.View)]
    [SwaggerOperation(Summary = "Staff performance", Description =
        "Resolved counts appear beside reopen counts, SLA breaches and satisfaction, "
        + "because volume on its own rewards closing tickets rather than fixing "
        + "problems. Callers who can see only their own queue receive the period "
        + "header and an empty table.")]
    [ProducesResponseType<StaffPerformanceReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffPerformanceReport>> AgentPerformance(
        [FromQuery] ReportQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetStaffPerformanceReportQuery(parameters), cancellationToken));

    /// <summary>Raised against resolved over time, with the resulting backlog.</summary>
    /// <summary>How often requesters ask for more severity than they may declare.</summary>
    [HttpGet("severity-claims")]
    [HasPermission(Permissions.Reports.View)]
    [SwaggerOperation(Summary = "Over-claimed severity", Description =
        "Requesters whose impact or urgency was reduced by the organization's cap, worst "
        + "rate first. A rate rather than a count, because ten over-claims in two hundred "
        + "tickets is not the same problem as four in four.")]
    [ProducesResponseType<SeverityClaimReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SeverityClaimReport>> SeverityClaims(
        [FromQuery] ReportQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetSeverityClaimReportQuery(parameters), cancellationToken));

    /// <summary>How individual requesters use the desk. Super Admin only.</summary>
    [HttpGet("customer-behaviour")]
    [HasPermission(Permissions.Reports.ViewCustomerBehaviour)]
    [SwaggerOperation(Summary = "Customer behaviour", Description =
        "Named requesters with how much they raise, how often they over-claim severity, "
        + "reopen, cancel, and how long they take to confirm a resolution — each against "
        + "the desk's own average, because there is no universal number for 'too many'. "
        + "Behind its own permission held by nobody but the Super Admin: every other "
        + "report describes the desk, and this one describes people.")]
    [ProducesResponseType<CustomerBehaviourReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CustomerBehaviourReport>> CustomerBehaviour(
        [FromQuery] ReportQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(
            new GetCustomerBehaviourReportQuery(parameters), cancellationToken));

    [HttpGet("volume-trend")]
    [HasPermission(Permissions.Reports.View)]
    [SwaggerOperation(Summary = "Volume and backlog", Description =
        "The backlog line is anchored to the real open count at the start of the "
        + "period, so it shows the queue growing even when raised and resolved are "
        + "climbing together.")]
    [ProducesResponseType<VolumeTrendReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<VolumeTrendReport>> VolumeTrend(
        [FromQuery] ReportQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetVolumeTrendReportQuery(parameters), cancellationToken));

    /// <summary>Requester satisfaction, with the response rate that qualifies it.</summary>
    [HttpGet("satisfaction")]
    [HasPermission(Permissions.Reports.View)]
    [SwaggerOperation(Summary = "Satisfaction", Description =
        "The response rate is returned alongside the average because the two are only "
        + "meaningful together.")]
    [ProducesResponseType<SatisfactionReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SatisfactionReport>> Satisfaction(
        [FromQuery] ReportQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetSatisfactionReportQuery(parameters), cancellationToken));

    /// <summary>Downloads a report as CSV.</summary>
    [HttpPost("export")]
    [HasPermission(Permissions.Reports.Export)]
    [Produces("text/csv")]
    [SwaggerOperation(Summary = "Export a report as CSV", Description =
        "The report name selects one of four handlers and never reaches a table or "
        + "column name. The export obeys the caller's data scope exactly as the "
        + "on-screen report does, and the download itself is written to the audit log.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(
        [FromBody] ReportExportRequest request, CancellationToken cancellationToken)
    {
        var file = await dispatcher.SendAsync(new ExportReportCommand(request), cancellationToken);

        return File(file.Content, file.ContentType, file.FileName);
    }
}
