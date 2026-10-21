using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Exceptions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocialTelemetry.Api.Common.Exceptions;

namespace SocialTelemetry.Tests.Common.Exceptions;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task NotFoundException_returns_not_found_problem_details()
    {
        var result = await HandleAsync(new NotFoundException("Private detail"));

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Resource not found.", result.ProblemDetails.Title);
        Assert.Equal("The requested resource was not found.", result.ProblemDetails.Detail);
    }

    [Fact]
    public async Task ConflictException_returns_conflict_problem_details()
    {
        var result = await HandleAsync(new ConflictException("Private detail"));

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("Conflict.", result.ProblemDetails.Title);
        Assert.Equal("The request conflicts with the current state of the resource.", result.ProblemDetails.Detail);
    }

    [Fact]
    public async Task Unknown_exception_returns_generic_internal_server_error_problem_details()
    {
        var result = await HandleAsync(new InvalidOperationException("Database provider details should not be exposed."));

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal("An unexpected error occurred.", result.ProblemDetails.Title);
        Assert.Equal("An unexpected error occurred while processing the request.", result.ProblemDetails.Detail);
    }

    [Fact]
    public async Task Unknown_exception_problem_details_does_not_expose_internal_details()
    {
        const string privateMessage = "Database provider details should not be exposed.";

        var result = await HandleAsync(new InvalidOperationException(privateMessage));

        Assert.DoesNotContain(privateMessage, result.ResponseBody);
        Assert.DoesNotContain("InvalidOperationException", result.ResponseBody);
        Assert.DoesNotContain("stack trace", result.ResponseBody, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [MemberData(nameof(ExistingFailureResponses), DisableDiscoveryEnumeration = true)]
    public async Task Neutral_failures_preserve_existing_public_status_detail_and_code(
        Exception failure, int statusCode, string title, string detail, string code)
    {
        var response = await HandleAsync(failure);
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(title, response.ProblemDetails.Title);
        Assert.Equal(detail, response.ProblemDetails.Detail);
        Assert.Equal(code, response.ProblemDetails.Extensions["code"]?.ToString());
    }

    public static IEnumerable<object[]> ExistingFailureResponses()
    {
        yield return new object[] { new AiProviderException(AiFailure.NotConnected), 409, "AI connection or request failed.", "Connect an AI provider first.", "NotConnected" };
        yield return new object[] { new AiProviderException(AiFailure.AuthorizationDenied), 400, "AI connection or request failed.", "ChatGPT connection was declined. Start a new connection when ready.", "AuthorizationDenied" };
        yield return new object[] { new AiProviderException(AiFailure.InvalidCallback), 400, "AI connection or request failed.", "The connection callback is invalid, expired, or already used. Start again.", "InvalidCallback" };
        yield return new object[] { new AiProviderException(AiFailure.InvalidIdentity), 502, "AI connection or request failed.", "The ChatGPT account could not be verified. Start a new connection.", "InvalidIdentity" };
        yield return new object[] { new AiProviderException(AiFailure.InferencePermissionMissing), 403, "AI connection or request failed.", "ChatGPT plan usage was not granted. Reconnect and enable plan usage.", "InferencePermissionMissing" };
        yield return new object[] { new AiProviderException(AiFailure.ReconnectRequired), 409, "AI connection or request failed.", "The ChatGPT credentials are no longer usable. Reconnect the saved account.", "ReconnectRequired" };
        yield return new object[] { new AiProviderException(AiFailure.RefreshFailed), 503, "AI connection or request failed.", "ChatGPT access could not be renewed. Retry later or reconnect.", "RefreshFailed" };
        yield return new object[] { new AiProviderException(AiFailure.InvalidClient), 500, "AI connection or request failed.", "The saved ChatGPT registration was rejected. Connect a new registration.", "InvalidClient" };
        yield return new object[] { new AiProviderException(AiFailure.UsageLimitReached), 429, "AI connection or request failed.", "The ChatGPT usage limit was reached. Manage usage at https://chatgpt.com/settings/usage.", "UsageLimitReached" };
        yield return new object[] { new AiProviderException(AiFailure.ModelNotSelected), 409, "AI connection or request failed.", "Select an available AI model first.", "ModelNotSelected" };
        yield return new object[] { new AiProviderException(AiFailure.ModelUnavailable), 409, "AI connection or request failed.", "The selected model is unavailable. Refresh the model list and select a model.", "ModelUnavailable" };
        yield return new object[] { new AiProviderException(AiFailure.ProviderUnavailable), 503, "AI connection or request failed.", "The AI provider is temporarily unavailable. Retry later.", "ProviderUnavailable" };
        yield return new object[] { new AiProviderException(AiFailure.ProviderRejected), 502, "AI connection or request failed.", "The AI provider rejected this operation. Check access and supported capabilities.", "ProviderRejected" };
        yield return new object[] { new AiProviderException(AiFailure.MalformedResponse), 502, "AI connection or request failed.", "The AI provider returned an invalid response. No result was accepted.", "MalformedResponse" };
        yield return new object[] { new AiProviderException(AiFailure.IncompleteResponse), 502, "AI connection or request failed.", "The AI provider did not complete the response. No partial result was accepted.", "IncompleteResponse" };
        yield return new object[] { new AiProviderException(AiFailure.StorageUnavailable), 500, "AI connection or request failed.", "The protected ChatGPT connection store is unavailable. Check local storage access.", "StorageUnavailable" };
        yield return new object[] { new AiProviderException(AiFailure.ConnectionBusy), 409, "AI connection or request failed.", "Another local process is updating the ChatGPT connection. Retry shortly.", "ConnectionBusy" };
        yield return new object[] { new AiProviderException(AiFailure.BrowserUnavailable), 500, "AI connection or request failed.", "The system browser could not be opened. Check the local desktop browser configuration.", "BrowserUnavailable" };
        yield return new object[] { new AiProviderException(AiFailure.LocalRequestRequired), 403, "AI connection or request failed.", "Use a local connection and the X-SocialTelemetry-Local: 1 header.", "LocalRequestRequired" };
        yield return new object[] { new AiProviderException(AiFailure.UnsupportedEvidence), 400, "AI connection or request failed.", "This evidence type or media format is not supported for analysis.", "UnsupportedEvidence" };
        yield return new object[] { new AiProviderException(AiFailure.EvidenceNotReady), 409, "AI connection or request failed.", "Selected evidence is not Ready.", "EvidenceNotReady" };
        yield return new object[] { new AiProviderException(AiFailure.TranscriptNotReady), 409, "AI connection or request failed.", "Selected audio requires a non-empty, reviewed persisted transcript. Save and review its current version first.", "TranscriptNotReady" };
        yield return new object[] { new AiProviderException(AiFailure.StaleTranscript), 409, "AI connection or request failed.", "The transcript no longer matches the selected audio. Provide a source-current transcript and review it before analysis.", "StaleTranscript" };
        yield return new object[] { new AiProviderException(AiFailure.EvidenceLimitExceeded), 400, "AI connection or request failed.", "The analysis context or evidence exceeds the supported limits.", "EvidenceLimitExceeded" };
        yield return new object[] { new AiProviderException(AiFailure.InvalidEvidenceSelection), 400, "AI connection or request failed.", "Select only existing attachments belonging to this interaction.", "InvalidEvidenceSelection" };
        yield return new object[] { new AiProviderException(AiFailure.CapabilityMissing), 409, "AI connection or request failed.", "The selected model does not have confirmed support for a required capability.", "CapabilityMissing" };
        yield return new object[] { new AiProviderException(AiFailure.InvalidStructuredResponse), 502, "AI connection or request failed.", "The structured analysis did not pass validation. No analysis was saved.", "InvalidStructuredResponse" };
        yield return new object[] { new AiProviderException(AiFailure.StaleContext), 409, "AI connection or request failed.", "The analysis context changed. Run analysis again using current data.", "StaleContext" };
        yield return new object[] { new AiProviderException(AiFailure.ExecutionInvalidated), 409, "AI connection or request failed.", "The AI connection changed or disconnected. No result was accepted.", "ExecutionInvalidated" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.ConfigurationRequired), 503, "Transcription failed.", "Configure accessible local decoder, transcription executable and model files before transcribing.", "ConfigurationRequired" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.InvalidAudio), 400, "Transcription failed.", "The file is not a valid supported audio input.", "InvalidAudio" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.LimitExceeded), 400, "Transcription failed.", "Audio or transcription output exceeds the configured limits.", "LimitExceeded" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.DecoderFailed), 502, "Transcription failed.", "The local audio decoder could not complete.", "DecoderFailed" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.TranscriptionFailed), 502, "Transcription failed.", "Local transcription could not complete.", "TranscriptionFailed" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.InvalidResult), 502, "Transcription failed.", "Local transcription did not produce usable text.", "InvalidResult" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.TimedOut), 504, "Transcription failed.", "Local transcription exceeded its time limit.", "TimedOut" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.Busy), 429, "Transcription failed.", "Local transcription is busy. Retry when the current operation finishes.", "Busy" };
        yield return new object[] { new SpeechToTextException(SpeechToTextFailure.CleanupFailed), 502, "Transcription failed.", "Owned native process cleanup could not be confirmed within the configured time limit.", "CleanupFailed" };
        yield return new object[] { new LocalProfileAccessException(LocalProfileFailure.SelectionRequired), 409, "Local profile access failed.", "Select a local profile first.", "SelectionRequired" };
        yield return new object[] { new LocalProfileAccessException(LocalProfileFailure.SelectionChanged), 409, "Local profile access failed.", "The selected profile changed. Reload before continuing.", "SelectionChanged" };
        yield return new object[] { new LocalProfileAccessException(LocalProfileFailure.AssertionRequired), 409, "Local profile access failed.", "The current profile and selection version must be supplied.", "AssertionRequired" };
        yield return new object[] { new LocalProfileAccessException(LocalProfileFailure.InvalidAssertion), 400, "Local profile access failed.", "The profile assertion is invalid.", "InvalidAssertion" };
        yield return new object[] { new LocalProfileAccessException(LocalProfileFailure.ProtectedAccessUnavailable), 403, "Local profile access failed.", "This profile requires account access that is not supported yet.", "ProtectedAccessUnavailable" };
    }

    [Fact]
    public async Task Neutral_invalid_input_keeps_the_generic_400_transport_response()
    {
        var response = await HandleAsync(new InvalidInputException());
        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Invalid request.", response.ProblemDetails.Title);
        Assert.Equal("The request could not be processed.", response.ProblemDetails.Detail);
    }

    private static async Task<HandlerResult> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();

        await using var serviceProvider = services.BuildServiceProvider();
        var handler = new GlobalExceptionHandler(
            serviceProvider.GetRequiredService<ILogger<GlobalExceptionHandler>>(),
            serviceProvider.GetRequiredService<IProblemDetailsService>());
        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/test/exceptions";
        httpContext.Request.Headers.Accept = "application/problem+json";
        httpContext.Response.Body = new MemoryStream();

        var wasHandled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        Assert.True(wasHandled);

        httpContext.Response.Body.Position = 0;
        var responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(problemDetails);

        return new HandlerResult(httpContext.Response.StatusCode, problemDetails, responseBody);
    }

    private sealed record HandlerResult(int StatusCode, ProblemDetails ProblemDetails, string ResponseBody);
}
