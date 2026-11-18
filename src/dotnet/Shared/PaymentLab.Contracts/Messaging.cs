namespace PaymentLab.Contracts;

/// <summary>RabbitMQ topology shared by Payments (.NET) and Connector (Go). Keep in sync with src/go/connector/internal/amqpx/topology.go.</summary>
public static class Topology
{
    public const string CommandsExchange = "payments.commands";   // direct
    public const string EventsExchange = "payments.events";       // topic
    public const string RetryExchange = "payments.retry";         // direct
    public const string DeadLetterExchange = "payments.dlx";      // direct

    public const string SubmitTransferKey = "bank.transfer.submit";
    public const string QueryStatusKey = "bank.transfer.status";
    public const string TransferResultKey = "bank.transfer.result";

    public const string ConnectorTransfersQueue = "connector.bank-transfers";
    public const string ConnectorStatusQueue = "connector.bank-status";
    public const string PaymentsResultsQueue = "payments.bank-results";
}

public static class MessageTypes
{
    public const string SubmitBankTransfer = "SubmitBankTransfer";
    public const string QueryBankTransferStatus = "QueryBankTransferStatus";
    public const string BankTransferResult = "BankTransferResult";
}

public static class MessageHeaders
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string Attempt = "x-attempt";
}

public sealed record SubmitBankTransfer(
    Guid MessageId,
    Guid PaymentId,
    int Attempt,
    long AmountMinor,
    string Currency,
    Guid DebtorAccountId,
    string CreditorIban,
    string? Reference,
    DateTimeOffset RequestedAt);

public sealed record QueryBankTransferStatus(
    Guid MessageId,
    Guid PaymentId,
    int Inquiry,
    DateTimeOffset RequestedAt);

public static class BankOutcomes
{
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string NotFound = "NotFound";   // bank has no record of the transfer (answer to a status query)
    public const string Unknown = "Unknown";     // connector could not determine the outcome
}

public sealed record BankTransferResult(
    Guid MessageId,
    Guid PaymentId,
    string Outcome,
    string? BankReference,
    string? ReasonCode,
    DateTimeOffset CompletedAt);
