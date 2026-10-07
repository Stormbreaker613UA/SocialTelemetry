using System.Diagnostics;
using OpenTelemetry;

namespace SocialTelemetry.Api.Infrastructure.Observability;

public sealed class PrivacyTraceProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> SafeTags =
    [
        "http.request.method", "http.response.status_code", "http.route", "network.protocol.version",
        "db.system.name", "db.operation.name", "db.response.status_code", "error.type",
        "ai.provider", "ai.model", "ai.requested_model", "ai.input.kind", "outcome", "failure.category"
    ];

    // Npgsql 10 calls Activity.AddException, which otherwise records exception messages and stacks.
    private readonly ActivityListener exceptionListener = new()
    {
        ShouldListenTo = source => source.Name is "Npgsql" or "System.Net.Http" or
            "Microsoft.AspNetCore" or SocialTelemetryTelemetry.Name,
        ExceptionRecorder = SanitizeException
    };

    public PrivacyTraceProcessor() => ActivitySource.AddActivityListener(exceptionListener);

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in activity.TagObjects.ToArray())
            if (!SafeTags.Contains(tag.Key)) activity.SetTag(tag.Key, null);
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status);
        if (activity.Source.Name == "Npgsql") activity.DisplayName = "database.command";
        else if (activity.Kind == ActivityKind.Server)
            activity.DisplayName = activity.GetTagItem("http.route") as string ?? "HTTP request";
        else if (activity.Source.Name == "System.Net.Http") activity.DisplayName = "HTTP client";
    }

    private static void SanitizeException(Activity activity, Exception exception, ref TagList tags)
    {
        tags = new TagList
        {
            { "exception.type", exception.GetType().Name },
            { "exception.message", null },
            { "exception.stacktrace", null }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) exceptionListener.Dispose();
        base.Dispose(disposing);
    }
}
