using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plooto.Ap.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` so migrations can be generated without booting the API host.</summary>
internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApDbContext>
{
    public ApDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApDbContext>().UseSqlite("Data Source=design-time.db").Options;
        return new ApDbContext(options);
    }
}
