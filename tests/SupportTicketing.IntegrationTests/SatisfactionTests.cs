using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The rules that make a satisfaction score mean something: only the requester, only
/// after the work is finished, and only once.
/// </summary>
[Collection(nameof(ApiCollection))]
public class SatisfactionTests(ApiFactory factory)
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
            Description = "Raised by the satisfaction suite.",
            Impact = "Medium",
            Urgency = "Medium",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    private async Task<TicketDetailResponse> RaiseAndResolveAsync(string subject)
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, subject);

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "Fixed and confirmed with the user." });

        return ticket;
    }

    [Fact]
    public async Task A_requester_can_rate_a_resolved_ticket()
    {
        var ticket = await RaiseAndResolveAsync("Rating a resolved ticket");
        var requester = await SignInAsync("requester@itg.test");

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/feedback",
            new SubmitRatingRequest
            {
                Rating = 5,
                ResolutionRating = 5,
                AgentRating = 4,
                Comment = "Sorted within the hour.",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var rating = (await response.Content.ReadFromJsonAsync<SatisfactionRatingResponse>())!;
        rating.Rating.ShouldBe(5);
        rating.AgentRating.ShouldBe(4);
        rating.RatedByName.ShouldBe("Rabia Khan");

        // Captured at submission so agent reporting survives a later reassignment.
        rating.RatedAgentName.ShouldBe("Ayesha Malik");
    }

    [Fact]
    public async Task A_ticket_cannot_be_rated_twice()
    {
        // Re-rating would let a score be lobbied upward after a disagreement.
        var ticket = await RaiseAndResolveAsync("Rating is final");
        var requester = await SignInAsync("requester@itg.test");

        var first = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/feedback", new SubmitRatingRequest { Rating = 5 });
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/feedback", new SubmitRatingRequest { Rating = 1 });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).ShouldContain("already_submitted");
    }

    [Fact]
    public async Task Only_the_requester_may_rate()
    {
        // Nobody else experienced the support, and an agent rating their own work
        // would be worse than no data at all.
        var ticket = await RaiseAndResolveAsync("Only the requester rates");
        var agent = await SignInAsync("agent@itg.test");

        var response = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/feedback", new SubmitRatingRequest { Rating = 5 });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unfinished_ticket_cannot_be_rated()
    {
        // A score given mid-flight measures impatience rather than outcome.
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Still in progress");

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/feedback", new SubmitRatingRequest { Rating = 5 });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("ticket_not_finished");
    }

    [Fact]
    public async Task A_rating_outside_one_to_five_is_rejected()
    {
        var ticket = await RaiseAndResolveAsync("Out of range rating");
        var requester = await SignInAsync("requester@itg.test");

        foreach (var invalid in new[] { 0, 6, -1 })
        {
            var response = await requester.PostAsJsonAsync(
                $"/api/v1/tickets/{ticket.Id}/feedback", new SubmitRatingRequest { Rating = invalid });

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"rating {invalid} should be refused");
        }
    }

    [Fact]
    public async Task An_unrated_ticket_reports_absence_rather_than_a_zero()
    {
        // A zeroed rating would pollute every average downstream.
        var ticket = await RaiseAndResolveAsync("No rating yet");
        var requester = await SignInAsync("requester@itg.test");

        var response = await requester.GetAsync($"/api/v1/tickets/{ticket.Id}/feedback");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_rating_is_not_visible_to_someone_who_cannot_see_the_ticket()
    {
        var ticket = await RaiseAndResolveAsync("Rating respects ticket scope");
        var requester = await SignInAsync("requester@itg.test");

        await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/feedback", new SubmitRatingRequest { Rating = 4 });

        var other = await SignInAsync("requester2@itg.test");

        (await other.GetAsync($"/api/v1/tickets/{ticket.Id}/feedback")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }
}
