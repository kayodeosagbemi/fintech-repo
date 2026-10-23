using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Infrastructure.Persistence;

namespace Plooto.Ap.Tests.TestSupport;

/// <summary>
/// In-memory SQLite that behaves like production (real migrations, real unique/CHECK constraints).
/// The connection stays open for the test's lifetime because an in-memory DB dies with its connection.
/// </summary>
public sealed class SqliteTestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = CreateContext();
        ctx.Database.Migrate();
    }

    /// <summary>A fresh context (own change tracker) over the shared connection.</summary>
    public ApDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
