using System.IO;
using ClosedXML.Excel;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

public sealed class ExcelWriter
{
    private static readonly XLColor HeaderBg     = XLColor.FromHtml("#2D2D2D");
    private static readonly XLColor Row1Bg        = XLColor.FromHtml("#1E1E1E");
    private static readonly XLColor Row2Bg        = XLColor.FromHtml("#2A2A2A");
    private static readonly XLColor MissingCellBg = XLColor.FromHtml("#8B0000");
    private static readonly XLColor White         = XLColor.White;

    // Legacy French sheet names — kept so append-to-existing still matches
    // files created before localization (and files created in the other language).
    private const string LegacyResultsSheet = "Résultats";
    private const string LegacyIncompleteSheet = "Extractions Incomplètes";

    private static string ResultsSheetName => TranslationSource.Get("ExportSheetResults");
    private static string IncompleteSheetName => TranslationSource.Get("ExportSheetIncomplete");

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
    /// Creates a brand-new workbook with Results and Incomplete Results sheets.
    /// </summary>
    public void Write(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows, bool markMissing = false)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        WriteSheet(workbook, ResultsSheetName, rows, highlightMissing: markMissing, showMissingText: markMissing);
        WriteSheet(workbook, IncompleteSheetName, rows.Where(r => r.IsIncomplete).ToList(), highlightMissing: true, showMissingText: false);
        workbook.SaveAs(outputPath);
    }

    /// <summary>
    /// Appends invoice data to an existing workbook. If a sheet with a known name
    /// (the localized or legacy "Résultats", or the specified sheetName) exists,
    /// data is appended below its last populated row. Otherwise, a new sheet is created.
    /// </summary>
    public void AppendToExisting(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows, string? targetSheetName = null, bool markMissing = false)
    {
        using var workbook = new XLWorkbook(outputPath);

        // Main results sheet
        var resultsWs = workbook.Worksheets.FirstOrDefault(w => IsResultsSheet(w, targetSheetName));

        if (resultsWs != null)
        {
            int lastRow = resultsWs.LastRowUsed()?.RowNumber() ?? 1;
            AppendRows(resultsWs, rows, lastRow + 1, highlightMissing: markMissing, includeHeaders: false, showMissingText: markMissing);
        }
        else
        {
            resultsWs = workbook.Worksheets.Add(targetSheetName ?? ResultsSheetName);
            WriteHeaders(resultsWs);
            AppendRows(resultsWs, rows, 2, highlightMissing: markMissing, includeHeaders: false, showMissingText: markMissing);
        }

        // Incomplete extractions sheet
        var incompleteRows = rows.Where(r => r.IsIncomplete).ToList();
        var incWs = workbook.Worksheets.FirstOrDefault(w => IsIncompleteSheet(w));

        if (incWs != null)
        {
            int lastRow = incWs.LastRowUsed()?.RowNumber() ?? 1;
            AppendRows(incWs, incompleteRows, lastRow + 1, highlightMissing: true, includeHeaders: false, showMissingText: false);
        }
        else
        {
            incWs = workbook.Worksheets.Add(IncompleteSheetName);
            WriteHeaders(incWs);
            AppendRows(incWs, incompleteRows, 2, highlightMissing: true, includeHeaders: false, showMissingText: false);
        }

        workbook.Save();
    }

    /// <summary>
    /// True when the worksheet is the main results sheet: matches the explicitly
    /// requested name, the localized name, or the legacy French name.
    /// </summary>
    private static bool IsResultsSheet(IXLWorksheet ws, string? targetSheetName)
    {
        return SheetNameEquals(ws.Name, targetSheetName)
            || SheetNameEquals(ws.Name, ResultsSheetName)
            || SheetNameEquals(ws.Name, LegacyResultsSheet);
    }

    private static bool IsIncompleteSheet(IXLWorksheet ws)
    {
        return SheetNameEquals(ws.Name, IncompleteSheetName)
            || SheetNameEquals(ws.Name, LegacyIncompleteSheet);
    }

    private static bool SheetNameEquals(string a, string? b)
        => b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

    private static void WriteSheet(XLWorkbook workbook, string sheetName, IEnumerable<InvoiceRowViewModel> rows, bool highlightMissing, bool showMissingText)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(sheetName);
        WriteHeaders(ws);
        AppendRows(ws, rows, 2, highlightMissing, includeHeaders: false, showMissingText: showMissingText);
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static void WriteHeaders(IXLWorksheet ws)
    {
        string[] headers = BuildHeaders();
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = White;
            cell.Style.Fill.BackgroundColor = HeaderBg;
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

            SetCell(ws, rowIndex, 6,  row.MontantHt,     rowBg, highlightMissing && row.MontantHtMissing, showMissingText && row.MontantHtMissing);
            SetCell(ws, rowIndex, 7,  row.MontantTva,    rowBg, highlightMissing && row.MontantTvaMissing, showMissingText && row.MontantTvaMissing);
            SetCell(ws, rowIndex, 8,  row.MontantTaxe,   rowBg, highlightMissing && row.MontantTaxeMissing, showMissingText && row.MontantTaxeMissing);
            SetCell(ws, rowIndex, 9,  row.MontantTtc,    rowBg, highlightMissing && row.MontantTtcMissing, showMissingText && row.MontantTtcMissing);

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
            confCell.Style.Font.FontColor = White;

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

    private static void SetCell(IXLWorksheet ws, int row, int col, string? value, XLColor rowBg, bool highlight)
    {
        SetCell(ws, row, col, value, rowBg, highlight, false);
    }

    private static void SetCell(IXLWorksheet ws, int row, int col, string? value, XLColor rowBg, bool highlight, bool showMissingText)
    {
        var cell = ws.Cell(row, col);
        cell.Value = showMissingText ? TranslationSource.Get("ExportMissingMarker") : (value ?? string.Empty);
        cell.Style.Fill.BackgroundColor = highlight ? MissingCellBg : rowBg;
        cell.Style.Font.FontColor = White;
    }


}
