using Microsoft.Extensions.Options;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;
using PaymentGateway.Services.Security;
using PaymentGateway.Services.Webhooks;

namespace PaymentGateway.Tests;

public class PaymentServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly PaymentService _service;
    private readonly MerchantService _merchants;
    private readonly Guid _merchantId;

    public PaymentServiceTests()
    {
        var options = Options.Create(new GatewayOptions { FeeBps = 250 }); // 2,5%
        _service = new PaymentService(_db.Context, new WebhookQueue(), options);
        _merchants = new MerchantService(_db.Context);

        var merchant = new Merchant
        {
            Name = "Loja Teste",
            Email = "loja@teste.com.br",
            ApiKeyHash = ApiKeyHasher.Hash("pk_teste")
        };
        _db.Context.Merchants.Add(merchant);
        _db.Context.SaveChanges();
        _merchantId = merchant.Id;
    }

    public void Dispose() => _db.Dispose();

    private static CreatePaymentRequest NewRequest(
        long amountInCents = 10_000, string cardNumber = "4242424242424242") => new()
    {
        AmountInCents = amountInCents,
        Currency = "BRL",
        Description = "Pedido de teste",
        Card = new CardRequest
        {
            Number = cardNumber,
            HolderName = "CLIENTE TESTE",
            ExpMonth = 12,
            ExpYear = DateTime.UtcNow.Year + 3,
            Cvv = "123"
        }
    };

    // ---------- Autorização ----------

    [Fact]
    public async Task Authorize_ComCartaoValido_CriaPagamentoAutorizado()
    {
        var (payment, created) = await _service.AuthorizeAsync(_merchantId, NewRequest(), null);

        Assert.True(created);
        Assert.Equal("Authorized", payment.Status);
        Assert.Equal("4242", payment.CardLast4);
        Assert.Equal("Visa", payment.CardBrand);
    }

    [Fact]
    public async Task Authorize_ComLuhnInvalido_LancaDomainException()
    {
        var request = NewRequest(cardNumber: "4242424242424241");

        await Assert.ThrowsAsync<DomainException>(
            () => _service.AuthorizeAsync(_merchantId, request, null));
    }

    [Fact]
    public async Task Authorize_ComCartaoDeTesteRecusado_CriaPagamentoDeclined()
    {
        var (payment, _) = await _service.AuthorizeAsync(
            _merchantId, NewRequest(cardNumber: "4000000000000002"), null);

        Assert.Equal("Declined", payment.Status);
        Assert.Equal("card_declined", payment.DeclineReason);
    }

    [Fact]
    public async Task Authorize_ComCartaoVencido_RecusaComExpiredCard()
    {
        var request = NewRequest();
        request.Card.ExpYear = DateTime.UtcNow.Year - 1;

        var (payment, _) = await _service.AuthorizeAsync(_merchantId, request, null);

        Assert.Equal("Declined", payment.Status);
        Assert.Equal("expired_card", payment.DeclineReason);
    }

    [Fact]
    public async Task Authorize_ComMesmaIdempotencyKey_NaoCobraDuasVezes()
    {
        var (first, firstCreated) = await _service.AuthorizeAsync(_merchantId, NewRequest(), "pedido-42");
        var (second, secondCreated) = await _service.AuthorizeAsync(_merchantId, NewRequest(), "pedido-42");

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(_db.Context.Payments); // apenas UM pagamento no banco
    }

    // ---------- Captura e ledger ----------

    [Fact]
    public async Task Capture_PagamentoAutorizado_GeraLedgerBalanceadoESaldo()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(10_000), null);

        var captured = await _service.CaptureAsync(_merchantId, payment.Id);

        Assert.Equal("Captured", captured.Status);

        // Partidas dobradas: total de débitos == total de créditos.
        var entries = _db.Context.LedgerEntries.ToList();
        var debits = entries.Where(e => e.Type == LedgerEntryType.Debit).Sum(e => e.AmountInCents);
        var credits = entries.Where(e => e.Type == LedgerEntryType.Credit).Sum(e => e.AmountInCents);
        Assert.Equal(debits, credits);

        // Saldo do lojista = valor - taxa de 2,5% (10000 - 250 = 9750).
        var balance = await _merchants.GetBalanceAsync(_merchantId);
        Assert.Equal(9_750, balance.AvailableInCents);
    }

    [Fact]
    public async Task Capture_PagamentoRecusado_LancaDomainException()
    {
        var (payment, _) = await _service.AuthorizeAsync(
            _merchantId, NewRequest(cardNumber: "4000000000000002"), null);

        await Assert.ThrowsAsync<DomainException>(
            () => _service.CaptureAsync(_merchantId, payment.Id));
    }

    [Fact]
    public async Task Capture_DuasVezes_LancaDomainException()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(), null);
        await _service.CaptureAsync(_merchantId, payment.Id);

        await Assert.ThrowsAsync<DomainException>(
            () => _service.CaptureAsync(_merchantId, payment.Id));
    }

    // ---------- Estorno ----------

    [Fact]
    public async Task Refund_PagamentoCapturado_ZeraOSaldo()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(10_000), null);
        await _service.CaptureAsync(_merchantId, payment.Id);

        var refunded = await _service.RefundAsync(_merchantId, payment.Id);

        Assert.Equal("Refunded", refunded.Status);

        var balance = await _merchants.GetBalanceAsync(_merchantId);
        Assert.Equal(0, balance.AvailableInCents);
    }

    [Fact]
    public async Task Refund_PagamentoNaoCapturado_LancaDomainException()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(), null);

        await Assert.ThrowsAsync<DomainException>(
            () => _service.RefundAsync(_merchantId, payment.Id));
    }

    // ---------- Cancelamento ----------

    [Fact]
    public async Task Void_PagamentoAutorizado_CancelaSemMexerNoLedger()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(), null);

        var voided = await _service.VoidAsync(_merchantId, payment.Id);

        Assert.Equal("Voided", voided.Status);
        Assert.Empty(_db.Context.LedgerEntries);
    }

    // ---------- Isolamento entre lojistas ----------

    [Fact]
    public async Task Get_PagamentoDeOutroLojista_LancaNotFound()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(), null);
        var outroLojista = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetAsync(outroLojista, payment.Id));
    }

    // ---------- Eventos ----------

    [Fact]
    public async Task FluxoCompleto_RegistraTodosOsEventos()
    {
        var (payment, _) = await _service.AuthorizeAsync(_merchantId, NewRequest(), null);
        await _service.CaptureAsync(_merchantId, payment.Id);
        await _service.RefundAsync(_merchantId, payment.Id);

        var events = await _service.GetEventsAsync(_merchantId, payment.Id);

        Assert.Equal(
            ["payment.authorized", "payment.captured", "payment.refunded"],
            events.Select(e => e.Type).ToArray());
    }
}
