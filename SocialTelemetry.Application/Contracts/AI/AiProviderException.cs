namespace SocialTelemetry.Application.Contracts.AI;

public enum AiFailure
{
    NotConnected,
    AuthorizationDenied,
    InvalidCallback,
    InvalidIdentity,
    InferencePermissionMissing,
    ReconnectRequired,
    RefreshFailed,
    InvalidClient,
    UsageLimitReached,
    ModelNotSelected,
    ModelUnavailable,
    ProviderUnavailable,
    ProviderRejected,
    MalformedResponse,
    IncompleteResponse,
    StorageUnavailable,
    ConnectionBusy,
    BrowserUnavailable,
    LocalRequestRequired,
    UnsupportedEvidence,
    EvidenceNotReady,
    TranscriptNotReady,
    StaleTranscript,
    EvidenceLimitExceeded,
    InvalidEvidenceSelection,
    CapabilityMissing,
    InvalidStructuredResponse,
    StaleContext,
    ExecutionInvalidated
}

public sealed class AiProviderException(AiFailure failure) : Exception(failure.ToString())
{
    public AiFailure Failure { get; } = failure;
}
