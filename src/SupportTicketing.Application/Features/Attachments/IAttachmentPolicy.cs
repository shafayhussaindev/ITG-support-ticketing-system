namespace SupportTicketing.Application.Features.Attachments;

/// <summary>
/// What may be uploaded, and how large it may be.
/// </summary>
/// <remarks>
/// An abstraction rather than constants, because the answer is a deployment decision:
/// a team that shares screen recordings needs a very different ceiling from one that
/// exchanges PDFs, and neither should have to rebuild the application to say so.
/// </remarks>
public interface IAttachmentPolicy
{
    /// <summary>Largest accepted upload, in bytes.</summary>
    long MaxFileSizeBytes { get; }

    /// <summary>Throws when the name or declared size is unacceptable, before any bytes are read.</summary>
    void EnsureAcceptable(string fileName, long declaredLength);

    /// <summary>Throws once the bytes actually received exceed the ceiling.</summary>
    void EnsureWithinLimit(long bytesSoFar);

    /// <summary>Whether a sniffed content type is on the accepted list.</summary>
    bool IsAcceptedType(string detectedContentType);

    /// <summary>A sentence for an error message, listing what would have been accepted.</summary>
    string DescribeAcceptedTypes();
}
