using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

internal sealed class SqliteSnapshotTransactions : DbTransactionInterceptor
{
    public static SqliteSnapshotTransactions Instance { get; } = new();

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Explicit Serializable is used for the read-only context snapshot. Default write transactions
        // retain BEGIN IMMEDIATE, reserving SQLite's writer before any final freshness reads.
        if (connection is SqliteConnection sqlite && eventData.IsolationLevel == IsolationLevel.Serializable)
            return ValueTask.FromResult(InterceptionResult<DbTransaction>.SuppressWithResult(
                sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: true)));
        return ValueTask.FromResult(result);
    }
}
