using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Domain.Entities;
using Plooto.Ap.Infrastructure;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging;
using Plooto.Ap.Messaging.Channels;
using Plooto.Ap.Services.Notification;
using Plooto.Ap.Services.Payment;
using Plooto.Ap.Services.Validation;

namespace Plooto.Ap.Services.Tests.TestSupport;

/// <summary>
/// The production service graph over a temp-file SQLite DB. A file (not :memory:) is used because the real background
/// services run concurrently, each on its own connection. Hosted services are NOT started unless <see cref="StartAsync"/>
/// is called, so most tests drive the dispatcher/consumers step by step.
/// </summary>
public sealed class PipelineHost : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ap-test-{Guid.NewGuid():N}.db");
    private readonly List<IHostedService> _started = new();

    public ServiceProvider Services { get; }

    public PipelineHost(Action<PaymentProcessorOptions>? processor = null, Action<OutboxOptions>? outbox = null,
        Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddDbContext<ApDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddApRepositories();
        services.AddApMessaging(o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.SweepInterval = TimeSpan.FromSeconds(5);
            o.StuckAfter = TimeSpan.FromSeconds(5);
            outbox?.Invoke(o);
        });
        services.AddValidationServices();
        services.AddPaymentServices(processor, r => r.Delay = TimeSpan.FromMilliseconds(5));
        services.AddNotificationServices();
        overrides?.Invoke(services);
        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApDbContext>().Database.Migrate();
    }

    public async Task StartAsync()
    {
        foreach (var svc in Services.GetServices<IHostedService>())
        {
            await svc.StartAsync(CancellationToken.None);
            _started.Add(svc);
        }
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<IServiceProvider, Task> work) =>
        WithScopeAsync<int>(async sp => { await work(sp); return 0; });

    public Task<(Company Company, Contractor Contractor, Invoice Invoice)> SeedAsync(string slug = "acme", decimal amount = 100m,
        bool requireApproval = false, decimal threshold = 0m, string invoiceNumber = "INV-1") =>
        WithScopeAsync(sp => ServiceTestHarness.SeedAsync(sp.GetRequiredService<ApDbContext>(), slug, amount, requireApproval, threshold, invoiceNumber));

    /// <summary>Runs the real create-payment path (validate, then persist) the way the API orchestrator does.</summary>
    public Task<ServiceResult<Domain.Entities.Payment>> CreatePaymentAsync(Guid companyId, Guid invoiceId, string key) =>
        WithScopeAsync(async sp =>
        {
            var validated = await sp.GetRequiredService<IValidationService>().ValidatePaymentAsync(companyId, invoiceId, default);
            return await sp.GetRequiredService<IPaymentService>()
                .CreatePaymentAsync(validated.Value!, key, PaymentMethod.Single, null, default);
        });

    public Task<T> QueryAsync<T>(Func<ApDbContext, Task<T>> query) =>
        WithScopeAsync(sp => query(sp.GetRequiredService<ApDbContext>()));

    /// <summary>Polls until <paramref name="condition"/> holds (the pipeline is asynchronous by design).</summary>
    public async Task<bool> WaitUntilAsync(Func<ApDbContext, Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (await QueryAsync(condition)) return true;
            await Task.Delay(25);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var svc in _started) await svc.StopAsync(CancellationToken.None);
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { /* best effort */ }
        }
    }
}
