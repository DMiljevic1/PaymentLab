using Microsoft.EntityFrameworkCore;

namespace Payments.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentSaga> Sagas => Set<PaymentSaga>();
    public DbSet<PaymentHistory> History => Set<PaymentHistory>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.CreditorIban).HasMaxLength(34);
            e.Property(x => x.Reference).HasMaxLength(140);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.BankReference).HasMaxLength(64);
            e.HasIndex(x => new { x.SourceAccountId, x.CreatedAt });
        });

        b.Entity<PaymentSaga>(e =>
        {
            e.ToTable("payment_sagas");
            e.HasKey(x => x.PaymentId);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ResumeState).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.CurrentStep).HasMaxLength(50);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.LockedBy).HasMaxLength(100);
            e.Property(x => x.TraceParent).HasMaxLength(100);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => x.NextAttemptAt).HasFilter("next_attempt_at IS NOT NULL");
            e.HasIndex(x => x.State);
        });

        b.Entity<PaymentHistory>(e =>
        {
            e.ToTable("payment_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.FromState).HasMaxLength(30);
            e.Property(x => x.ToState).HasMaxLength(30);
            e.Property(x => x.Step).HasMaxLength(50);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => x.PaymentId);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.Id).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_messages_unpublished");
            e.HasIndex(x => x.PublishedAt);
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => x.MessageId);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.PaymentId).HasFilter("processed_at IS NULL").HasDatabaseName("ix_inbox_messages_pending");
            e.HasIndex(x => x.ProcessedAt);
        });
    }
}
