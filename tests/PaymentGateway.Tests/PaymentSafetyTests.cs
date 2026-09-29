using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services;
using PaymentGateway.Services.Acquiring;
using PaymentGateway.Services.Dtos;
using PaymentGateway.Services.Webhooks;

namespace PaymentGateway.Tests;

public sealed class PaymentSafetyTests
{
    [Theory]
    [InlineData("pm_undocumented")]
    [InlineData("pm_card_visa_extra")]
    public async Task UnsupportedPaymentMethodDoesNotLeavePendingPaymentOrConsumeKey(string paymentMethodId)
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var merchantId = AddMerchant(fixture.Context);
        var service = NewService(fixture.Context, queue, new FakePaymentProcessor());

        await Assert.ThrowsAsync<DomainException>(() =>
            service.AuthorizeAsync(merchantId, NewRequest(10_000, paymentMethodId), "invalid-method"));

        Assert.Empty(await fixture.Context.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Context.IdempotencyRecords.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(49L)]
    [InlineData(100_000_000L)]
    [InlineData(long.MaxValue)]
    public async Task OutOfRangeAmountIsRejectedBeforeAnyProviderOrLedgerWrite(long amountInCents)
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var merchantId = AddMerchant(fixture.Context);
        var processor = new InspectableProcessor("fake");
        var service = NewService(fixture.Context, queue, processor);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.AuthorizeAsync(merchantId, NewRequest(amountInCents), "oversized"));

        Assert.Equal(0, processor.AuthorizeCalls);
        Assert.Empty(await fixture.Context.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Context.IdempotencyRecords.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Context.LedgerEntries.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false, 10_000L, "BRL")]
    [InlineData(true, 9_999L, "BRL")]
    [InlineData(true, 10_000L, "USD")]
    public async Task ForeignStripeIntentCannotBindOrWriteLedger(
        bool samePaymentId, long providerAmount, string providerCurrency)
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var db = fixture.Context;
        var merchantId = AddMerchant(db);
        var payment = Payment.CreatePending(merchantId, 10_000, "BRL", "Order", "stripe");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var processor = new InspectableProcessor("stripe")
        {
            Current = new ProviderPaymentResult(
                "pi_unrelated", ProviderPaymentStatus.Captured, "4242", "visa", null, null,
                samePaymentId ? payment.Id : Guid.NewGuid(), providerAmount, providerCurrency)
        };
        var service = NewService(db, queue, processor);

        var error = await Record.ExceptionAsync(() =>
            service.ReconcileProviderEventAsync("evt_unrelated", "pi_unrelated", payment.Id));
        Assert.True(error is null or DomainException, error?.ToString());

        db.ChangeTracker.Clear();
        var persisted = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Pending, persisted.Status);
        Assert.Null(persisted.ProviderPaymentId);
        Assert.Empty(await db.LedgerEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task MatchingStripeIntentCanBindPendingPayment()
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var db = fixture.Context;
        var merchantId = AddMerchant(db);
        var payment = Payment.CreatePending(merchantId, 10_000, "BRL", "Order", "stripe");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var processor = new InspectableProcessor("stripe")
        {
            Current = new ProviderPaymentResult(
                "pi_matching", ProviderPaymentStatus.Authorized, "4242", "visa", null, null,
                payment.Id, 10_000, "BRL")
        };
        var service = NewService(db, queue, processor);

        await service.ReconcileProviderEventAsync("evt_matching", "pi_matching", payment.Id);

        db.ChangeTracker.Clear();
        var persisted = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Authorized, persisted.Status);
        Assert.Equal("pi_matching", persisted.ProviderPaymentId);
    }

    [Fact]
    public async Task RefundReversesCapturedFeeEvenAfterConfigurationChanges()
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var db = fixture.Context;
        var merchantId = AddMerchant(db);
        var processor = new FakePaymentProcessor();
        var captureService = NewService(db, queue, processor, feeBps: 250);
        var (payment, _) = await captureService.AuthorizeAsync(
            merchantId, NewRequest(10_000), "create-fee-change");
        await captureService.CaptureAsync(merchantId, payment.Id, "capture-fee-change");

        var refundService = NewService(db, queue, processor, feeBps: 300);
        var refunded = await refundService.RefundAsync(
            merchantId, payment.Id, "refund-fee-change");

        Assert.Equal("Refunded", refunded.Status);
        var entries = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.PaymentId == payment.Id).ToListAsync();
        Assert.Equal(6, entries.Count);
        Assert.Equal(9_750, Assert.Single(entries, e =>
            e.Account == LedgerAccount.MerchantPayable && e.Type == LedgerEntryType.Credit).AmountInCents);
        Assert.Equal(9_750, Assert.Single(entries, e =>
            e.Account == LedgerAccount.MerchantPayable && e.Type == LedgerEntryType.Debit).AmountInCents);
        Assert.Equal(250, Assert.Single(entries, e =>
            e.Account == LedgerAccount.AcquirerRevenue && e.Type == LedgerEntryType.Debit).AmountInCents);
    }

    [Fact]
    public async Task SwitchingProcessorCannotSendExistingPaymentToWrongProvider()
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var db = fixture.Context;
        var merchantId = AddMerchant(db);
        var fakeService = NewService(db, queue, new FakePaymentProcessor());
        var (payment, _) = await fakeService.AuthorizeAsync(
            merchantId, NewRequest(10_000), "create-before-switch");

        var stripe = new InspectableProcessor("stripe");
        var switchedService = NewService(db, queue, stripe);
        await Assert.ThrowsAsync<DomainException>(() =>
            switchedService.CaptureAsync(merchantId, payment.Id, "capture-after-switch"));

        Assert.Equal(0, stripe.CaptureCalls);
        db.ChangeTracker.Clear();
        var persisted = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Authorized, persisted.Status);
        Assert.Null(persisted.PendingOperation);
        Assert.Empty(await db.LedgerEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OldUnresolvedStripeAuthorizationDoesNotCreateAnotherIntentOnRetry()
    {
        using var fixture = new TestDb();
        using var queue = new WebhookQueue();
        var db = fixture.Context;
        var merchantId = AddMerchant(db);
        var processor = new InspectableProcessor("stripe") { ThrowOnAuthorize = true };
        var service = NewService(db, queue, processor);
        var request = NewRequest(10_000);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.AuthorizeAsync(merchantId, request, "unresolved-stripe-create"));
        Assert.Equal(1, processor.AuthorizeCalls);

        var pending = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Pending, pending.Status);
        Assert.Null(pending.ProviderPaymentId);
        db.Entry(pending).Property(p => p.CreatedAtUtc).CurrentValue =
            DateTime.UtcNow.AddHours(-23).AddMinutes(-1);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainException>(() =>
            service.AuthorizeAsync(merchantId, request, "unresolved-stripe-create"));
        Assert.Equal(1, processor.AuthorizeCalls);
        Assert.Single(await db.Payments.AsNoTracking().ToListAsync());
        Assert.Single(await db.IdempotencyRecords.AsNoTracking().ToListAsync());
    }

    private static PaymentService NewService(
        AppDbContext db, IWebhookQueue queue, IPaymentProcessor processor, int feeBps = 250) =>
        new(db, queue, Options.Create(new GatewayOptions { FeeBps = feeBps }), processor);

    private static CreatePaymentRequest NewRequest(long amount, string method = "pm_card_visa") => new()
    {
        AmountInCents = amount,
        Currency = "BRL",
        Description = "Safety test",
        PaymentMethodId = method
    };

    private static Guid AddMerchant(AppDbContext db)
    {
        var merchant = new Merchant
        {
            Name = "Safety Test Merchant",
            Email = $"safety-{Guid.NewGuid():N}@example.com",
            ApiKeyHash = new string('a', 64)
        };
        db.Merchants.Add(merchant);
        db.SaveChanges();
        return merchant.Id;
    }

    private sealed class InspectableProcessor(string name) : IPaymentProcessor
    {
        public string Name => name;
        public int AuthorizeCalls { get; private set; }
        public int CaptureCalls { get; private set; }
        public bool ThrowOnAuthorize { get; set; }
        public ProviderPaymentResult? Current { get; set; }

        public Task<ProviderPaymentResult> AuthorizeAsync(
            ProviderAuthorizeRequest request, CancellationToken ct = default)
        {
            AuthorizeCalls++;
            if (ThrowOnAuthorize)
                throw new HttpRequestException("Simulated lost provider response.");
            return Task.FromResult(Current ?? new ProviderPaymentResult(
                $"{Name}_pi_{request.PaymentId:N}", ProviderPaymentStatus.Authorized,
                "4242", "visa", null, null, request.PaymentId,
                request.AmountInCents, request.Currency));
        }

        public Task<ProviderPaymentResult> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Current ?? throw new InvalidOperationException("No provider payment configured."));

        public Task<ProviderPaymentResult> CaptureAsync(
            string id, string key, CancellationToken ct = default)
        {
            CaptureCalls++;
            throw new InvalidOperationException("A mismatched provider must never receive CaptureAsync.");
        }

        public Task<ProviderPaymentResult> VoidAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ProviderPaymentResult> RefundAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
