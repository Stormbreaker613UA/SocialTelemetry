using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Contracts.Profiles;

namespace SocialTelemetry.Api.Common.Exceptions;

// Keep HTTP policy and provider-facing guidance outside the neutral operation failures.
public static class FailurePresentation
{
    public static int StatusCode(LocalAccessFailure failure) => failure switch
    {
        LocalAccessFailure.Required or LocalAccessFailure.InvalidCredentials => 401,
        LocalAccessFailure.LockedOut => 429,
        LocalAccessFailure.InvalidInput => 400,
        LocalAccessFailure.SecurityChanged => 409,
        LocalAccessFailure.LocalRequestRequired => 403,
        _ => 503
    };

    public static string Detail(LocalAccessFailure failure) => failure switch
    {
        LocalAccessFailure.Required => "Sign in to this local profile before continuing.",
        LocalAccessFailure.InvalidCredentials => "The supplied local credentials are invalid.",
        LocalAccessFailure.LockedOut => "Local authentication is temporarily limited. Try again later.",
        LocalAccessFailure.InvalidInput => "The supplied local access input does not meet the requirements.",
        LocalAccessFailure.SecurityChanged => "Local access changed. Sign in again before continuing.",
        LocalAccessFailure.LocalRequestRequired => "A secure same-origin local request is required.",
        _ => "Local access storage is unavailable."
    };
    public static int StatusCode(AiFailure failure) => failure switch
    {
        AiFailure.AuthorizationDenied or AiFailure.InvalidCallback or AiFailure.UnsupportedEvidence or
            AiFailure.EvidenceLimitExceeded or AiFailure.InvalidEvidenceSelection => 400,
        AiFailure.InferencePermissionMissing or AiFailure.LocalRequestRequired => 403,
        AiFailure.NotConnected or AiFailure.ReconnectRequired or AiFailure.ModelNotSelected or
            AiFailure.ModelUnavailable or AiFailure.ConnectionBusy or AiFailure.EvidenceNotReady or
            AiFailure.TranscriptNotReady or AiFailure.StaleTranscript or AiFailure.CapabilityMissing or
            AiFailure.StaleContext or AiFailure.ExecutionInvalidated => 409,
        AiFailure.UsageLimitReached => 429,
        AiFailure.ProviderUnavailable or AiFailure.RefreshFailed => 503,
        AiFailure.StorageUnavailable or AiFailure.BrowserUnavailable or AiFailure.InvalidClient => 500,
        _ => 502
    };

    public static string Detail(AiFailure failure) => failure switch
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
        AiFailure.TranscriptNotReady => "Selected audio requires a non-empty, reviewed persisted transcript. Save and review its current version first.",
        AiFailure.StaleTranscript => "The transcript no longer matches the selected audio. Provide a source-current transcript and review it before analysis.",
        AiFailure.EvidenceLimitExceeded => "The analysis context or evidence exceeds the supported limits.",
        AiFailure.InvalidEvidenceSelection => "Select only existing attachments belonging to this interaction.",
        AiFailure.CapabilityMissing => "The selected model does not have confirmed support for a required capability.",
        AiFailure.InvalidStructuredResponse => "The structured analysis did not pass validation. No analysis was saved.",
        AiFailure.StaleContext => "The analysis context changed. Run analysis again using current data.",
        AiFailure.ExecutionInvalidated => "The AI connection changed or disconnected. No result was accepted.",
        _ => "The AI provider operation failed."
    };

    public static int StatusCode(SpeechToTextFailure failure) => failure switch
    {
        SpeechToTextFailure.ConfigurationRequired => 503,
        SpeechToTextFailure.InvalidAudio or SpeechToTextFailure.LimitExceeded => 400,
        SpeechToTextFailure.TimedOut => 504,
        SpeechToTextFailure.Busy => 429,
        _ => 502
    };

    public static string Detail(SpeechToTextFailure failure) => failure switch
{
    SpeechToTextFailure.ConfigurationRequired => "Configure accessible local decoder, transcription executable and model files before transcribing.",
    SpeechToTextFailure.InvalidAudio => "The file is not a valid supported audio input.",
    SpeechToTextFailure.LimitExceeded => "Audio or transcription output exceeds the configured limits.",
    SpeechToTextFailure.DecoderFailed => "The local audio decoder could not complete.",
    SpeechToTextFailure.TranscriptionFailed => "Local transcription could not complete.",
    SpeechToTextFailure.InvalidResult => "Local transcription did not produce usable text.",
    SpeechToTextFailure.Busy => "Local transcription is busy. Retry when the current operation finishes.",
    SpeechToTextFailure.CleanupFailed => "Owned native process cleanup could not be confirmed within the configured time limit.",
    _ => "Local transcription exceeded its time limit."
};

    public static int StatusCode(LocalProfileFailure failure) => failure == LocalProfileFailure.InvalidAssertion ? 400 :
        failure == LocalProfileFailure.ProtectedAccessUnavailable ? 403 : 409;

    public static string Detail(LocalProfileFailure failure) => failure switch
{
    LocalProfileFailure.SelectionRequired => "Select a local profile first.",
    LocalProfileFailure.SelectionChanged => "The selected profile changed. Reload before continuing.",
    LocalProfileFailure.AssertionRequired => "The current profile and selection version must be supplied.",
    LocalProfileFailure.InvalidAssertion => "The profile assertion is invalid.",
    LocalProfileFailure.ProtectedAccessUnavailable => "This profile requires account access that is not supported yet.",
    _ => "Local profile access is unavailable."
};
}
