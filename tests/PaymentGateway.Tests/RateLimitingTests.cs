using System.Net;
using PaymentGateway.Tests.Integration;

namespace PaymentGateway.Tests;

public sealed class RateLimitingTests
{
    [Fact]
    public async Task Invalid_api_keys_share_the_source_ip_rate_limit()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.EnsureSchemaCreated();

        for (var i = 0; i < 100; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/payments");
            request.Headers.Add("X-Api-Key", $"invalid-{i}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var overLimitRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/payments");
        overLimitRequest.Headers.Add("X-Api-Key", "another-invalid-key");
        using var overLimit = await client.SendAsync(overLimitRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }
}
