using Microsoft.EntityFrameworkCore;
using PaymentGateway.Domain.Entities;

namespace PaymentGateway.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ProviderWebhookEvent> ProviderWebhookEvents => Set<ProviderWebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Merchant>(merchant =>
        {
            merchant.Property(m => m.Name).HasMaxLength(200).IsRequired();
            merchant.Property(m => m.Email).HasMaxLength(200).IsRequired();
            merchant.Property(m => m.ApiKeyHash).HasMaxLength(64).IsRequired();
            merchant.HasIndex(m => m.ApiKeyHash).IsUnique();
            merchant.HasIndex(m => m.Email).IsUnique();
        });

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            payment.Property(p => p.CardLast4).HasMaxLength(4).IsRequired();
            payment.Property(p => p.CardBrand).HasMaxLength(20).IsRequired();
            payment.Property(p => p.Provider).HasMaxLength(30).IsRequired();
            payment.Property(p => p.ProviderPaymentId).HasMaxLength(200);
            payment.Property(p => p.PendingOperation).HasMaxLength(30);
            payment.Property(p => p.Version).IsConcurrencyToken();
            payment.Property(p => p.Description).HasMaxLength(500);
            payment.Property(p => p.DeclineReason).HasMaxLength(100);

            // Enum como texto no banco: legível em consultas manuais e estável
            // caso a ordem dos membros do enum mude.
            payment.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);

            payment.HasIndex(p => new { p.MerchantId, p.CreatedAtUtc });
            payment.HasIndex(p => new { p.Provider, p.ProviderPaymentId }).IsUnique();
        });

        modelBuilder.Entity<LedgerEntry>(entry =>
        {
            entry.Property(e => e.Account).HasConversion<string>().HasMaxLength(30);
            entry.Property(e => e.Type).HasConversion<string>().HasMaxLength(10);
            entry.Property(e => e.Description).HasMaxLength(300);
            entry.HasIndex(e => new { e.MerchantId, e.Account });
            entry.HasIndex(e => e.PaymentId);
        });

        modelBuilder.Entity<PaymentEvent>(paymentEvent =>
        {
            paymentEvent.Property(e => e.Type).HasMaxLength(50).IsRequired();
            paymentEvent.HasIndex(e => e.PaymentId);
            paymentEvent.HasIndex(e => new
            {
                e.DeliveredAtUtc, e.DeliverySkippedAtUtc,
                e.NextDeliveryAttemptAtUtc, e.DeliveryLeaseUntilUtc
            });
        });

        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.Property(r => r.IdempotencyKey).HasMaxLength(100).IsRequired();
            record.Property(r => r.Operation).HasMaxLength(30).IsRequired();
            record.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();

            // Índice único: a mesma chave não pode gerar dois pagamentos
            // para o mesmo lojista, mesmo em requisições concorrentes.
            record.HasIndex(r => new { r.MerchantId, r.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<ProviderWebhookEvent>(providerEvent =>
        {
            providerEvent.HasKey(e => new { e.Provider, e.EventId });
            providerEvent.Property(e => e.Provider).HasMaxLength(30);
            providerEvent.Property(e => e.EventId).HasMaxLength(200);
            providerEvent.Property(e => e.ProviderPaymentId).HasMaxLength(200);
        });
    }
}
