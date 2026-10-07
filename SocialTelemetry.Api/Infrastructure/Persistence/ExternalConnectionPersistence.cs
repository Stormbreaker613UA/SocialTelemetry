using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Data.Sqlite;
using SocialTelemetry.Api.Common.Exceptions;

namespace SocialTelemetry.Api.Infrastructure.Persistence;

public static class ExternalConnectionPersistence
{
    public static async Task SaveAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsStableIdentityConflict(exception.InnerException))
        {
            // Translate only these ownership-scoped constraints; provider details stay in persistence.
            throw new ConflictException();
        }
    }

    private static bool IsStableIdentityConflict(Exception? exception)
    {
        if (exception is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_PersonConnection_StableIdentity" or "IX_UserConnection_StableIdentity" })
            return true;
        // SQLite reports the constrained columns, not the index name. Match only our two exact keys.
        if (exception is not SqliteException { SqliteExtendedErrorCode: 2067 } sqlite) return false;
        return sqlite.Message.Contains("UNIQUE constraint failed: PersonExternalConnections.PersonId, PersonExternalConnections.Platform, PersonExternalConnections.ExternalUserId'", StringComparison.Ordinal) ||
            sqlite.Message.Contains("UNIQUE constraint failed: UserProfileExternalConnections.UserProfileId, UserProfileExternalConnections.Platform, UserProfileExternalConnections.ExternalUserId'", StringComparison.Ordinal);
    }
}
