using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

/// <summary>Thrown when appending into an existing workbook whose column layout
/// no longer matches the current export schema. Surfaces a clear message
/// instead of silently writing misaligned columns.</summary>
public sealed class ExcelSchemaMismatchException : Exception
{
    public ExcelSchemaMismatchException()
        : base(TranslationSource.Get("ExportSchemaMismatch"))
    {
    }
}

public sealed class ExcelWriter
{
    // ── Light theme (spreadsheet-friendly) ─────────────────────────────────
    // Dark row fills and near-black headers were an anti-pattern for files
    // opened in Excel's default light theme. The header now uses the app's
    // brand accent (Colors.xaml ColorAccent), rows alternate white / very
    // light gray, and missing cells use a soft red fill with dark red text —
    // the standard spreadsheet "warning" convention.
    private static readonly XLColor HeaderBg        = XLColor.FromHtml("#D9472B"); // app accent
    private static readonly XLColor HeaderBorder    = XLColor.FromHtml("#C03A20"); // accent hover (darker)
    private static readonly XLColor HeaderText      = XLColor.White;
    private static readonly XLColor Row1Bg          = XLColor.FromHtml("#FFFFFF");
    private static readonly XLColor Row2Bg          = XLColor.FromHtml("#F5F5F5");
    private static readonly XLColor TextColor       = XLColor.FromHtml("#1E1E1E");
    private static readonly XLColor MissingCellBg   = XLColor.FromHtml("#FDECEA"); // soft red fill
    private static readonly XLColor MissingCellText = XLColor.FromHtml("#B71C1C"); // dark red text (ColorError)

    private const string AmountNumberFormat = "#,##0.00 €";

    // Legacy French sheet names — kept so append-to-existing still matches
    // files created before localization (and files created in the other language).
    private const string LegacyResultsSheet = "Résultats";
    private const string LegacyIncompleteSheet = "Extractions Incomplètes";
    private const string LegacyItemsSheet = "Articles";

    private static string ResultsSheetName => TranslationSource.Get("ExportSheetResults");
    private static string IncompleteSheetName => TranslationSource.Get("ExportSheetIncomplete");
    private static string ItemsSheetName => TranslationSource.Get("ExportSheetItems");

    /// <summary>
    /// Builds the localized column headers. Read at export time so a culture
    /// switch before exporting is reflected in the generated workbook.
    /// </summary>
    private static string[] BuildHeaders() => new[]
    {
        TranslationSource.Get("ExportHeaderNumero"),
        TranslationSource.Get("ExportHeaderDate"),
        TranslationSource.Get("ExportHeaderFournisseur"),
        TranslationSource.Get("ExportHeaderClient"),
        TranslationSource.Get("ExportHeaderDirection"),
        TranslationSource.Get("ExportHeaderMontantHt"),
        TranslationSource.Get("ExportHeaderTva"),
        TranslationSource.Get("ExportHeaderTaxe"),
        TranslationSource.Get("ExportHeaderTtc"),
        TranslationSource.Get("ExportHeaderItemsCount"),
        TranslationSource.Get("ExportHeaderTvaPerItem"),
        TranslationSource.Get("ExportHeaderItemUnit"),
        TranslationSource.Get("ExportHeaderTaxRate"),
        TranslationSource.Get("ExportHeaderTaxBaseHt"),
        TranslationSource.Get("ExportHeaderTaxAmount"),
        TranslationSource.Get("ExportHeaderConfidence"),
        TranslationSource.Get("ExportHeaderFile"),
        TranslationSource.Get("ExportHeaderEngine"),
    };

    /// <summary>
    /// Headers for the per-item "Articles" sheet. One row per line item, so an
    /// invoice with 0 items contributes 0 rows and one with 12 contributes 12 —
    /// invoices with and without items mix cleanly with fixed-width rows.
    /// </summary>
    private static string[] BuildItemsHeaders() => new[]
    {
        TranslationSource.Get("ExportHeaderNumero"),        // ref back to the main sheet
        TranslationSource.Get("ExportHeaderFournisseur"),
        TranslationSource.Get("ExportHeaderItemDesignation"),
        TranslationSource.Get("ExportHeaderItemQuantity"),
        TranslationSource.Get("ExportHeaderItemPrice"),
        TranslationSource.Get("ExportHeaderTaxRate"),       // VAT rate
        TranslationSource.Get("ExportHeaderItemMontant"),
    };

