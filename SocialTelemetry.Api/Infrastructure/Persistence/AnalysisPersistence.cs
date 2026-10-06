using Npgsql;

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
        }
        return false;
    }
}
