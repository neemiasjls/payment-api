using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaymentGateway.Api.Controllers;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Tests;

public class MerchantSecurityTests
{
    [Fact]
    public async Task Registration_IsForbiddenOutsideDemoEnvironments()
    {
        using var db = new TestDb();
        var controller = new MerchantsController(
            new MerchantService(db.Context), new TestHostEnvironment("Production"));

        var result = await controller.Register(new CreateMerchantRequest
        {
            Name = "New merchant",
            Email = "new@example.com"
        }, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(db.Context.Merchants);
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://internal.local/hook")]
    [InlineData("https://example.com:8443/hook")]
    [InlineData("https://user@example.com/hook")]
    [InlineData("https://example.com/hook#fragment")]
    public async Task Registration_RejectsUnsafeWebhookUrls(string url)
    {
        using var db = new TestDb();
        var service = new MerchantService(db.Context, new TestHostEnvironment("Production"));

        await Assert.ThrowsAsync<DomainException>(() => service.RegisterAsync(new CreateMerchantRequest
        {
            Name = "New merchant",
            Email = "new@example.com",
            WebhookUrl = url
        }));

        Assert.Empty(db.Context.Merchants);
    }

    [Fact]
    public async Task Registration_AllowsPublicHttpsWebhook()
    {
        using var db = new TestDb();
        var service = new MerchantService(db.Context, new TestHostEnvironment("Production"));

        var merchant = await service.RegisterAsync(new CreateMerchantRequest
        {
            Name = "New merchant",
            Email = "new@example.com",
            WebhookUrl = "https://example.com/hook"
        });

        Assert.NotNull(merchant.WebhookSecret);
        Assert.Equal("https://example.com/hook", Assert.Single(db.Context.Merchants).WebhookUrl);
    }

    [Fact]
    public async Task Registration_AllowsLoopbackHttpOnlyInDemo()
    {
        using var db = new TestDb();
        var service = new MerchantService(db.Context, new TestHostEnvironment("Testing"));

        await service.RegisterAsync(new CreateMerchantRequest
        {
            Name = "New merchant",
            Email = "new@example.com",
            WebhookUrl = "http://localhost:5005/hook"
        });

        Assert.Equal("http://localhost:5005/hook", Assert.Single(db.Context.Merchants).WebhookUrl);
    }

    [Theory]
    [InlineData("Development", "POST", "/api/v1/merchants", true)]
    [InlineData("Testing", "POST", "/api/v1/merchants", true)]
    [InlineData("Production", "POST", "/api/v1/merchants", false)]
    [InlineData("Production", "POST", "/api/v1/webhooks/stripe", true)]
    [InlineData("Production", "GET", "/api/v1/webhooks/stripe", false)]
    [InlineData("Production", "POST", "/api/v1/webhooks/stripe/extra", false)]
    public async Task PublicRoutes_AreRestrictedToDemoRegistrationAndStripeWebhook(
        string environment, string method, string path, bool expectedPublic)
    {
        using var db = new TestDb();
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, new TestHostEnvironment(environment));

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new MerchantService(db.Context));

        Assert.Equal(expectedPublic, nextCalled);
        Assert.Equal(expectedPublic ? StatusCodes.Status204NoContent : StatusCodes.Status401Unauthorized,
            context.Response.StatusCode);
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PaymentGateway.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
