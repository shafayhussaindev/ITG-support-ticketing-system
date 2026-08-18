using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

[Collection(nameof(ApiCollection))]
public class ReportingAndKnowledgeTests(ApiFactory factory)
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
            Description = "Raised by the reporting suite to give the dashboard something to count.",
            Impact = "Medium",
            Urgency = "High",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    // ------------------------------------------------------------- dashboard

    [Fact]
    public async Task The_dashboard_reports_the_scope_it_used()
    {
        var agent = await SignInAsync("agent@itg.test");

        var dashboard = await agent.GetFromJsonAsync<DashboardResponse>("/api/v1/dashboard");

        dashboard.ShouldNotBeNull();
        dashboard.Scope.ShouldBe("Team");
        dashboard.VolumeByDay.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Quiet_days_appear_as_zero_rather_than_being_dropped()
    {
        // A line chart that silently skips empty days compresses its axis and makes a
        // weekend lull look like a collapse in volume.
        var agent = await SignInAsync("agent@itg.test");

        var dashboard = await agent.GetFromJsonAsync<DashboardResponse>("/api/v1/dashboard?days=30");

        dashboard!.VolumeByDay.Count.ShouldBeGreaterThanOrEqualTo(30);

        var dates = dashboard.VolumeByDay.Select(p => p.Date.Date).ToList();
        dates.ShouldBe(dates.OrderBy(d => d).ToList(), "days must be contiguous and ordered");
    }

    [Fact]
    public async Task A_requester_sees_only_their_own_tickets_in_the_dashboard()
    {
        var owner = await SignInAsync("requester@itg.test");
        await RaiseAsync(owner, "Counted for its owner only");

        var other = await SignInAsync("requester2@itg.test");

        var ownerView = await owner.GetFromJsonAsync<DashboardResponse>("/api/v1/dashboard");
        var otherView = await other.GetFromJsonAsync<DashboardResponse>("/api/v1/dashboard");

        ownerView!.Scope.ShouldBe("Own");
        otherView!.Scope.ShouldBe("Own");

        // Same endpoint, different numbers, because the scope filter differs.
        ownerView.Kpis.TotalOpen.ShouldBeGreaterThan(0);
        otherView.Kpis.TotalOpen.ShouldBeLessThan(ownerView.Kpis.TotalOpen);
    }

    [Fact]
    public async Task Chart_segments_carry_a_drill_down_query()
    {
        var requester = await SignInAsync("requester@itg.test");
        await RaiseAsync(requester, "Drill-down fixture");

        var dashboard = await requester.GetFromJsonAsync<DashboardResponse>("/api/v1/dashboard");

        var segment = dashboard!.ByStatus.FirstOrDefault();
        segment.ShouldNotBeNull();

        // The drill-down reuses the ticket list's own filter contract, so a chart click
        // lands on exactly the rows the segment counted.
        segment.DrillDownQuery.ShouldNotBeNullOrWhiteSpace();
        segment.DrillDownQuery.ShouldContain("openOnly=true");
    }

    // --------------------------------------------------------- knowledge base

    [Fact]
    public async Task An_internal_article_never_reaches_a_requester()
    {
        // The knowledge-base equivalent of the internal-note rule. Internal articles
        // name individuals and out-of-hours expectations.
        var requester = await SignInAsync("requester@itg.test");

        var response = await requester.GetAsync("/api/v1/knowledge/articles?pageSize=50");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();

        // Asserted against the bytes on the wire, not the parsed objects.
        raw.ShouldNotContain("payroll cut-off");
        raw.ShouldNotContain("Internal");
    }

    [Fact]
    public async Task An_agent_can_see_internal_articles_and_drafts()
    {
        var agent = await SignInAsync("agent@itg.test");

        var page = await agent.GetFromJsonAsync<PagedTicketsOfArticles>("/api/v1/knowledge/articles?pageSize=50");

        page!.Items.ShouldContain(a => a.Visibility == "Internal");
        page.Items.ShouldContain(a => a.Status == "Draft");
    }

    [Fact]
    public async Task A_requester_is_not_offered_drafts()
    {
        // Half-written instructions are worse than none.
        var requester = await SignInAsync("requester@itg.test");

        var page = await requester.GetFromJsonAsync<PagedTicketsOfArticles>("/api/v1/knowledge/articles?pageSize=50");

        page!.Items.ShouldAllBe(a => a.Status == "Published");
    }

    [Fact]
    public async Task Suggestions_match_the_words_a_requester_would_type()
    {
        var requester = await SignInAsync("requester@itg.test");

        var suggestions = await requester.GetFromJsonAsync<List<ArticleListItemResponse>>(
            "/api/v1/knowledge/suggestions?text=shared printer offline after power cut");

        suggestions.ShouldNotBeEmpty();
        suggestions!.ShouldContain(a => a.Title.Contains("Printer"));
        suggestions.ShouldAllBe(a => a.Status == "Published");
    }

    [Fact]
    public async Task Suggestions_ignore_short_words_that_match_everything()
    {
        var requester = await SignInAsync("requester@itg.test");

        var suggestions = await requester.GetFromJsonAsync<List<ArticleListItemResponse>>(
            "/api/v1/knowledge/suggestions?text=the a of is to");

        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_article_starts_as_a_draft_regardless_of_who_writes_it()
    {
        // Publishing is a separate, separately permitted act, so nothing reaches
        // readers just because somebody pressed save.
        var agent = await SignInAsync("agent@itg.test");

        var response = await agent.PostAsJsonAsync("/api/v1/knowledge/articles", new CreateArticleRequest
        {
            Title = "How to clear the shipment import queue",
            Summary = "Steps for a stuck overnight shipment import.",
            Content = "Detailed steps for clearing the queue and re-running the import.",
            Visibility = "Organization",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var article = (await response.Content.ReadFromJsonAsync<ArticleDetailResponse>())!;
        article.Status.ShouldBe("Draft");
        article.CurrentVersion.ShouldBe(1);
        article.Slug.ShouldBe("how-to-clear-the-shipment-import-queue");
    }

    [Fact]
    public async Task An_agent_cannot_publish_but_a_lead_can()
    {
        var agent = await SignInAsync("agent@itg.test");

        var created = await agent.PostAsJsonAsync("/api/v1/knowledge/articles", new CreateArticleRequest
        {
            Title = "Draft awaiting publication approval",
            Summary = "Written by an agent, published by a lead.",
            Content = "Body text long enough to be a real article.",
        });

        var article = (await created.Content.ReadFromJsonAsync<ArticleDetailResponse>())!;

        var refused = await agent.PostAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}/status",
            new ChangeArticleStatusRequest { Status = "Published" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var lead = await SignInAsync("lead@itg.test");

        var allowed = await lead.PostAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}/status",
            new ChangeArticleStatusRequest { Status = "Published" });

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());

        var published = (await allowed.Content.ReadFromJsonAsync<ArticleDetailResponse>())!;
        published.Status.ShouldBe("Published");
        published.PublishedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Editing_an_article_snapshots_the_previous_wording()
    {
        var lead = await SignInAsync("lead@itg.test");

        var created = await lead.PostAsJsonAsync("/api/v1/knowledge/articles", new CreateArticleRequest
        {
            Title = "Versioned article",
            Summary = "First wording.",
            Content = "The original body.",
        });

        var article = (await created.Content.ReadFromJsonAsync<ArticleDetailResponse>())!;

        await lead.PutAsJsonAsync($"/api/v1/knowledge/articles/{article.Id}", new UpdateArticleRequest
        {
            Title = "Versioned article",
            Summary = "Second wording.",
            Content = "The revised body.",
            ChangeNote = "Clarified step three.",
        });

        var versions = await lead.GetFromJsonAsync<List<ArticleVersionResponse>>(
            $"/api/v1/knowledge/articles/{article.Id}/versions");

        // People act on what an article said when they read it, so the earlier text
        // has to stay recoverable after a rewrite.
        versions!.Count.ShouldBe(2);
        versions.ShouldContain(v => v.Version == 1);
        versions.ShouldContain(v => v.Version == 2 && v.ChangeNote == "Clarified step three.");
    }

    [Fact]
    public async Task Voting_twice_does_not_inflate_the_helpful_count()
    {
        var requester = await SignInAsync("requester@itg.test");

        var page = await requester.GetFromJsonAsync<PagedTicketsOfArticles>("/api/v1/knowledge/articles?pageSize=5");
        var article = page!.Items.First();

        var before = article.HelpfulCount;

        for (var i = 0; i < 3; i++)
        {
            await requester.PostAsJsonAsync(
                $"/api/v1/knowledge/articles/{article.Id}/feedback",
                new ArticleFeedbackRequest { WasHelpful = true });
        }

        var after = await requester.GetFromJsonAsync<ArticleDetailResponse>(
            $"/api/v1/knowledge/articles/{article.Id}");

        after!.HelpfulCount.ShouldBe(before + 1, "one reader is one vote no matter how often they click");
        after.MyVoteWasHelpful.ShouldBe(true);
    }

    private sealed class PagedTicketsOfArticles
    {
        public List<ArticleListItemResponse> Items { get; set; } = [];
        public int TotalCount { get; set; }
    }
}
