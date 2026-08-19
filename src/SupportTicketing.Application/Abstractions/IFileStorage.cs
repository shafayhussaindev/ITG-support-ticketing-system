namespace SupportTicketing.Application.Abstractions;

/// <summary>
/// Where uploaded files physically live.
/// </summary>
/// <remarks>
/// <para>
/// An abstraction rather than direct file access, so moving from a disk on the
/// application server to blob storage is a registration change rather than a rewrite
/// of every handler that touches a file. Deployments differ on this and the answer is
/// rarely known when the feature is written.
/// </para>
/// <para>
/// Implementations own the layout of <paramref name="storagePath"/> entirely. Callers
/// treat it as opaque: they pass back the string they were given and never construct
/// one, which is what keeps a filename supplied by a user from reaching a path.
/// </para>
/// </remarks>
public interface IFileStorage
{
    /// <summary>
    /// Persists a stream and returns the opaque path needed to read it back.
    /// </summary>
    /// <param name="organizationId">Partitions storage per tenant so one tenant's files are separable.</param>
    /// <param name="storedFileName">A generated name. Never the name the user supplied.</param>
    Task<string> SaveAsync(
        Guid organizationId,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading, or throws if it has gone missing.</summary>
    Task<Stream> OpenAsync(string storagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stored file. Succeeds when the file is already absent.
    /// </summary>
    /// <remarks>
    /// Idempotent because the database row and the file are not written atomically:
    /// a retry after a partial failure must not turn into an error of its own.
    /// </remarks>
    Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default);

    /// <summary>Whether the stored file is still present, for diagnosing a broken download.</summary>
    Task<bool> ExistsAsync(string storagePath, CancellationToken cancellationToken = default);
}
