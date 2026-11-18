using Microsoft.EntityFrameworkCore;
using Payments.Data;
using Payments.Saga;

namespace Payments.Api;

public sealed record SagaResponse(
    Guid PaymentId, string State, string? CurrentStep, IReadOnlyList<string> CompletedSteps, int Attempt,
    DateTimeOffset? NextAttemptAt, DateTimeOffset? WaitingUntil, string? LastError, string? ResumeState,
    string? LockedBy, DateTimeOffset? LockedUntil, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Operator endpoints for sagas that could not finish on their own. Not routed through the gateway.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/admin/sagas").WithTags("Admin");

        g.MapGet("/", async (SagaState? state, int? limit, PaymentsDbContext db, CancellationToken ct) =>
        {
            var q = db.Sagas.AsNoTracking();
            if (state is not null) q = q.Where(s => s.State == state);
            var rows = await q.OrderBy(s => s.UpdatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
            return Results.Ok(rows.Select(ToResponse));
        });

        g.MapGet("/summary", async (PaymentsDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Sagas.AsNoTracking().GroupBy(s => s.State)
                .Select(x => new { State = x.Key.ToString(), Count = x.Count() }).ToListAsync(ct)));

        g.MapGet("/{id:guid}", async (Guid id, PaymentsDbContext db, CancellationToken ct) =>
        {
            var s = await db.Sagas.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentId == id, ct);
            return s is null ? Results.NotFound() : Results.Ok(ToResponse(s));
        });

        g.MapPost("/{id:guid}/retry", (Guid id, SagaProcessor processor, IServiceScopeFactory scopes, SagaSignals signals, TimeProvider clock, ILogger<SagaWorker> logger, CancellationToken ct) =>
            OperatorAction(id, (m, c) => m.OperatorRetry(c), processor, scopes, signals, clock, logger, ct));

        g.MapPost("/{id:guid}/abort", (Guid id, SagaProcessor processor, IServiceScopeFactory scopes, SagaSignals signals, TimeProvider clock, ILogger<SagaWorker> logger, CancellationToken ct) =>
            OperatorAction(id, (m, c) => m.OperatorAbort(c), processor, scopes, signals, clock, logger, ct));
    }

    private static async Task<IResult> OperatorAction(Guid id, Action<SagaStateMachine, SagaContext> action,
        SagaProcessor processor, IServiceScopeFactory scopes, SagaSignals signals, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        if (!await processor.TryClaimAsync(id, ct))
            return Results.Conflict(new { error = "saga not found or currently being processed" });

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var machine = scope.ServiceProvider.GetRequiredService<SagaStateMachine>();
        var saga = await db.Sagas.SingleAsync(s => s.PaymentId == id, ct);
        var payment = await db.Payments.SingleAsync(p => p.Id == id, ct);
        var ctx = new SagaContext(payment, saga, db, clock, logger);

        IResult result;
        try
        {
            action(machine, ctx);
            result = Results.Ok(ToResponse(saga));
        }
        catch (InvalidOperationException ex)
        {
            db.ChangeTracker.Clear();
            saga = await db.Sagas.SingleAsync(s => s.PaymentId == id, ct);
            result = Results.Conflict(new { error = ex.Message });
        }

        saga.LockedBy = null;
        saga.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        signals.SagaReady(id);
        return result;
    }

    private static SagaResponse ToResponse(PaymentSaga s) =>
        new(s.PaymentId, s.State.ToString(), s.CurrentStep, s.CompletedSteps, s.Attempt, s.NextAttemptAt, s.WaitingUntil,
            s.LastError, s.ResumeState?.ToString(), s.LockedBy, s.LockedUntil, s.CreatedAt, s.UpdatedAt);
}
