using PaymentLab.Contracts;
using RabbitMQ.Client;

namespace Payments.Messaging;

/// <summary>Declares the exchanges and the queues owned by the Payments service. Declarations are idempotent.</summary>
public static class RabbitTopology
{
    public static async Task DeclareAsync(IChannel ch, CancellationToken ct)
    {
        await ch.ExchangeDeclareAsync(Topology.CommandsExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await ch.ExchangeDeclareAsync(Topology.EventsExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await ch.ExchangeDeclareAsync(Topology.RetryExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await ch.ExchangeDeclareAsync(Topology.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        const string dlq = Topology.PaymentsResultsQueue + ".dlq";
        await ch.QueueDeclareAsync(dlq, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        await ch.QueueBindAsync(dlq, Topology.DeadLetterExchange, Topology.PaymentsResultsQueue, cancellationToken: ct);

        await ch.QueueDeclareAsync(Topology.PaymentsResultsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = Topology.PaymentsResultsQueue,
            }, cancellationToken: ct);
        await ch.QueueBindAsync(Topology.PaymentsResultsQueue, Topology.EventsExchange, Topology.TransferResultKey, cancellationToken: ct);
    }
}
