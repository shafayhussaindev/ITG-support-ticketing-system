namespace SupportTicketing.Application.Features.Attachments;

/// <summary>
/// Decides what an uploaded file actually is, and what may be done with it.
/// </summary>
/// <remarks>
/// <para>
/// The content type the browser sends is a claim by the client, and the file
/// extension is a claim by whoever named the file. Neither is evidence. A file called
/// <c>screenshot.png</c> declared as <c>image/png</c> can contain anything at all, and
/// deciding to render it inline on that basis is how a support system becomes a way to
/// serve HTML from a trusted origin to whoever opens the ticket.
/// </para>
/// <para>
/// So the first bytes are read and matched against known signatures, and the answer to
/// "what is this" comes from the file itself. The declared type is kept only so the two
/// can be compared afterwards, because a mismatch is worth being able to find.
/// </para>
/// </remarks>
public static class FileTypes
{
    /// <summary>How many bytes need reading to identify anything here.</summary>
    public const int SignatureLength = 32;

    private sealed record Signature(string ContentType, byte[] Magic, int Offset = 0);

    /// <summary>
    /// Ordered longest-first, so a specific signature is preferred over a prefix it
    /// happens to share with a more general one.
    /// </summary>
    private static readonly Signature[] Signatures =
    [
        // ---- images ----
        new("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        new("image/jpeg", [0xFF, 0xD8, 0xFF]),
        new("image/gif", "GIF89a"u8.ToArray()),
        new("image/gif", "GIF87a"u8.ToArray()),
        new("image/webp", "WEBP"u8.ToArray(), Offset: 8),
        new("image/bmp", "BM"u8.ToArray()),

        // ---- video ----
        // The ftyp box sits at offset 4 in an MP4 or QuickTime container; the brand
        // that follows distinguishes them, and for playback purposes it need not.
        new("video/mp4", "ftyp"u8.ToArray(), Offset: 4),
        new("video/webm", [0x1A, 0x45, 0xDF, 0xA3]),
        new("video/x-msvideo", "AVI "u8.ToArray(), Offset: 8),

        // ---- documents ----
        new("application/pdf", "%PDF-"u8.ToArray()),

        // Office formats and ordinary archives are both Zip containers, so the
        // signature can only say "Zip". Which of them it is does not change how it is
        // handled — everything here is served as a download.
        new("application/zip", [0x50, 0x4B, 0x03, 0x04]),
    ];

    /// <summary>
    /// Types safe to render in the browser rather than force to a download.
    /// </summary>
    /// <remarks>
    /// Raster images and video only. Deliberately excludes SVG, which is a document
    /// that can carry script, and PDF, whose viewers have a long history of being an
    /// execution surface. Both are still accepted as uploads; they are just handed
    /// over as files rather than rendered in place.
    /// </remarks>
    private static readonly HashSet<string> InlineSafe = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
        "video/mp4", "video/webm",
    };

    /// <summary>
    /// Identifies a file from its leading bytes.
    /// </summary>
    /// <returns>
    /// The detected type, or <c>application/octet-stream</c> when nothing matches.
    /// An unrecognised file is still accepted — a colleague's log file or CSV has no
    /// signature — it is simply never rendered, only downloaded.
    /// </returns>
    public static string Detect(ReadOnlySpan<byte> head)
    {
        foreach (var signature in Signatures.OrderByDescending(s => s.Magic.Length))
        {
            var end = signature.Offset + signature.Magic.Length;

            if (head.Length < end)
            {
                continue;
            }

            if (head.Slice(signature.Offset, signature.Magic.Length).SequenceEqual(signature.Magic))
            {
                return signature.ContentType;
            }
        }

        return "application/octet-stream";
    }

    public static bool IsImage(string contentType) =>
        contentType.StartsWith("image/", StringComparison.Ordinal);

    public static bool IsVideo(string contentType) =>
        contentType.StartsWith("video/", StringComparison.Ordinal);

    /// <summary>Whether the browser may display this rather than download it.</summary>
    public static bool CanRenderInline(string contentType) => InlineSafe.Contains(contentType);

    /// <summary>
    /// The type to put on the response.
    /// </summary>
    /// <remarks>
    /// Anything not on the inline list is served as <c>application/octet-stream</c>
    /// regardless of what it really is. Sending the true type invites the browser to
    /// find a handler for it, and the point of the download path is that the browser
    /// should not be interpreting these files at all.
    /// </remarks>
    public static string ResponseContentType(string detected) =>
        CanRenderInline(detected) ? detected : "application/octet-stream";

    /// <summary>
    /// Strips a filename down to something safe to echo back in a header.
    /// </summary>
    /// <remarks>
    /// The name never reaches the filesystem — storage generates its own — but it does
    /// reach a <c>Content-Disposition</c> header, where a quote or a newline would let
    /// the uploader write headers of their own.
    /// </remarks>
    public static string SanitiseFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "attachment";
        }

        var trimmed = Path.GetFileName(name.Trim());
        var cleaned = new string([.. trimmed.Where(c =>
            !char.IsControl(c) && c != '"' && c != '\\' && c != '/' && c != ';')]);

        cleaned = cleaned.Trim();

        return string.IsNullOrEmpty(cleaned)
            ? "attachment"
            : cleaned[..Math.Min(cleaned.Length, 180)];
    }
}
