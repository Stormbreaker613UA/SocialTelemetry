using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Domain.Interactions;
using StoredSuggestion = SocialTelemetry.Domain.People.SuggestedProfileUpdate;

namespace SocialTelemetry.Application.Features.Analysis;

public sealed record AnalysisMutation(InteractionAnalysis Analysis, IReadOnlyList<StoredSuggestion> Suggestions);

// Specific analysis boundaries: callbacks contain bounded context reads/file freshness checks
// or pure entity construction. Never provider inference, STT or independent commits.
public interface IAnalysisPersistence
{
    Task<BuiltAnalysisContext> SnapshotAsync(Func<CancellationToken, Task<BuiltAnalysisContext>> build, CancellationToken cancellationToken);
    Task<InteractionAnalysis> CommitAsync(Guid owner,
        Func<CancellationToken, Task> verifyContext,
        Func<CancellationToken, Task<IAiExecutionLease>> acquireExecutionLease,
        Func<AnalysisMutation> create,
        CancellationToken cancellationToken);
}
