using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PaymentGateway.Data;
using PaymentGateway.Services.Webhooks;

namespace PaymentGateway.Tests.Integration;

/// <summary>
/// Sobe a API COMPLETA em memória (pipeline real: middlewares, rate limiter,
/// controllers, EF Core), trocando apenas o banco por um SQLite em memória.
/// É o mais perto de produção que um teste automatizado consegue chegar.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ambiente "Testing": o Program pula migrations e seed —
        // o schema é criado aqui pela própria fábrica de testes.
        builder.UseEnvironment("Testing");

        _connection.Open();

        builder.ConfigureServices(services =>
        {
            // Remove o registro original do DbContext (apontando para o
            // arquivo .db) e registra um novo usando a conexão em memória.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            // O WebhookDispatcher fica de fora dos testes de integração: ele
            // consumiria a MESMA conexão SQLite em memória em outra thread, e
            // conexões SQLite não suportam uso concorrente (testes flutuariam).
            var dispatcher = services.Single(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(WebhookDispatcher));
            services.Remove(dispatcher);
        });
    }

    public void EnsureSchemaCreated()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}
