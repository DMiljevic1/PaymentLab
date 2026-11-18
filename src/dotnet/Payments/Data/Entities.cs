using Payments.Saga;

namespace Payments.Data;

public enum PaymentType { External, Internal }

/// <summary>Client-visible status; a projection of the saga state (see PaymentSagaDefinition.ToPaymentStatus).</summary>
public enum PaymentStatus { Pending, Completed, Failed, Rejected, UnderReview }

public sealed class Payment
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public PaymentType Type { get; set; }
    public Guid SourceAccountId { get; set; }
    public Guid? DestinationAccountId { get; set; }
    public string? CreditorIban { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Reference { get; set; }
    public PaymentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? BankReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Persisted saga instance. One row per payment.</summary>
public sealed class PaymentSaga
{
    public Guid PaymentId { get; set; }
    public SagaState State { get; set; }
    /// <summary>Step currently executing (forward) or being compensated (backward).</summary>
    public string? CurrentStep { get; set; }
    /// <summary>Steps whose effects are in place, in execution order. Compensation pops from the end.</summary>
    public List<string> CompletedSteps { get; set; } = [];
    public int Attempt { get; set; }
    /// <summary>When the worker should look at this saga next. NULL = nothing scheduled (final or parked).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>Deadline of an asynchronous step that is waiting for a message.</summary>
    public DateTimeOffset? WaitingUntil { get; set; }
    public string? LastError { get; set; }
    /// <summary>State to go back to when an operator retries a saga in RequiresAttention.</summary>
    public SagaState? ResumeState { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>W3C traceparent of the request that created the payment, so later steps join the same trace.</summary>
    public string? TraceParent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }
}

/// <summary>Append-only log of saga state transitions.</summary>
public sealed class PaymentHistory
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public string? FromState { get; set; }
    public string ToState { get; set; } = "";
    public string? Step { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Transactional outbox: messages are written in the same DB transaction as the saga change and published later.</summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid PaymentId { get; set; }
    public string Exchange { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? TraceParent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int PublishAttempts { get; set; }
}

/// <summary>Inbox: received messages are stored first (dedup by message id) and applied to the saga by the worker.</summary>
public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? TraceParent { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
