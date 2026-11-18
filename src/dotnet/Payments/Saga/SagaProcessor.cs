using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Data;
using Payments.Observability;
using PaymentLab.Contracts;
using PaymentLab.ServiceDefaults;

namespace Payments.Saga;

/// <summary>In-process wake-up signals so new work does not have to wait for the next poll.</summary>
public sealed class SagaSignals
{
    private readonly Channel<Guid> _sagas = Channel.CreateBounded<Guid>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _outbox = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void SagaReady(Guid paymentId) => _sagas.Writer.TryWrite(paymentId);
    public ChannelReader<Guid> ReadySagas => _sagas.Reader;

    public void OutboxReady() => _outbox.Writer.TryWrite(true);
    public ChannelReader<bool> OutboxSignal => _outbox.Reader;
}

/// <summary>
/// Loads one saga under a lease, runs the state machine and saves everything (saga, payment,
/// history, outbox, inbox) in a single transaction. A lease (locked_by / locked_until) rather than
/// a held row lock is used, because steps make HTTP calls that must not run inside a DB transaction.
/// </summary>
public sealed class SagaProcessor(
    IServiceScopeFactory scopes,
    SagaSignals signals,
    IOptions<SagaOptions> options,
    TimeProvider clock,
    ILogger<SagaProcessor> logger)
{
    public string Owner { get; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SagaOptions _opt = options.Value;

    /// <summary>Claims up to BatchSize due sagas (or sagas with unprocessed inbox messages).</summary>
    public async Task<List<Guid>> ClaimDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        double lease = _opt.LeaseSeconds;
        var started = Stopwatch.GetTimestamp();
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE payment_sagas SET locked_by = {Owner}, locked_until = now() + make_interval(secs => {lease})
            WHERE payment_id IN (
                SELECT s.payment_id FROM payment_sagas s
                WHERE (s.next_attempt_at <= now()
                       OR s.payment_id IN (SELECT i.payment_id FROM inbox_messages i WHERE i.processed_at IS NULL))
                  AND (s.locked_until IS NULL OR s.locked_until < now())
                ORDER BY s.next_attempt_at NULLS LAST
                LIMIT {_opt.BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING payment_id AS "Value"
            """).ToListAsync(ct);
        PaymentsTelemetry.ClaimDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        PaymentsTelemetry.ClaimBatchSize.Record(claimed.Count);
        return claimed;
    }

    /// <summary>Tries to claim one specific saga (after a nudge). Returns false if someone else holds it.</summary>
    public async Task<bool> TryClaimAsync(Guid paymentId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        double lease = _opt.LeaseSeconds;
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE payment_sagas SET locked_by = {Owner}, locked_until = now() + make_interval(secs => {lease})
            WHERE payment_id = {paymentId} AND (locked_until IS NULL OR locked_until < now())
            """, ct);
        return rows == 1;
    }

    public async Task ProcessClaimedAsync(Guid paymentId, CancellationToken ct, string trigger = "poll")
    {
        var sentMessages = false;
        var result = "ok";
        Activity? activity = null;
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["payment_id"] = paymentId });
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<PaymentsDbContext>();
            var machine = sp.GetRequiredService<SagaStateMachine>();

            var saga = await db.Sagas.SingleAsync(s => s.PaymentId == paymentId, ct);
            if (saga.LockedBy != Owner)
            {
                result = "lease_lost";
                return;
            }
            var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, ct);
            var inbox = await db.Inbox
                .Where(i => i.PaymentId == paymentId && i.ProcessedAt == null)
                .OrderBy(i => i.ReceivedAt)
                .ToListAsync(ct);

            // Every run of the saga continues the trace of the original POST /payments request, so one
            // payment is one trace. Incoming bank results are attached as links to the message's own trace context.
            var links = inbox.Where(i => i.TraceParent is not null)
                .Select(i => new ActivityLink(Telemetry.ParentFrom(i.TraceParent)))
                .Where(l => l.Context != default)
                .ToList();
            activity = PaymentsTelemetry.Source.StartActivity(ActivityKind.Internal, Telemetry.ParentFrom(saga.TraceParent),
                links: links, name: $"saga {saga.State}");
            activity?.SetTag("payment.id", paymentId);
            activity?.SetTag("payment.type", payment.Type.ToString());
            activity?.SetTag("saga.trigger", trigger);
            activity?.SetTag("saga.state.before", saga.State.ToString());
            activity?.SetTag("saga.inbox_messages", inbox.Count);

            var initialState = saga.State;
            var ctx = new SagaContext(payment, saga, db, clock, logger);
            var messages = inbox
                .Where(i => i.Type == MessageTypes.BankTransferResult)
                .Select(i => JsonSerializer.Deserialize<BankTransferResult>(i.Payload, Json.Options)!)
                .ToList();
            foreach (var i in inbox) i.ProcessedAt = ctx.Now;

            await machine.RunAsync(ctx, messages, ct);

            saga.LockedBy = null;
            saga.LockedUntil = null;
            await db.SaveChangesAsync(ct);
            sentMessages = ctx.HasOutgoingMessages;

            activity?.SetTag("saga.state.after", saga.State.ToString());
            RecordOutcome(payment, initialState, saga.State, ctx.Now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            result = "error";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "Processing saga {PaymentId} failed", paymentId);
            await ReleaseLeaseAsync(paymentId);
        }
        finally
        {
            activity?.Dispose();
            PaymentsTelemetry.SagaRuns.Add(1, new("trigger", trigger), new("result", result));
        }

        if (sentMessages) signals.OutboxReady();
    }

    private static void RecordOutcome(Payment payment, SagaState before, SagaState after, DateTimeOffset now)
    {
        if (before == after) return;
        if (!PaymentSagaDefinition.IsFinal(after) && after != SagaState.RequiresAttention) return;

        var type = new KeyValuePair<string, object?>("type", payment.Type.ToString());
        var state = new KeyValuePair<string, object?>("state", after.ToString());
        PaymentsTelemetry.PaymentsFinished.Add(1, type, state);
        if (PaymentSagaDefinition.IsFinal(after))
            PaymentsTelemetry.PaymentDuration.Record((now - payment.CreatedAt).TotalSeconds, type, state);
    }

    private async Task ReleaseLeaseAsync(Guid paymentId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE payment_sagas SET locked_by = NULL, locked_until = NULL WHERE payment_id = {paymentId} AND locked_by = {Owner}");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not release lease for {PaymentId}: {Error}", paymentId, ex.Message);
        }
    }
}
