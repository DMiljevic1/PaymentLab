using Payments.Data;
using PaymentLab.Contracts;

namespace Payments.Saga;

/// <summary>
/// How a step behaves on failure (classic saga terminology):
/// Compensatable – can be undone; Pivot – point of no return, once it succeeds the saga must complete;
/// Retriable – runs after the pivot, never compensated, retried until it succeeds (or escalated).
/// </summary>
public enum StepKind { Compensatable, Pivot, Retriable }

/// <summary>What a step tells the state machine after it ran.</summary>
public abstract record StepOutcome
{
    private StepOutcome() { }

    /// <summary>The step's effect is in place; move to the next step.</summary>
    public sealed record Completed : StepOutcome;
    /// <summary>Asynchronous step: a message was sent, wait for a reply until the timeout.</summary>
    public sealed record Wait(TimeSpan Timeout, SagaState? State = null) : StepOutcome;
    /// <summary>Transient failure; try again later with backoff (bounded by the step's MaxAttempts).</summary>
    public sealed record Retry(string Error) : StepOutcome;
    /// <summary>Business failure (e.g. insufficient funds, bank rejected); start compensation.</summary>
    public sealed record Reject(string Reason) : StepOutcome;
    /// <summary>The saga cannot decide on its own; park it for an operator.</summary>
    public sealed record Escalate(string Reason) : StepOutcome;
    /// <summary>The message was not relevant for the current state (duplicate, late reply).</summary>
    public sealed record Ignore(string Why) : StepOutcome;

    public static readonly StepOutcome Done = new Completed();
}

public interface ISagaStep
{
    string Name { get; }
    /// <summary>Saga state while this step is in progress.</summary>
    SagaState State { get; }
    StepKind Kind { get; }
    int MaxAttempts { get; }

    bool AppliesTo(Payment payment);

    Task<StepOutcome> ExecuteAsync(SagaContext ctx, CancellationToken ct);

    /// <summary>Called when an asynchronous step's deadline passed without a reply.</summary>
    Task<StepOutcome> OnTimeoutAsync(SagaContext ctx, CancellationToken ct) =>
        Task.FromResult<StepOutcome>(new StepOutcome.Retry("step timed out"));

    /// <summary>Called when a reply message for this saga arrives while the step is waiting.</summary>
    Task<StepOutcome> OnMessageAsync(SagaContext ctx, BankTransferResult message, CancellationToken ct) =>
        Task.FromResult<StepOutcome>(new StepOutcome.Ignore($"{Name} does not expect messages"));

    /// <summary>Undo the step's effect. Must be idempotent.</summary>
    Task<StepOutcome> CompensateAsync(SagaContext ctx, CancellationToken ct) => Task.FromResult(StepOutcome.Done);
}

/// <summary>Everything a step may touch while it runs. Changes are saved by SagaProcessor in one transaction.</summary>
public sealed class SagaContext(Payment payment, PaymentSaga saga, PaymentsDbContext db, TimeProvider clock, ILogger logger)
{
    public Payment Payment => payment;
    public PaymentSaga Saga => saga;
    public ILogger Logger => logger;
    public DateTimeOffset Now => clock.GetUtcNow();
    public bool HasOutgoingMessages { get; private set; }

    /// <summary>Queues a message in the outbox (same transaction as the saga state change).</summary>
    public void Send<T>(string routingKey, string type, Guid messageId, T message)
    {
        db.Outbox.Add(new OutboxMessage
        {
            MessageId = messageId,
            PaymentId = payment.Id,
            Exchange = Topology.CommandsExchange,
            RoutingKey = routingKey,
            Type = type,
            Payload = System.Text.Json.JsonSerializer.Serialize(message, Json.Options),
            TraceParent = System.Diagnostics.Activity.Current?.Id ?? saga.TraceParent,
            CreatedAt = Now,
        });
        HasOutgoingMessages = true;
    }

    public void RecordTransition(SagaState? from, SagaState to, string? detail)
    {
        db.History.Add(new PaymentHistory
        {
            PaymentId = payment.Id,
            FromState = from?.ToString(),
            ToState = to.ToString(),
            Step = saga.CurrentStep,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            OccurredAt = Now,
        });
    }
}

public sealed class SagaOptions
{
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 50;
    public int MaxParallelism { get; set; } = 8;
    public int LeaseSeconds { get; set; } = 60;
    public int BankTimeoutSeconds { get; set; } = 30;
    public int BankInquiryTimeoutSeconds { get; set; } = 10;
    public int MaxBankInquiries { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 30;
}
