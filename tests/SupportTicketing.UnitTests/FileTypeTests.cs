using SupportTicketing.Application.Features.Attachments;

namespace SupportTicketing.UnitTests;

/// <summary>
/// Content-type detection, which is the part of file handling that decides whether
/// an upload can become a cross-site scripting vector.
/// </summary>
public class FileTypeTests
{
    private static byte[] WithHeader(params byte[] magic) =>
        [.. magic, .. new byte[FileTypes.SignatureLength]];

    [Theory]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData("video/webm", new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })]
    [InlineData("application/zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    public void Recognises_a_format_from_its_signature(string expected, byte[] magic)
    {
        FileTypes.Detect(WithHeader(magic)).ShouldBe(expected);
    }

    [Fact]
    public void Recognises_mp4_by_the_ftyp_box_at_offset_four()
    {
        // The first four bytes are the box length, so the brand starts at 4. A
        // detector that only looks at offset zero misses every MP4 ever made.
        byte[] header = [0x00, 0x00, 0x00, 0x20, .. "ftypisom"u8.ToArray(), .. new byte[16]];

        FileTypes.Detect(header).ShouldBe("video/mp4");
    }

    [Fact]
    public void Falls_back_to_octet_stream_for_something_it_cannot_place()
    {
        // A log file or a CSV has no signature. It is accepted, just never rendered.
        FileTypes.Detect("2026-08-19 ERROR Could not connect"u8).ShouldBe("application/octet-stream");
    }

    [Fact]
    public void Refuses_to_render_html_dressed_as_an_image()
    {
        // The attack this whole mechanism exists for: a file named screenshot.png,
        // declared image/png, containing markup. Served inline from a trusted origin
        // it runs as the person viewing the ticket.
        var detected = FileTypes.Detect("<html><script>alert(1)</script>"u8);

        detected.ShouldBe("application/octet-stream");
        FileTypes.CanRenderInline(detected).ShouldBeFalse();
        FileTypes.ResponseContentType(detected).ShouldBe("application/octet-stream");
    }

    [Fact]
    public void Never_renders_svg_inline()
    {
        // SVG is a document that can carry script, so it is not on the inline list at
        // all — there is no signature for it here and it lands as octet-stream, which
        // is exactly the treatment wanted.
        var detected = FileTypes.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\">"u8);

        FileTypes.CanRenderInline(detected).ShouldBeFalse();
    }

    [Fact]
    public void Renders_only_raster_images_and_video_inline()
    {
        FileTypes.CanRenderInline("image/png").ShouldBeTrue();
        FileTypes.CanRenderInline("video/mp4").ShouldBeTrue();

        // A PDF viewer is an execution surface with a long history. Accepted as an
        // upload, handed over as a download.
        FileTypes.CanRenderInline("application/pdf").ShouldBeFalse();
        FileTypes.CanRenderInline("application/zip").ShouldBeFalse();
    }

    [Fact]
    public void Handles_a_file_shorter_than_the_signature_window()
    {
        // A two-byte upload must not index past the end of the buffer.
        Should.NotThrow(() => FileTypes.Detect(new byte[] { 0x50, 0x4B }));
    }

    // ------------------------------------------------------------ file names

    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\config", "config")]
    [InlineData("C:\\Users\\me\\shot.png", "shot.png")]
    public void Strips_any_path_from_a_supplied_name(string supplied, string expected)
    {
        // The name never reaches the filesystem — storage generates its own — but it
        // does reach a header and a page, and traversal segments have no business in
        // either.
        FileTypes.SanitiseFileName(supplied).ShouldBe(expected);
    }

    [Fact]
    public void Removes_characters_that_would_break_out_of_a_header()
    {
        // A quote or a semicolon in Content-Disposition lets the uploader append
        // header directives of their own.
        var cleaned = FileTypes.SanitiseFileName("re\"port;name.png");

        cleaned.ShouldNotContain("\"");
        cleaned.ShouldNotContain(";");
    }

    [Fact]
    public void Removes_control_characters()
    {
        FileTypes.SanitiseFileName("shot\r\nX-Injected: yes.png")
            .ShouldNotContain("\n");
    }

    [Fact]
    public void Falls_back_to_a_name_when_nothing_usable_survives()
    {
        FileTypes.SanitiseFileName("").ShouldBe("attachment");
        FileTypes.SanitiseFileName("   ").ShouldBe("attachment");
        FileTypes.SanitiseFileName("///").ShouldBe("attachment");
    }

    [Fact]
    public void Caps_an_absurdly_long_name()
    {
        FileTypes.SanitiseFileName(new string('a', 500) + ".png").Length.ShouldBeLessThanOrEqualTo(180);
    }
}
