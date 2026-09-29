using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;
using PaymentGateway.Services.Reconciliation;

namespace PaymentGateway.Tests;

public sealed class PaymentReconciliationWorkerTests
{
    [Fact]
    public async Task Sweep_rotates_through_tied_timestamps_and_continues_after_one_failure()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new RecordingPaymentService();
        using var provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<IPaymentService>(recorder)
            .BuildServiceProvider();

        Guid[] eligibleIds;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            var merchantId = Guid.NewGuid();
            db.Merchants.Add(new Merchant
            {
                Id = merchantId,
                Name = "Demo",
                Email = "worker@example.test",
                ApiKeyHash = new string('a', 64)
            });

            var pending = Payment.CreatePending(merchantId, 1000, "BRL", null, "stripe");
            pending.ApplyAuthorization("pi_pending", PaymentStatus.Pending, null, null, null);
            var action = Payment.CreatePending(merchantId, 1000, "BRL", null, "stripe");
            action.ApplyAuthorization("pi_action", PaymentStatus.RequiresAction, null, null, null);
            var authorized = Payment.CreatePending(merchantId, 1000, "BRL", null, "stripe");
            authorized.ApplyAuthorization("pi_authorized", PaymentStatus.Authorized, null, null, null);
            var captured = Payment.CreatePending(merchantId, 1000, "BRL", null, "stripe");
            captured.ApplyAuthorization("pi_captured", PaymentStatus.Authorized, null, null, null);
            captured.Capture();
            eligibleIds = [pending.Id, action.Id, authorized.Id, captured.Id];

            var noProviderId = Payment.CreatePending(merchantId, 1000, "BRL", null, "stripe");
            var fake = Payment.CreatePending(merchantId, 1000, "BRL", null, "fake");
            fake.ApplyAuthorization("fake_authorized", PaymentStatus.Authorized, null, null, null);
            var terminal = Payment.CreatePending(merchantId, 1000, "BRL", null, "stripe");
            terminal.ApplyAuthorization("pi_terminal", PaymentStatus.Authorized, null, null, null);
            terminal.Void();

            db.Payments.AddRange(pending, action, authorized, captured, noProviderId, fake, terminal);
            var commonTime = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            foreach (var payment in db.ChangeTracker.Entries<Payment>())
                payment.Property(p => p.CreatedAtUtc).CurrentValue = commonTime;
            await db.SaveChangesAsync();
        }

        recorder.FailId = eligibleIds.Order().First();
        var worker = new PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PaymentReconciliationOptions { BatchSize = 2 }),
            NullLogger<PaymentReconciliationWorker>.Instance);

        var first = await worker.ReconcileBatchAsync();
        var second = await worker.ReconcileBatchAsync();
        var third = await worker.ReconcileBatchAsync();

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(1, third);
        Assert.Equal(6, recorder.Calls.Count);
        Assert.Equal(4, recorder.Calls.Take(4).Distinct().Count());
        Assert.Equal(eligibleIds.Order(), recorder.Calls.Take(4).Order());
        Assert.Equal(recorder.Calls.Take(2), recorder.Calls.Skip(4));
    }

    private sealed class RecordingPaymentService : IPaymentService
    {
        public List<Guid> Calls { get; } = [];
        public Guid FailId { get; set; }

        public Task<PaymentResponse> RefreshAsync(
            Guid merchantId, Guid paymentId, CancellationToken ct = default)
        {
            Calls.Add(paymentId);
            if (paymentId == FailId)
                throw new InvalidOperationException("Simulated provider outage");
            return Task.FromResult<PaymentResponse>(null!);
        }

        public Task<(PaymentResponse Payment, bool Created)> AuthorizeAsync(
            Guid merchantId, CreatePaymentRequest request, string? idempotencyKey,
            CancellationToken ct = default) => throw new NotImplementedException();

        public Task<PaymentResponse> CaptureAsync(
            Guid merchantId, Guid paymentId, string? idempotencyKey = null,
            CancellationToken ct = default) => throw new NotImplementedException();

        public Task<PaymentResponse> VoidAsync(
            Guid merchantId, Guid paymentId, string? idempotencyKey = null,
            CancellationToken ct = default) => throw new NotImplementedException();

        public Task<PaymentResponse> RefundAsync(
            Guid merchantId, Guid paymentId, string? idempotencyKey = null,
            CancellationToken ct = default) => throw new NotImplementedException();

        public Task<PaymentResponse> GetAsync(
            Guid merchantId, Guid paymentId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<PagedResponse<PaymentResponse>> ListAsync(
            Guid merchantId, int page, int pageSize, PaymentStatus? status,
            CancellationToken ct = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<PaymentEventResponse>> GetEventsAsync(
            Guid merchantId, Guid paymentId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task ReconcileProviderEventAsync(
            string eventId, string providerPaymentId, Guid? localPaymentId,
            CancellationToken ct = default) => throw new NotImplementedException();
    }
}
