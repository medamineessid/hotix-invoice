using System.Text.Json;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

public sealed class GetStringFieldTests
{
    /// <summary>Reproduces the original crash: Gemini/Grok return a Number
    /// instead of a String for montant_ht. Before the fix, GetStringField
    /// threw InvalidOperationException for non-String/non-Null kinds.</summary>
    [Fact]
    public void NumberValue_ReturnsRawText_DoesNotThrow()
    {
        var json = """{"montant_ht": 3800.00}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "montant_ht");
        Assert.Equal("3800.00", result);
    }

    [Fact]
    public void StringValue_ReturnsString()
    {
        var json = """{"numero_facture": "FAC-001"}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "numero_facture");
        Assert.Equal("FAC-001", result);
    }

    [Fact]
    public void TrueValue_ReturnsTrueString()
    {
        var json = """{"flag": true}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "flag");
        Assert.Equal("true", result);
    }

    [Fact]
    public void FalseValue_ReturnsFalseString()
    {
        var json = """{"flag": false}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "flag");
        Assert.Equal("false", result);
    }

    [Fact]
    public void NullValue_ReturnsNull()
    {
        var json = """{"field": null}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "field");
        Assert.Null(result);
    }

    [Fact]
    public void MissingKey_ReturnsNull()
    {
        var json = """{"other": "val"}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "missing");
        Assert.Null(result);
    }

    [Fact]
    public void ArrayValue_ReturnsNull()
    {
        var json = """{"items": [1, 2, 3]}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "items");
        Assert.Null(result);
    }

    [Fact]
    public void ObjectValue_ReturnsNull()
    {
        var json = """{"nested": {"a": 1}}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = MainViewModel.GetStringField(dict, "nested");
        Assert.Null(result);
    }

    /// <summary>Verifies the return type is compatible with the downstream
    /// decimal.TryParse(..., NumberStyles.Any, InvariantCulture) used in
    /// the amount reconciliation code.</summary>
    [Fact]
    public void NumberValue_IsCompatibleWithDownstreamDecimalParsing()
    {
        var json = """{"montant_ht": 3800.00, "montant_tva": "680.00", "montant_ttc": 4480.0}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        var ht = MainViewModel.GetStringField(dict, "montant_ht");     // Number
        var tva = MainViewModel.GetStringField(dict, "montant_tva");   // String
        var ttc = MainViewModel.GetStringField(dict, "montant_ttc");   // Number (trailing zero dropped: "4480")

        // All must be non-null
        Assert.NotNull(ht);
        Assert.NotNull(tva);
        Assert.NotNull(ttc);

        // Must survive the same preprocessing the reconcile code applies
        static decimal Parse(string s) => decimal.Parse(
            s.Replace(",", ".").Replace(" ", ""),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture);

        var htDec = Parse(ht);
        var tvaDec = Parse(tva);
        var ttcDec = Parse(ttc);

        Assert.Equal(3800.00m, htDec);
        Assert.Equal(680.00m, tvaDec);
        Assert.Equal(4480.0m, ttcDec);

        // Arithmetic identity: HT + TVA = TTC => tax = 0
        Assert.Equal(0m, ttcDec - htDec - tvaDec);
    }
}
