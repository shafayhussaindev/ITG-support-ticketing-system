using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Infrastructure.Persistence;

/// <summary>
/// Allocates ticket numbers with a single atomic statement.
/// </summary>
/// <remarks>
/// <para>
/// The <c>UPDATE … OUTPUT INSERTED.LastValue</c> form increments and reads the counter
/// in one statement, so the row lock is held for the whole read-modify-write and two
/// concurrent callers cannot observe the same value. A separate <c>SELECT</c> followed
/// by an <c>UPDATE</c> would leave a window between them.
/// </para>
/// <para>
/// The insert of a missing counter row races with itself the first time an
/// organization raises a ticket in a new year, so it is guarded by the unique index
/// and a retry rather than by a check-then-insert, which has the same window.
/// </para>
/// <para>
/// The unique index on (OrganizationId, TicketNumber) remains the final backstop: if
/// this ever produced a duplicate, the insert fails loudly instead of two tickets
/// silently sharing a number.
/// </para>
/// </remarks>
public sealed class TicketNumberGenerator(AppDbContext db) : ITicketNumberGenerator
{
    private const int SequenceDigits = 6;
    private const int UniqueViolation = 2601;
    private const int PrimaryKeyViolation = 2627;

    public async Task<string> NextAsync(Guid organizationId, string prefix, CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix) ? "TKT" : prefix.Trim().ToUpperInvariant();

        var next = await TryIncrementAsync(organizationId, normalizedPrefix, year, cancellationToken);

        if (next is null)
        {
            await EnsureSequenceRowAsync(organizationId, normalizedPrefix, year, cancellationToken);

            next = await TryIncrementAsync(organizationId, normalizedPrefix, year, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Could not allocate a ticket number for organization {organizationId} in {year}.");
        }

        return $"{normalizedPrefix}-{year}-{next.Value.ToString().PadLeft(SequenceDigits, '0')}";
    }

    private async Task<long?> TryIncrementAsync(
        Guid organizationId, string prefix, int year, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE TicketNumberSequences
            SET LastValue = LastValue + 1
            OUTPUT INSERTED.LastValue
            WHERE OrganizationId = @orgId AND Prefix = @prefix AND [Year] = @year;
            """;

        var connection = db.Database.GetDbConnection();
        var openedHere = false;

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            // Enlisting in the ambient transaction matters: the number must be
            // allocated and the ticket inserted atomically, or a rolled-back creation
            // would burn a number and leave a gap in the sequence.
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

            command.Parameters.Add(new SqlParameter("@orgId", organizationId));
            command.Parameters.Add(new SqlParameter("@prefix", prefix));
            command.Parameters.Add(new SqlParameter("@year", year));

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? null : Convert.ToInt64(result);
        }
        finally
        {
            if (openedHere)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task EnsureSequenceRowAsync(
        Guid organizationId, string prefix, int year, CancellationToken cancellationToken)
    {
        try
        {
            db.TicketNumberSequences.Add(new Domain.Tickets.TicketNumberSequence
            {
                OrganizationId = organizationId,
                Prefix = prefix,
                Year = year,
                LastValue = 0,
            });

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another request created the counter first, which is the outcome we
            // wanted. Detach so the duplicate does not resurface on the next save.
            foreach (var entry in db.ChangeTracker.Entries<Domain.Tickets.TicketNumberSequence>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException sql
        && sql.Number is UniqueViolation or PrimaryKeyViolation;
}
