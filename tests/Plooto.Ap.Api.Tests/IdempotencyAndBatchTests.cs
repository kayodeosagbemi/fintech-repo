using System.Net;
using System.Net.Http.Json;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Api.Tests.TestSupport;

namespace Plooto.Ap.Api.Tests;

public class IdempotencyTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static Task<HttpResponseMessage> PostBatchAsync(ApiSession s, IEnumerable<Guid> invoiceIds, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, s.Path("/payments/batch"))
        {
            Content = JsonContent.Create(new CreateBatchPaymentRequest(invoiceIds.Select(i => new BatchPaymentItem(i)).ToList())),
        };
        if (key is not null) request.Headers.Add(HeaderNames.IdempotencyKey, key);
        return s.Client.SendAsync(request);
    }

    [Fact]
    public async Task CreatePayment_MissingIdempotencyKey_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        var r = await s.Client.PostAsJsonAsync(s.Path("/payments"), new CreatePaymentRequest(invoice.Id));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task BatchPayment_MissingIdempotencyKey_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBatchAsync(s, [invoice.Id], null)).StatusCode);
    }

    [Fact]
    public async Task CreatePayment_DuplicateKeySameBody_ReturnsCachedResponse_DoesNotCreateSecondPayment()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);

        var first = await s.PostPaymentAsync(invoice.Id, "same-key");
        var second = await s.PostPaymentAsync(invoice.Id, "same-key");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);   // cached response replayed verbatim
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        var p1 = (await first.Content.ReadFromJsonAsync<PaymentResponse>())!;
        var p2 = (await second.Content.ReadFromJsonAsync<PaymentResponse>())!;
        Assert.Equal(p1.Id, p2.Id);
        Assert.Single((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task CreatePayment_DuplicateKeyDifferentBody_Returns409()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var inv1 = await s.CreateInvoiceAsync(c.Id, "INV-1");
        var inv2 = await s.CreateInvoiceAsync(c.Id, "INV-2");
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(inv1.Id, "reused")).StatusCode);

        var r = await s.PostPaymentAsync(inv2.Id, "reused");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Single((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task CreatePayment_ConcurrentDuplicateRequests_OnlyOnePaymentIsCreated()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => s.PostPaymentAsync(invoice.Id, "race-key")));

        // Winner executes; the rest are served the stored response or told the original is still in progress.
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"unexpected {r.StatusCode}"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);
        var ids = new HashSet<Guid>();
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
            ids.Add((await r.Content.ReadFromJsonAsync<PaymentResponse>())!.Id);
        Assert.Single(ids);
        var payments = (await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!;
        Assert.Single(payments);
        Assert.Equal(ids.Single(), payments[0].Id);
    }

    [Fact]
    public async Task CreatePayment_ConcurrentDifferentKeysSameInvoice_OnlyOnePaymentIsCreated()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => s.PostPaymentAsync(invoice.Id, $"key-{i}")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task RejectedRequest_ReleasesTheKey_SoItCanBeRetriedAfterFixingTheProblem()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var invoice = await s.CreateInvoiceAsync(c.Id);
        // First attempt targets a different (unknown) invoice with this key -> 404, which must not poison the key...
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostPaymentAsync(Guid.NewGuid(), "fixable")).StatusCode);
        // ...a different body with the same key is "different request" only if the first was stored; it wasn't.
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(invoice.Id, "fixable")).StatusCode);
    }

    [Fact]
    public async Task SameKeyInTwoCompanies_AreIndependent()
    {
        var a = await factory.CreateSessionAsync("A");
        var b = await factory.CreateSessionAsync("B");
        var ia = await a.CreateInvoiceAsync((await a.CreateContractorAsync()).Id);
        var ib = await b.CreateInvoiceAsync((await b.CreateContractorAsync()).Id);
        Assert.Equal(HttpStatusCode.Created, (await a.PostPaymentAsync(ia.Id, "shared")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await b.PostPaymentAsync(ib.Id, "shared")).StatusCode);
    }

    [Fact]
    public async Task FailedPayment_RetriedWithSameKey_ReplaysTheFailure_ByDesign()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id, amount: 10.13m);   // .13 = declined
        var first = (await (await s.PostPaymentAsync(invoice.Id, "k")).Content.ReadFromJsonAsync<PaymentResponse>())!;
        await s.WaitForPaymentStatusAsync(first.Id, "Failed");

        var replay = await s.PostPaymentAsync(invoice.Id, "k");

        Assert.Equal(first.Id, (await replay.Content.ReadFromJsonAsync<PaymentResponse>())!.Id);
        Assert.Equal(HttpStatusCode.Created, (await s.PostPaymentAsync(invoice.Id, "k-new")).StatusCode);   // new key = deliberate retry
    }

    [Fact]
    public async Task BatchPayment_DuplicateKey_ReturnsCachedBatchResponse()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var ids = new[] { (await s.CreateInvoiceAsync(c.Id, "INV-1")).Id, (await s.CreateInvoiceAsync(c.Id, "INV-2")).Id };

        var first = await PostBatchAsync(s, ids, "batch-key");
        var second = await PostBatchAsync(s, ids, "batch-key");

        Assert.Equal((HttpStatusCode)207, first.StatusCode);
        Assert.Equal((HttpStatusCode)207, second.StatusCode);
        var b1 = (await first.Content.ReadFromJsonAsync<BatchPaymentResponse>())!;
        var b2 = (await second.Content.ReadFromJsonAsync<BatchPaymentResponse>())!;
        Assert.Equal(b1.BatchId, b2.BatchId);
        Assert.Equal(2, (await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!.Count);
    }
}

