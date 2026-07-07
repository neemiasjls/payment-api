using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Services.Security;

namespace PaymentGateway.Api.Setup;

/// <summary>
/// Semeia um lojista de demonstração em ambiente de desenvolvimento,
/// para testar a API sem precisar se cadastrar antes.
/// </summary>
public static class DbSeeder
{
    public const string DemoApiKey = "pk_test_demo_1234567890abcdef";

    public static void Seed(AppDbContext db)
    {
        if (db.Merchants.Any())
            return;

        db.Merchants.Add(new Merchant
        {
            Name = "Loja Demo",
            Email = "demo@lojademo.com.br",
            ApiKeyHash = ApiKeyHasher.Hash(DemoApiKey)
        });

        db.SaveChanges();
    }
}
