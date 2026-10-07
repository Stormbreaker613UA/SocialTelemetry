using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_PersonConnection_StableIdentity" or "IX_UserConnection_StableIdentity" })
        {
            // Translate only these ownership-scoped constraints; provider details stay in persistence.
            throw new ConflictException();
        }
    }
}
