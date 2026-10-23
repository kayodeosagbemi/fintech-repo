using System.Globalization;
using Plooto.Ap.Api.Contracts;
using Plooto.Ap.Client.Sdk;
using Plooto.Ap.Client.UseCases;

// Thin I/O shell: prompts, a menu switch, and printing. Every operation is a call into Plooto.Ap.Client.UseCases, which is
// what a Blazor front-end would call too (PLAN_1 sections 6 and 8). No business logic lives here.

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var suite = new ClientSuite(http);
var ui = new ConsoleUi();

// Optional defaults so a demo doesn't need retyping: PLOOTO_API_URL, PLOOTO_COMPANY_ID, PLOOTO_API_KEY.
var envUrl = Environment.GetEnvironmentVariable("PLOOTO_API_URL");
if (!string.IsNullOrWhiteSpace(envUrl)) suite.Session.BaseUrl = envUrl.TrimEnd('/');
if (Guid.TryParse(Environment.GetEnvironmentVariable("PLOOTO_COMPANY_ID"), out var envCompany)) suite.Session.CompanyId = envCompany;
suite.Session.ApiKey = Environment.GetEnvironmentVariable("PLOOTO_API_KEY") ?? suite.Session.ApiKey;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    ui.PrintMenu(suite.Session);
    var choice = ui.Ask("Choose an option");
    if (choice is null) break;   // end of input (piped/redirected stdin)

    try
    {
        switch (choice.Trim())
        {
            case "1": ConfigureConnection(); break;
            case "2": await CreateCompanyAsync(); break;
            case "3": await CreateContractorAsync(); break;
            case "4": await CreateInvoiceAsync(); break;
            case "5": await ImportCsvAsync(); break;
            case "6": await DownloadTemplateAsync(); break;
            case "7": await CreatePaymentAsync(); break;
            case "8": await CreateBatchPaymentAsync(); break;
            case "9": await ResolveApprovalAsync(); break;
            case "10": await ViewPaymentAsync(); break;
            case "11": await ViewStatementAsync(); break;
            case "12": return;
            case "13": await ListInvoicesAsync(); break;
            case "14": await ListContractorsAsync(); break;
            case "15": await SetApprovalPolicyAsync(); break;
            default: ui.Error("Unknown option."); break;
        }
    }
    catch (OperationCanceledException) { ui.Error("Cancelled."); return; }
    catch (InvalidOperationException ex) { ui.Error(ex.Message); }
}

// ---- Menu handlers (prompt -> use case -> print) --------------------------------------------------------------------------

void ConfigureConnection()
{
    var url = ui.Ask($"API base URL [{suite.Session.BaseUrl}]");
    var company = ui.Ask("Company id (blank to keep current)");
    var key = ui.Ask("API key (blank to keep current)");
    Guid? companyId = suite.Session.CompanyId;
    if (!string.IsNullOrWhiteSpace(company))
    {
        if (!Guid.TryParse(company, out var parsed)) { ui.Error("Company id must be a GUID."); return; }
        companyId = parsed;
    }
    var result = suite.Configure.Execute(string.IsNullOrWhiteSpace(url) ? suite.Session.BaseUrl : url!,
        companyId, string.IsNullOrWhiteSpace(key) ? suite.Session.ApiKey : key);
    if (result.Success) ui.Ok("Connection settings saved for this session.");
    else ui.Error(result.Error!);
}

async Task CreateCompanyAsync()
{
    var name = ui.Ask("Company name");
    if (string.IsNullOrWhiteSpace(name)) { ui.Error("Name is required."); return; }
    var r = await suite.CreateCompany.ExecuteAsync(name, cts.Token);
    if (!ui.Report(r)) return;
    ui.Ok($"Company created. This session is now authenticated as it.");
    ui.Line($"  Company id : {r.Value!.Id}");
    ui.Line($"  API key    : {r.Value.ApiKey}   (shown once - save it to reconnect later)");
}

async Task CreateContractorAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var r = await suite.CreateContractor.ExecuteAsync(ui.Ask("Contractor name") ?? "", ui.Ask("Email") ?? "",
        ui.Ask("Payout account reference (e.g. acct_123)") ?? "", cts.Token);
    if (ui.Report(r)) ui.Ok($"Contractor {r.Value!.Name} <{r.Value.Email}> created ({r.Value.Id}).");
}

