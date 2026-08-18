using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Common;

namespace SupportTicketing.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps created/updated metadata and converts deletes into archival.
/// </summary>
/// <remarks>
/// Doing this in an interceptor rather than in each handler means a new entity or a
/// new code path cannot accidentally skip auditing, and a <c>Remove</c> call
/// anywhere in the codebase becomes a soft delete rather than data loss.
/// </remarks>
public sealed class AuditingInterceptor(ICurrentUser currentUser, IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var actor = currentUser.UserId;

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            GuardAppendOnly(entry);

            switch (entry.State)
            {
                case EntityState.Added when entry.Entity is IAuditable added:
                    added.CreatedAtUtc = now;
                    added.CreatedBy ??= actor;
                    break;

                case EntityState.Modified when entry.Entity is IAuditable modified:
                    modified.UpdatedAtUtc = now;
                    modified.UpdatedBy = actor;

                    // Creation metadata is immutable once written.
                    entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedBy)).IsModified = false;
                    break;

                case EntityState.Deleted when entry.Entity is ISoftDeletable deletable:
                    // Ticket history must never be destroyed, so a physical delete of an
                    // archivable entity is rewritten as an archive.
                    entry.State = EntityState.Modified;
                    deletable.IsDeleted = true;
                    deletable.DeletedAtUtc = now;
                    deletable.DeletedBy = actor;

                    if (entry.Entity is IAuditable auditableDeleted)
                    {
                        auditableDeleted.UpdatedAtUtc = now;
                        auditableDeleted.UpdatedBy = actor;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Append-only entities may be inserted and never touched again. This turns a
    /// silent history rewrite into a loud failure.
    /// </summary>
    private static void GuardAppendOnly(EntityEntry entry)
    {
        if (entry.Entity is not IAppendOnly)
        {
            return;
        }

        if (entry.State is EntityState.Modified or EntityState.Deleted)
        {
            throw new InvalidOperationException(
                $"'{entry.Entity.GetType().Name}' is append-only. Attempted state: {entry.State}. " +
                "Audit and history records cannot be modified or deleted.");
        }
    }
}
