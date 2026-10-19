using SocialTelemetry.Application.Contracts.Profiles;
using SocialTelemetry.Application.Contracts.Transcription;
using SocialTelemetry.Application.Contracts.AI;
using SocialTelemetry.Application.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SocialTelemetry.Api.Common.Exceptions;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = exception switch
        {
            LocalProfileAccessException profile => (FailurePresentation.StatusCode(profile.Failure), "Local profile access failed.", FailurePresentation.Detail(profile.Failure)),
            SpeechToTextException transcription => (FailurePresentation.StatusCode(transcription.Failure), "Transcription failed.", FailurePresentation.Detail(transcription.Failure)),
            AiProviderException providerException => (FailurePresentation.StatusCode(providerException.Failure), "AI connection or request failed.", FailurePresentation.Detail(providerException.Failure)),
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found.", "The requested resource was not found."),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict.", "The request conflicts with the current state of the resource."),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "Request too large.", "The request exceeds the allowed size."),
            BadHttpRequestException or InvalidInputException => (StatusCodes.Status400BadRequest, "Invalid request.", "The request could not be processed."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "An unexpected error occurred while processing the request.")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                "Unhandled exception of type {ExceptionType} while processing {RequestMethod} {RequestPath}",
                exception.GetType().Name,
                httpContext.Request.Method,
                httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail
        };
        if (exception is SpeechToTextException transcriptionException)
            problemDetails.Extensions["code"] = transcriptionException.Failure.ToString();
        if (exception is LocalProfileAccessException profileException)
            problemDetails.Extensions["code"] = profileException.Failure.ToString();
        if (exception is AiProviderException aiException)
        {
            problemDetails.Extensions["code"] = aiException.Failure.ToString();
            logger.LogWarning("AI operation failed with {AiFailure} for {RequestMethod} {RequestPath}",
                aiException.Failure, httpContext.Request.Method, httpContext.Request.Path);
        }
        var wasWritten = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        if (!wasWritten)
        {
            // Download clients may only accept image/audio; errors still need a safe JSON response.
            await httpContext.Response.WriteAsJsonAsync(
                problemDetails, options: null, contentType: "application/problem+json", cancellationToken);
        }

        return true;
    }
}
