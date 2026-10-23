using System.Net;
using System.Text;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Client.Sdk;

namespace Plooto.Ap.Client.Tests;

public class ApiClientTests
{
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public int Calls;
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var step = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(step(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly Guid CompanyId = Guid.NewGuid();
    private static readonly string PaymentJson =
        $"{{\"id\":\"{Guid.NewGuid()}\",\"companyId\":\"{CompanyId}\",\"invoiceId\":\"{Guid.NewGuid()}\",\"contractorId\":\"{Guid.NewGuid()}\",\"amount\":5,\"currency\":\"USD\",\"status\":\"Approved\",\"method\":\"Single\",\"createdAt\":\"2026-01-01T00:00:00+00:00\"}}";

    private static (ApiClient Client, ScriptedHandler Handler) Create(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    {
        var handler = new ScriptedHandler(script);
        var session = new ClientSession { BaseUrl = "http://api.test", ApiKey = "secret-key", CompanyId = CompanyId };
        return (new ApiClient(new HttpClient(handler), session, new ApiClientOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }), handler);
    }

    [Fact]
    public async Task SendsBearerKeyAndIdempotencyHeader_ToTheCompanyScopedRoute()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.Created, PaymentJson));

        var r = await client.CreatePaymentAsync(Guid.NewGuid(), "key-123");

        Assert.True(r.IsSuccess);
        var req = handler.Requests.Single();
        Assert.Equal($"http://api.test/api/v1/companies/{CompanyId}/payments", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", req.Headers.Authorization.Parameter);
        Assert.Equal("key-123", req.Headers.GetValues(HeaderNames.IdempotencyKey).Single());
    }

    [Fact]
    public async Task ServerErrors_AreRetriedWithBackoff_ThenSucceed()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway), _ => Json(HttpStatusCode.OK, "[]"));

        var r = await client.ListInvoicesAsync();

        Assert.True(r.IsSuccess);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ServerErrors_AreRetriedOnlyUpToTheLimit()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var r = await client.ListInvoicesAsync();
        Assert.Equal(500, r.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ClientErrors_AreNeverRetried_AndTheServerMessageIsSurfaced(HttpStatusCode code)
    {
        var (client, handler) = Create(_ => Json(code, "{\"error\":\"nope, because reasons\"}"));

        var r = await client.ListInvoicesAsync();

        Assert.False(r.IsSuccess);
        Assert.Equal((int)code, r.StatusCode);
        Assert.Equal("nope, because reasons", r.Error);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NetworkErrors_AreRetried_ThenReportedAsStatusZero()
    {
        var (client, handler) = Create(_ => throw new HttpRequestException("connection refused"));

        var r = await client.ListInvoicesAsync();

        Assert.Equal(0, r.StatusCode);
        Assert.Contains("Could not reach the API", r.Error);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PostWithoutIdempotencyKey_IsNotRetried_BecauseItCouldDuplicate()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var r = await client.CreateContractorAsync(new CreateContractorRequest("Jane", "jane@example.com", "acct"));
        Assert.Equal(503, r.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PostWithIdempotencyKey_IsRetried_WithTheSameKeyEveryAttempt()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Json(HttpStatusCode.Created, PaymentJson));

        var r = await client.CreatePaymentAsync(Guid.NewGuid(), "same-key");

        Assert.True(r.IsSuccess);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Requests, req => Assert.Equal("same-key", req.Headers.GetValues(HeaderNames.IdempotencyKey).Single()));
    }

    [Fact]
    public async Task NonJsonErrorBody_FallsBackToRawText()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>proxy says no</html>") });
        var r = await client.ListInvoicesAsync();
        Assert.Contains("proxy says no", r.Error);
    }

    [Fact]
    public async Task CreateCompany_DoesNotSendCredentials()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.Created,
            $"{{\"id\":\"{Guid.NewGuid()}\",\"name\":\"A\",\"slug\":\"a\",\"createdAt\":\"2026-01-01T00:00:00+00:00\",\"apiKey\":\"k\"}}"));
        var r = await client.CreateCompanyAsync("A");
        Assert.True(r.IsSuccess);
        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public async Task CompanyScopedCall_WithoutACompany_ThrowsAHelpfulError()
    {
        var client = new ApiClient(new HttpClient(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), new ClientSession());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListInvoicesAsync());
        Assert.Contains("No company is configured", ex.Message);
    }

    [Fact]
    public async Task QueryParameters_AreEscaped()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, "[]"));
        await client.ListContractorsAsync(email: "a+b@example.com");
        Assert.EndsWith("/contractors?email=a%2Bb%40example.com", handler.Requests.Single().RequestUri!.ToString());
    }
}
