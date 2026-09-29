using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Services.Webhooks;

namespace PaymentGateway.Tests;

public sealed class WebhookDispatcherTests
{
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://user:password@example.com/hook")]
    [InlineData("https://example.com:8080/hook")]
    [InlineData("https://example.com\\@localhost/hook")]
    [InlineData("https://internal.local/hook")]
    public void PublicWebhookUrl_RejectsUnsafeTargets(string url)
    {
        Assert.False(WebhookTargetValidator.TryValidate(url, false, out _));
    }

    [Fact]
    public void LoopbackHttp_IsAvailableOnlyForLocalDemo()
    {
        Assert.True(WebhookTargetValidator.TryValidate("http://127.0.0.1:5000/hook", true, out _));
        Assert.False(WebhookTargetValidator.TryValidate("http://127.0.0.1:5000/hook", false, out _));
        Assert.True(WebhookTargetValidator.TryValidate("https://webhook.site/path", false, out _));
    }

    [Fact]
    public async Task EventWithoutEndpoint_IsPersistentlySkipped()
    {
        using var fixture = new DispatcherFixture();
        var eventId = await fixture.InsertEventAsync(null, null);

        Assert.Equal(1, await fixture.Dispatcher.DispatchDueAsync());

        using var db = fixture.CreateDbContext();
        var paymentEvent = await db.PaymentEvents.AsNoTracking().SingleAsync(e => e.Id == eventId);
        Assert.NotNull(paymentEvent.DeliverySkippedAtUtc);
        Assert.Null(paymentEvent.DeliveredAtUtc);
        Assert.Equal(0, paymentEvent.DeliveryAttempts);

        // Um novo worker encontra o mesmo banco, mas não reenvia um evento
        // que já foi encerrado sem endpoint configurado.
        using var restarted = fixture.CreateNewDispatcher();
        Assert.Equal(0, await restarted.DispatchDueAsync());
    }

    [Fact]
    public async Task FailedDelivery_RetriesFromDatabaseAfterDispatcherRestart()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var fixture = new DispatcherFixture();
        var eventId = await fixture.InsertEventAsync(
            $"http://127.0.0.1:{port}/hook", "whsec_test_secret");

        var firstRequestTask = RespondOnceAsync(listener, 503);
        Assert.Equal(1, await fixture.Dispatcher.DispatchDueAsync());
        var firstRequest = await firstRequestTask;
        Assert.Equal(WebhookSigner.Sign(firstRequest.Body, "whsec_test_secret"), firstRequest.Signature);

        using (var db = fixture.CreateDbContext())
        {
            var failed = await db.PaymentEvents.SingleAsync(e => e.Id == eventId);
            Assert.Equal(1, failed.DeliveryAttempts);
            Assert.Equal("HTTP 503", failed.LastDeliveryError);
            Assert.NotNull(failed.NextDeliveryAttemptAtUtc);
            Assert.Null(failed.DeliveredAtUtc);

            // Avança apenas a agenda para testar o replay sem esperar o backoff.
            failed.NextDeliveryAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        using var restarted = fixture.CreateNewDispatcher();
        var secondRequestTask = RespondOnceAsync(listener, 200);
        Assert.Equal(1, await restarted.DispatchDueAsync());
        var secondRequest = await secondRequestTask;
        Assert.Equal(firstRequest.Body, secondRequest.Body);

        using var verifyDb = fixture.CreateDbContext();
        var delivered = await verifyDb.PaymentEvents.AsNoTracking().SingleAsync(e => e.Id == eventId);
        Assert.Equal(2, delivered.DeliveryAttempts);
        Assert.NotNull(delivered.DeliveredAtUtc);
        Assert.Null(delivered.LastDeliveryError);
        Assert.Null(delivered.NextDeliveryAttemptAtUtc);
        Assert.Equal(0, await restarted.DispatchDueAsync());
    }

    private static async Task<(string Body, string Signature)> RespondOnceAsync(
        TcpListener listener, int statusCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var contentLength = 0;
        string? signature = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line && line.Length > 0)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            if (line.StartsWith("X-Webhook-Signature:", StringComparison.OrdinalIgnoreCase))
                signature = line["X-Webhook-Signature:".Length..].Trim();
        }

        var body = new char[contentLength];
        var read = 0;
        while (read < body.Length)
        {
            var count = await reader.ReadAsync(body.AsMemory(read), timeout.Token);
            if (count == 0)
                throw new IOException("Webhook body ended unexpectedly.");
            read += count;
        }

        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Unavailable")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, timeout.Token);
        return (new string(body), Assert.IsType<string>(signature));
    }

    private sealed class DispatcherFixture : IDisposable
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(), $"payment-webhooks-{Guid.NewGuid():N}.db");
        private readonly ServiceProvider _services;

        public WebhookDispatcher Dispatcher => _services.GetRequiredService<WebhookDispatcher>();

        public DispatcherFixture()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostEnvironment>(new TestEnvironment());
            services.AddSingleton<IWebhookQueue, WebhookQueue>();
            services.AddSingleton<WebhookDispatcher>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={_databasePath}"));
            _services = services.BuildServiceProvider();

            using var db = CreateDbContext();
            db.Database.EnsureCreated();
        }

        public AppDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_databasePath}")
                .Options);

        public WebhookDispatcher CreateNewDispatcher() => new(
            new WebhookQueue(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<IHostEnvironment>(),
            _services.GetRequiredService<ILogger<WebhookDispatcher>>());

        public async Task<Guid> InsertEventAsync(string? url, string? secret)
        {
            using var db = CreateDbContext();
            var merchant = new Merchant
            {
                Name = "Webhook Test",
                Email = $"{Guid.NewGuid():N}@example.com",
                ApiKeyHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                WebhookUrl = url,
                WebhookSecret = secret
            };
            var paymentEvent = new PaymentEvent
            {
                MerchantId = merchant.Id,
                PaymentId = Guid.NewGuid(),
                Type = "payment.authorized",
                PayloadJson = "{\"status\":\"Authorized\"}"
            };
            db.Merchants.Add(merchant);
            db.PaymentEvents.Add(paymentEvent);
            await db.SaveChangesAsync();
            return paymentEvent.Id;
        }

        public void Dispose()
        {
            _services.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(_databasePath);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "PaymentGateway.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
