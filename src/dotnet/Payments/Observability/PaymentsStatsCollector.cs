using Microsoft.EntityFrameworkCore;
using Payments.Data;
using Payments.Saga;

namespace Payments.Observability;

/// <summary>
/// Periodically reads backlog numbers from the database (sagas per state, outbox / inbox backlog) and
/// exposes them as gauges. Only non-final sagas are counted, which the index on state keeps cheap.
/// </summary>
public sealed class PaymentsStatsCollector(IServiceScopeFactory scopes, ILogger<PaymentsStatsCollector> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly string ActiveStates = string.Join(",",
        Enum.GetValues<SagaState>().Where(s => !PaymentSagaDefinition.IsFinal(s)).Select(s => $"'{s}'"));

    private static readonly string Sql = $"""
        SELECT state, count(*) FROM payment_sagas WHERE state IN ({ActiveStates}) GROUP BY state;
        SELECT coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8
          FROM payment_sagas WHERE state IN ({ActiveStates}) AND state <> 'RequiresAttention';
        SELECT count(*) FROM payment_sagas WHERE state IN ({ActiveStates}) AND locked_until > now();
        SELECT count(*), coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8
          FROM outbox_messages WHERE published_at IS NULL;
        SELECT count(*) FROM inbox_messages WHERE processed_at IS NULL;
        """;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                PaymentsTelemetry.Publish(await CollectAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Collecting payment stats failed: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task<PaymentsTelemetry.Snapshot> CollectAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Sql;
            cmd.CommandTimeout = 5;
            await using var r = await cmd.ExecuteReaderAsync(ct);

            var byState = Enum.GetValues<SagaState>().Where(s => !PaymentSagaDefinition.IsFinal(s))
                .ToDictionary(s => s.ToString(), _ => 0L);
            while (await r.ReadAsync(ct)) byState[r.GetString(0)] = r.GetInt64(1);

            await r.NextResultAsync(ct); await r.ReadAsync(ct);
            var oldestActive = r.GetDouble(0);
            await r.NextResultAsync(ct); await r.ReadAsync(ct);
            var leased = r.GetInt64(0);
            await r.NextResultAsync(ct); await r.ReadAsync(ct);
            var outboxPending = r.GetInt64(0);
            var outboxAge = r.GetDouble(1);
            await r.NextResultAsync(ct); await r.ReadAsync(ct);
            var inboxPending = r.GetInt64(0);

            return new PaymentsTelemetry.Snapshot(byState, oldestActive, outboxPending, outboxAge, inboxPending, leased);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
