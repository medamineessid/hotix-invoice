using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using Hotix.InvoiceClient;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Pins the cross-boundary tax_summary contract on the client side:
/// the server emits "tax_amount" and TaxSummaryRow must deserialize it — this
/// is the data source for the Excel export's "Tax Amount" column.</summary>
public sealed class TaxSummaryContractTests
{
    /// <summary>Mirrors the server's /extract response after Bug B
    /// standardization. Before the fix the server sent "tva_amount", which
    /// left TaxAmount null and blanked the Excel column.</summary>
    [Fact]
    public void ServerTaxAmountJson_PopulatesTaxAmount()
    {
        var json = """{"tax_summary":[{"rate":0.2,"base_ht":1000.0,"tax_amount":200.0}]}""";
        var result = JsonSerializer.Deserialize<InvoiceResult>(json)!;

        Assert.Single(result.TaxSummary!);
        Assert.Equal(200.0, result.TaxSummary![0].TaxAmount!.Value, 3);
        Assert.Equal(1000.0, result.TaxSummary![0].BaseHt!.Value, 3);
    }

    /// <summary>The display formatting ExcelWriter uses must render the value
    /// (not the "—" placeholder) once the amount is populated. Formatting is
    /// culture-dependent ("200,00" in fr-FR vs "200.00" in en-US), so assert
    /// on the digits, not the separator.</summary>
    [Fact]
    public void ExcelTaxAmountDisplay_UsesPopulatedValue()
    {
        var row = new TaxSummaryRow { Rate = 0.2, BaseHt = 1000.0, TaxAmount = 200.0 };

        Assert.NotEqual("—", row.TaxAmountDisplay);
        Assert.NotEqual("—", row.BaseHtDisplay);
        Assert.Contains("200", row.TaxAmountDisplay);
        Assert.Contains("1000", row.BaseHtDisplay);
    }

