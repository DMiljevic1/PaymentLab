using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Payments.Data;
using Payments.Observability;
using Payments.Saga;
using PaymentLab.Contracts;

namespace Payments.Api;

public sealed record CreatePaymentRequest(
    PaymentType Type,
    Guid SourceAccountId,
    Guid? DestinationAccountId,
    string? CreditorIban,
    long AmountMinor,
    string? Currency,
    string? Reference);

public sealed record PaymentResponse(
    Guid Id,
    string Type,
    string Status,
    string SagaState,
    Guid SourceAccountId,
    Guid? DestinationAccountId,
    string? CreditorIban,
    long AmountMinor,
    string Currency,
    string? Reference,
    string? FailureReason,
    string? BankReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record HistoryResponse(string? FromState, string ToState, string? Step, string? Detail, DateTimeOffset OccurredAt);

public static class PaymentEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static void MapPaymentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/payments").WithTags("Payments");

        g.MapPost("/", CreateAsync);

        g.MapGet("/{id:guid}", async (Guid id, PaymentsDbContext db, CancellationToken ct) =>
        {
            var row = await Query(db).SingleOrDefaultAsync(x => x.Payment.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(ToResponse(row.Payment, row.State));
        });

        g.MapGet("/", async (Guid? accountId, PaymentStatus? status, int? page, int? pageSize, PaymentsDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var skip = (Math.Max(page ?? 1, 1) - 1) * size;
            var q = Query(db);
            if (accountId is not null) q = q.Where(x => x.Payment.SourceAccountId == accountId);
            if (status is not null) q = q.Where(x => x.Payment.Status == status);
            var rows = await q.OrderByDescending(x => x.Payment.CreatedAt).Skip(skip).Take(size).ToListAsync(ct);
            return Results.Ok(rows.Select(r => ToResponse(r.Payment, r.State)));
        });

        g.MapGet("/{id:guid}/history", async (Guid id, PaymentsDbContext db, CancellationToken ct) =>
        {
            var rows = await db.History.AsNoTracking().Where(h => h.PaymentId == id).OrderBy(h => h.Id)
                .Select(h => new HistoryResponse(h.FromState, h.ToState, h.Step, h.Detail, h.OccurredAt))
                .ToListAsync(ct);
            return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows);
        });
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest req,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        PaymentsDbContext db,
        SagaSignals signals,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            return Results.Problem($"Header {IdempotencyHeader} is required (max 100 chars)", statusCode: 400);

        var errors = Validate(req);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var hash = Hash(req);
        var existing = await Query(db).SingleOrDefaultAsync(x => x.Payment.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return Replay(existing.Payment, existing.State, hash);

        var now = clock.GetUtcNow();
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            Type = req.Type,
            SourceAccountId = req.SourceAccountId,
            DestinationAccountId = req.Type == PaymentType.Internal ? req.DestinationAccountId : null,
            CreditorIban = req.Type == PaymentType.External ? req.CreditorIban!.Replace(" ", "").ToUpperInvariant() : null,
            AmountMinor = req.AmountMinor,
            Currency = (req.Currency ?? "EUR").ToUpperInvariant(),
            Reference = req.Reference,
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var saga = new PaymentSaga
        {
            PaymentId = payment.Id,
            State = SagaState.Created,
            NextAttemptAt = now,
            TraceParent = Activity.Current?.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Payments.Add(payment);
        db.Sagas.Add(saga);
        db.History.Add(new PaymentHistory { PaymentId = payment.Id, ToState = nameof(SagaState.Created), OccurredAt = now });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Same key submitted concurrently (client retry racing the original request).
            db.ChangeTracker.Clear();
            var winner = await Query(db).SingleAsync(x => x.Payment.IdempotencyKey == idempotencyKey, ct);
            return Replay(winner.Payment, winner.State, hash);
        }

        PaymentsTelemetry.PaymentsCreated.Add(1, new KeyValuePair<string, object?>("type", payment.Type.ToString()));
        Activity.Current?.SetTag("payment.id", payment.Id);
        Activity.Current?.SetTag("payment.type", payment.Type.ToString());
        signals.SagaReady(payment.Id);
        return Results.Accepted($"/payments/{payment.Id}", ToResponse(payment, saga.State));
    }

    private static IResult Replay(Payment payment, SagaState state, string hash)
    {
        var same = payment.RequestHash == hash;
        PaymentsTelemetry.IdempotentReplays.Add(1, new KeyValuePair<string, object?>("result", same ? "replayed" : "mismatch"));
        Activity.Current?.SetTag("payment.id", payment.Id);
        Activity.Current?.SetTag("payment.idempotent_replay", true);
        return same
            ? Results.Ok(ToResponse(payment, state))
            : Results.Problem($"{IdempotencyHeader} was already used with a different request", statusCode: 422);
    }

    private sealed class Row
    {
        public required Payment Payment { get; init; }
        public required SagaState State { get; init; }
    }

    private static IQueryable<Row> Query(PaymentsDbContext db) =>
        from p in db.Payments.AsNoTracking()
        join s in db.Sagas.AsNoTracking() on p.Id equals s.PaymentId
        select new Row { Payment = p, State = s.State };

    private static Dictionary<string, string[]> Validate(CreatePaymentRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (r.AmountMinor <= 0) e["amountMinor"] = ["must be > 0"];
        if (r.SourceAccountId == Guid.Empty) e["sourceAccountId"] = ["required"];
        if (r.Currency is { Length: not 3 }) e["currency"] = ["must be a 3-letter ISO code"];
        if (r.Reference is { Length: > 140 }) e["reference"] = ["max 140 chars"];
        switch (r.Type)
        {
            case PaymentType.External when string.IsNullOrWhiteSpace(r.CreditorIban) || r.CreditorIban.Replace(" ", "").Length is < 15 or > 34:
                e["creditorIban"] = ["a valid IBAN is required for external payments"];
                break;
            case PaymentType.Internal when r.DestinationAccountId is null || r.DestinationAccountId == Guid.Empty:
                e["destinationAccountId"] = ["required for internal payments"];
                break;
            case PaymentType.Internal when r.DestinationAccountId == r.SourceAccountId:
                e["destinationAccountId"] = ["must differ from the source account"];
                break;
        }
        return e;
    }

    private static string Hash(CreatePaymentRequest r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, Json.Options))));

    private static PaymentResponse ToResponse(Payment p, SagaState state) =>
        new(p.Id, p.Type.ToString(), p.Status.ToString(), state.ToString(), p.SourceAccountId, p.DestinationAccountId,
            p.CreditorIban, p.AmountMinor, p.Currency, p.Reference, p.FailureReason, p.BankReference,
            p.CreatedAt, p.UpdatedAt, p.CompletedAt);
}
