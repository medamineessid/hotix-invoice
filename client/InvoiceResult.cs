using System.Text.Json.Serialization;

namespace Hotix.InvoiceClient;

public sealed class InvoiceResult
{
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

    [JsonPropertyName("gemini_fallback_reason")]
    public string? GeminiFallbackReason { get; set; }

    [JsonPropertyName("computed_fields")]
    public List<string>? ComputedFields { get; set; }

    [JsonPropertyName("amount_mismatch")]
    public bool AmountMismatch { get; set; }

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