    /// <summary>
    /// Creates a brand-new workbook with Results, Incomplete Results and
    /// (optionally) Articles sheets.
    /// </summary>
    public void Write(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows, bool markMissing = false, bool includeItemsSheet = true)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        WriteSheet(workbook, ResultsSheetName, rows, highlightMissing: markMissing, showMissingText: markMissing);
        WriteSheet(workbook, IncompleteSheetName, rows.Where(r => r.IsIncomplete).ToList(), highlightMissing: true, showMissingText: false);
        if (includeItemsSheet)
            WriteItemsSheet(workbook, rows);
        workbook.SaveAs(outputPath);
    }

    /// <summary>
    /// Appends invoice data to an existing workbook. If a sheet with a known name
    /// (the localized or legacy "Résultats", or the specified sheetName) exists,
    /// data is appended below its last populated row. Otherwise, a new sheet is
    /// created. The Articles sheet is found-or-created the same way.
    /// </summary>
    public void AppendToExisting(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows, string? targetSheetName = null, bool markMissing = false, bool includeItemsSheet = true)
    {
        using var workbook = new XLWorkbook(outputPath);

        // Main results sheet
        var resultsWs = workbook.Worksheets.FirstOrDefault(w => IsResultsSheet(w, targetSheetName));

        if (resultsWs != null)
        {
            EnsureCompatibleColumns(resultsWs, BuildHeaders().Length);
            int lastRow = resultsWs.LastRowUsed()?.RowNumber() ?? 1;
            AppendRows(resultsWs, rows, lastRow + 1, highlightMissing: markMissing, includeHeaders: false, showMissingText: markMissing);
            resultsWs.RangeUsed()?.SetAutoFilter();
        }
        else
        {
            resultsWs = workbook.Worksheets.Add(targetSheetName ?? ResultsSheetName);
            WriteHeaders(resultsWs);
            AppendRows(resultsWs, rows, 2, highlightMissing: markMissing, includeHeaders: false, showMissingText: markMissing);
            resultsWs.Columns().AdjustToContents();
            resultsWs.SheetView.FreezeRows(1);
            resultsWs.RangeUsed()?.SetAutoFilter();
        }

        // Incomplete extractions sheet
        var incompleteRows = rows.Where(r => r.IsIncomplete).ToList();
        var incWs = workbook.Worksheets.FirstOrDefault(w => IsIncompleteSheet(w));

        if (incWs != null)
        {
            EnsureCompatibleColumns(incWs, BuildHeaders().Length);
            int lastRow = incWs.LastRowUsed()?.RowNumber() ?? 1;
            AppendRows(incWs, incompleteRows, lastRow + 1, highlightMissing: true, includeHeaders: false, showMissingText: false);
            incWs.RangeUsed()?.SetAutoFilter();
        }
        else
        {
            incWs = workbook.Worksheets.Add(IncompleteSheetName);
            WriteHeaders(incWs);
            AppendRows(incWs, incompleteRows, 2, highlightMissing: true, includeHeaders: false, showMissingText: false);
            incWs.Columns().AdjustToContents();
            incWs.SheetView.FreezeRows(1);
            incWs.RangeUsed()?.SetAutoFilter();
        }

        // Articles (per-item) sheet — mirrored for append mode
        if (includeItemsSheet)
        {
            var itemsRows = rows.Where(r => r.IncludeItemsInExport && r.Items.Count > 0).ToList();
            var itemsWs = workbook.Worksheets.FirstOrDefault(IsItemsSheet);

            if (itemsWs != null)
            {
                EnsureCompatibleColumns(itemsWs, BuildItemsHeaders().Length);
                int lastRow = itemsWs.LastRowUsed()?.RowNumber() ?? 1;
                AppendItemsRows(itemsWs, itemsRows, lastRow + 1);
                itemsWs.RangeUsed()?.SetAutoFilter();
            }
            else
            {
                itemsWs = workbook.Worksheets.Add(ItemsSheetName);
                WriteItemsHeaders(itemsWs);
                AppendItemsRows(itemsWs, itemsRows, 2);
                itemsWs.Columns().AdjustToContents();
                itemsWs.SheetView.FreezeRows(1);
                itemsWs.RangeUsed()?.SetAutoFilter();
            }
        }

        workbook.Save();
    }

    /// <summary>
    /// Returns the list of worksheet names in an existing workbook for selection.
    /// </summary>
    public static List<string> GetWorksheetNames(string filePath)
    {
        var names = new List<string>();
        using var workbook = new XLWorkbook(filePath);
        foreach (var ws in workbook.Worksheets)
            names.Add(ws.Name);
        return names;
    }

    /// <summary>True when the worksheet is the main results sheet: matches the
    /// explicitly requested name, the localized name, or the legacy French name.</summary>
    private static bool IsResultsSheet(IXLWorksheet ws, string? targetSheetName)
    {
        return SheetNameEquals(ws.Name, targetSheetName)
            || SheetNameEquals(ws.Name, ResultsSheetName)
            || SheetNameEquals(ws.Name, LegacyResultsSheet);
    }

    private static bool IsIncompleteSheet(IXLWorksheet ws)
        => SheetNameEquals(ws.Name, IncompleteSheetName) || SheetNameEquals(ws.Name, LegacyIncompleteSheet);

    private static bool IsItemsSheet(IXLWorksheet ws)
        => SheetNameEquals(ws.Name, ItemsSheetName) || SheetNameEquals(ws.Name, LegacyItemsSheet);

    private static bool SheetNameEquals(string a, string? b)
        => b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Guards against schema drift: the existing sheet's header row must
    /// contain exactly the expected number of columns, otherwise appending would
    /// silently write misaligned data. Throws <see cref="ExcelSchemaMismatchException"/>.</summary>
    private static void EnsureCompatibleColumns(IXLWorksheet ws, int expectedColumns)
    {
        int headerCount = 0;
        for (int c = 1; c <= 30; c++)
        {
            if (ws.Cell(1, c).IsEmpty()) break;
            headerCount = c;
        }

        // headerCount == 0 means the sheet has no header row — let AppendRows
        // fall back to the caller's behavior rather than guessing here.
        if (headerCount != 0 && headerCount != expectedColumns)
            throw new ExcelSchemaMismatchException();
    }

    private static void WriteSheet(XLWorkbook workbook, string sheetName, IEnumerable<InvoiceRowViewModel> rows, bool highlightMissing, bool showMissingText)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(sheetName);
        WriteHeaders(ws);
        AppendRows(ws, rows, 2, highlightMissing, includeHeaders: false, showMissingText: showMissingText);
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        ws.RangeUsed()?.SetAutoFilter();
    }

    private static void WriteHeaders(IXLWorksheet ws)
    {
        string[] headers = BuildHeaders();
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderText;
            cell.Style.Fill.BackgroundColor = HeaderBg;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = HeaderBorder;
        }
    }

    private static void WriteItemsHeaders(IXLWorksheet ws)
    {
        string[] headers = BuildItemsHeaders();
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderText;
            cell.Style.Fill.BackgroundColor = HeaderBg;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = HeaderBorder;
        }
    }

    private static void AppendRows(IXLWorksheet ws, IEnumerable<InvoiceRowViewModel> rows, int startRow, bool highlightMissing, bool includeHeaders, bool showMissingText = false)
    {
        int rowIndex = startRow;

        if (includeHeaders)
        {
            WriteHeaders(ws);
            rowIndex++;
        }

        foreach (var row in rows)
        {
            XLColor rowBg = rowIndex % 2 == 0 ? Row2Bg : Row1Bg;

            SetCell(ws, rowIndex, 1,  row.NumeroFacture, rowBg, highlightMissing && row.NumeroFactureMissing, showMissingText && row.NumeroFactureMissing);
            SetCell(ws, rowIndex, 2,  row.Date,          rowBg, highlightMissing && row.DateMissing, showMissingText && row.DateMissing);
            SetCell(ws, rowIndex, 3,  row.Fournisseur,   rowBg, highlightMissing && row.FournisseurMissing, showMissingText && row.FournisseurMissing);
            SetCell(ws, rowIndex, 4,  row.Client,        rowBg, highlightMissing && row.ClientMissing, showMissingText && row.ClientMissing);

            // Direction (Achat/Vente)
            SetCell(ws, rowIndex, 5,  row.DirectionDisplay, rowBg, false);

            // Amounts — real numbers with currency formatting, so Excel can
            // right-align, SUM, and apply number formats. Missing amounts are
            // left truly empty (no dash) so SUM over the column still works.
            SetAmountCell(ws, rowIndex, 6,  row.MontantHt,     rowBg, highlightMissing && row.MontantHtMissing);
            SetAmountCell(ws, rowIndex, 7,  row.MontantTva,    rowBg, highlightMissing && row.MontantTvaMissing);
            SetAmountCell(ws, rowIndex, 8,  row.MontantTaxe,   rowBg, highlightMissing && row.MontantTaxeMissing);
            SetAmountCell(ws, rowIndex, 9,  row.MontantTtc,    rowBg, highlightMissing && row.MontantTtcMissing);

            // Items count
            SetCell(ws, rowIndex, 10, row.ItemsCountDisplay, rowBg, false);

            // Per-item VAT rate summary (distinct rates present, e.g. "10%, 19%")
            string vatRatesSummary = row.Items.Count > 0
                ? string.Join(", ", row.Items
                    .Where(i => i.TvaRate.HasValue)
                    .Select(i => i.TvaDisplay)
                    .Distinct()
                    .OrderBy(r => r))
                : "—";
            SetCell(ws, rowIndex, 11, vatRatesSummary, rowBg, false);

            // Unit of first item (or "—" if no items/unit)
            string firstUnit = row.Items.Count > 0 && !string.IsNullOrEmpty(row.Items[0].Unit)
                ? row.Items[0].Unit : "—";
            SetCell(ws, rowIndex, 12, firstUnit, rowBg, false);

            // Tax summary per-rate breakdown (all rows, joined per-column)
            string taxRate = row.TaxSummary.Count > 0
                ? string.Join(", ", row.TaxSummary.Select(r => r.RateDisplay))
                : "—";
            string taxBaseHt = row.TaxSummary.Count > 0
                ? string.Join(", ", row.TaxSummary.Select(r => r.BaseHtDisplay))
                : "—";
            string taxAmount = row.TaxSummary.Count > 0
                ? string.Join(", ", row.TaxSummary.Select(r => r.TaxAmountDisplay))
                : "—";
            SetCell(ws, rowIndex, 13, taxRate, rowBg, false);
            SetCell(ws, rowIndex, 14, taxBaseHt, rowBg, false);
            SetCell(ws, rowIndex, 15, taxAmount, rowBg, false);

            // Confidence as integer %
            var confCell = ws.Cell(rowIndex, 16);
            confCell.Value = row.HasError ? "—" : $"{(int)Math.Round(row.Confidence * 100)}%";
            confCell.Style.Fill.BackgroundColor = rowBg;
            confCell.Style.Font.FontColor = TextColor;

            SetCell(ws, rowIndex, 17, row.FileName, rowBg, false);

            // Engine used
            string engineLabel = row.EngineUsed == "gemini" ? TranslationSource.Get("ExportEngineGemini")
                : row.EngineUsed == "grok" ? TranslationSource.Get("ExportEngineGrok")
                : TranslationSource.Get("ExportEngineOcr");
            SetCell(ws, rowIndex, 18, engineLabel, rowBg, false);

            rowIndex++;
        }

        // Only adjust column widths for new sheets; preserve existing widths when appending
        if (startRow == 2)
        {
            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
        }
    }

    private static void WriteItemsSheet(XLWorkbook workbook, IEnumerable<InvoiceRowViewModel> rows)
    {
        var itemsRows = rows.Where(r => r.IncludeItemsInExport && r.Items.Count > 0).ToList();
        IXLWorksheet ws = workbook.Worksheets.Add(ItemsSheetName);
        WriteItemsHeaders(ws);
        AppendItemsRows(ws, itemsRows, 2);
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        ws.RangeUsed()?.SetAutoFilter();
    }

    /// <summary>One row per line item of every invoice with items enabled.
    /// Invoices without items (or toggled off) contribute no rows.</summary>
    private static void AppendItemsRows(IXLWorksheet ws, IEnumerable<InvoiceRowViewModel> rows, int startRow)
    {
        int rowIndex = startRow;

        foreach (var row in rows)
        {
            XLColor rowBg = rowIndex % 2 == 0 ? Row2Bg : Row1Bg;

            foreach (var item in row.Items)
            {
                SetCell(ws, rowIndex, 1, row.NumeroFacture, rowBg, false);
                SetCell(ws, rowIndex, 2, row.Fournisseur,   rowBg, false);
                SetCell(ws, rowIndex, 3, item.Designation,  rowBg, false);

                // Quantity — real number, left empty when unknown
                var qtyCell = ws.Cell(rowIndex, 4);
                if (item.Quantite.HasValue)
                {
                    qtyCell.Value = item.Quantite.Value;
                    qtyCell.Style.NumberFormat.Format = "#,##0.###";
                }
                qtyCell.Style.Fill.BackgroundColor = rowBg;
                qtyCell.Style.Font.FontColor = TextColor;

                // Unit price — real number, currency format
                var priceCell = ws.Cell(rowIndex, 5);
                if (item.PrixUnitaire.HasValue)
                {
                    priceCell.Value = item.PrixUnitaire.Value;
                    priceCell.Style.NumberFormat.Format = AmountNumberFormat;
                }
                priceCell.Style.Fill.BackgroundColor = rowBg;
                priceCell.Style.Font.FontColor = TextColor;

                // VAT rate — real number, percent format (0.2 → "20 %")
                var vatCell = ws.Cell(rowIndex, 6);
                if (item.TvaRate.HasValue)
                {
                    vatCell.Value = item.TvaRate.Value;
                    vatCell.Style.NumberFormat.Format = "0.0%";
                }
                vatCell.Style.Fill.BackgroundColor = rowBg;
                vatCell.Style.Font.FontColor = TextColor;

                // Amount — real number, currency format
                var amountCell = ws.Cell(rowIndex, 7);
                if (item.Montant.HasValue)
                {
                    amountCell.Value = item.Montant.Value;
                    amountCell.Style.NumberFormat.Format = AmountNumberFormat;
                }
                amountCell.Style.Fill.BackgroundColor = rowBg;
                amountCell.Style.Font.FontColor = TextColor;

                rowIndex++;
            }
        }
    }

    private static void SetCell(IXLWorksheet ws, int row, int col, string? value, XLColor rowBg, bool highlight)
    {
        SetCell(ws, row, col, value, rowBg, highlight, false);
    }

    private static void SetCell(IXLWorksheet ws, int row, int col, string? value, XLColor rowBg, bool highlight, bool showMissingText)
    {
        var cell = ws.Cell(row, col);
        bool missing = string.IsNullOrEmpty(value);
        cell.Value = showMissingText && missing
            ? TranslationSource.Get("ExportMissingMarker")
            : (value ?? string.Empty);
        cell.Style.Fill.BackgroundColor = highlight ? MissingCellBg : rowBg;
        cell.Style.Font.FontColor = highlight ? MissingCellText : TextColor;
    }

    /// <summary>Writes an amount column as a real number with a currency format.
    /// Missing values leave the cell truly empty (no dash/text) so SUM formulas
    /// over the column still work with gaps.</summary>
    private static void SetAmountCell(IXLWorksheet ws, int row, int col, string? value, XLColor rowBg, bool highlight)
    {
        var cell = ws.Cell(row, col);
        decimal? amount = ParseAmount(value);

        if (amount.HasValue)
        {
            cell.Value = amount.Value;
            cell.Style.NumberFormat.Format = AmountNumberFormat;
            cell.Style.Font.FontColor = TextColor;
        }
        else
        {
            // Leave the cell truly empty — do NOT write a dash or empty string.
            cell.Style.Font.FontColor = highlight ? MissingCellText : TextColor;
        }

        cell.Style.Fill.BackgroundColor = highlight ? MissingCellBg : rowBg;
    }

    /// <summary>Parses user-facing amount strings ("1250.000", "1 250,00", "1 250.00")
    /// into a decimal. Returns null for empty/unparsable input.</summary>
    private static decimal? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string cleaned = value
            .Replace(" ", string.Empty)
            .Replace("\u00A0", string.Empty)
            .Replace(",", ".");

        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d)
            ? d
            : null;
    }
}
