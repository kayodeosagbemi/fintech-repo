using System.Text;
using Plooto.Ap.Client.Sdk;

namespace Plooto.Ap.Client.Tests;

public class InvoiceCsvServiceTests
{
    private const string Header = "InvoiceNumber,ContractorEmail,Amount,Currency,DueDate,Description";
    private readonly InvoiceCsvService _csv = new();

    private Task<CsvParseResult> ParseAsync(params string[] lines) =>
        _csv.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines))));

    [Fact]
    public async Task ValidRows_AreParsed()
    {
        var r = await ParseAsync(Header,
            "INV-1,jane@example.com,2500.00,USD,2099-11-15,October consulting",
            "INV-2,sam@example.com,10,,2099-12-01,");

        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(new InvoiceCsvRow(2, "INV-1", "jane@example.com", 2500.00m, "USD", new DateOnly(2099, 11, 15), "October consulting"), r.Rows[0]);
        Assert.Null(r.Rows[1].Currency);          // blank => company default, decided server-side
        Assert.Null(r.Rows[1].Description);
    }

    [Fact]
    public async Task QuotedFieldsWithCommasAndNewlines_AreParsedAsOneRow()
    {
        var r = await ParseAsync(Header, "INV-1,jane@example.com,5,USD,2099-01-01,\"Design, build,\nand ship\"", "INV-2,jane@example.com,6,USD,2099-01-01,plain");

        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("Design, build,\nand ship", r.Rows[0].Description);
    }

    [Fact]
    public async Task CommentAndBlankLines_AreIgnored()
    {
        var r = await ParseAsync(Header, "# a comment", "", "INV-1,jane@example.com,5,USD,2099-01-01,x", "# INV-2,commented@out.com,1,USD,2099-01-01,y");

        Assert.Empty(r.Errors);
        Assert.Equal("INV-1", Assert.Single(r.Rows).InvoiceNumber);
    }

    [Fact]
    public async Task GeneratedTemplate_ImportsNothing_SoLeavingTheExampleInPlaceIsSafe()
    {
        using var ms = new MemoryStream();
        await _csv.WriteTemplateAsync(ms);
        ms.Position = 0;
        var text = Encoding.UTF8.GetString(ms.ToArray());

        var r = await _csv.ParseAsync(ms);

        Assert.StartsWith(Header, text);
        Assert.Contains("# INV-1001,jane.doe@example.com", text);
        Assert.Empty(r.Rows);
        Assert.Empty(r.Errors);
    }

    [Theory]
    [InlineData("INV-1,jane@example.com,abc,USD,2099-01-01,x", "Amount 'abc' is not a number")]
    [InlineData("INV-1,jane@example.com,0,USD,2099-01-01,x", "greater than zero")]
    [InlineData("INV-1,jane@example.com,-5,USD,2099-01-01,x", "greater than zero")]
    [InlineData("INV-1,jane@example.com,5,USD,01/02/2099,x", "must be yyyy-MM-dd")]
    [InlineData("INV-1,jane@example.com,5,USD,,x", "DueDate is required")]
    [InlineData("INV-1,not-an-email,5,USD,2099-01-01,x", "not a valid email")]
    [InlineData("INV-1,,5,USD,2099-01-01,x", "ContractorEmail is required")]
    [InlineData(",jane@example.com,5,USD,2099-01-01,x", "InvoiceNumber is required")]
    [InlineData("INV-1,jane@example.com,5,DOLLARS,2099-01-01,x", "3-letter ISO 4217")]
    [InlineData("INV-1,jane@example.com,,USD,2099-01-01,x", "Amount is required")]
    public async Task BadRow_IsReportedWithLineNumberAndReason(string row, string expectedReason)
    {
        var r = await ParseAsync(Header, row);

        Assert.Empty(r.Rows);
        var error = Assert.Single(r.Errors);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains(expectedReason, error.Reason);
        Assert.Equal(row, error.RawLine);
    }

    [Fact]
    public async Task OneBadRow_DoesNotAbortTheFile_AndMultipleProblemsAreAllListed()
    {
        var r = await ParseAsync(Header,
            "INV-1,jane@example.com,5,USD,2099-01-01,ok",
            "INV-2,bad-email,abc,USD,2099-01-01,two problems",
            "INV-3,sam@example.com,7,USD,2099-01-01,ok");

        Assert.Equal(["INV-1", "INV-3"], r.Rows.Select(x => x.InvoiceNumber));
        var error = Assert.Single(r.Errors);
        Assert.Equal(3, error.LineNumber);
        Assert.Contains("not a valid email", error.Reason);
        Assert.Contains("not a number", error.Reason);
    }

    [Fact]
    public async Task DuplicateInvoiceNumberWithinFile_LaterRowRejected()
    {
        var r = await ParseAsync(Header, "INV-1,jane@example.com,5,USD,2099-01-01,a", "inv-1,jane@example.com,6,USD,2099-01-01,b");
        Assert.Single(r.Rows);
        var e = Assert.Single(r.Errors);
        Assert.Equal(3, e.LineNumber);
        Assert.Contains("duplicated", e.Reason);
    }

    [Fact]
    public async Task PastDueDate_IsAcceptedWithAWarning()
    {
        var r = await ParseAsync(Header, "INV-1,jane@example.com,5,USD,2001-01-01,old");
        Assert.Single(r.Rows);
        Assert.Empty(r.Errors);
        Assert.Equal(2, Assert.Single(r.Warnings).LineNumber);
    }

    [Fact]
    public async Task MissingRequiredColumn_IsASingleFileLevelError()
    {
        var r = await ParseAsync("InvoiceNumber,Amount,DueDate", "INV-1,5,2099-01-01");
        Assert.Empty(r.Rows);
        Assert.Contains("ContractorEmail", Assert.Single(r.Errors).Reason);
    }

    [Fact]
    public async Task EmptyFile_IsReported()
    {
        var r = await ParseAsync();
        Assert.Empty(r.Rows);
        Assert.Contains("empty", Assert.Single(r.Errors).Reason);
    }

    [Fact]
    public async Task ColumnsMayBeReorderedAndHeaderMatchedCaseInsensitively_ExtraColumnsIgnored()
    {
        var r = await ParseAsync("duedate,AMOUNT,contractoremail,invoicenumber,Notes", "2099-01-01,5,jane@example.com,INV-1,whatever");
        Assert.Empty(r.Errors);
        Assert.Equal("INV-1", Assert.Single(r.Rows).InvoiceNumber);
    }

    [Fact]
    public async Task ShortRow_MissingTrailingFields_IsReportedNotThrown()
    {
        var r = await ParseAsync(Header, "INV-1,jane@example.com,5");
        Assert.Empty(r.Rows);
        Assert.Contains("DueDate is required", Assert.Single(r.Errors).Reason);
    }
}
