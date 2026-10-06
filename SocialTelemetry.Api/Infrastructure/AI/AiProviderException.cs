namespace SocialTelemetry.Api.Infrastructure.AI;

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
    EvidenceLimitExceeded,
    InvalidEvidenceSelection,
    CapabilityMissing,
    InvalidStructuredResponse,
    StaleContext,
    ExecutionInvalidated
}

public sealed class AiProviderException(AiFailure failure) : Exception(GetDetail(failure))
{
    public AiFailure Failure { get; } = failure;

    public int StatusCode => Failure switch
    {
        AiFailure.AuthorizationDenied or AiFailure.InvalidCallback or AiFailure.UnsupportedEvidence or
            AiFailure.EvidenceLimitExceeded or AiFailure.InvalidEvidenceSelection => 400,
        AiFailure.InferencePermissionMissing or AiFailure.LocalRequestRequired => 403,
        AiFailure.NotConnected or AiFailure.ReconnectRequired or AiFailure.ModelNotSelected or
            AiFailure.ModelUnavailable or AiFailure.ConnectionBusy or AiFailure.EvidenceNotReady or
            AiFailure.CapabilityMissing or AiFailure.StaleContext or AiFailure.ExecutionInvalidated => 409,
        AiFailure.UsageLimitReached => 429,
        AiFailure.ProviderUnavailable or AiFailure.RefreshFailed => 503,
        AiFailure.StorageUnavailable or AiFailure.BrowserUnavailable or AiFailure.InvalidClient => 500,
        _ => 502
    };

    private static string GetDetail(AiFailure failure) => failure switch
    {
        AiFailure.NotConnected => "Connect an AI provider first.",
        AiFailure.AuthorizationDenied => "ChatGPT connection was declined. Start a new connection when ready.",
        AiFailure.InvalidCallback => "The connection callback is invalid, expired, or already used. Start again.",
        AiFailure.InvalidIdentity => "The ChatGPT account could not be verified. Start a new connection.",
        AiFailure.InferencePermissionMissing => "ChatGPT plan usage was not granted. Reconnect and enable plan usage.",
        AiFailure.ReconnectRequired => "The ChatGPT credentials are no longer usable. Reconnect the saved account.",
        AiFailure.RefreshFailed => "ChatGPT access could not be renewed. Retry later or reconnect.",
        AiFailure.InvalidClient => "The saved ChatGPT registration was rejected. Connect a new registration.",
        AiFailure.UsageLimitReached => "The ChatGPT usage limit was reached. Manage usage at https://chatgpt.com/settings/usage.",
        AiFailure.ModelNotSelected => "Select an available AI model first.",
        AiFailure.ModelUnavailable => "The selected model is unavailable. Refresh the model list and select a model.",
        AiFailure.ProviderUnavailable => "The AI provider is temporarily unavailable. Retry later.",
        AiFailure.ProviderRejected => "The AI provider rejected this operation. Check access and supported capabilities.",
        AiFailure.MalformedResponse => "The AI provider returned an invalid response. No result was accepted.",
        AiFailure.IncompleteResponse => "The AI provider did not complete the response. No partial result was accepted.",
        AiFailure.StorageUnavailable => "The protected ChatGPT connection store is unavailable. Check local storage access.",
        AiFailure.ConnectionBusy => "Another local process is updating the ChatGPT connection. Retry shortly.",
        AiFailure.BrowserUnavailable => "The system browser could not be opened. Check the local desktop browser configuration.",
        AiFailure.LocalRequestRequired => "Use a local connection and the X-SocialTelemetry-Local: 1 header.",
        AiFailure.UnsupportedEvidence => "This evidence type or media format is not supported for analysis.",
        AiFailure.EvidenceNotReady => "Selected evidence is not Ready.",
        AiFailure.EvidenceLimitExceeded => "The analysis context or evidence exceeds the supported limits.",
        AiFailure.InvalidEvidenceSelection => "Select only existing attachments belonging to this interaction.",
        AiFailure.CapabilityMissing => "The selected model does not support a required analysis capability.",
        AiFailure.InvalidStructuredResponse => "The structured analysis did not pass validation. No analysis was saved.",
        AiFailure.StaleContext => "The analysis context changed. Run analysis again using current data.",
        AiFailure.ExecutionInvalidated => "The AI connection changed or disconnected. No result was accepted.",
        _ => "The AI provider operation failed."
    };
}
