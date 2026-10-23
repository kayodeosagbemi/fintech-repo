using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Api.Middleware;
using Plooto.Ap.Api.Tests.TestSupport;
using Plooto.Ap.Domain.Common;
using Plooto.Ap.Infrastructure.Persistence;

namespace Plooto.Ap.Api.Tests;

public class ErrorHandlingMiddlewareTests
{
    private static async Task<(int Status, string Body)> RunAsync(Exception toThrow)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        var mw = new ErrorHandlingMiddleware(_ => throw toThrow, NullLogger<ErrorHandlingMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task NonDomainInvalidOperation_Returns500_WithGenericBody()
    {
        var (status, body) = await RunAsync(new InvalidOperationException("secret connection string xyz"));
        Assert.Equal(500, status);
        Assert.DoesNotContain("secret", body);
        Assert.Contains("An unexpected error occurred.", body);
    }

    [Fact]
    public async Task DomainRuleException_Returns409_WithItsMessage()
    {
        var (status, body) = await RunAsync(new DomainRuleException("Invoice is already paid."));
        Assert.Equal(409, status);
        Assert.Contains("Invoice is already paid.", body);
    }

    [Fact]
    public async Task ArgumentException_Returns400_WithoutParameterSuffix()
    {
        var (status, body) = await RunAsync(new ArgumentException("Amount must be positive.", "amount"));
        Assert.Equal(400, status);
        Assert.Contains("Amount must be positive.", body);
        Assert.DoesNotContain("Parameter", body);
    }

    [Fact]
    public async Task BadHttpRequest_Returns400_Generic()
    {
        var (status, body) = await RunAsync(new BadHttpRequestException("Failed to read parameter at /secret/path"));
        Assert.Equal(400, status);
        Assert.DoesNotContain("secret", body);
    }
}

public class ReviewFixApiTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static Task<HttpResponseMessage> PostRaw(ApiSession s, string path, string json, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, s.Path(path)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.TryAddWithoutValidation(HeaderNames.IdempotencyKey, key);
        return s.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostBatch(ApiSession s, IEnumerable<Guid> ids, string key) =>
        PostRaw(s, "/payments/batch", System.Text.Json.JsonSerializer.Serialize(
            new CreateBatchPaymentRequest(ids.Select(i => new BatchPaymentItem(i)).ToList())), key);

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, "/payments", "{ not json", "bad-json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, "/invoices", "{ not json", null)).StatusCode);
    }

    [Fact]
    public async Task BatchWithNullItem_EmptyGuid_OrNullItems_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, "/payments/batch", "{\"items\":[null]}", "b1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, "/payments/batch", "{\"items\":[{\"invoiceId\":\"00000000-0000-0000-0000-000000000000\"}]}", "b2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, "/payments/batch", "{\"items\":null}", "b3")).StatusCode);
    }

    [Fact]
    public async Task CreatePayment_EmptyInvoiceId_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostPaymentAsync(Guid.Empty)).StatusCode);
    }

    [Fact]
    public async Task IdempotencyKey_Over128Chars_Returns400_And128IsAccepted()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostPaymentAsync(inv.Id, new string('x', 129))).StatusCode);
        var batch = await PostBatch(s, [inv.Id], new string('y', 128));
        Assert.Equal(HttpStatusCode.MultiStatus, batch.StatusCode);
        var body = (await batch.Content.ReadFromJsonAsync<BatchPaymentResponse>())!;
        Assert.Equal("Created", body.Results.Single().Outcome);
    }

    [Fact]
    public async Task PaddedKey_IsTrimmed_AndReplaysAsTrimmedKey()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        var first = await s.PostPaymentAsync(inv.Id, " abc ");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await s.PostPaymentAsync(inv.Id, "abc");
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((await first.Content.ReadFromJsonAsync<PaymentResponse>())!.Id,
            (await second.Content.ReadFromJsonAsync<PaymentResponse>())!.Id);
    }

    [Fact]
    public async Task AllRejectedBatch_DoesNotCacheResponse_SoSameKeyCanBeRetried()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        var first = await PostBatch(s, [Guid.NewGuid()], "retry-me");
        Assert.Equal(HttpStatusCode.MultiStatus, first.StatusCode);
        Assert.Equal("Rejected", (await first.Content.ReadFromJsonAsync<BatchPaymentResponse>())!.Results.Single().Outcome);

        var second = await PostBatch(s, [inv.Id], "retry-me");
        Assert.Equal(HttpStatusCode.MultiStatus, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("Created", (await second.Content.ReadFromJsonAsync<BatchPaymentResponse>())!.Results.Single().Outcome);
    }

    [Fact]
    public async Task CompanyRoute_NonHyphenatedOwnId_Returns200()
    {
        var s = await factory.CreateSessionAsync();
        var r = await s.Client.GetAsync($"{ApiRoutes.Companies}/{s.CompanyId:N}/invoices");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task SetRecordStateAsync(ApiSession s, string key, long createdAtTicks)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE IdempotencyRecords SET Status = 'InProgress', CreatedAt = {createdAtTicks} WHERE CompanyId = {s.CompanyId} AND Key = {key}");
    }

    [Fact]
    public async Task StaleInProgressRecord_IsTakenOver_AndSameKeyRequestSucceeds()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        var first = await s.PostPaymentAsync(inv.Id, "crashed");
        var paymentId = (await first.Content.ReadFromJsonAsync<PaymentResponse>())!.Id;
        // Simulate a process that died after claiming the key: InProgress and 5 minutes old.
        await SetRecordStateAsync(s, "crashed", DateTimeOffset.UtcNow.AddMinutes(-5).UtcTicks);

        var retry = await s.PostPaymentAsync(inv.Id, "crashed");

        Assert.True(retry.IsSuccessStatusCode, $"got {retry.StatusCode}");
        Assert.Equal(paymentId, (await retry.Content.ReadFromJsonAsync<PaymentResponse>())!.Id);
        Assert.Single((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task FreshInProgressRecord_Returns409WithRetryAfter()
    {
        var s = await factory.CreateSessionAsync();
        var inv = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(inv.Id, "inflight")).StatusCode);
        await SetRecordStateAsync(s, "inflight", DateTimeOffset.UtcNow.UtcTicks);

        var r = await s.PostPaymentAsync(inv.Id, "inflight");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
    }
}
