using SocialTelemetry.Api.Domain.People;
using SocialTelemetry.Api.Domain.Interactions;

namespace SocialTelemetry.Api.Infrastructure.AI.Models;

public sealed record AnalysisUser(Guid Id, string DisplayName, string? AboutMe, string? CommunicationStyle,
    string? Goals, string? Preferences, string? Boundaries, string? AiInstructions);
public sealed record AnalysisInteraction(Guid Id, string Title, string Description, string? UserThoughts,
    DateTimeOffset OccurredAt, IReadOnlyList<Guid> ParticipantIds);
public sealed record AnalysisFact(Guid Id, string Value, string? Source);
public sealed record AnalysisInference(Guid Id, string Value, decimal Confidence, Guid? SourceInteractionId);
public sealed record AnalysisPerson(Guid Id, string DisplayName, int? Age, string? Gender, string? Description,
    RelationshipContext RelationshipContext, string? HowWeMet, string? Notes,
    IReadOnlyList<AnalysisFact> ConfirmedFacts, IReadOnlyList<AnalysisInference> AiInferences);

// Unknown speakers stay null. Future normalized conversation sources can fill these fields.
public sealed record EvidenceMessage(string Text, Guid? SpeakerPersonId = null, string? SpeakerLabel = null);
public sealed record AnalysisEvidence(Guid Id, AttachmentType Type, IReadOnlyList<EvidenceMessage> Messages,
    string? MimeType, string? ImageDigest);
public sealed record InteractionAnalysisContext(AnalysisUser User, AnalysisInteraction CurrentInteraction,
    IReadOnlyList<AnalysisPerson> Participants, IReadOnlyList<AnalysisInteraction> PreviousInteractions,
    IReadOnlyList<AnalysisEvidence> Evidence, string? UserQuestion);
public sealed record BuiltAnalysisContext(InteractionAnalysisContext Context, IReadOnlyList<AiImageInput> Images,
    string InputJson, string Fingerprint);