async Task CreateInvoiceAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var email = ui.Ask("Contractor email") ?? "";
    var number = ui.Ask("Invoice number") ?? "";
    if (!ui.TryDecimal(ui.Ask("Amount"), out var amount)) return;
    var currency = ui.Ask("Currency [blank = company default]");
    if (!ui.TryDate(ui.Ask("Due date (yyyy-MM-dd)"), out var due)) return;
    var description = ui.Ask("Description (optional)");
    var r = await suite.CreateInvoice.ExecuteAsync(email, number, amount, currency, due, description, cts.Token);
    if (ui.Report(r)) ui.Ok($"Invoice {r.Value!.InvoiceNumber} created for {r.Value.Amount:N2} {r.Value.Currency} ({r.Value.Id}).");
}

async Task ImportCsvAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var path = ui.Ask("Path to CSV file");
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { ui.Error($"File not found: {path}"); return; }
    await using var stream = File.OpenRead(path);
    var result = await suite.ImportInvoices.ExecuteAsync(stream, cts.Token);

    ui.Line($"Import finished: {result.Created.Count} created, {result.Errors.Count} failed, {result.Warnings.Count} warning(s).");
    foreach (var c in result.Created) ui.Ok($"  line {c.LineNumber,3}: {c.InvoiceNumber} -> {c.InvoiceId}");
    foreach (var w in result.Warnings) ui.Warn($"  line {w.LineNumber,3}: {w.Message}");
    foreach (var e in result.Errors) ui.Error($"  line {e.LineNumber,3}: {e.Reason}   [{e.RawLine}]");
}

async Task DownloadTemplateAsync()
{
    var path = ui.Ask("Save template to [invoice-template.csv]");
    if (string.IsNullOrWhiteSpace(path)) path = "invoice-template.csv";
    await using (var file = File.Create(path))
        await suite.DownloadTemplate.ExecuteAsync(file, cts.Token);
    ui.Ok($"Template written to {Path.GetFullPath(path)}");
}

async Task CreatePaymentAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var invoiceId = await ui.PickInvoiceAsync(suite, "Invoice id to pay", cts.Token);
    if (invoiceId is null) return;
    var key = ui.Ask("Idempotency key [blank = new attempt; reuse a key to retry safely]");
    var r = await suite.CreatePayment.ExecuteAsync(invoiceId.Value, string.IsNullOrWhiteSpace(key) ? null : key.Trim(), cts.Token);
    if (!ui.Report(r)) return;
    ui.Ok($"Payment {r.Value!.Id}: {r.Value.Status} ({r.Value.Amount:N2} {r.Value.Currency}). HTTP {r.StatusCode}");
    if (r.Value.Status == "Pending") ui.Warn("  Above the approval threshold: use option 9 to approve or reject it.");
    else ui.Line("  Processing is asynchronous: use option 10 to watch it complete.");
}

async Task CreateBatchPaymentAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var ids = ui.AskGuids("Invoice ids to pay (comma or space separated; blank = all Submitted invoices)");
    if (ids is null) return;
    if (ids.Count == 0)
    {
        var list = await suite.ListInvoices.ExecuteAsync("Submitted", cts.Token);
        if (!ui.Report(list)) return;
        ids = list.Value!.Select(i => i.Id).ToList();
        ui.Line($"  Using {ids.Count} Submitted invoice(s).");
        if (ids.Count == 0) return;
    }
    var key = ui.Ask("Idempotency key [blank = new batch]");
    var r = await suite.CreateBatchPayment.ExecuteAsync(ids, string.IsNullOrWhiteSpace(key) ? null : key.Trim(), cts.Token);
    if (!ui.Report(r)) return;
    ui.Ok($"Batch {r.Value!.BatchId} processed ({r.Value.Results.Count} item(s)):");
    foreach (var item in r.Value.Results)
    {
        if (item.PaymentId is not null) ui.Line($"  {item.InvoiceId}: {item.Outcome} -> payment {item.PaymentId}");
        else ui.Error($"  {item.InvoiceId}: {item.StatusCode} {item.Error}");
    }
}

async Task ResolveApprovalAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var pending = await suite.ResolveApproval.ListPendingAsync(cts.Token);
    if (!ui.Report(pending)) return;
    if (pending.Value!.Count == 0) { ui.Line("No approvals are waiting."); return; }
    foreach (var a in pending.Value) ui.Line($"  {a.Id}  payment {a.PaymentId}  invoice {a.InvoiceId}  requested {a.CreatedAt:u}");

    var idText = ui.Ask("Approval id");
    if (!Guid.TryParse(idText, out var approvalId)) { ui.Error("Approval id must be a GUID."); return; }
    var decision = ui.Ask("Decision: (a)pprove or (r)eject")?.Trim().ToLowerInvariant();
    if (decision is not ("a" or "approve" or "r" or "reject")) { ui.Error("Enter 'a' or 'r'."); return; }
    var email = ui.Ask("Approver email (optional)");
    var r = await suite.ResolveApproval.ExecuteAsync(approvalId, decision.StartsWith('a'), email, cts.Token);
    if (ui.Report(r)) ui.Ok($"Payment {r.Value!.Id} is now {r.Value.Status}.");
}

