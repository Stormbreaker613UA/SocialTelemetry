using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SocialTelemetry.Api.Infrastructure.AI;

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
            AiProviderException providerException => (providerException.StatusCode, "AI connection or request failed.", providerException.Message),
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found.", "The requested resource was not found."),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict.", "The request conflicts with the current state of the resource."),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "Request too large.", "The request exceeds the allowed size."),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request.", "The request could not be processed."),
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
