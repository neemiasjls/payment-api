using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Tests;

public class MerchantServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly MerchantService _service;

    public MerchantServiceTests()
    {
        _service = new MerchantService(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private static CreateMerchantRequest NewRequest(string email = "loja@teste.com.br") => new()
    {
        Name = "Loja Teste",
        Email = email
    };

    [Fact]
    public async Task Register_RetornaApiKey_EArmazenaApenasOHash()
    {
        var response = await _service.RegisterAsync(NewRequest());

        Assert.StartsWith("pk_", response.ApiKey);

        var merchant = Assert.Single(_db.Context.Merchants);
        // A chave em texto puro não pode estar no banco — só o hash dela.
        Assert.NotEqual(response.ApiKey, merchant.ApiKeyHash);
        Assert.Equal(64, merchant.ApiKeyHash.Length); // SHA-256 em hex
    }

    [Fact]
    public async Task Register_ComEmailDuplicado_LancaDomainException()
    {
        await _service.RegisterAsync(NewRequest("mesmo@email.com"));

        await Assert.ThrowsAsync<DomainException>(
            () => _service.RegisterAsync(NewRequest("mesmo@email.com")));
    }

    [Fact]
    public async Task FindByApiKey_ComChaveValida_RetornaOLojista()
    {
        var created = await _service.RegisterAsync(NewRequest());

        var merchant = await _service.FindByApiKeyAsync(created.ApiKey);

        Assert.NotNull(merchant);
        Assert.Equal(created.Id, merchant.Id);
    }

    [Fact]
    public async Task FindByApiKey_ComChaveInvalida_RetornaNull()
    {
        await _service.RegisterAsync(NewRequest());

        var merchant = await _service.FindByApiKeyAsync("pk_chave_que_nao_existe");

        Assert.Null(merchant);
    }

    [Fact]
    public async Task GetBalance_LojistaNovo_ComecaZerado()
    {
        var created = await _service.RegisterAsync(NewRequest());

        var balance = await _service.GetBalanceAsync(created.Id);

        Assert.Equal(0, balance.AvailableInCents);
    }
}
