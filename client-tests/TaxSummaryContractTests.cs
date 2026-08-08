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

            // Column 15 = Tax Amount, column 13 = Tax Rate, column 14 = Tax Base HT.
            string taxAmountCell = ws.Cell(2, 15).GetString();
            Assert.False(string.IsNullOrWhiteSpace(taxAmountCell));
            Assert.NotEqual("—", taxAmountCell);
            Assert.Contains("200", taxAmountCell);
            Assert.Contains("20", ws.Cell(2, 13).GetString());
            Assert.Contains("1000", ws.Cell(2, 14).GetString());
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
