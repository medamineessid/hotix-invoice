using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hotix.InvoiceClient;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Pins the LLM schema-parity contract on the C# side: the two
/// client-direct prompts (Gemini, Grok) and the Gemini responseSchema must all
/// declare the exact same wire-format keys, and those keys must match the
/// server-side prompt (pinned cross-boundary in
/// server/tests/test_llm_schema_parity.py). A rename on either side drifts
/// silently and blanks a column in the UI/export — the tva_amount/tax_amount
/// bug class; a real unite/unit drift existed server-side before the parity
/// tests were added.</summary>
public sealed class LlmSchemaParityTests
{
    private static readonly string[] TopLevelKeys =
    {
        "numero_facture", "date", "fournisseur", "client",
        "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
    };

    private static readonly string[] ItemKeys =
    {
        "designation", "quantite", "unit", "prix_unitaire", "tva_rate", "montant",
    };

    private static readonly string[] TaxSummaryKeys =
    {
        "rate", "base_ht", "tax_amount",
    };

    public LlmSchemaParityTests()
    {
        TranslationSource.Instance.CurrentCulture = "en";
    }

    private static void AssertDeclaresKeys(string promptName, string prompt, string[] keys)
    {
        foreach (var key in keys)
        {
            // Word-boundary match: "unit" matches but "unite"/"montant_ht" do not.
            Assert.True(Regex.IsMatch(prompt, $@"\b{Regex.Escape(key)}\b"),
                $"[{promptName}] prompt does not declare key '{key}'.");
        }
    }

    [Fact]
    public void GeminiAndGrokPrompts_DeclareIdenticalSchema()
    {
        var gemini = TranslationSource.Get("GeminiExtractionText");
        var grok = TranslationSource.Get("GrokExtractionText");

        Assert.False(string.IsNullOrEmpty(gemini), "GeminiExtractionText key is missing");
        Assert.False(string.IsNullOrEmpty(grok), "GrokExtractionText key is missing");

        AssertDeclaresKeys("Gemini", gemini, TopLevelKeys);
        AssertDeclaresKeys("Gemini", gemini, ItemKeys);
        AssertDeclaresKeys("Gemini", gemini, TaxSummaryKeys);

        AssertDeclaresKeys("Grok", grok, TopLevelKeys);
        AssertDeclaresKeys("Grok", grok, ItemKeys);
        AssertDeclaresKeys("Grok", grok, TaxSummaryKeys);
    }

    /// <summary>Reflects the private static GeminiResponseSchema anonymous
    /// object (it is not exposed for serialization), serializes it, and checks
    /// the declared property names match the prompts exactly — so a schema
    /// rename can't drift away from the prompt wording (or vice versa).</summary>
    [Fact]
    public void GeminiResponseSchema_MatchesPromptSchema()
    {
        var field = typeof(MainViewModel).GetField("GeminiResponseSchema", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var schema = field!.GetValue(null);
        Assert.NotNull(schema);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
        var properties = doc.RootElement.GetProperty("properties");

        // The schema's top-level properties = the 8 scalar fields, in exact
        // declaration order, followed by the two nested arrays. The order pin
        // is intentional: Assert.Equal on arrays is order-sensitive, which
        // also guards against accidental reordering of the schema (do NOT
        // replace with a set comparison).
        var expectedTopLevel = TopLevelKeys.Concat(new[] { "items", "tax_summary" }).ToArray();
        var topLevel = properties.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(expectedTopLevel, topLevel);

        var itemProps = properties
            .GetProperty("items").GetProperty("items").GetProperty("properties");
        Assert.Equal(ItemKeys, itemProps.EnumerateObject().Select(p => p.Name).ToArray());

        var taxProps = properties
            .GetProperty("tax_summary").GetProperty("items").GetProperty("properties");
        Assert.Equal(TaxSummaryKeys, taxProps.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void GrokPrompt_DoesNotDriftBackToLegacyKeys()
    {
        // Pin the exact keys that were historically wrong: "unite" (server
        // prompt drift) and "tva_amount" (Bug B). Both must stay gone.
        var gemini = TranslationSource.Get("GeminiExtractionText");
        var grok = TranslationSource.Get("GrokExtractionText");

        Assert.DoesNotMatch(@"\bunite\b", gemini);
        Assert.DoesNotMatch(@"\bunite\b", grok);
        Assert.DoesNotMatch(@"\btva_amount\b", gemini);
        Assert.DoesNotMatch(@"\btva_amount\b", grok);
    }
}
