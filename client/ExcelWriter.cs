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

    private static readonly string[] Headers =
        { "N° Facture", "Date", "Fournisseur", "Client", "Montant HT", "TVA", "Taxe", "TTC", "Confiance", "Fichier", "Moteur" };

    /// <summary>
    /// Creates a brand-new workbook with Results and Incomplete Results sheets.
    /// </summary>
    public void Write(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows, bool markMissing = false)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        WriteSheet(workbook, "Résultats", rows, highlightMissing: markMissing, showMissingText: markMissing);
        WriteSheet(workbook, "Extractions Incomplètes", rows.Where(r => r.IsIncomplete).ToList(), highlightMissing: true, showMissingText: false);
        workbook.SaveAs(outputPath);
    }

    /// <summary>
    /// Appends invoice data to an existing workbook. If a sheet with a known name
    /// ("Résultats" or the specified sheetName) exists, data is appended below its
    /// last populated row. Otherwise, a new sheet is created.
    /// </summary>
    public void AppendToExisting(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows, string? targetSheetName = null, bool markMissing = false)
    {
        using var workbook = new XLWorkbook(outputPath);

        // Main results sheet
        string mainSheet = targetSheetName ?? "Résultats";
        var resultsWs = workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name, mainSheet, StringComparison.OrdinalIgnoreCase));

        if (resultsWs != null)
        {
            int lastRow = resultsWs.LastRowUsed()?.RowNumber() ?? 1;
            AppendRows(resultsWs, rows, lastRow + 1, highlightMissing: markMissing, includeHeaders: false, showMissingText: markMissing);
        }
        else
        {
            resultsWs = workbook.Worksheets.Add(mainSheet);
            WriteHeaders(resultsWs);
            AppendRows(resultsWs, rows, 2, highlightMissing: markMissing, includeHeaders: false, showMissingText: markMissing);
        }

        // Incomplete extractions sheet
        var incompleteRows = rows.Where(r => r.IsIncomplete).ToList();
        string incompleteSheetName = "Extractions Incomplètes";
        var incWs = workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name, incompleteSheetName, StringComparison.OrdinalIgnoreCase));

        if (incWs != null)
        {
            int lastRow = incWs.LastRowUsed()?.RowNumber() ?? 1;
            AppendRows(incWs, incompleteRows, lastRow + 1, highlightMissing: true, includeHeaders: false, showMissingText: false);
        }
        else
        {
            incWs = workbook.Worksheets.Add(incompleteSheetName);
            WriteHeaders(incWs);
            AppendRows(incWs, incompleteRows, 2, highlightMissing: true, includeHeaders: false, showMissingText: false);
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
        for (int c = 0; c < Headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = Headers[c];
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
            SetCell(ws, rowIndex, 5,  row.MontantHt,     rowBg, highlightMissing && row.MontantHtMissing, showMissingText && row.MontantHtMissing);
            SetCell(ws, rowIndex, 6,  row.MontantTva,    rowBg, highlightMissing && row.MontantTvaMissing, showMissingText && row.MontantTvaMissing);
            SetCell(ws, rowIndex, 7,  row.MontantTaxe,   rowBg, highlightMissing && row.MontantTaxeMissing, showMissingText && row.MontantTaxeMissing);
            SetCell(ws, rowIndex, 8,  row.MontantTtc,    rowBg, highlightMissing && row.MontantTtcMissing, showMissingText && row.MontantTtcMissing);

            // Confidence as integer %
            var confCell = ws.Cell(rowIndex, 9);
            confCell.Value = row.HasError ? "—" : $"{(int)Math.Round(row.Confidence * 100)}%";
            confCell.Style.Fill.BackgroundColor = rowBg;
            confCell.Style.Font.FontColor = White;

            SetCell(ws, rowIndex, 10, row.FileName, rowBg, false);

            // Engine used
            string engineLabel = row.EngineUsed == "gemini" ? "Gemini (cloud)" : "OCR local";
            SetCell(ws, rowIndex, 11, engineLabel, rowBg, false);

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
        cell.Value = showMissingText ? "[MISSING]" : (value ?? string.Empty);
        cell.Style.Fill.BackgroundColor = highlight ? MissingCellBg : rowBg;
        cell.Style.Font.FontColor = White;
    }


}
