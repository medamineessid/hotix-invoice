using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotix.InvoiceClient;

public sealed class InvoiceResult
{
    /// <summary>
    /// Builds an <see cref="InvoiceResult"/> from a JSON field dictionary as
    /// returned by the cloud extraction engines (Gemini / Grok).
    /// </summary>
    public static InvoiceResult FromFieldDictionary(
        Dictionary<string, JsonElement> fields, double confidence, string rawText, string engineUsed)
    {
        static string? Get(Dictionary<string, JsonElement> dict, string key) =>
            dict.TryGetValue(key, out var el) && el.ValueKind != JsonValueKind.Null
                ? el.GetString()
                : null;

        return new InvoiceResult
        {
            NumeroFacture = Get(fields, "numero_facture"),
            Date          = Get(fields, "date"),
            Fournisseur   = Get(fields, "fournisseur"),
            Client        = Get(fields, "client"),
            MontantHt     = Get(fields, "montant_ht"),
            MontantTva    = Get(fields, "montant_tva"),
            MontantTaxe   = Get(fields, "montant_taxe"),
            MontantTtc    = Get(fields, "montant_ttc"),
            Confidence    = confidence,
            RawText       = rawText,
            EngineUsed    = engineUsed,
        };
    }

    [JsonPropertyName("numero_facture")]
    public string? NumeroFacture { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("fournisseur")]
    public string? Fournisseur { get; set; }

    [JsonPropertyName("client")]
    public string? Client { get; set; }

    [JsonPropertyName("montant_ht")]
    public string? MontantHt { get; set; }

    [JsonPropertyName("montant_tva")]
    public string? MontantTva { get; set; }

    [JsonPropertyName("montant_taxe")]
    public string? MontantTaxe { get; set; }

    [JsonPropertyName("montant_ttc")]
    public string? MontantTtc { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("raw_text")]
    public string? RawText { get; set; }

    [JsonPropertyName("engine_used")]
    public string EngineUsed { get; set; } = "ocr";

    [JsonIgnore]
    public bool HasMissingFields =>
        string.IsNullOrWhiteSpace(NumeroFacture)
        || string.IsNullOrWhiteSpace(Date)
        || string.IsNullOrWhiteSpace(Fournisseur)
        || string.IsNullOrWhiteSpace(Client)
        || string.IsNullOrWhiteSpace(MontantHt)
        || string.IsNullOrWhiteSpace(MontantTva)
        || string.IsNullOrWhiteSpace(MontantTaxe)
        || string.IsNullOrWhiteSpace(MontantTtc);

    [JsonIgnore]
    public string MissingFieldsSummary => string.Join(", ", new[]
    {
        string.IsNullOrWhiteSpace(NumeroFacture) ? "numero_facture" : null,
        string.IsNullOrWhiteSpace(Date) ? "date" : null,
        string.IsNullOrWhiteSpace(Fournisseur) ? "fournisseur" : null,
        string.IsNullOrWhiteSpace(Client) ? "client" : null,
        string.IsNullOrWhiteSpace(MontantHt) ? "montant_ht" : null,
        string.IsNullOrWhiteSpace(MontantTva) ? "montant_tva" : null,
        string.IsNullOrWhiteSpace(MontantTaxe) ? "montant_taxe" : null,
        string.IsNullOrWhiteSpace(MontantTtc) ? "montant_ttc" : null,
    }.Where(value => value is not null));
}
