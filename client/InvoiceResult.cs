using System.Text.Json.Serialization;

namespace Hotix.InvoiceClient;

public sealed class InvoiceItem
{
    [JsonPropertyName("designation")]
    public string? Designation { get; set; }

    [JsonPropertyName("quantite")]
    public double? Quantite { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("prix_unitaire")]
    public double? PrixUnitaire { get; set; }

    [JsonPropertyName("tva_rate")]
    public double? TvaRate { get; set; }

    [JsonPropertyName("montant")]
    public double? Montant { get; set; }

    [JsonIgnore]
    public string DisplayLine => Unit != null ? $"{Designation ?? "—"} ({Unit})" : (Designation ?? "—");

    [JsonIgnore]
    public string QuantiteDisplay => Quantite.HasValue ? $"{Quantite.Value:0.##}" : "—";

    [JsonIgnore]
    public string PriceDisplay => PrixUnitaire.HasValue ? $"{PrixUnitaire.Value:F2}" : "—";

    [JsonIgnore]
    public string TvaDisplay => TvaRate.HasValue ? $"{(TvaRate.Value * 100):F1}%" : "—";

    [JsonIgnore]
    public string MontantDisplay => Montant.HasValue ? $"{Montant.Value:F2}" : "—";
}

/// <summary>Per-rate tax breakdown row from the invoice's tax summary block.
/// Appears separately from line items, usually near the totals.</summary>
public sealed class TaxSummaryRow
{
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    [JsonPropertyName("base_ht")]
    public double? BaseHt { get; set; }

    [JsonPropertyName("tax_amount")]
    public double? TaxAmount { get; set; }

    [JsonIgnore]
    public string RateDisplay => Rate.HasValue ? $"{(Rate.Value * 100):F1}%" : "—";

    [JsonIgnore]
    public string BaseHtDisplay => BaseHt.HasValue ? $"{BaseHt.Value:F2}" : "—";

    [JsonIgnore]
    public string TaxAmountDisplay => TaxAmount.HasValue ? $"{TaxAmount.Value:F2}" : "—";
}

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

    [JsonPropertyName("items")]
    public List<InvoiceItem>? Items { get; set; }

    [JsonPropertyName("tax_summary")]
    public List<TaxSummaryRow>? TaxSummary { get; set; }

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
