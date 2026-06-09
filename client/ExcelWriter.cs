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
        { "N° Facture", "Date", "Fournisseur", "Client", "Montant HT", "TVA", "Taxe", "TTC", "Confiance", "Fichier" };

    public void Write(string outputPath, IReadOnlyList<InvoiceRowViewModel> rows)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        WriteSheet(workbook, "Résultats", rows, highlightMissing: false);
        WriteSheet(workbook, "Extractions Incomplètes", rows.Where(r => r.IsIncomplete).ToList(), highlightMissing: true);
        workbook.SaveAs(outputPath);
    }

    private static void WriteSheet(XLWorkbook workbook, string sheetName, IEnumerable<InvoiceRowViewModel> rows, bool highlightMissing)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(sheetName);

        // Header row
        for (int c = 0; c < Headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = Headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = White;
            cell.Style.Fill.BackgroundColor = HeaderBg;
        }

        int rowIndex = 2;
        foreach (var row in rows)
        {
            XLColor rowBg = rowIndex % 2 == 0 ? Row2Bg : Row1Bg;

            SetCell(ws, rowIndex, 1,  row.NumeroFacture, rowBg, highlightMissing && row.NumeroFactureMissing);
            SetCell(ws, rowIndex, 2,  row.Date,          rowBg, highlightMissing && row.DateMissing);
            SetCell(ws, rowIndex, 3,  row.Fournisseur,   rowBg, highlightMissing && row.FournisseurMissing);
            SetCell(ws, rowIndex, 4,  row.Client,        rowBg, highlightMissing && row.ClientMissing);
            SetCell(ws, rowIndex, 5,  row.MontantHt,     rowBg, highlightMissing && row.MontantHtMissing);
            SetCell(ws, rowIndex, 6,  row.MontantTva,    rowBg, highlightMissing && row.MontantTvaMissing);
            SetCell(ws, rowIndex, 7,  row.MontantTaxe,   rowBg, highlightMissing && row.MontantTaxeMissing);
            SetCell(ws, rowIndex, 8,  row.MontantTtc,    rowBg, highlightMissing && row.MontantTtcMissing);

            // Confidence as integer %
            var confCell = ws.Cell(rowIndex, 9);
            confCell.Value = row.HasError ? "—" : $"{(int)Math.Round(row.Confidence * 100)}%";
            confCell.Style.Fill.BackgroundColor = rowBg;
            confCell.Style.Font.FontColor = White;

            SetCell(ws, rowIndex, 10, row.FileName, rowBg, false);

            rowIndex++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static void SetCell(IXLWorksheet ws, int row, int col, string? value, XLColor rowBg, bool highlight)
    {
        var cell = ws.Cell(row, col);
        cell.Value = value ?? string.Empty;
        cell.Style.Fill.BackgroundColor = highlight ? MissingCellBg : rowBg;
        cell.Style.Font.FontColor = White;
    }
}
