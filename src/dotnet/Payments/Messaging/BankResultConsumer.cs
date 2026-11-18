using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Data;
using Payments.Saga;
using PaymentLab.Contracts;
using PaymentLab.ServiceDefaults;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Payments.Messaging;

/// <summary>
/// Consumes bank results. A message is only stored in the inbox here (deduplicated by message id)
/// and acked after the insert committed; the saga worker applies it. Malformed messages go to the DLQ.
/// </summary>
public sealed class BankResultConsumer(
    RabbitMqConnection rabbit,
    IServiceScopeFactory scopes,
    SagaSignals signals,
    TimeProvider clock,
    ILogger<BankResultConsumer> logger) : BackgroundService
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Startup.RetryAsync("Bank result consumer setup", async token =>
        {
            var connection = await rabbit.GetAsync(token);
            _channel = await connection.CreateChannelAsync(cancellationToken: token);
            await RabbitTopology.DeclareAsync(_channel, token);
            await _channel.BasicQosAsync(0, rabbit.Options.Prefetch, global: false, token);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnReceivedAsync;
            await _channel.BasicConsumeAsync(Topology.PaymentsResultsQueue, autoAck: false, consumer, token);
        }, logger, ct, maxAttempts: int.MaxValue, delay: TimeSpan.FromSeconds(3));

        await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        BankTransferResult? result;
        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(ea.Body.Span);
            result = JsonSerializer.Deserialize<BankTransferResult>(payload, Json.Options);
            if (result is null || result.PaymentId == Guid.Empty || result.MessageId == Guid.Empty)
                throw new JsonException("missing paymentId/messageId");
        }
        catch (Exception ex)
        {
            logger.LogError("Malformed bank result (delivery {Tag}), dead-lettering: {Error}", ea.DeliveryTag, ex.Message);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        try
        {
            var traceParent = ea.BasicProperties.Headers?.TryGetValue(MessageHeaders.TraceParent, out var tp) == true && tp is byte[] b
                ? Encoding.UTF8.GetString(b) : null;

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO inbox_messages (message_id, payment_id, type, payload, trace_parent, received_at)
                VALUES ({result.MessageId}, {result.PaymentId}, {MessageTypes.BankTransferResult}, {payload}::jsonb, {traceParent}, {clock.GetUtcNow()})
                ON CONFLICT (message_id) DO NOTHING
                """);

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            if (inserted == 1)
                signals.SagaReady(result.PaymentId);
            else
                logger.LogInformation("Duplicate bank result {MessageId} for payment {PaymentId}", result.MessageId, result.PaymentId);
        }
        catch (Exception ex)
        {
            logger.LogError("Storing bank result for {PaymentId} failed, requeueing: {Error}", result.PaymentId, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        base.Dispose();
    }
}
