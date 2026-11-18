using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Data;

namespace Wallet.Services;

public enum ReserveResult { Reserved, AlreadyCommitted, InsufficientFunds, AccountNotFound, CurrencyMismatch, Released, Conflict }
public enum CommitResult { Committed, NotFound, Released, CreditAccountNotFound }
public enum ReleaseResult { Released, AlreadyCommitted }

/// <summary>
/// Reserve / commit / release. Every operation is idempotent per payment id, because the saga
/// in the Payments service retries them after timeouts without knowing whether the first call landed.
/// </summary>
public sealed class WalletService(WalletDbContext db, TimeProvider clock, ILogger<WalletService> logger)
{
    public async Task<Account> CreateAccountAsync(string ownerName, string currency, long initialBalanceMinor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            OwnerName = ownerName,
            Currency = currency,
            BalanceMinor = initialBalanceMinor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        if (initialBalanceMinor != 0)
        {
            db.LedgerEntries.Add(new LedgerEntry
            {
                AccountId = account.Id, AmountMinor = initialBalanceMinor, Type = LedgerEntryType.Deposit, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<ReserveResult> ReserveAsync(Guid paymentId, Guid accountId, long amountMinor, string currency, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.Reservations.SingleOrDefaultAsync(r => r.PaymentId == paymentId, ct);
        if (existing is not null)
            return MapExisting(existing, accountId, amountMinor);

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return ReserveResult.AccountNotFound;
        if (!string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase)) return ReserveResult.CurrencyMismatch;
        if (account.AvailableMinor < amountMinor) return ReserveResult.InsufficientFunds;

        var now = clock.GetUtcNow();
        db.Reservations.Add(new Reservation
        {
            PaymentId = paymentId,
            AccountId = accountId,
            AmountMinor = amountMinor,
            Currency = account.Currency,
            Status = ReservationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            // Increment in SQL rather than read-modify-write, so concurrent reservations never overwrite each other.
            await db.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.ReservedMinor, a => a.ReservedMinor + amountMinor)
                .SetProperty(a => a.UpdatedAt, now), ct);
            await tx.CommitAsync(ct);
            return ReserveResult.Reserved;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent retry of the same reserve won the race.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await db.Reservations.AsNoTracking().SingleAsync(r => r.PaymentId == paymentId, ct);
            return MapExisting(winner, accountId, amountMinor);
        }
    }

    private static ReserveResult MapExisting(Reservation r, Guid accountId, long amountMinor)
    {
        if (r.Status == ReservationStatus.Released) return ReserveResult.Released;
        if (r.AccountId != accountId || r.AmountMinor != amountMinor) return ReserveResult.Conflict;
        return r.Status == ReservationStatus.Committed ? ReserveResult.AlreadyCommitted : ReserveResult.Reserved;
    }

    public async Task<CommitResult> CommitAsync(Guid paymentId, Guid? creditAccountId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var reservation = await db.Reservations
            .FromSql($"SELECT * FROM reservations WHERE payment_id = {paymentId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (reservation is null) return CommitResult.NotFound;
        if (reservation.Status == ReservationStatus.Committed) return CommitResult.Committed;
        if (reservation.Status == ReservationStatus.Released) return CommitResult.Released;

        // Lock accounts in a stable order to avoid deadlocks between concurrent internal transfers.
        var ids = new List<Guid> { reservation.AccountId };
        if (creditAccountId is { } c && c != reservation.AccountId) ids.Add(c);
        var idArray = ids.ToArray();
        var accounts = await db.Accounts
            .FromSql($"SELECT *, xmin FROM accounts WHERE id = ANY({idArray}) ORDER BY id FOR UPDATE")
            .ToDictionaryAsync(a => a.Id, ct);

        var source = accounts[reservation.AccountId];
        Account? credit = null;
        if (creditAccountId is { } cid && !accounts.TryGetValue(cid, out credit))
            return CommitResult.CreditAccountNotFound;

        var now = clock.GetUtcNow();
        source.ReservedMinor -= reservation.AmountMinor;
        source.BalanceMinor -= reservation.AmountMinor;
        source.UpdatedAt = now;
        db.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = source.Id, PaymentId = paymentId, AmountMinor = -reservation.AmountMinor,
            Type = LedgerEntryType.Debit, CreatedAt = now,
        });

        if (credit is not null)
        {
            credit.BalanceMinor += reservation.AmountMinor;
            credit.UpdatedAt = now;
            db.LedgerEntries.Add(new LedgerEntry
            {
                AccountId = credit.Id, PaymentId = paymentId, AmountMinor = reservation.AmountMinor,
                Type = LedgerEntryType.Credit, CreatedAt = now,
            });
        }

        reservation.Status = ReservationStatus.Committed;
        reservation.CreditAccountId = creditAccountId;
        reservation.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CommitResult.Committed;
    }

    public async Task<ReleaseResult> ReleaseAsync(Guid paymentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();

        var reservation = await db.Reservations
            .FromSql($"SELECT * FROM reservations WHERE payment_id = {paymentId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

        if (reservation is null)
        {
            // Release arrived before (or instead of) the reserve: leave a tombstone so a late reserve is refused.
            db.Reservations.Add(new Reservation
            {
                PaymentId = paymentId, AccountId = Guid.Empty, AmountMinor = 0, Status = ReservationStatus.Released,
                CreatedAt = now, UpdatedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return await ReleaseAsync(paymentId, ct);
            }
            logger.LogInformation("Release for unknown reservation {PaymentId}, tombstone written", paymentId);
            return ReleaseResult.Released;
        }

        switch (reservation.Status)
        {
            case ReservationStatus.Released:
                return ReleaseResult.Released;
            case ReservationStatus.Committed:
                return ReleaseResult.AlreadyCommitted;
        }

        var account = await db.Accounts
            .FromSql($"SELECT *, xmin FROM accounts WHERE id = {reservation.AccountId} FOR UPDATE")
            .SingleAsync(ct);
        account.ReservedMinor -= reservation.AmountMinor;
        account.UpdatedAt = now;
        reservation.Status = ReservationStatus.Released;
        reservation.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ReleaseResult.Released;
    }
}
