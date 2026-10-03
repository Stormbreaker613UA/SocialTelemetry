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
