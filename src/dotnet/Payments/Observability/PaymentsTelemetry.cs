using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Payments.Observability;

/// <summary>Custom spans and business metrics of the Payments service.</summary>
public static class PaymentsTelemetry
{
    public const string Name = "PaymentLab.Payments";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    private static readonly double[] DurationBuckets =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600];

    // ---- payments
    public static readonly Counter<long> PaymentsCreated =
        Meter.CreateCounter<long>("payments.created", "{payment}", "Payments accepted by the API");
    public static readonly Counter<long> IdempotentReplays =
        Meter.CreateCounter<long>("payments.idempotent_replays", "{request}", "POST /payments answered from an existing payment (same Idempotency-Key)");
    public static readonly Counter<long> PaymentsFinished =
        Meter.CreateCounter<long>("payments.finished", "{payment}", "Payments that reached a final or parked state");
    public static readonly Histogram<double> PaymentDuration =
        Meter.CreateHistogram<double>("payment.processing.duration", "s", "Time from creation to a final state", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });

    // ---- saga
    public static readonly Histogram<double> StepDuration =
        Meter.CreateHistogram<double>("saga.step.duration", "s", "Duration of one saga step invocation", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
    public static readonly Counter<long> StepRetries =
        Meter.CreateCounter<long>("saga.step.retries", "{retry}", "Saga step retries scheduled");
    public static readonly Counter<long> Transitions =
        Meter.CreateCounter<long>("saga.transitions", "{transition}", "Saga state transitions");
    public static readonly Counter<long> SagaRuns =
        Meter.CreateCounter<long>("saga.runs", "{run}", "Saga processing runs by trigger and result");
    public static readonly Histogram<double> ClaimDuration =
        Meter.CreateHistogram<double>("saga.claim.duration", "s", "Duration of claiming due sagas", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
    public static readonly Histogram<long> ClaimBatchSize =
        Meter.CreateHistogram<long>("saga.claim.batch_size", "{saga}", "Sagas claimed per poll", tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [0, 1, 5, 10, 25, 50, 100, 250] });

    // ---- messaging
    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("outbox.published", "{message}", "Outbox messages published and confirmed");
    public static readonly Counter<long> OutboxPublishErrors =
        Meter.CreateCounter<long>("outbox.publish_errors", "{error}", "Outbox relay iterations that failed");
    public static readonly Histogram<double> OutboxLag =
        Meter.CreateHistogram<double>("outbox.publish.lag", "s", "Time from writing an outbox row to the broker confirm", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
    public static readonly Counter<long> BankResultsReceived =
        Meter.CreateCounter<long>("bank.results.received", "{message}", "Bank results consumed from RabbitMQ");

    // ---- gauges fed by PaymentsStatsCollector
    private static volatile Snapshot _snapshot = Snapshot.Empty;
    public static void Publish(Snapshot snapshot) => _snapshot = snapshot;

    static PaymentsTelemetry()
    {
        Meter.CreateObservableGauge("payments.by_state", () =>
            _snapshot.ActiveByState.Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("state", kv.Key))),
            "{payment}", "Payments in non-final saga states");
        Meter.CreateObservableGauge("saga.oldest_active.age", () => _snapshot.OldestActiveAgeSeconds,
            "s", "Age of the oldest payment that is not in a final state (excluding RequiresAttention)");
        Meter.CreateObservableGauge("outbox.pending", () => _snapshot.OutboxPending, "{message}", "Outbox rows not yet published");
        Meter.CreateObservableGauge("outbox.oldest_pending.age", () => _snapshot.OutboxOldestAgeSeconds,
            "s", "Age of the oldest unpublished outbox row");
        Meter.CreateObservableGauge("inbox.pending", () => _snapshot.InboxPending, "{message}", "Inbox rows not yet applied by the saga");
        Meter.CreateObservableGauge("saga.leased", () => _snapshot.Leased, "{saga}", "Sagas currently leased by a worker");
    }

    public sealed record Snapshot(
        IReadOnlyDictionary<string, long> ActiveByState,
        double OldestActiveAgeSeconds,
        long OutboxPending,
        double OutboxOldestAgeSeconds,
        long InboxPending,
        long Leased)
    {
        public static readonly Snapshot Empty = new(new Dictionary<string, long>(), 0, 0, 0, 0, 0);
    }
}
