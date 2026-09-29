using System.Net;
using System.Net.Http.Json;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Tests.Integration;

public class ApiIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(ApiFactory factory)
    {
        factory.EnsureSchemaCreated();
        _client = factory.CreateClient();
    }

    private static object NewPaymentBody(long amountInCents = 10_000, string paymentMethodId = "pm_card_visa") => new
    {
        amountInCents,
        currency = "BRL",
        description = "Pedido de integração",
        paymentMethodId
    };

    private static async Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    /// <summary>Cadastra um lojista novo e devolve um client já autenticado.</summary>
    private async Task<HttpClient> RegisterAndAuthenticateAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/merchants", new
        {
            name = "Loja Integração",
            email = $"loja-{Guid.NewGuid():N}@teste.com.br"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var merchant = await response.Content.ReadFromJsonAsync<MerchantCreatedResponse>();
        Assert.NotNull(merchant);

        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", merchant.ApiKey);
        return _client;
    }

    [Fact]
    public async Task Health_Retorna200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Respostas_TrazemHeadersDeSeguranca()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Payments_SemApiKey_Retorna401()
    {
        var response = await _client.GetAsync("/api/v1/payments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payments_ComApiKeyInvalida_Retorna401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/payments");
        request.Headers.Add("X-Api-Key", "pk_chave_invalida");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FluxoCompleto_AutorizaCapturaEConsultaSaldo()
    {
        var client = await RegisterAndAuthenticateAsync();

        // Autoriza R$ 100,00
        var createResponse = await PostWithKeyAsync(client, "/api/v1/payments", NewPaymentBody(10_000));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var payment = await createResponse.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(payment);
        Assert.Equal("Authorized", payment.Status);

        // Captura
        var captureResponse = await PostWithKeyAsync(client, $"/api/v1/payments/{payment.Id}/capture");
        Assert.Equal(HttpStatusCode.OK, captureResponse.StatusCode);

        // Saldo = 10000 - 2,5% de taxa = 9750
        var balance = await client.GetFromJsonAsync<BalanceResponse>("/api/v1/merchants/me/balance");
        Assert.NotNull(balance);
        Assert.Equal(9_750, balance.AvailableInCents);

        // Estorna e o saldo volta a zero
        var refundResponse = await PostWithKeyAsync(client, $"/api/v1/payments/{payment.Id}/refund");
        Assert.Equal(HttpStatusCode.OK, refundResponse.StatusCode);

        balance = await client.GetFromJsonAsync<BalanceResponse>("/api/v1/merchants/me/balance");
        Assert.NotNull(balance);
        Assert.Equal(0, balance.AvailableInCents);
    }

    [Fact]
    public async Task Idempotencia_MesmaChave_NaoCriaSegundoPagamento()
    {
        var client = await RegisterAndAuthenticateAsync();
        var idempotencyKey = $"pedido-{Guid.NewGuid():N}";

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments")
        {
            Content = JsonContent.Create(NewPaymentBody())
        };
        first.Headers.Add("Idempotency-Key", idempotencyKey);
        var firstResponse = await client.SendAsync(first);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments")
        {
            Content = JsonContent.Create(NewPaymentBody())
        };
        second.Headers.Add("Idempotency-Key", idempotencyKey);
        var secondResponse = await client.SendAsync(second);

        // Primeira cria (201); a repetição devolve o mesmo recurso (200).
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var firstPayment = await firstResponse.Content.ReadFromJsonAsync<PaymentResponse>();
        var secondPayment = await secondResponse.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.Equal(firstPayment!.Id, secondPayment!.Id);
    }

    [Fact]
    public async Task Authorize_CartaoRecusado_RetornaStatusDeclined()
    {
        var client = await RegisterAndAuthenticateAsync();

        var response = await PostWithKeyAsync(
            client, "/api/v1/payments", NewPaymentBody(paymentMethodId: "pm_card_visa_chargeDeclined"));

        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(payment);
        Assert.Equal("Declined", payment.Status);
        Assert.Equal("generic_decline", payment.DeclineReason);
    }

    [Fact]
    public async Task Authorize_PaymentMethodInvalido_Retorna422()
    {
        var client = await RegisterAndAuthenticateAsync();

        var response = await PostWithKeyAsync(
            client, "/api/v1/payments", NewPaymentBody(paymentMethodId: "pm_invalido"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Capture_PagamentoInexistente_Retorna404()
    {
        var client = await RegisterAndAuthenticateAsync();

        var response = await PostWithKeyAsync(client, $"/api/v1/payments/{Guid.NewGuid()}/capture");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
