using Microsoft.EntityFrameworkCore;
using Wallet.Data;
using Wallet.Services;

namespace Wallet.Api;

public sealed record CreateAccountRequest(string OwnerName, string? Currency, long InitialBalanceMinor);
public sealed record AccountResponse(Guid Id, string OwnerName, string Currency, long BalanceMinor, long ReservedMinor, long AvailableMinor, DateTimeOffset CreatedAt);
public sealed record LedgerEntryResponse(long Id, Guid? PaymentId, long AmountMinor, string Type, DateTimeOffset CreatedAt);

public sealed record ReserveRequest(Guid PaymentId, Guid AccountId, long AmountMinor, string Currency);
public sealed record CommitRequest(Guid? CreditAccountId);
public sealed record ReservationResponse(string Status, string? Reason = null);

public static class Endpoints
{
    public static void MapWalletEndpoints(this WebApplication app)
    {
        var accounts = app.MapGroup("/accounts").WithTags("Accounts");

        accounts.MapPost("/", async (CreateAccountRequest req, WalletService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.OwnerName)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["ownerName"] = ["required"] });
            if (req.InitialBalanceMinor < 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["initialBalanceMinor"] = ["must be >= 0"] });
            var a = await svc.CreateAccountAsync(req.OwnerName, (req.Currency ?? "EUR").ToUpperInvariant(), req.InitialBalanceMinor, ct);
            return Results.Created($"/accounts/{a.Id}", ToResponse(a));
        });

        accounts.MapGet("/{id:guid}", async (Guid id, WalletDbContext db, CancellationToken ct) =>
        {
            var a = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return a is null ? Results.NotFound() : Results.Ok(ToResponse(a));
        });

        accounts.MapGet("/{id:guid}/ledger", async (Guid id, int? limit, WalletDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var entries = await db.LedgerEntries.AsNoTracking()
                .Where(e => e.AccountId == id)
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Take(take)
                .Select(e => new LedgerEntryResponse(e.Id, e.PaymentId, e.AmountMinor, e.Type.ToString(), e.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(entries);
        });

        // Internal API used by the Payments saga. Not routed through the gateway.
        var reservations = app.MapGroup("/reservations").WithTags("Reservations (internal)");

        reservations.MapPost("/", async (ReserveRequest req, WalletService svc, CancellationToken ct) =>
        {
            if (req.AmountMinor <= 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["amountMinor"] = ["must be > 0"] });
            var result = await svc.ReserveAsync(req.PaymentId, req.AccountId, req.AmountMinor, req.Currency, ct);
            return result switch
            {
                ReserveResult.Reserved => Results.Ok(new ReservationResponse("Reserved")),
                ReserveResult.AlreadyCommitted => Results.Ok(new ReservationResponse("Committed")),
                ReserveResult.Conflict => Results.Conflict(new ReservationResponse("Conflict", "Reservation exists with different account or amount")),
                _ => Results.UnprocessableEntity(new ReservationResponse("Rejected", result.ToString())),
            };
        });

        reservations.MapPost("/{paymentId:guid}/commit", async (Guid paymentId, CommitRequest? req, WalletService svc, CancellationToken ct) =>
        {
            var result = await svc.CommitAsync(paymentId, req?.CreditAccountId, ct);
            return result switch
            {
                CommitResult.Committed => Results.Ok(new ReservationResponse("Committed")),
                CommitResult.NotFound => Results.NotFound(new ReservationResponse("NotFound")),
                CommitResult.Released => Results.Conflict(new ReservationResponse("Released")),
                _ => Results.UnprocessableEntity(new ReservationResponse("Rejected", result.ToString())),
            };
        });

        reservations.MapPost("/{paymentId:guid}/release", async (Guid paymentId, WalletService svc, CancellationToken ct) =>
        {
            var result = await svc.ReleaseAsync(paymentId, ct);
            return result == ReleaseResult.Released
                ? Results.Ok(new ReservationResponse("Released"))
                : Results.Conflict(new ReservationResponse("Committed"));
        });
    }

    private static AccountResponse ToResponse(Account a) =>
        new(a.Id, a.OwnerName, a.Currency, a.BalanceMinor, a.ReservedMinor, a.AvailableMinor, a.CreatedAt);
}
