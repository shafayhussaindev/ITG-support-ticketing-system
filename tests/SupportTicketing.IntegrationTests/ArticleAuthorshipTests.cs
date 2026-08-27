using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Knowledge;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Being able to finish what you started.
/// </summary>
/// <remarks>
/// Staff hold <c>knowledge.create</c> but not <c>knowledge.edit</c>, so somebody who
/// could write an article could not then fix a typo in it or move it to review. Drafts
/// stalled on the first mistake. An author may now always change their own unpublished
/// work; everybody else's, and anything already published, still needs the edit
/// permission.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class ArticleAuthorshipTests(ApiFactory factory)
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

    private static async Task<ArticleDetailResponse> DraftAsync(HttpClient author, string title)
    {
        var created = await author.PostAsJsonAsync("/api/v1/knowledge/articles", new CreateArticleRequest
        {
            Title = title,
            Summary = "Written by the authorship tests.",
            Content = "The original text, which the author should be able to correct.",
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ArticleDetailResponse>())!;
    }

    private static UpdateArticleRequest Rewrite(string title) => new()
    {
        Title = title,
        Summary = "Corrected by the author.",
        Content = "The corrected text.",
    };

    [Fact]
    public async Task An_author_can_correct_their_own_draft()
    {
        var staff = await SignInAsync("agent@itg.test");

        var article = await DraftAsync(staff, "Label printer drops offline mid-shift");

        // The exact thing that was impossible: the person who wrote it fixing it.
        var edited = await staff.PutAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}", Rewrite("Label printer drops offline mid-shift (corrected)"));

        edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());

        var reloaded = (await staff.GetFromJsonAsync<ArticleDetailResponse>(
            $"/api/v1/knowledge/articles/{article.Id}"))!;

        reloaded.Title.ShouldBe("Label printer drops offline mid-shift (corrected)");
    }

    [Fact]
    public async Task An_author_can_send_their_own_draft_for_review()
    {
        var staff = await SignInAsync("agent@itg.test");

        var article = await DraftAsync(staff, "Cutting floor scanner will not pair");

        var moved = await staff.PostAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}/status",
            new ChangeArticleStatusRequest { Status = "InReview" });

        moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Somebody_elses_draft_still_needs_the_edit_permission()
    {
        var specialist = await SignInAsync("specialist@itg.test");
        var staff = await SignInAsync("agent@itg.test");

        // Written by the specialist, who does hold knowledge.edit.
        var article = await DraftAsync(specialist, "Dye batch reconciliation, step by step");

        // Staff do not, and it is not theirs, so the relaxation must not apply.
        var attempt = await staff.PutAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}", Rewrite("Rewritten by somebody else"));

        attempt.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_published_article_still_needs_the_edit_permission_even_from_its_author()
    {
        var staff = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var article = await DraftAsync(staff, "Shift handover checklist");

        var published = await lead.PostAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}/status",
            new ChangeArticleStatusRequest { Status = "Published" });

        published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());

        // Once it is in front of readers it is no longer a private draft, so quietly
        // rewriting it is a different act from finishing it.
        var attempt = await staff.PutAsJsonAsync(
            $"/api/v1/knowledge/articles/{article.Id}", Rewrite("Quietly rewritten after publication"));

        attempt.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
