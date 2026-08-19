using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// File sharing on a ticket, end to end.
/// </summary>
/// <remarks>
/// Most of these are about refusal. An attachment endpoint is the place a support
/// system most easily becomes a way to serve hostile content from a trusted origin,
/// or to read a ticket somebody was not entitled to see.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class AttachmentTests(ApiFactory factory)
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        .. new byte[64],
    ];

    private static readonly byte[] Mp4Bytes =
    [
        0x00, 0x00, 0x00, 0x20, .. "ftypisom"u8.ToArray(), .. new byte[128],
    ];

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
            Description = "Raised by the attachment suite.",
            Impact = "Medium",
            Urgency = "Medium",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    private static MultipartFormDataContent FileForm(
        byte[] bytes, string fileName, string declaredType, bool internalOnly = false)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(declaredType);

        var form = new MultipartFormDataContent { { content, "file", fileName } };

        if (internalOnly)
        {
            form.Add(new StringContent("true"), "isInternalOnly");
        }

        return form;
    }

    private static async Task<AttachmentResponse> UploadAsync(
        HttpClient client, Guid ticketId, byte[] bytes, string name, string type, bool internalOnly = false)
    {
        var response = await client.PostAsync(
            $"/api/v1/tickets/{ticketId}/attachments", FileForm(bytes, name, type, internalOnly));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AttachmentResponse>())!;
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task A_requester_can_attach_a_screenshot_and_get_it_back()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Printer jam, photo attached");

        var uploaded = await UploadAsync(requester, ticket.Id, PngBytes, "jam.png", "image/png");

        uploaded.FileName.ShouldBe("jam.png");
        uploaded.ContentType.ShouldBe("image/png");
        uploaded.SizeBytes.ShouldBe(PngBytes.Length);

        var download = await requester.GetAsync(
            $"/api/v1/tickets/{ticket.Id}/attachments/{uploaded.Id}");

        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(PngBytes);

        // An image may be shown in place; that is the whole reason for sniffing.
        download.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
    }

    [Fact]
    public async Task An_agent_can_attach_a_screen_recording()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Cannot reproduce, recording attached");
        var uploaded = await UploadAsync(agent, ticket.Id, Mp4Bytes, "repro.mp4", "video/mp4");

        uploaded.ContentType.ShouldBe("video/mp4");

        var download = await agent.GetAsync($"/api/v1/tickets/{ticket.Id}/attachments/{uploaded.Id}");
        download.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
    }

    // --------------------------------------------------------------- refusals

    [Fact]
    public async Task Html_disguised_as_an_image_is_never_served_as_one()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Disguised upload");

        var markup = "<html><script>alert(document.cookie)</script></html>"u8.ToArray();
        var uploaded = await UploadAsync(requester, ticket.Id, markup, "screenshot.png", "image/png");

        // The name and the declared type both say PNG. The bytes do not, and the
        // bytes are what count.
        uploaded.ContentType.ShouldBe("application/octet-stream");

        var download = await requester.GetAsync($"/api/v1/tickets/{ticket.Id}/attachments/{uploaded.Id}");

        // Served as a download with a neutral type, so no browser is invited to
        // execute it in this application's origin.
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        download.Headers.TryGetValues("X-Content-Type-Options", out var nosniff).ShouldBeTrue();
        nosniff!.ShouldContain("nosniff");
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Empty upload");

        var response = await requester.PostAsync(
            $"/api/v1/tickets/{ticket.Id}/attachments", FileForm([], "nothing.png", "image/png"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_ticket_outside_the_callers_scope_is_not_found_rather_than_forbidden()
    {
        var requester = await SignInAsync("requester@itg.test");
        var other = await SignInAsync("requester2@itg.test");

        var ticket = await RaiseAsync(requester, "Private to its requester");

        // 404, not 403: a 403 would confirm the identifier names a real ticket, which
        // is all somebody enumerating needs.
        var response = await other.PostAsync(
            $"/api/v1/tickets/{ticket.Id}/attachments", FileForm(PngBytes, "x.png", "image/png"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_requester_cannot_see_an_internal_attachment()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Internal file check");

        var hidden = await UploadAsync(
            agent, ticket.Id, PngBytes, "internal-notes.png", "image/png", internalOnly: true);

        hidden.IsInternalOnly.ShouldBeTrue();

        // Absent from the list entirely — filtered at the database, not hidden by the
        // interface.
        var listed = await requester.GetFromJsonAsync<IReadOnlyList<AttachmentResponse>>(
            $"/api/v1/tickets/{ticket.Id}/attachments");

        listed!.ShouldNotContain(a => a.Id == hidden.Id);

        // And not fetchable by guessing the identifier either.
        var download = await requester.GetAsync($"/api/v1/tickets/{ticket.Id}/attachments/{hidden.Id}");
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The agent who uploaded it still sees it.
        var agentView = await agent.GetFromJsonAsync<IReadOnlyList<AttachmentResponse>>(
            $"/api/v1/tickets/{ticket.Id}/attachments");

        agentView!.ShouldContain(a => a.Id == hidden.Id);
    }

    [Fact]
    public async Task A_path_traversal_filename_cannot_escape_the_stored_name()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Traversal attempt");

        var uploaded = await UploadAsync(
            requester, ticket.Id, PngBytes, "../../../etc/passwd.png", "image/png");

        // The name is displayed, never used to build a path — and it arrives stripped.
        uploaded.FileName.ShouldBe("passwd.png");
        uploaded.FileName.ShouldNotContain("..");
    }

    // ---------------------------------------------------------------- removal

    [Fact]
    public async Task An_attachment_can_be_removed_and_stops_downloading()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Attachment to remove");
        var uploaded = await UploadAsync(requester, ticket.Id, PngBytes, "remove-me.png", "image/png");

        var deleted = await lead.DeleteAsync($"/api/v1/tickets/{ticket.Id}/attachments/{uploaded.Id}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var download = await requester.GetAsync($"/api/v1/tickets/{ticket.Id}/attachments/{uploaded.Id}");
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_requester_cannot_delete_an_attachment()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await RaiseAsync(requester, "Deletion permission check");
        var uploaded = await UploadAsync(requester, ticket.Id, PngBytes, "mine.png", "image/png");

        // Uploading is not deleting. attachment.delete starts at Team Lead.
        var response = await requester.DeleteAsync(
            $"/api/v1/tickets/{ticket.Id}/attachments/{uploaded.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_upload_is_recorded_in_the_audit_log()
    {
        var requester = await SignInAsync("requester@itg.test");
        var admin = await SignInAsync("admin@itg.test");

        var ticket = await RaiseAsync(requester, "Audited upload");
        await UploadAsync(requester, ticket.Id, PngBytes, "audited.png", "image/png");

        var audit = await admin.GetFromJsonAsync<Application.Abstractions.PagedResult<
            Contracts.Auditing.AuditLogResponse>>(
            "/api/v1/audit?action=AttachmentUploaded");

        audit!.Items.ShouldContain(entry => entry.EntityReference == ticket.TicketNumber);
    }
}
