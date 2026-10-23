using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace Plooto.Ap.Client.Sdk;

/// <summary>One valid row. The company never appears in the file: it always comes from the session's API key.</summary>
public sealed record InvoiceCsvRow(int LineNumber, string RawLine, string InvoiceNumber, string ContractorEmail, decimal Amount,
    string? Currency, DateOnly DueDate, string? Description);

public sealed record CsvRowError(int LineNumber, string RawLine, string Reason);
public sealed record CsvRowWarning(int LineNumber, string Message);

public sealed record CsvParseResult(IReadOnlyList<InvoiceCsvRow> Rows, IReadOnlyList<CsvRowError> Errors, IReadOnlyList<CsvRowWarning> Warnings);

/// <summary>
/// Parses/produces the invoice-import CSV (PLAN_1 section 7). Operates on streams, not paths and never on System.Console,
/// so a Blazor InputFile can feed it unchanged. A bad row never aborts the file: it is reported and the rest still imports.
/// </summary>
public sealed class InvoiceCsvService
{
    public static readonly string[] RequiredColumns = ["InvoiceNumber", "ContractorEmail", "Amount", "DueDate"];
    public static readonly string[] AllColumns = ["InvoiceNumber", "ContractorEmail", "Amount", "Currency", "DueDate", "Description"];
    private const string DateFormat = "yyyy-MM-dd";

    private static CsvConfiguration Config() => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        AllowComments = true,          // lines starting with '#' are skipped before they become rows
        Comment = '#',
        TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null,      // reported per row below instead of throwing
        BadDataFound = null,
        HeaderValidated = null,
        PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),   // column names are case-insensitive
    };

    public async Task<CsvParseResult> ParseAsync(Stream csv, CancellationToken ct = default)
    {
        var rows = new List<InvoiceCsvRow>();
        var errors = new List<CsvRowError>();
        var warnings = new List<CsvRowWarning>();
        var seenNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(csv, leaveOpen: true);
        using var parser = new CsvReader(reader, Config());

        if (!await parser.ReadAsync() || !parser.ReadHeader() || parser.HeaderRecord is not { Length: > 0 } header)
        {
            errors.Add(new CsvRowError(1, "", "The file is empty or has no header row."));
            return new CsvParseResult(rows, errors, warnings);
        }

        var missing = RequiredColumns.Where(c => !header.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            errors.Add(new CsvRowError(parser.Parser.Row, string.Join(",", header), $"Missing required column(s): {string.Join(", ", missing)}."));
            return new CsvParseResult(rows, errors, warnings);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        while (await parser.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var line = parser.Parser.Row;
            var raw = parser.Parser.RawRecord.TrimEnd('\r', '\n');
            if (parser.Parser.Count == 0 || string.IsNullOrWhiteSpace(raw)) continue;   // blank line

            string Field(string name) => (parser.TryGetField<string>(name, out var v) ? v : null)?.Trim() ?? "";
            var reasons = new List<string>();

            var number = Field("InvoiceNumber");
            if (number.Length == 0) reasons.Add("InvoiceNumber is required");
            else if (number.Length > 100) reasons.Add("InvoiceNumber is longer than 100 characters");
            else if (seenNumbers.TryGetValue(number, out var firstLine)) reasons.Add($"InvoiceNumber '{number}' is duplicated (first seen on line {firstLine})");

            var email = Field("ContractorEmail");
            if (email.Length == 0) reasons.Add("ContractorEmail is required");
            else if (!email.Contains('@') || email.Contains(' ')) reasons.Add($"ContractorEmail '{email}' is not a valid email");

            decimal amount = 0;
            var amountText = Field("Amount");
            if (amountText.Length == 0) reasons.Add("Amount is required");
            else if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)) reasons.Add($"Amount '{amountText}' is not a number");
            else if (amount <= 0) reasons.Add("Amount must be greater than zero");

            var currency = Field("Currency");
            if (currency.Length > 0 && (currency.Length != 3 || !currency.All(char.IsLetter))) reasons.Add($"Currency '{currency}' must be a 3-letter ISO 4217 code");

            DateOnly due = default;
            var dueText = Field("DueDate");
            if (dueText.Length == 0) reasons.Add("DueDate is required");
            else if (!DateOnly.TryParseExact(dueText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out due)) reasons.Add($"DueDate '{dueText}' must be {DateFormat}");

            if (reasons.Count > 0)
            {
                errors.Add(new CsvRowError(line, raw, string.Join("; ", reasons)));
                continue;
            }

            if (due < today) warnings.Add(new CsvRowWarning(line, $"DueDate {due:yyyy-MM-dd} is in the past (imported anyway)."));
            seenNumbers[number] = line;
            var description = Field("Description");
            rows.Add(new InvoiceCsvRow(line, raw, number, email, amount, currency.Length == 0 ? null : currency.ToUpperInvariant(), due,
                description.Length == 0 ? null : description));
        }

        return new CsvParseResult(rows, errors, warnings);
    }

    /// <summary>Writes the header plus an example row. The example is commented out so leaving it in place can never import it.</summary>
    public async Task WriteTemplateAsync(Stream destination, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(destination, new System.Text.UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        await writer.WriteLineAsync(string.Join(",", AllColumns));
        await writer.WriteLineAsync("# Example row below: copy it without the leading '#', then edit. Lines starting with '#' are ignored on import.");
        await writer.WriteLineAsync("# INV-1001,jane.doe@example.com,2500.00,USD,2026-11-15,October consulting services");
        await writer.FlushAsync(ct);
    }
}
