using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auditing;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Analytical reports, CSV export, and the audit log viewer.
/// </summary>
/// <remarks>
/// The interesting assertions here are about what each caller is <em>not</em> shown.
/// A report is a very convenient way to leak data one aggregate at a time, and an
/// export is a report that leaves the building, so both are checked against scope
/// rather than only against shape.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class ReportsAndAuditTests(ApiFactory factory)
{
    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { Email = email, Password = ApiFactory.DemoPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    private static async Task<TicketDetailResponse> RaiseAsync(HttpClient client, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject,
            Description = "Raised by the reporting suite.",
            Impact = "High",
            Urgency = "High",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    // ------------------------------------------------------------- reports

    [Fact]
    public async Task Sla_compliance_breaks_down_by_priority_team_and_category()
    {
        var manager = await SignInAsync("manager@itg.test");

        var response = await manager.GetAsync("/api/v1/reports/sla-compliance?fromUtc=2026-01-01T00:00:00Z");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var report = (await response.Content.ReadFromJsonAsync<SlaComplianceReport>())!;

        report.Period.Scope.ShouldBe("Organization");
        report.ByPriority.ShouldNotBeNull();
        report.ByTeam.ShouldNotBeNull();
        report.ByCategory.ShouldNotBeNull();

        // Every breakdown must account for exactly the same population as the
        // overall row. A grouping that silently drops rows produces subtotals that
        // do not add up, which is the fastest way to lose trust in a report.
        report.ByPriority.Sum(r => r.Tracked).ShouldBe(report.Overall.Tracked);
        report.ByTeam.Sum(r => r.Tracked).ShouldBe(report.Overall.Tracked);
        report.ByCategory.Sum(r => r.Tracked).ShouldBe(report.Overall.Tracked);
    }

    [Fact]
    public async Task Compliance_is_null_rather_than_a_hundred_percent_when_nothing_has_settled()
    {
        var manager = await SignInAsync("manager@itg.test");

        // A window in the future contains no tickets at all, so no clock in it has
        // settled. The honest answer is "no data", not a flattering 100%.
        var response = await manager.GetAsync(
            "/api/v1/reports/sla-compliance?fromUtc=2099-01-01T00:00:00Z&toUtc=2099-01-31T00:00:00Z");

        var report = (await response.Content.ReadFromJsonAsync<SlaComplianceReport>())!;

        report.Overall.Tracked.ShouldBe(0);
        report.Overall.CompliancePercent.ShouldBeNull();
    }

    [Fact]
    public async Task A_requester_cannot_open_a_report_at_all()
    {
        var requester = await SignInAsync("requester@itg.test");

        // reports.view starts at Team Lead. Aggregates over other people's tickets
        // are management information, and the permission model says so rather than
        // relying on the navigation menu to hide the link.
        var response = await requester.GetAsync("/api/v1/reports/volume-trend");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_team_leads_figures_are_narrower_than_a_managers()
    {
        var lead = await SignInAsync("lead@itg.test");
        var manager = await SignInAsync("manager@itg.test");

        var leadReport = (await (await lead.GetAsync(
            "/api/v1/reports/volume-trend?fromUtc=2026-01-01T00:00:00Z"))
            .Content.ReadFromJsonAsync<VolumeTrendReport>())!;

        var managerReport = (await (await manager.GetAsync(
            "/api/v1/reports/volume-trend?fromUtc=2026-01-01T00:00:00Z"))
            .Content.ReadFromJsonAsync<VolumeTrendReport>())!;

        // Same endpoint, same period, different populations — decided by the token
        // rather than by anything either caller sent.
        leadReport.Period.Scope.ShouldBe("Team");
        managerReport.Period.Scope.ShouldBe("Organization");
        leadReport.Period.TicketsInScope.ShouldBeLessThanOrEqualTo(managerReport.Period.TicketsInScope);
    }

    [Fact]
    public async Task Agent_performance_is_empty_for_someone_who_cannot_see_a_team()
    {
        // An administrator holds reports.view but not ticket.view_team: they
        // configure the system rather than supervise the people using it.
        var admin = await SignInAsync("admin@itg.test");

        var response = await admin.GetAsync("/api/v1/reports/agent-performance");

        // Not a 403. The caller may open the report; there is simply nothing
        // individual in it for them. A 403 here would look like a broken screen.
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var report = (await response.Content.ReadFromJsonAsync<AgentPerformanceReport>())!;
        report.Agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_backlog_line_starts_from_the_real_opening_position()
    {
        var manager = await SignInAsync("manager@itg.test");

        var report = (await (await manager.GetAsync(
            "/api/v1/reports/volume-trend?fromUtc=2026-01-01T00:00:00Z"))
            .Content.ReadFromJsonAsync<VolumeTrendReport>())!;

        report.Days.ShouldNotBeEmpty();

        // Day one's backlog is the opening position adjusted by that day's own
        // movement — not zero, which would draw a phantom cliff on every chart.
        var first = report.Days[0];
        first.Backlog.ShouldBe(Math.Max(0, report.OpeningBacklog + first.Raised - first.Resolved));

        // Zero-filled: one point per day, no gaps.
        report.Days.Count.ShouldBe(report.Period.Days);
    }

    [Fact]
    public async Task Satisfaction_reports_the_response_rate_beside_the_average()
    {
        var manager = await SignInAsync("manager@itg.test");

        var report = (await (await manager.GetAsync(
            "/api/v1/reports/satisfaction?fromUtc=2026-01-01T00:00:00Z"))
            .Content.ReadFromJsonAsync<SatisfactionReport>())!;

        // Always five buckets, including the scores nobody gave, so the distribution
        // chart has a stable x-axis instead of shifting as opinions change.
        report.Distribution.Count.ShouldBe(5);
        report.Distribution.Select(d => d.Label).ShouldBe(["1", "2", "3", "4", "5"]);

        if (report.Responses == 0)
        {
            report.AverageRating.ShouldBeNull();
        }
        else
        {
            report.Eligible.ShouldBeGreaterThanOrEqualTo(report.Responses);
        }
    }

    // -------------------------------------------------------------- export

    [Fact]
    public async Task Exporting_returns_a_csv_file_and_records_that_it_happened()
    {
        var manager = await SignInAsync("manager@itg.test");
        var admin = await SignInAsync("admin@itg.test");

        var response = await manager.PostAsJsonAsync("/api/v1/reports/export", new ReportExportRequest
        {
            Report = "tickets",
            FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");

        var csv = await response.Content.ReadAsStringAsync();
        csv.ShouldContain("Ticket,Subject,Type,Status,Priority");

        // The download is the moment data leaves the system's access controls, so
        // it must be findable afterwards.
        var audit = (await (await admin.GetAsync("/api/v1/audit?action=Exported&entityType=Report"))
            .Content.ReadFromJsonAsync<PagedResult<AuditLogResponse>>())!;

        audit.Items.ShouldContain(entry => entry.EntityReference == "tickets");
    }

    [Fact]
    public async Task An_unknown_report_name_is_rejected_rather_than_guessed_at()
    {
        var manager = await SignInAsync("manager@itg.test");

        var response = await manager.PostAsJsonAsync("/api/v1/reports/export", new ReportExportRequest
        {
            Report = "tickets; DROP TABLE Tickets",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_requester_cannot_export()
    {
        var requester = await SignInAsync("requester@itg.test");

        var response = await requester.PostAsJsonAsync(
            "/api/v1/reports/export", new ReportExportRequest { Report = "tickets" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_subject_that_looks_like_a_formula_is_neutralised_in_the_export()
    {
        var requester = await SignInAsync("requester@itg.test");
        var manager = await SignInAsync("manager@itg.test");

        await RaiseAsync(requester, "=HYPERLINK(\"http://evil.example\",\"click\")");

        var response = await manager.PostAsJsonAsync("/api/v1/reports/export", new ReportExportRequest
        {
            Report = "tickets",
            FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var csv = await response.Content.ReadAsStringAsync();

        // Quoted and apostrophe-prefixed. Excel treats a leading '=' as a formula
        // however it is quoted, so the prefix — not the quoting — is what disarms it.
        csv.ShouldContain("\"'=HYPERLINK");
        csv.ShouldNotContain("\n=HYPERLINK");
    }

    // --------------------------------------------------------------- audit

    [Fact]
    public async Task The_audit_log_is_readable_by_an_administrator()
    {
        var admin = await SignInAsync("admin@itg.test");

        var page = (await (await admin.GetAsync("/api/v1/audit?pageSize=20"))
            .Content.ReadFromJsonAsync<PagedResult<AuditLogResponse>>())!;

        page.Items.ShouldNotBeEmpty();
        page.TotalCount.ShouldBeGreaterThan(0);

        // Newest first — this list is read as "what just happened".
        page.Items
            .Select(i => i.OccurredAtUtc)
            .ShouldBe(page.Items.Select(i => i.OccurredAtUtc).OrderByDescending(d => d));
    }

    [Fact]
    public async Task An_agent_cannot_read_the_audit_log()
    {
        var agent = await SignInAsync("agent@itg.test");

        var response = await agent.GetAsync("/api/v1/audit");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Failed_sign_ins_are_findable_in_the_log()
    {
        var anonymous = factory.CreateClient();

        await anonymous.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { Email = "agent@itg.test", Password = "definitely-not-the-password" });

        var admin = await SignInAsync("admin@itg.test");

        var page = (await (await admin.GetAsync("/api/v1/audit?failuresOnly=true&action=LoginFailed"))
            .Content.ReadFromJsonAsync<PagedResult<AuditLogResponse>>())!;

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(entry => entry.IsFailure);
    }

    [Fact]
    public async Task An_unknown_action_filter_is_rejected_rather_than_ignored()
    {
        var admin = await SignInAsync("admin@itg.test");

        // Ignoring the filter would return everything while the caller believes they
        // are looking at a narrow slice — the worst possible outcome for an audit tool.
        var response = await admin.GetAsync("/api/v1/audit?action=DefinitelyNotAnAction");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_entity_trail_reads_forwards_and_shows_recorded_field_values()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var admin = await SignInAsync("admin@itg.test");

        var ticket = await RaiseAsync(requester, "Audit trail check");

        var accept = await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        accept.EnsureSuccessStatusCode();

        var trail = (await (await admin.GetAsync($"/api/v1/audit/entities/{ticket.Id}"))
            .Content.ReadFromJsonAsync<IReadOnlyList<AuditLogResponse>>())!;

        trail.Count.ShouldBeGreaterThanOrEqualTo(2);
        trail.Select(e => e.OccurredAtUtc).ShouldBe(trail.Select(e => e.OccurredAtUtc).OrderBy(d => d));
        trail[0].Action.ShouldBe("Created");
        trail[0].EntityReference.ShouldBe(ticket.TicketNumber);

        // The stored JSON is flattened for display rather than handed to the client raw.
        trail.ShouldContain(e => e.Changes.Count > 0);
    }

    [Fact]
    public async Task Filter_options_come_from_the_rows_that_exist()
    {
        var requester = await SignInAsync("requester@itg.test");
        var admin = await SignInAsync("admin@itg.test");

        // Raised here rather than assumed: the options come from rows that exist, so
        // the test has to make one exist rather than depend on another test's leftovers.
        await RaiseAsync(requester, "Filter options check");

        var options = (await (await admin.GetAsync("/api/v1/audit/filters"))
            .Content.ReadFromJsonAsync<AuditFilterOptions>())!;

        options.TotalEntries.ShouldBeGreaterThan(0);
        options.Actions.ShouldNotBeEmpty();
        options.EntityTypes.ShouldContain("Ticket");
        options.EarliestEntryUtc.ShouldNotBeNull();

        // Offering the whole enum would advertise actions this deployment has never
        // performed and send administrators hunting for entries that cannot exist.
        options.Actions.ShouldNotContain("AttachmentDownloaded");
    }

    [Fact]
    public async Task One_tenants_audit_log_never_shows_another_tenants_activity()
    {
        var itg = await SignInAsync("admin@itg.test");
        var fabrikam = await SignInAsync("admin@fab.test");

        var itgPage = (await (await itg.GetAsync("/api/v1/audit?pageSize=100"))
            .Content.ReadFromJsonAsync<PagedResult<AuditLogResponse>>())!;

        var fabrikamPage = (await (await fabrikam.GetAsync("/api/v1/audit?pageSize=100"))
            .Content.ReadFromJsonAsync<PagedResult<AuditLogResponse>>())!;

        itgPage.Items.ShouldAllBe(entry =>
            entry.ActorEmail == null || !entry.ActorEmail.EndsWith("@fab.test"));

        fabrikamPage.Items.ShouldAllBe(entry =>
            entry.ActorEmail == null || !entry.ActorEmail.EndsWith("@itg.test"));
    }
}
