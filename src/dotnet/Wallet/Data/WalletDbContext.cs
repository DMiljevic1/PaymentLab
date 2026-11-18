using Microsoft.EntityFrameworkCore;

namespace Wallet.Data;

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OwnerName).HasMaxLength(200);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Version).IsRowVersion(); // maps to Postgres xmin
            e.ToTable(t => t.HasCheckConstraint("ck_accounts_reserved_non_negative", "reserved_minor >= 0"));
        });

        b.Entity<Reservation>(e =>
        {
            e.HasKey(x => x.PaymentId);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.AccountId, x.Status });
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.AccountId, x.CreatedAt });
            e.HasIndex(x => x.PaymentId);
        });
    }
}

public sealed class Account
{
    public Guid Id { get; set; }
    public string OwnerName { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    /// <summary>Booked balance in minor units. Always equals SUM(ledger_entries.amount_minor).</summary>
    public long BalanceMinor { get; set; }
    /// <summary>Sum of active reservations. Available = Balance - Reserved.</summary>
    public long ReservedMinor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }

    public long AvailableMinor => BalanceMinor - ReservedMinor;
}

public enum ReservationStatus { Active, Committed, Released }

public sealed class Reservation
{
    /// <summary>One reservation per payment; the payment id is the idempotency key for reserve/commit/release.</summary>
    public Guid PaymentId { get; set; }
    public Guid AccountId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "EUR";
    public ReservationStatus Status { get; set; }
    public Guid? CreditAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum LedgerEntryType { Deposit, Debit, Credit }

public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? PaymentId { get; set; }
    /// <summary>Signed amount: positive = money in, negative = money out.</summary>
    public long AmountMinor { get; set; }
    public LedgerEntryType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
