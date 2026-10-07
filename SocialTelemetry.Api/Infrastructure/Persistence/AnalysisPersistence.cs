using Npgsql;
using Microsoft.Data.Sqlite;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

internal static class AnalysisPersistence
{
    // Keep provider error inspection out of feature code. Other database failures stay unexpected.
    public static bool IsConcurrentSourceChange(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation })
                return true;
            // BUSY (including BUSY_SNAPSHOT), LOCKED, or an FK removed while final persistence waits.
            if (current is SqliteException { SqliteErrorCode: 5 or 6 } or
                SqliteException { SqliteExtendedErrorCode: 787 })
                return true;
        }
        return false;
    }
}
