using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Infrastructure.Repositories;
using Plooto.Ap.Tests.TestSupport;

namespace Plooto.Ap.Tests.Infrastructure;

public class IdempotencyTakeoverTests
{
    [Fact]
    public async Task TryTakeOverStale_FreshRecordNotTaken_StaleTakenExactlyOnce()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var co = await TestData.AddCompanyAsync(ctx);
        var store = new IdempotencyStore(ctx);
        Assert.True(await store.TryBeginAsync(co.Id, "k", "h1", default));

        Assert.False(await store.TryTakeOverStaleAsync(co.Id, "k", "h1", TimeSpan.FromSeconds(30), default));   // fresh

        var old = DateTimeOffset.UtcNow.AddMinutes(-5).UtcTicks;
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE IdempotencyRecords SET CreatedAt = {old} WHERE Key = 'k'");
        Assert.True(await store.TryTakeOverStaleAsync(co.Id, "k", "h1", TimeSpan.FromSeconds(30), default));
        Assert.False(await store.TryTakeOverStaleAsync(co.Id, "k", "h1", TimeSpan.FromSeconds(30), default));   // fresh again
    }

    [Fact]
    public async Task TryTakeOverStale_CompletedRecord_IsNeverTaken()
    {
        using var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        var co = await TestData.AddCompanyAsync(ctx);
        var store = new IdempotencyStore(ctx);
        await store.TryBeginAsync(co.Id, "k", "h", default);
        await store.CompleteAsync(co.Id, "k", 201, "{}", default);
        var old = DateTimeOffset.UtcNow.AddMinutes(-5).UtcTicks;
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE IdempotencyRecords SET CreatedAt = {old} WHERE Key = 'k'");
        Assert.False(await store.TryTakeOverStaleAsync(co.Id, "k", "h", TimeSpan.FromSeconds(30), default));
    }
}