    /// <summary>End-to-end Excel round trip: export a synthetic invoice with
    /// tax data, then read the actual "Tax Amount" cell back from the .xlsx
    /// (column 15 on the Results sheet, row 2 = first data row).</summary>
    [Fact]
    public void ExcelExport_TaxAmountColumn_IsPopulated()
    {
        var result = new InvoiceResult
        {
            NumeroFacture = "INV-2024-001",
            Date = "2024-03-15",
            Fournisseur = "SARL Dupont et Fils",
            Client = "Entreprise Martin EURL",
            MontantHt = "1250.000",
            MontantTva = "250.000",
            MontantTaxe = "0.000",
            MontantTtc = "1500.000",
            Confidence = 0.9,
            EngineUsed = "ocr",
            Items = new System.Collections.Generic.List<InvoiceItem>(),
            TaxSummary = new System.Collections.Generic.List<TaxSummaryRow>
            {
                new() { Rate = 0.2, BaseHt = 1000.0, TaxAmount = 200.0 },
            },
        };
        var row = InvoiceRowViewModel.FromSuccess("C:\\tmp\\invoice.png", result);

        string outPath = Path.Combine(Path.GetTempPath(), $"hotix_export_test_{Guid.NewGuid():N}.xlsx");
        try
        {
            new ExcelWriter().Write(outPath, new[] { row });

            using var workbook = new XLWorkbook(outPath);
            var ws = workbook.Worksheets.First(); // Results sheet

            // With the fixed-column layout, a 20% rate lands in columns
            // 19 (Base HT 20%) and 20 (TVA 20%).
            string taxAmountCell = ws.Cell(2, 20).GetString();
            Assert.False(string.IsNullOrWhiteSpace(taxAmountCell));
            Assert.NotEqual("—", taxAmountCell);
            Assert.Contains("200", taxAmountCell);
            Assert.Contains("1000", ws.Cell(2, 19).GetString());

            // The other 3 standard-rate column pairs (2.1%, 5.5%, 10%) must
            // stay empty since this invoice has no entry for those rates.
            Assert.True(ws.Cell(2, 13).IsEmpty());
            Assert.True(ws.Cell(2, 14).IsEmpty());
            Assert.True(ws.Cell(2, 15).IsEmpty());
            Assert.True(ws.Cell(2, 16).IsEmpty());
            Assert.True(ws.Cell(2, 17).IsEmpty());
            Assert.True(ws.Cell(2, 18).IsEmpty());

            // The "autres taux" columns must show the placeholder, not be blank.
            Assert.Equal("—", ws.Cell(2, 21).GetString());
            Assert.Equal("—", ws.Cell(2, 22).GetString());
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    /// <summary>Reproduces the real invoice from the bug report: a single
    /// invoice with both a 10% and a 20% VAT rate must land in two
    /// completely separate column pairs, never joined into one cell.</summary>
    [Fact]
    public void ExcelExport_MultipleStandardRates_UseSeparateColumns()
    {
        var result = new InvoiceResult
        {
            NumeroFacture = "1511215",
            Date = "2015-11-21",
            Fournisseur = "Peinture Laurent et Fils",
            Client = "SOCIETE BENOIT",
            MontantHt = "6362.000",
            MontantTva = "780.830",
            MontantTaxe = "780.830",
            MontantTtc = "7142.830",
            Confidence = 0.9,
            EngineUsed = "gemini",
            Items = new System.Collections.Generic.List<InvoiceItem>(),
            TaxSummary = new System.Collections.Generic.List<TaxSummaryRow>
            {
                new() { Rate = 0.10, BaseHt = 4915.75, TaxAmount = 491.58 },
                new() { Rate = 0.20, BaseHt = 1446.25, TaxAmount = 289.25 },
            },
        };
        var row = InvoiceRowViewModel.FromSuccess("C:\\tmp\\invoice.png", result);

        string outPath = Path.Combine(Path.GetTempPath(), $"hotix_export_test_{Guid.NewGuid():N}.xlsx");
        try
        {
            new ExcelWriter().Write(outPath, new[] { row });

            using var workbook = new XLWorkbook(outPath);
            var ws = workbook.Worksheets.First();

            // 10% pair = columns 17-18
            Assert.Contains("4915", ws.Cell(2, 17).GetString());
            Assert.Contains("491", ws.Cell(2, 18).GetString());

            // 20% pair = columns 19-20
            Assert.Contains("1446", ws.Cell(2, 19).GetString());
            Assert.Contains("289", ws.Cell(2, 20).GetString());

            // 2.1% and 5.5% pairs must stay empty — this invoice has neither.
            Assert.True(ws.Cell(2, 13).IsEmpty());
            Assert.True(ws.Cell(2, 14).IsEmpty());
            Assert.True(ws.Cell(2, 15).IsEmpty());
            Assert.True(ws.Cell(2, 16).IsEmpty());
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    /// <summary>A VAT rate outside the 4 standard French rates (e.g. a
    /// foreign 7% rate) must not be silently dropped — it must appear in the
    /// "autres taux" catch-all columns instead.</summary>
    [Fact]
    public void ExcelExport_NonStandardRate_GoesToOtherColumns()
    {
        var result = new InvoiceResult
        {
            NumeroFacture = "INV-FOREIGN-1",
            Date = "2024-01-01",
            Fournisseur = "Foreign Supplier",
            Client = "Client",
            MontantHt = "1000.000",
            MontantTva = "70.000",
            MontantTaxe = "70.000",
            MontantTtc = "1070.000",
            Confidence = 0.8,
            EngineUsed = "ocr",
            Items = new System.Collections.Generic.List<InvoiceItem>(),
            TaxSummary = new System.Collections.Generic.List<TaxSummaryRow>
            {
                new() { Rate = 0.07, BaseHt = 1000.0, TaxAmount = 70.0 },
            },
        };
        var row = InvoiceRowViewModel.FromSuccess("C:\\tmp\\invoice2.png", result);

        string outPath = Path.Combine(Path.GetTempPath(), $"hotix_export_test_{Guid.NewGuid():N}.xlsx");
        try
        {
            new ExcelWriter().Write(outPath, new[] { row });

            using var workbook = new XLWorkbook(outPath);
            var ws = workbook.Worksheets.First();

            // All 4 standard-rate pairs must be empty.
            for (int col = 13; col <= 20; col++)
                Assert.True(ws.Cell(2, col).IsEmpty(), $"column {col} should be empty for a 7% rate");

            // The 7% entry must appear in the "autres taux" columns (21-22).
            Assert.Contains("7", ws.Cell(2, 21).GetString());
            Assert.Contains("1000", ws.Cell(2, 21).GetString());
            Assert.Contains("70", ws.Cell(2, 22).GetString());
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
