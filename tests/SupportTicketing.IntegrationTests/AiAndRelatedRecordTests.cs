using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Phase 5: ERP record links, and the safety rules around AI.
/// </summary>
/// <remarks>
/// No provider key is configured in the test environment, which is the point of most
/// of the AI tests here: the system must behave correctly and identically when the
/// model is unavailable.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class AiAndRelatedRecordTests(ApiFactory factory)
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
            Description = "Raised by the Phase 5 suite to exercise record links and AI fallback.",
            Impact = "High",
            Urgency = "High",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    // ---------------------------------------------------------- ERP records

    [Fact]
    public async Task An_agent_can_link_a_ticket_to_a_purchase_order()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Purchase order will not post");

        var response = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/related-records",
            new RelatedRecordRequest
            {
                RecordType = "PurchaseOrder",
                RecordReference = "PO-2026-11841",
                RecordLabel = "Autumn knitwear, supplier 4471",
                SourceSystem = "ERP",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var record = (await response.Content.ReadFromJsonAsync<RelatedRecordResponse>())!;
        record.RecordType.ShouldBe("PurchaseOrder");
        record.RecordReference.ShouldBe("PO-2026-11841");

        // The link surfaces on the ticket itself, which is what the business-context
        // panel reads.
        var reloaded = await agent.GetFromJsonAsync<TicketDetailResponse>($"/api/v1/tickets/{ticket.Id}");
        reloaded!.RelatedRecords.ShouldContain(r => r.RecordReference == "PO-2026-11841");
    }

    [Fact]
    public async Task The_same_record_cannot_be_linked_twice()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Duplicate link guard");

        var body = new RelatedRecordRequest
        {
            RecordType = "Shipment",
            RecordReference = "SHP-55021",
        };

        (await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/related-records", body))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/related-records", body))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_dangerous_link_scheme_is_rejected()
    {
        // This value is rendered as a clickable link, so a javascript: URL stored here
        // would execute in the browser of the next agent who clicked it.
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Link scheme guard");

        var response = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/related-records",
            new RelatedRecordRequest
            {
                RecordType = "Invoice",
                RecordReference = "INV-9001",
                RecordUrl = "javascript:alert(document.cookie)",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tickets_can_be_found_by_the_record_they_reference()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Shipment delayed at customs");

        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/related-records",
            new RelatedRecordRequest { RecordType = "Shipment", RecordReference = "SHP-77310" });

        var found = await agent.GetFromJsonAsync<List<TicketListItemResponse>>(
            "/api/v1/tickets/by-record?recordType=Shipment&recordReference=SHP-77310");

        found!.ShouldContain(t => t.Id == ticket.Id);
    }

    [Fact]
    public async Task A_requester_cannot_link_business_records()
    {
        // Linking commercial references is a support action, not a requester one.
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Requester attempts a link");

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/related-records",
            new RelatedRecordRequest { RecordType = "PurchaseOrder", RecordReference = "PO-1" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ AI

    [Fact]
    public async Task With_no_provider_configured_the_deterministic_answer_is_returned()
    {
        // The central safety property. No API key exists in this environment, and a
        // ticket must still get a priority — from the matrix, with the reason the
        // model was silent reported honestly rather than disguised as an AI answer.
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "AI unavailable fallback");

        var response = await agent.PostAsJsonAsync(
            $"/api/v1/ai/tickets/{ticket.Id}/priority-recommendation", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<AiFallbackShape>();

        body.ShouldNotBeNull();
        body.UsedFallback.ShouldBeTrue();
        body.DeterministicValue.ShouldBe("High");
        body.SuggestedValue.ShouldBeNull();
        body.RecommendationId.ShouldBeNull();
        body.UnavailableReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unavailable_model_never_blocks_ticket_creation()
    {
        // Ticket operations must not depend on a third party being reachable.
        var requester = await SignInAsync("requester@itg.test");

        var response = await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = "Raised while AI is switched off",
            Description = "Creation must succeed regardless of AI availability.",
            Impact = "Critical",
            Urgency = "Critical",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var ticket = (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
        ticket.Priority.ShouldBe("Critical");
        ticket.PriorityDecisionSource.ShouldBe("Rule");
    }

    [Fact]
    public async Task Requesting_a_recommendation_requires_the_ai_permission()
    {
        // A requester holds no ai.use permission, so the endpoint is closed to them
        // even though the ticket is their own.
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "AI permission guard");

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/ai/tickets/{ticket.Id}/priority-recommendation", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AI_respects_ticket_scope()
    {
        // The AI endpoint must not become a way to learn about a ticket the caller
        // cannot otherwise see.
        var owner = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(owner, "AI respects scope");

        var otherTenant = await SignInAsync("admin@fab.test");

        var response = await otherTenant.PostAsJsonAsync(
            $"/api/v1/ai/tickets/{ticket.Id}/priority-recommendation", new { });

        // Forbidden or not-found are both acceptable; leaking existence is not.
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ai_status_requires_the_configure_permission_and_never_returns_the_key()
    {
        var agent = await SignInAsync("agent@itg.test");

        // An agent may use AI but not configure it.
        (await agent.GetAsync("/api/v1/ai/status")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = await SignInAsync("admin@itg.test");

        var response = await admin.GetAsync("/api/v1/ai/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var raw = await response.Content.ReadAsStringAsync();

        // The status reports whether a key exists, never the key itself.
        raw.ShouldNotContain("apiKey");
        raw.ShouldNotContain("sk-");
        raw.ShouldContain("providerConfigured");
    }

    [Fact]
    public async Task AI_is_off_until_an_administrator_turns_it_on()
    {
        // Defaulting to enabled would mean an upgrade silently starts sending ticket
        // text to a third party.
        var admin = await SignInAsync("admin@itg.test");

        var status = await admin.GetFromJsonAsync<AiStatusShape>("/api/v1/ai/status");

        status.ShouldNotBeNull();
        status.Enabled.ShouldBeFalse();
        status.AutoApplyEnabled.ShouldBeFalse();
        status.Capabilities.Values.ShouldAllBe(v => v == false);
    }

    private sealed class AiFallbackShape
    {
        public Guid? RecommendationId { get; set; }
        public string DeterministicValue { get; set; } = "";
        public string? SuggestedValue { get; set; }
        public bool UsedFallback { get; set; }
        public string? UnavailableReason { get; set; }
    }

    private sealed class AiStatusShape
    {
        public bool ProviderConfigured { get; set; }
        public bool Enabled { get; set; }
        public bool AutoApplyEnabled { get; set; }
        public Dictionary<string, bool> Capabilities { get; set; } = [];
    }
}
