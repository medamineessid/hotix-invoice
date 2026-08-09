using System.IO;
using ClosedXML.Excel;
using Hotix.InvoiceClient;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Round-trip tests for the redesigned Excel export: amounts are real
/// numbers with a currency format, the theme is light/spreadsheet-friendly,
/// AutoFilter is set, and the per-item "Articles" sheet mixes invoices with and
/// without items. Also pins the schema-mismatch guard on append.</summary>
public sealed class ExcelWriterTests
{
    private static string ResultsSheetName => TranslationSource.Get("ExportSheetResults");
    private static string IncompleteSheetName => TranslationSource.Get("ExportSheetIncomplete");
    private static string ItemsSheetName => TranslationSource.Get("ExportSheetItems");

    public ExcelWriterTests()
    {
        TranslationSource.Instance.CurrentCulture = "en";
    }

    private static InvoiceRowViewModel MakeInvoice(string numero, bool incomplete = false, int itemCount = 0)
    {
        var items = new List<InvoiceItem>();
        for (int i = 0; i < itemCount; i++)
        {
            items.Add(new InvoiceItem
            {
                Designation = $"Item {i + 1}",
                Quantite = i + 1,
                PrixUnitaire = 10.5,
                TvaRate = 0.2,
                Montant = (i + 1) * 10.5 * 1.2,
            });
        }

        var result = new InvoiceResult
        {
            NumeroFacture = numero,
            Date = "2024-03-15",
            Fournisseur = "SARL Dupont et Fils",
            Client = "Entreprise Martin EURL",
            MontantHt = incomplete ? null : "1250.000",
            MontantTva = incomplete ? null : "250.000",
            MontantTaxe = "0.000",
            MontantTtc = incomplete ? null : "1500.000",
            Confidence = 0.9,
            EngineUsed = "ocr",
            Items = items,
            TaxSummary = new List<TaxSummaryRow>(),
        };
        return InvoiceRowViewModel.FromSuccess("C:\\tmp\\invoice.png", result);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"hotix_test_{Guid.NewGuid():N}.xlsx");

    // ── P2-A: amounts are numbers, not strings ─────────────────────────────

