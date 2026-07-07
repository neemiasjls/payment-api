using Microsoft.EntityFrameworkCore;
using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services.Dtos;
using PaymentGateway.Services.Security;

namespace PaymentGateway.Services;

public interface IMerchantService
{
    Task<MerchantCreatedResponse> RegisterAsync(CreateMerchantRequest request, CancellationToken ct = default);
    Task<Merchant?> FindByApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<BalanceResponse> GetBalanceAsync(Guid merchantId, CancellationToken ct = default);
}

public class MerchantService : IMerchantService
{
    private readonly AppDbContext _db;

    public MerchantService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<MerchantCreatedResponse> RegisterAsync(CreateMerchantRequest request, CancellationToken ct = default)
    {
        var emailInUse = await _db.Merchants.AnyAsync(m => m.Email == request.Email, ct);
        if (emailInUse)
            throw new DomainException("Já existe um lojista cadastrado com este e-mail.");

        var apiKey = ApiKeyHasher.GenerateApiKey();

        var merchant = new Merchant
        {
            Name = request.Name,
            Email = request.Email,
            WebhookUrl = request.WebhookUrl,
            ApiKeyHash = ApiKeyHasher.Hash(apiKey)
        };

        _db.Merchants.Add(merchant);
        await _db.SaveChangesAsync(ct);

        return new MerchantCreatedResponse(merchant.Id, merchant.Name, merchant.Email, apiKey);
    }

    public Task<Merchant?> FindByApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var hash = ApiKeyHasher.Hash(apiKey);
        return _db.Merchants.FirstOrDefaultAsync(m => m.ApiKeyHash == hash, ct);
    }

    public async Task<BalanceResponse> GetBalanceAsync(Guid merchantId, CancellationToken ct = default)
    {
        // O saldo é sempre derivado do ledger (créditos - débitos na conta
        // do lojista), nunca um campo mutável — impossível "perder" a conta.
        var entries = _db.LedgerEntries
            .Where(e => e.MerchantId == merchantId && e.Account == LedgerAccount.MerchantPayable);

        var credits = await entries
            .Where(e => e.Type == LedgerEntryType.Credit)
            .SumAsync(e => e.AmountInCents, ct);

        var debits = await entries
            .Where(e => e.Type == LedgerEntryType.Debit)
            .SumAsync(e => e.AmountInCents, ct);

        return new BalanceResponse(merchantId, credits - debits, "BRL");
    }
}
