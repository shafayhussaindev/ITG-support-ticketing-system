using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Features.Attachments;

namespace SupportTicketing.Infrastructure.Storage;

/// <summary>
/// The configured upload rules.
/// </summary>
/// <remarks>
/// <para>
/// The accepted list is by detected type, not by extension. Renaming a file changes
/// its extension and nothing else, so a list of extensions is a list of things an
/// uploader can trivially satisfy.
/// </para>
/// <para>
/// It is an allowlist rather than a blocklist. A blocklist has to anticipate every
/// dangerous format, including the ones invented after it was written; an allowlist
/// only has to name the handful a support desk actually exchanges.
/// </para>
/// </remarks>
public sealed class AttachmentPolicy(IOptions<FileStorageOptions> options) : IAttachmentPolicy
{
    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal)
    {
        // Screenshots, which is the overwhelming majority of what gets attached.
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",

        // Screen recordings.
        "video/mp4", "video/webm", "video/x-msvideo",

        // Documents and exports.
        "application/pdf",
        "application/zip",

        // Logs, CSV exports and configuration files have no signature to detect, so
        // they arrive as octet-stream. Refusing that would refuse the single most
        // useful thing a staff member can ask a requester for.
        "application/octet-stream",
    };

    private readonly FileStorageOptions _options = options.Value;

    public long MaxFileSizeBytes => (long)_options.MaxFileSizeMb * 1024 * 1024;

    public void EnsureAcceptable(string fileName, long declaredLength)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            Reject("file", "The upload has no filename.");
        }

        if (declaredLength <= 0)
        {
            Reject("file", "The file is empty.");
        }

        // Checked here as well as during the read, so an honest client is refused
        // before it spends bandwidth sending a file that was always going to bounce.
        if (declaredLength > MaxFileSizeBytes)
        {
            Reject(
                "file",
                $"'{fileName}' is {Describe(declaredLength)}, over the "
                + $"{_options.MaxFileSizeMb}MB limit.");
        }
    }

    public void EnsureWithinLimit(long bytesSoFar)
    {
        if (bytesSoFar > MaxFileSizeBytes)
        {
            // Thrown mid-stream, which aborts the read and stops the rest of the file
            // reaching the disk. A declared length is a claim; this is the enforcement.
            Reject("file", $"The upload exceeds the {_options.MaxFileSizeMb}MB limit.");
        }
    }

    public bool IsAcceptedType(string detectedContentType) => Accepted.Contains(detectedContentType);

    public string DescribeAcceptedTypes() =>
        "Images, video, PDFs, Zip archives and plain files are accepted.";

    private static string Describe(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#}KB",
        _ => $"{bytes / (1024.0 * 1024):0.#}MB",
    };

    private static void Reject(string field, string message) =>
        throw new ValidationException([new ValidationFailure(field, message)]);
}
