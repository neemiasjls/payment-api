using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services;
using PaymentGateway.Services.Acquiring;
using PaymentGateway.Services.Dtos;
using PaymentGateway.Services.Webhooks;

namespace PaymentGateway.Tests;

public class PaymentReliabilityTests
{
    private static readonly IOptions<GatewayOptions> GatewaySettings =
        Options.Create(new GatewayOptions { FeeBps = 250 });

    private static readonly IWebhookQueue NoopWebhookQueue = new NoopQueue();

    [Fact]
    public async Task Authorize_ReusedKeyWithDifferentPayload_IsRejectedBeforeSecondProviderCall()
    {
        using var database = new FileDatabase();
        using var db = database.CreateContext();
        var processor = new CountingProcessor();
        var service = NewService(db, processor);

        await service.AuthorizeAsync(database.MerchantId, NewRequest(10_000), "order-1");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.AuthorizeAsync(database.MerchantId, NewRequest(20_000), "order-1"));

        Assert.Equal(1, processor.AuthorizationCalls);
        Assert.Single(db.Payments);
        Assert.Single(db.IdempotencyRecords);
    }

    [Fact]
    public async Task FinancialOperation_ReusedKeyForDifferentOperation_IsRejected()
    {
        using var database = new FileDatabase();
        using var db = database.CreateContext();
        var service = NewService(db, new FakePaymentProcessor());
        var (payment, _) = await service.AuthorizeAsync(database.MerchantId, NewRequest(), "authorize-1");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CaptureAsync(database.MerchantId, payment.Id, "authorize-1"));

        Assert.Empty(db.LedgerEntries);
        Assert.Equal("Authorized", (await service.GetAsync(database.MerchantId, payment.Id)).Status);
    }

    [Fact]
    public async Task ConcurrentCapture_WithSeparateContexts_WritesLedgerOnce()
    {
        using var database = new FileDatabase();
        var processor = new CoordinatedProcessor();
        Guid paymentId;
        using (var setupDb = database.CreateContext())
        {
            var (payment, _) = await NewService(setupDb, processor)
                .AuthorizeAsync(database.MerchantId, NewRequest(), null);
            paymentId = payment.Id;
        }

        processor.SynchronizeCaptures();
        using var firstDb = database.CreateContext();
        using var secondDb = database.CreateContext();
        var first = NewService(firstDb, processor).CaptureAsync(database.MerchantId, paymentId);
        var second = NewService(secondDb, processor).CaptureAsync(database.MerchantId, paymentId);

        var responses = await Task.WhenAll(first, second);
        Assert.All(responses, response => Assert.Equal("Captured", response.Status));

        using var inspectDb = database.CreateContext();
        Assert.Equal(3, await inspectDb.LedgerEntries.CountAsync(e => e.PaymentId == paymentId));
        Assert.Equal(1, await inspectDb.PaymentEvents.CountAsync(
            e => e.PaymentId == paymentId && e.Type == "payment.captured"));
    }

    [Fact]
    public async Task ConcurrentRefund_WithSeparateContexts_WritesReversalOnce()
    {
        using var database = new FileDatabase();
        var processor = new CoordinatedProcessor();
        Guid paymentId;
        using (var setupDb = database.CreateContext())
        {
            var service = NewService(setupDb, processor);
            var (payment, _) = await service.AuthorizeAsync(database.MerchantId, NewRequest(), null);
            await service.CaptureAsync(database.MerchantId, payment.Id);
            paymentId = payment.Id;
        }

        processor.SynchronizeRefunds();
        using var firstDb = database.CreateContext();
        using var secondDb = database.CreateContext();
        var first = NewService(firstDb, processor).RefundAsync(database.MerchantId, paymentId);
        var second = NewService(secondDb, processor).RefundAsync(database.MerchantId, paymentId);

        var responses = await Task.WhenAll(first, second);
        Assert.All(responses, response => Assert.Equal("Refunded", response.Status));

        using var inspectDb = database.CreateContext();
        Assert.Equal(6, await inspectDb.LedgerEntries.CountAsync(e => e.PaymentId == paymentId));
        Assert.Equal(1, await inspectDb.PaymentEvents.CountAsync(
            e => e.PaymentId == paymentId && e.Type == "payment.refunded"));
    }

    [Fact]
    public async Task ProviderWebhook_ReconcilesCurrentProviderStateAndDeduplicatesEvents()
    {
        using var database = new FileDatabase();
        using var db = database.CreateContext();
        var processor = new MutableProcessor();
        var service = NewService(db, processor);
        var (pending, _) = await service.AuthorizeAsync(database.MerchantId, NewRequest(), null);
        Assert.Equal("Pending", pending.Status);
        Assert.NotNull(pending.ProviderPaymentId);

        processor.SetStatus(ProviderPaymentStatus.Authorized);
        await service.ReconcileProviderEventAsync("evt-authorized", pending.ProviderPaymentId, pending.Id);
        Assert.Equal("Authorized", (await service.GetAsync(database.MerchantId, pending.Id)).Status);

        processor.SetStatus(ProviderPaymentStatus.Captured);
        await service.ReconcileProviderEventAsync("evt-captured", pending.ProviderPaymentId, pending.Id);
        var getCallsAfterCapture = processor.GetCalls;
        await service.ReconcileProviderEventAsync("evt-captured", pending.ProviderPaymentId, pending.Id);

        Assert.Equal(getCallsAfterCapture, processor.GetCalls);
        Assert.Equal("Captured", (await service.GetAsync(database.MerchantId, pending.Id)).Status);
        Assert.Equal(3, await db.LedgerEntries.CountAsync(e => e.PaymentId == pending.Id));
        Assert.Equal(2, await db.ProviderWebhookEvents.CountAsync());
        Assert.Equal(1, await db.PaymentEvents.CountAsync(
            e => e.PaymentId == pending.Id && e.Type == "payment.captured"));
    }

    private static CreatePaymentRequest NewRequest(long amountInCents = 10_000) => new()
    {
        AmountInCents = amountInCents,
        Currency = "BRL",
        Description = "Reliability test",
        PaymentMethodId = "pm_card_visa"
    };

    private static PaymentService NewService(AppDbContext db, IPaymentProcessor processor) =>
        new(db, NoopWebhookQueue, GatewaySettings, processor);

    private sealed class FileDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"paymentgateway-reliability-{Guid.NewGuid():N}");
        private readonly DbContextOptions<AppDbContext> _options;

        public Guid MerchantId { get; } = Guid.NewGuid();

        public FileDatabase()
        {
            Directory.CreateDirectory(_directory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "payments.db"),
                DefaultTimeout = 20,
                Pooling = false
            }.ToString();
            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString).Options;

            using var db = CreateContext();
            db.Database.EnsureCreated();
            db.Merchants.Add(new Merchant
            {
                Id = MerchantId,
                Name = "Reliability Test Merchant",
                Email = $"merchant-{MerchantId:N}@example.com",
                ApiKeyHash = new string('a', 64)
            });
            db.SaveChanges();
        }

        public AppDbContext CreateContext() => new(_options);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoopQueue : IWebhookQueue
    {
        public void Enqueue(Guid paymentEventId) { }
        public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingProcessor : IPaymentProcessor
    {
        private readonly FakePaymentProcessor _inner = new();
        public string Name => _inner.Name;
        public int AuthorizationCalls { get; private set; }

        public Task<ProviderPaymentResult> AuthorizeAsync(
            ProviderAuthorizeRequest request, CancellationToken cancellationToken = default)
        {
            AuthorizationCalls++;
            return _inner.AuthorizeAsync(request, cancellationToken);
        }

        public Task<ProviderPaymentResult> CaptureAsync(
            string id, string key, CancellationToken ct = default) => _inner.CaptureAsync(id, key, ct);
        public Task<ProviderPaymentResult> VoidAsync(
            string id, string key, CancellationToken ct = default) => _inner.VoidAsync(id, key, ct);
        public Task<ProviderPaymentResult> RefundAsync(
            string id, string key, CancellationToken ct = default) => _inner.RefundAsync(id, key, ct);
        public Task<ProviderPaymentResult> GetAsync(
            string id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
    }

    private sealed class CoordinatedProcessor : IPaymentProcessor
    {
        private readonly FakePaymentProcessor _inner = new();
        private PairGate? _captureGate;
        private PairGate? _refundGate;
        public string Name => _inner.Name;

        public void SynchronizeCaptures() => _captureGate = new PairGate();
        public void SynchronizeRefunds() => _refundGate = new PairGate();

        public Task<ProviderPaymentResult> AuthorizeAsync(
            ProviderAuthorizeRequest request, CancellationToken ct = default) =>
            _inner.AuthorizeAsync(request, ct);

        public async Task<ProviderPaymentResult> CaptureAsync(
            string id, string key, CancellationToken ct = default)
        {
            if (_captureGate is not null)
                await _captureGate.WaitAsync(ct);
            return await _inner.CaptureAsync(id, key, ct);
        }

        public Task<ProviderPaymentResult> VoidAsync(
            string id, string key, CancellationToken ct = default) => _inner.VoidAsync(id, key, ct);

        public async Task<ProviderPaymentResult> RefundAsync(
            string id, string key, CancellationToken ct = default)
        {
            if (_refundGate is not null)
                await _refundGate.WaitAsync(ct);
            return await _inner.RefundAsync(id, key, ct);
        }

        public Task<ProviderPaymentResult> GetAsync(
            string id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
    }

    private sealed class PairGate
    {
        private readonly TaskCompletionSource _bothArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public async Task WaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _bothArrived.TrySetResult();
            await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }

    private sealed class MutableProcessor : IPaymentProcessor
    {
        private ProviderPaymentResult? _current;
        public string Name => "controlled";
        public int GetCalls { get; private set; }

        public void SetStatus(ProviderPaymentStatus status) =>
            _current = _current is null
                ? throw new InvalidOperationException("Authorize first.")
                : _current with { Status = status };

        public Task<ProviderPaymentResult> AuthorizeAsync(
            ProviderAuthorizeRequest request, CancellationToken ct = default)
        {
            _current = new ProviderPaymentResult(
                $"controlled_pi_{request.PaymentId:N}", ProviderPaymentStatus.Pending,
                "4242", "visa", null, null);
            return Task.FromResult(_current);
        }

        public Task<ProviderPaymentResult> GetAsync(
            string id, CancellationToken ct = default)
        {
            GetCalls++;
            if (_current is null || _current.ProviderPaymentId != id)
                throw new InvalidOperationException("Unknown provider payment.");
            return Task.FromResult(_current);
        }

        public Task<ProviderPaymentResult> CaptureAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderPaymentResult> VoidAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderPaymentResult> RefundAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
