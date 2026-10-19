using System.Data;
using Microsoft.EntityFrameworkCore;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Application.Features.Analysis;
using SocialTelemetry.Application.Observability;
using SocialTelemetry.Domain.Interactions;

namespace SocialTelemetry.Infrastructure.Persistence;

public sealed class AnalysisExecutionPersistence(AppDbContext database) : IAnalysisPersistence
{
    public async Task<BuiltAnalysisContext> SnapshotAsync(Func<CancellationToken, Task<BuiltAnalysisContext>> build,
        CancellationToken cancellationToken)
    {
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        using var contextOperation = SocialTelemetryTelemetry.Start("analysis.build_context", cancellationToken);
        var built = await build(cancellationToken);
        await snapshot.CommitAsync(cancellationToken);
        contextOperation.Complete();
        return built;
    }

    public async Task<InteractionAnalysis> CommitAsync(Guid owner,
        Func<CancellationToken, Task> verifyContext,
        Func<CancellationToken, Task<IAiExecutionLease>> acquireExecutionLease,
        Func<AnalysisMutation> create,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            // Acquire before reading, so a waiting writer's committed changes enter the rebuild.
            await database.LockAnalysisContextAsync(owner, cancellationToken);
            await verifyContext(cancellationToken);
            // The lease starts after the DB wait and freshness check and lives through commit.
            using var lease = await acquireExecutionLease(cancellationToken);
            var mutation = create();
            database.InteractionAnalyses.Add(mutation.Analysis);
            database.SuggestedProfileUpdates.AddRange(mutation.Suggestions);
            await database.SaveChangesAsync(cancellationToken);
            await lease.ValidateAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return mutation.Analysis;
        }
        catch (Exception exception) when (AnalysisPersistence.IsConcurrentSourceChange(exception))
        {
            throw new AiProviderException(AiFailure.StaleContext);
        }
    }
}