async Task ViewPaymentAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var id = ui.Ask("Payment id (blank = list all)");
    if (string.IsNullOrWhiteSpace(id))
    {
        var list = await suite.PaymentDetails.ListAsync(ct: cts.Token);
        if (!ui.Report(list)) return;
        foreach (var p in list.Value!) ui.Line($"  {p.Id}  {p.Status,-10} {p.Amount,12:N2} {p.Currency}  {p.CreatedAt:u}");
        if (list.Value.Count == 0) ui.Line("No payments yet.");
        return;
    }
    if (!Guid.TryParse(id, out var paymentId)) { ui.Error("Payment id must be a GUID."); return; }
    var r = await suite.PaymentDetails.ExecuteAsync(paymentId, cts.Token);
    if (!ui.Report(r)) return;
    var d = r.Value!;
    ui.Line($"Payment {d.Payment.Id}");
    ui.Line($"  Status      : {d.Payment.Status}{(d.Payment.FailureReason is null ? "" : $" ({d.Payment.FailureReason})")}");
    ui.Line($"  Amount      : {d.Payment.Amount:N2} {d.Payment.Currency} via {d.Payment.Rail}");
    ui.Line($"  Method      : {d.Payment.Method}{(d.Payment.BatchId is null ? "" : $" (batch {d.Payment.BatchId})")}");
    ui.Line($"  Created     : {d.Payment.CreatedAt:u}   Completed: {(d.Payment.CompletedAt?.ToString("u") ?? "-")}");
    ui.Line($"  Invoice     : {d.Invoice?.InvoiceNumber} ({d.Invoice?.Status})");
    ui.Line($"  Contractor  : {d.Contractor?.Name} <{d.Contractor?.Email}>");
    if (d.ApprovalId is not null) ui.Line($"  Approval    : {d.ApprovalId} ({d.ApprovalStatus})");
}

async Task ViewStatementAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    DateTimeOffset? from = null, to = null;
    var f = ui.Ask("From date yyyy-MM-dd (optional)");
    var t = ui.Ask("To date yyyy-MM-dd (optional)");
    if (!string.IsNullOrWhiteSpace(f)) { if (!ui.TryDate(f, out var fd)) return; from = new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); }
    if (!string.IsNullOrWhiteSpace(t)) { if (!ui.TryDate(t, out var td)) return; to = new DateTimeOffset(td.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero); }
    var r = await suite.Statement.ExecuteAsync(from, to, cts.Token);
    if (!ui.Report(r)) return;
    ui.Line($"Statement - {r.Value!.PaymentCount} payment(s), total paid {r.Value.TotalPaid:N2}");
    foreach (var l in r.Value.Lines)
        ui.Line($"  {l.Date:yyyy-MM-dd HH:mm}  {l.Type,-8} {l.CounterParty,-24} {l.Amount,12:N2}  balance {l.RunningBalance,12:N2} {l.Currency}");
}

async Task ListInvoicesAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var r = await suite.ListInvoices.ExecuteAsync(ui.Ask("Status filter (optional: Submitted, Approved, Paid, Void...)") is { Length: > 0 } s ? s : null, cts.Token);
    if (!ui.Report(r)) return;
    foreach (var i in r.Value!) ui.Line($"  {i.Id}  {i.InvoiceNumber,-12} {i.Status,-10} {i.Amount,12:N2} {i.Currency}  due {i.DueDate:yyyy-MM-dd}");
    if (r.Value.Count == 0) ui.Line("No invoices.");
}

async Task ListContractorsAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var r = await suite.ListContractors.ExecuteAsync(cts.Token);
    if (!ui.Report(r)) return;
    foreach (var c in r.Value!) ui.Line($"  {c.Id}  {c.Name,-20} {c.Email}");
    if (r.Value.Count == 0) ui.Line("No contractors.");
}