    [Fact]
    public void AmountColumns_AreNumericWithCurrencyFormat()
    {
        string path = TempPath();
        try
        {
            new ExcelWriter().Write(path, new[] { MakeInvoice("FAC-1") });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ResultsSheetName);

            for (int col = 6; col <= 9; col++)
            {
                var cell = ws.Cell(2, col);
                Assert.True(cell.Value.IsNumber, $"Column {col} must be a number, was {cell.Value}");
                Assert.Contains("€", cell.Style.NumberFormat.Format);
            }

            Assert.Equal(1250.0, ws.Cell(2, 6).GetDouble(), 3);
            Assert.Equal(250.0, ws.Cell(2, 7).GetDouble(), 3);
            Assert.Equal(1500.0, ws.Cell(2, 9).GetDouble(), 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingAmount_LeavesCellTrulyEmpty_NotDash()
    {
        string path = TempPath();
        try
        {
            new ExcelWriter().Write(path, new[] { MakeInvoice("FAC-2", incomplete: true) });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ResultsSheetName);

            Assert.True(ws.Cell(2, 6).IsEmpty(), "Missing amount must be an empty cell, not a dash/text");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── P2-B: light, spreadsheet-friendly theme ───────────────────────────

    [Fact]
    public void Theme_IsLight_AccentHeader_ZebraRows_AutoFilter()
    {
        string path = TempPath();
        try
        {
            new ExcelWriter().Write(path, new[] { MakeInvoice("FAC-3"), MakeInvoice("FAC-4") });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ResultsSheetName);

            // Header uses the app's accent color, not near-black
            Assert.Equal(XLColor.FromHtml("#D9472B").Color.ToArgb(),
                ws.Cell(1, 1).Style.Fill.BackgroundColor.Color.ToArgb());
            Assert.True(ws.Cell(1, 1).Style.Font.Bold);

            // Zebra bands are white / very light gray — NOT near-black.
            // (Either order is fine — row 2 is even → Row2Bg, row 3 → Row1Bg.)
            int row2Argb = ws.Cell(2, 1).Style.Fill.BackgroundColor.Color.ToArgb();
            int row3Argb = ws.Cell(3, 1).Style.Fill.BackgroundColor.Color.ToArgb();
            var allowed = new[]
            {
                XLColor.FromHtml("#FFFFFF").Color.ToArgb(),
                XLColor.FromHtml("#F5F5F5").Color.ToArgb(),
            };
            Assert.Contains(row2Argb, allowed);
            Assert.Contains(row3Argb, allowed);
            Assert.NotEqual(row2Argb, row3Argb); // real zebra alternation

            // Data text is dark, not white
            Assert.Equal(XLColor.FromHtml("#1E1E1E").Color.ToArgb(),
                ws.Cell(2, 1).Style.Font.FontColor.Color.ToArgb());

            // Filterable immediately (ClosedXML 0.104: ws.AutoFilter.IsEnabled after SetAutoFilter)
            Assert.NotNull(ws.AutoFilter);
            Assert.True(ws.AutoFilter.IsEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── P2-C: per-invoice items sheet ─────────────────────────────────────

    [Fact]
    public void ItemsSheet_OneRowPerItem_WithInvoiceReference()
    {
        string path = TempPath();
        try
        {
            var withItems = MakeInvoice("FAC-5", itemCount: 2);
            var withoutItems = MakeInvoice("FAC-6"); // 0 items → contributes nothing
            new ExcelWriter().Write(path, new[] { withItems, withoutItems });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ItemsSheetName);

            // 2 invoices, only 1 has items → exactly 2 data rows, no blank rows
            Assert.Equal(2, ws.LastRowUsed()!.RowNumber() - 1);
            Assert.Equal("FAC-5", ws.Cell(2, 1).GetString());
            Assert.Equal("FAC-5", ws.Cell(3, 1).GetString());
            Assert.Equal("Item 1", ws.Cell(2, 3).GetString());

            // Numeric unit price + amount with currency format
            Assert.True(ws.Cell(2, 5).Value.IsNumber);
            Assert.Contains("€", ws.Cell(2, 5).Style.NumberFormat.Format);
            Assert.True(ws.Cell(2, 7).Value.IsNumber);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ItemsSheet_SkippedEntirely_WhenTopLevelToggleOff()
    {
        string path = TempPath();
        try
        {
            new ExcelWriter().Write(path, new[] { MakeInvoice("FAC-7", itemCount: 3) }, includeItemsSheet: false);

            using var workbook = new XLWorkbook(path);
            Assert.DoesNotContain(workbook.Worksheets, w => w.Name == ItemsSheetName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ItemsSheet_RespectsPerInvoiceToggle()
    {
        string path = TempPath();
        try
        {
            var excluded = MakeInvoice("FAC-8", itemCount: 2);
            excluded.IncludeItemsInExport = false; // opted out at row level
            var included = MakeInvoice("FAC-9", itemCount: 1);
            new ExcelWriter().Write(path, new[] { excluded, included });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ItemsSheetName);

            Assert.Equal(1, ws.LastRowUsed()!.RowNumber() - 1);
            Assert.Equal("FAC-9", ws.Cell(2, 1).GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── P1: append safety + schema drift ──────────────────────────────────

    [Fact]
    public void Append_ToCurrentSchemaWorkbook_AppendsBelow()
    {
        string path = TempPath();
        try
        {
            new ExcelWriter().Write(path, new[] { MakeInvoice("FAC-10") });
            new ExcelWriter().AppendToExisting(path, new[] { MakeInvoice("FAC-11") });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ResultsSheetName);
            Assert.Equal("FAC-10", ws.Cell(2, 1).GetString());
            Assert.Equal("FAC-11", ws.Cell(3, 1).GetString());
            Assert.True(ws.Cell(3, 6).Value.IsNumber, "appended amount must stay numeric");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Append_SchemaMismatch_ThrowsClearException()
    {
        string path = TempPath();
        try
        {
            // Simulate an older/different workbook: a results sheet with 5 columns
            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet(ResultsSheetName);
                for (int c = 1; c <= 5; c++)
                    ws.Cell(1, c).Value = $"H{c}";
                wb.SaveAs(path);
            }

            var ex = Assert.Throws<ExcelSchemaMismatchException>(() =>
                new ExcelWriter().AppendToExisting(path, new[] { MakeInvoice("FAC-12") }));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Append_MirrorsItemsSheet()
    {
        string path = TempPath();
        try
        {
            new ExcelWriter().Write(path, new[] { MakeInvoice("FAC-13", itemCount: 1) });
            new ExcelWriter().AppendToExisting(path, new[] { MakeInvoice("FAC-14", itemCount: 1) });

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(ItemsSheetName);
            Assert.Equal(2, ws.LastRowUsed()!.RowNumber() - 1);
            Assert.Equal("FAC-13", ws.Cell(2, 1).GetString());
            Assert.Equal("FAC-14", ws.Cell(3, 1).GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
