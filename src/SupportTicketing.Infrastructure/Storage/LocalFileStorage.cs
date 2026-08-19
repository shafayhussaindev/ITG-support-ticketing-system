using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Where uploaded files live. Must be outside the web root, or the application
    /// serves them directly and every check in the download path is bypassed by
    /// anyone who can guess a URL.
    /// </summary>
    [Required]
    public string RootPath { get; set; } = "app-data/attachments";

    /// <summary>Largest accepted upload, in megabytes.</summary>
    [Range(1, 2048)]
    public int MaxFileSizeMb { get; set; } = 100;

    /// <summary>Largest accepted image, which has no reason to approach the video limit.</summary>
    [Range(1, 128)]
    public int MaxImageSizeMb { get; set; } = 20;
}

/// <summary>
/// Stores uploads on the application server's disk.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation, and the right one for a single-server deployment. It
/// sits behind <see cref="IFileStorage"/> so a move to blob storage is a registration
/// change rather than a rewrite — which matters, because a second application instance
/// behind a load balancer cannot see this disk and the switch becomes urgent the day
/// somebody scales out.
/// </para>
/// <para>
/// The layout is <c>{root}/{organization}/{yyyy}/{MM}/{storedName}</c>. Partitioned by
/// tenant so one organization's files can be exported or destroyed on their own, and by
/// month because a single directory holding a hundred thousand entries is slow to list
/// on every filesystem worth naming.
/// </para>
/// </remarks>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(IOptions<FileStorageOptions> options, ILogger<LocalFileStorage> logger)
    {
        _logger = logger;
        _root = Path.GetFullPath(options.Value.RootPath);

        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(
        Guid organizationId,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var relative = Path.Combine(
            organizationId.ToString("N"),
            now.ToString("yyyy"),
            now.ToString("MM"),
            storedFileName);

        var absolute = ResolveWithin(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        // FileMode.CreateNew, not Create: the stored name is a fresh GUID, so an
        // existing file at that path means something has gone badly wrong and
        // overwriting it would destroy somebody else's upload silently.
        await using var file = new FileStream(
            absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true);

        await content.CopyToAsync(file, cancellationToken);

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public Task<Stream> OpenAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var absolute = ResolveWithin(storagePath);

        if (!File.Exists(absolute))
        {
            // The database row survives a lost file, so this is reachable: a restored
            // database paired with an unrestored disk, or a half-finished migration to
            // another store. Saying which path was missing is what makes that
            // diagnosable rather than mysterious.
            throw new FileNotFoundException(
                $"The stored file '{storagePath}' is missing from the storage root. The "
                + "attachment record exists but its contents do not — check that the "
                + "storage volume is mounted and was included in the last restore.",
                absolute);
        }

        Stream stream = new FileStream(
            absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var absolute = ResolveWithin(storagePath);

        try
        {
            // Idempotent: the row and the file are not written atomically, so a retry
            // after a partial failure must not become an error of its own.
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
            }
        }
        catch (IOException exception)
        {
            // A locked file must not fail the request that owns the database change.
            // The row is gone either way; this leaves an orphan on disk to be swept up.
            _logger.LogWarning(
                exception, "Could not remove the stored file {Path}; it is now orphaned.", storagePath);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storagePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolveWithin(storagePath)));

    /// <summary>
    /// Turns a relative storage path into an absolute one, refusing anything that
    /// escapes the root.
    /// </summary>
    /// <remarks>
    /// Callers only ever pass back a path this class produced, so this should never
    /// fire. It exists because "should never" and "cannot" are different claims, and
    /// the consequence of being wrong is arbitrary file read or write on the server.
    /// </remarks>
    private string ResolveWithin(string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, relative));

        if (!combined.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, _root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"The storage path '{relative}' resolves outside the storage root.");
        }

        return combined;
    }
}
