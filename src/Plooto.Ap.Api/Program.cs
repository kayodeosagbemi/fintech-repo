using Microsoft.EntityFrameworkCore;
using Plooto.Ap.Api;
using Microsoft.AspNetCore.Authentication;
using Plooto.Ap.Api.Auth;
using Plooto.Ap.Api.Endpoints;
using Plooto.Ap.Api.Middleware;
using Plooto.Ap.Infrastructure;
using Plooto.Ap.Infrastructure.Persistence;
using Plooto.Ap.Messaging;
using Plooto.Ap.Services.Notification;
using Plooto.Ap.Services.Payment;
using Plooto.Ap.Services.Validation;

var builder = WebApplication.CreateBuilder(args);

// SQLite file lives in <content root>/data (git-ignored). Tests/hosts may override ConnectionStrings:Ap.
var connectionString = builder.Configuration.GetConnectionString("Ap");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
    Directory.CreateDirectory(dataDir);
    connectionString = $"Data Source={Path.Combine(dataDir, "ap.db")}";
}

builder.Services.AddDbContext<ApDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddApRepositories();

// Every "service" from the design diagram runs inside this one host (PLAN_1 section 4): 1 deployable, 4 BackgroundServices.
builder.Services.AddApMessaging(o => builder.Configuration.GetSection("Outbox").Bind(o));
builder.Services.AddValidationServices();
builder.Services.AddPaymentServices(p => builder.Configuration.GetSection("PaymentProcessor").Bind(p),
    r => builder.Configuration.GetSection("SimulatedRail").Bind(r));
builder.Services.AddNotificationServices();
builder.Services.AddScoped<PaymentOrchestrator>();
builder.Services.AddScoped<IdempotencyFilter>();

builder.Services
    .AddAuthentication(ApiKeyAuthenticationOptions.Scheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.Scheme, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

// Fail fast: a migration failure throws here and the process exits with the EF Core exception logged. No silent partial schema.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApDbContext>();
    await db.Database.MigrateAsync();
    // WAL lets the API and the background consumers read while another writes. Persistent once set on the file.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    if (app.Environment.IsDevelopment())
        await DevSeeder.SeedAsync(db);
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// Every company-scoped route requires a valid key AND that the key belongs to the company in the URL.
var scoped = app.MapGroup("/api/v1/companies/{companyId:guid}")
    .RequireAuthorization()
    .AddEndpointFilter<CompanyScopeFilter>();
app.MapCompanyEndpoints(scoped);
scoped.MapPreferencesEndpoints();
scoped.MapContractorEndpoints();
scoped.MapInvoiceEndpoints();
scoped.MapPaymentEndpoints();
scoped.MapApprovalEndpoints();
scoped.MapStatementEndpoint();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
