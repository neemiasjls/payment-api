using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentGateway.Data;

namespace PaymentGateway.Tests;

/// <summary>
/// Banco SQLite em memória para os testes: comporta-se como o banco real
/// (constraints, índices únicos, SQL de verdade), mas some ao fechar a
/// conexão — cada teste roda isolado do outro.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Context { get; }

    public TestDb()
    {
        // A conexão precisa ficar aberta: o SQLite ":memory:" é destruído
        // quando a última conexão fecha.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
