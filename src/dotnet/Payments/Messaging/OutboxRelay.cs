using System.Text;
using Microsoft.EntityFrameworkCore;
using Payments.Data;
using Payments.Saga;
using PaymentLab.Contracts;
using PaymentLab.ServiceDefaults;
using RabbitMQ.Client;

namespace Payments.Messaging;

/// <summary>
/// Publishes outbox rows to RabbitMQ with publisher confirms. A row is marked published only after the
/// broker confirmed it, so a crash in between leads to a duplicate publish (consumers deduplicate), never a loss.
/// </summary>
public sealed class OutboxRelay(
    RabbitMqConnection rabbit,
    IServiceScopeFactory scopes,
    SagaSignals signals,
    TimeProvider clock,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        IChannel? channel = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                channel ??= await CreateChannelAsync(ct);
                var published = await PublishBatchAsync(channel, ct);
                await CleanupAsync(ct);
                if (published < BatchSize)
                    await WaitForSignalAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Outbox relay error: {Error}", ex.Message);
                if (channel is { IsClosed: true })
                {
                    channel.Dispose();
                    channel = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        channel?.Dispose();
    }

    private async Task<IChannel> CreateChannelAsync(CancellationToken ct)
    {
        var connection = await rabbit.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await RabbitTopology.DeclareAsync(channel, ct);
        return channel;
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Outbox
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);
        if (batch.Count == 0) return 0;

        foreach (var msg in batch)
        {
            msg.PublishAttempts++;
            var props = new BasicProperties
            {
                MessageId = msg.MessageId.ToString(),
                Type = msg.Type,
                CorrelationId = msg.PaymentId.ToString(),
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(clock.GetUtcNow().ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?> { [MessageHeaders.Attempt] = 1 },
            };
            if (msg.TraceParent is not null) props.Headers[MessageHeaders.TraceParent] = msg.TraceParent;

            // mandatory: an unroutable message is returned and the publish fails instead of being silently dropped
            await channel.BasicPublishAsync(msg.Exchange, msg.RoutingKey, mandatory: true, props,
                Encoding.UTF8.GetBytes(msg.Payload), ct);
            msg.PublishedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogDebug("Published {Count} outbox messages", batch.Count);
        return batch.Count;
    }

    private async Task WaitForSignalAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleDelay);
        try { await signals.OutboxSignal.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now - _lastCleanup < TimeSpan.FromMinutes(1)) return;
        _lastCleanup = now;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var cutoff = now - Retention;
        var outbox = await db.Outbox.Where(m => m.PublishedAt != null && m.PublishedAt < cutoff).ExecuteDeleteAsync(ct);
        var inbox = await db.Inbox.Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff).ExecuteDeleteAsync(ct);
        if (outbox + inbox > 0)
            logger.LogInformation("Cleaned up {Outbox} outbox and {Inbox} inbox messages", outbox, inbox);
    }
}
