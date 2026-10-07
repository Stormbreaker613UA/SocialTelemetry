using System.Diagnostics;
using System.Diagnostics.Metrics;
using SocialTelemetry.Api.Infrastructure.AI;

namespace SocialTelemetry.Api.Infrastructure.Observability;

public static class SocialTelemetryTelemetry
{
    public const string Name = "SocialTelemetry.Api";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Metrics = new(Name);
    private static readonly Counter<long> AiExecutions = Metrics.CreateCounter<long>("socialtelemetry.ai.executions");
    private static readonly Histogram<double> AiDuration = Metrics.CreateHistogram<double>("socialtelemetry.ai.duration", "s");
    private static readonly Counter<long> ReconciliationActions = Metrics.CreateCounter<long>("socialtelemetry.storage.reconciliation.actions");

    public static Operation Start(string name, CancellationToken cancellationToken = default) => new(name, cancellationToken: cancellationToken);

    public static Operation StartAi(string provider, string model, bool vision)
    {
        var operation = new Operation("ai.execute", provider);
        operation.Activity?.SetTag("ai.provider", provider);
        operation.Activity?.SetTag("ai.requested_model", model);
        operation.Activity?.SetTag("ai.input.kind", vision ? "text_image" : "text");
        return operation;
    }

    public static void RecordReconciliation(string area, string action) =>
        ReconciliationActions.Add(1, new("storage.area", area), new("action", action));

    public sealed class Operation : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private string? provider;
        private readonly CancellationToken cancellationToken;
        private string outcome = "failure";
        internal Activity? Activity { get; }

        internal Operation(string name, string? provider = null, CancellationToken cancellationToken = default)
        {
            this.provider = provider;
            this.cancellationToken = cancellationToken;
            Activity = Activities.StartActivity(name);
        }

        public void SetExecutionProvenance(string providerId, string model)
        {
            provider = providerId;
            Activity?.SetTag("ai.provider", providerId);
            Activity?.SetTag("ai.model", model);
        }

        public void Complete() => outcome = "success";

        public void Fail(Exception exception)
        {
            outcome = exception is OperationCanceledException ? "cancelled" : "failure";
            Activity?.SetTag("error.type", exception.GetType().Name);
            if (exception is AiProviderException providerFailure)
                Activity?.SetTag("failure.category", providerFailure.Failure.ToString());
        }

        public void Dispose()
        {
            if (outcome == "failure" && cancellationToken.IsCancellationRequested) outcome = "cancelled";
            Activity?.SetTag("outcome", outcome);
            Activity?.SetStatus(outcome == "failure" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            if (provider is not null)
            {
                // Model IDs remain trace metadata; arbitrary future model catalogs must not grow metric cardinality.
                var tags = new TagList { { "provider", provider }, { "outcome", outcome } };
                AiExecutions.Add(1, tags);
                AiDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }
            Activity?.Dispose();
        }
    }
}
