using SocialTelemetry.Application.Contracts.AI.Models;
using SocialTelemetry.Application.Options;
using SocialTelemetry.Domain.Interactions;
using SocialTelemetry.Domain.People;
using ProfileEntity = SocialTelemetry.Domain.Users.UserProfile;

namespace SocialTelemetry.Application.Features.Analysis;

// A rejected read carries no materialized private fields. SQL performs bounds checks first.
public sealed record BoundedContextRead<T>(bool ExceedsLimit, T? Value) where T : class;
public sealed record TranscriptEvidence(string? Text, Guid Version, TranscriptReviewStatus ReviewStatus,
    DateTimeOffset? ReviewedAt, string SourceStorageKey, string SourceSha256,
    string? Provider, string? Model, string? TranscriptionVersion);

public interface IAnalysisContextQuery
{
    Task<BoundedContextRead<Interaction>> InteractionAsync(Guid owner, Guid interactionId, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<BoundedContextRead<ProfileEntity>> UserAsync(Guid owner, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ParticipantIdsAsync(Guid owner, Guid interactionId, int maximumCount, CancellationToken cancellationToken);
    Task<BoundedContextRead<IReadOnlyList<Person>>> PeopleAsync(Guid owner, IReadOnlyList<Guid> participantIds, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<BoundedContextRead<IReadOnlyList<AnalysisInteraction>>> HistoryAsync(Guid owner, Interaction interaction, IReadOnlyList<Guid> participantIds, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<BoundedContextRead<IReadOnlyList<AnalysisFact>>> FactsAsync(Guid owner, Guid personId, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<BoundedContextRead<IReadOnlyList<AnalysisInference>>> InferencesAsync(Guid owner, Guid personId, Guid[] sourceIds, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<BoundedContextRead<IReadOnlyList<InteractionAttachment>>> AttachmentsAsync(Guid owner, Guid interactionId, IReadOnlyList<Guid>? selectedIds, AnalysisOptions limits, CancellationToken cancellationToken);
    Task<BoundedContextRead<TranscriptEvidence>> TranscriptAsync(Guid owner, Guid attachmentId, Guid interactionId, int textLimit, CancellationToken cancellationToken);
}