async Task SetApprovalPolicyAsync()
{
    if (!ui.RequireCompany(suite.Session)) return;
    var current = await suite.ApprovalPolicy.GetAsync(cts.Token);
    if (!ui.Report(current)) return;
    ui.Line($"Current: approval {(current.Value!.RequireApprovalAboveThreshold ? "required above " + current.Value.ApprovalThreshold.ToString("N2") : "not required")}");
    var on = ui.Ask("Require human approval for large payments? (y/n)")?.Trim().ToLowerInvariant();
    if (on is not ("y" or "n")) { ui.Error("Enter y or n."); return; }
    decimal threshold = 0;
    if (on == "y" && !ui.TryDecimal(ui.Ask("Approval threshold (payments above this need approval)"), out threshold)) return;
    var r = await suite.ApprovalPolicy.ExecuteAsync(on == "y", threshold, cts.Token);
    if (ui.Report(r)) ui.Ok("Approval policy updated.");
}

/// <summary>Console-only presentation helpers. Nothing here is business logic.</summary>
internal sealed class ConsoleUi
{
    public string? Ask(string prompt)
    {
        Console.Write($"{prompt}: ");
        return Console.ReadLine();
    }

    public void Line(string text) => Console.WriteLine(text);
    public void Ok(string text) => Colored(ConsoleColor.Green, text);
    public void Warn(string text) => Colored(ConsoleColor.Yellow, text);
    public void Error(string text) => Colored(ConsoleColor.Red, text);

    private static void Colored(ConsoleColor color, string text)
    {
        if (Console.IsOutputRedirected) { Console.WriteLine(text); return; }
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public void PrintMenu(ClientSession session)
    {
        Console.WriteLine();
        Console.WriteLine("=== Plooto AP - Local Test Console ===");
        Console.WriteLine($" API: {session.BaseUrl}   Company: {(session.CompanyId?.ToString() ?? "(none)")}   Key: {(string.IsNullOrEmpty(session.ApiKey) ? "(none)" : "set")}");
        Console.WriteLine("  1. Configure API base URL & API key");
        Console.WriteLine("  2. Create a company (bootstrap - returns an API key)");
        Console.WriteLine("  3. Create a contractor");
        Console.WriteLine("  4. Create a single invoice");
        Console.WriteLine("  5. Import invoices from CSV");
        Console.WriteLine("  6. Download a blank CSV invoice template");
        Console.WriteLine("  7. Create a single payment");
        Console.WriteLine("  8. Create a batch payment");
        Console.WriteLine("  9. Resolve a pending approval (approve/reject)");
        Console.WriteLine(" 10. View payment details");
        Console.WriteLine(" 11. View company statement");
        Console.WriteLine(" 12. Exit");
        Console.WriteLine(" --- helpers ---");
        Console.WriteLine(" 13. List invoices   14. List contractors   15. Set approval policy");
    }

    public bool RequireCompany(ClientSession session)
    {
        if (session.IsAuthenticated) return true;
        Error("Not connected to a company. Use option 2 to create one, or option 1 to enter an existing company id and API key.");
        return false;
    }

    /// <summary>Prints a failed result and returns false; returns true for success so callers can chain.</summary>
    public bool Report<T>(UseCaseResult<T> result)
    {
        if (result.Success) return true;
        Error($"Failed{(result.StatusCode > 0 ? $" (HTTP {result.StatusCode})" : "")}: {result.Error}");
        return false;
    }

    public bool TryDecimal(string? text, out decimal value)
    {
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return true;
        Error($"'{text}' is not a valid number.");
        return false;
    }

    public bool TryDate(string? text, out DateOnly value)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;
        Error($"'{text}' is not a valid date (use yyyy-MM-dd).");
        return false;
    }

    /// <summary>Returns null on bad input, an empty list for blank input, otherwise the parsed ids.</summary>
    public List<Guid>? AskGuids(string prompt)
    {
        var text = Ask(prompt);
        if (string.IsNullOrWhiteSpace(text)) return [];
        var ids = new List<Guid>();
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(part, out var id)) { Error($"'{part}' is not a GUID."); return null; }
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>Shows the company's unpaid invoices to pick from, then reads an id.</summary>
    public async Task<Guid?> PickInvoiceAsync(ClientSuite suite, string prompt, CancellationToken ct)
    {
        var list = await suite.ListInvoices.ExecuteAsync("Submitted", ct);
        if (list.Success && list.Value!.Count > 0)
        {
            Line("Submitted invoices:");
            foreach (var i in list.Value) Line($"  {i.Id}  {i.InvoiceNumber,-12} {i.Amount,12:N2} {i.Currency}");
        }
        var text = Ask(prompt);
        if (Guid.TryParse(text, out var id)) return id;
        Error("Invoice id must be a GUID.");
        return null;
    }
}