public class BatchPaymentTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static async Task<(HttpResponseMessage Response, BatchPaymentResponse? Body)> BatchAsync(ApiSession s, IEnumerable<Guid> ids, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, s.Path("/payments/batch"))
        {
            Content = JsonContent.Create(new CreateBatchPaymentRequest(ids.Select(i => new BatchPaymentItem(i)).ToList())),
        };
        request.Headers.Add(HeaderNames.IdempotencyKey, key ?? Guid.NewGuid().ToString());
        var response = await s.Client.SendAsync(request);
        var body = (int)response.StatusCode == 207 ? await response.Content.ReadFromJsonAsync<BatchPaymentResponse>() : null;
        return (response, body);
    }

    [Fact]
    public async Task AllValid_Returns207_WithEveryItemCreated_AndAllComplete()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await s.CreateInvoiceAsync(c.Id, $"INV-{i}", 10m + i)).Id);

        var (response, body) = await BatchAsync(s, ids);

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        Assert.All(body!.Results, r => { Assert.Equal(201, r.StatusCode); Assert.Equal("Created", r.Outcome); Assert.NotNull(r.PaymentId); });
        var batchPayments = (await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path($"/payments?batchId={body.BatchId}")))!;
        Assert.Equal(3, batchPayments.Count);
        Assert.All(batchPayments, p => Assert.Equal("Batch", p.Method));
        foreach (var p in batchPayments) await s.WaitForPaymentStatusAsync(p.Id, "Completed");
    }

    [Fact]
    public async Task OneInvalidInvoice_ReturnsPartialSuccess_WithPerItemErrors()
    {
        var s = await factory.CreateSessionAsync();
        var c = await s.CreateContractorAsync();
        var good = await s.CreateInvoiceAsync(c.Id, "INV-1");
        var bogus = Guid.NewGuid();

        var (response, body) = await BatchAsync(s, [good.Id, bogus]);

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        Assert.Equal(201, body!.Results.Single(r => r.InvoiceId == good.Id).StatusCode);
        var bad = body.Results.Single(r => r.InvoiceId == bogus);
        Assert.Equal(404, bad.StatusCode);
        Assert.Null(bad.PaymentId);
        Assert.False(string.IsNullOrEmpty(bad.Error));
        Assert.Single((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task DuplicateInvoiceAcrossTwoBatchCalls_SecondAttemptRejectedPerItem()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);

        var (_, first) = await BatchAsync(s, [invoice.Id]);
        var (_, second) = await BatchAsync(s, [invoice.Id]);   // different idempotency key: a genuinely new call

        Assert.Equal(201, first!.Results.Single().StatusCode);
        Assert.Equal(409, second!.Results.Single().StatusCode);
        Assert.Single((await s.Client.GetFromJsonAsync<List<PaymentResponse>>(s.Path("/payments")))!);
    }

    [Fact]
    public async Task SameInvoiceTwiceInOneBatch_SecondItemRejected()
    {
        var s = await factory.CreateSessionAsync();
        var invoice = await s.CreateInvoiceAsync((await s.CreateContractorAsync()).Id);

        var (_, body) = await BatchAsync(s, [invoice.Id, invoice.Id]);

        Assert.Equal([201, 409], body!.Results.Select(r => r.StatusCode));
    }

    [Fact]
    public async Task EmptyOrOversizedBatch_Returns400()
    {
        var s = await factory.CreateSessionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await BatchAsync(s, [])).Response.StatusCode);
        var tooMany = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await BatchAsync(s, tooMany)).Response.StatusCode);
    }

    [Fact]
    public async Task BatchAboveApprovalThreshold_EachItemWaitsForItsOwnApproval()
    {
        var s = await factory.CreateSessionAsync();
        (await s.Client.PutAsJsonAsync(s.Path("/preferences"), new UpdateCompanyPreferencesRequest("USD", true, 50m, "ACH"))).EnsureSuccessStatusCode();
        var c = await s.CreateContractorAsync();
        var ids = new[] { (await s.CreateInvoiceAsync(c.Id, "INV-1", 100m)).Id, (await s.CreateInvoiceAsync(c.Id, "INV-2", 20m)).Id };

        var (_, body) = await BatchAsync(s, ids);

        var gated = await s.GetPaymentAsync(body!.Results[0].PaymentId!.Value);
        Assert.Equal("Pending", gated.Payment.Status);
        await s.WaitForPaymentStatusAsync(body.Results[1].PaymentId!.Value, "Completed");   // under threshold: not gated
        Assert.Single((await s.Client.GetFromJsonAsync<List<ApprovalResponse>>(s.Path("/approvals?status=Pending")))!);
    }
}
