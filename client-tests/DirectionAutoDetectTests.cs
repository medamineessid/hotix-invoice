using Hotix.InvoiceClient;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Pins the four-case auto-detection business rule for invoice
/// direction (case-insensitive "Hotix" match on Fournisseur/Client):
///   1. Fournisseur contains "Hotix", Client does not   → issued
///   2. Client contains "Hotix", Fournisseur does not   → received
///   3. Neither contains "Hotix" (third-party)          → unset, manual
///   4. Both contain "Hotix" (Hotix→Hotix)              → unset + ambiguity flag
/// Also verifies that a complete invoice with an auto-detected direction is
/// NOT classified as incomplete because of direction.</summary>
public sealed class DirectionAutoDetectTests
{
    public DirectionAutoDetectTests()
    {
        TranslationSource.Instance.CurrentCulture = "en";
    }

    private static InvoiceRowViewModel MakeRow(string? fournisseur, string? client)
    {
        var result = new InvoiceResult
        {
            NumeroFacture = "FAC-X",
            Date = "2024-03-15",
            Fournisseur = fournisseur,
            Client = client,
            MontantHt = "1250.000",
            MontantTva = "250.000",
            MontantTaxe = "0.000",
            MontantTtc = "1500.000",
            Confidence = 0.9,
            EngineUsed = "ocr",
        };
        return InvoiceRowViewModel.FromSuccess("C:\\tmp\\invoice.png", result);
    }

    // ── Case 1: Hotix is the supplier → issued ────────────────────────────

    [Theory]
    [InlineData("Hotix", "Entreprise Martin EURL")]
    [InlineData("HOTIX SAS", "Entreprise Martin EURL")]
    [InlineData("hotix sarl", "Entreprise Martin EURL")]
    public void FournisseurContainsHotix_ClientDoesNot_DirectionIsIssued(string fournisseur, string client)
    {
        var row = MakeRow(fournisseur, client);
        Assert.Equal("issued", row.InvoiceDirection);
        Assert.False(row.HasDirectionAmbiguity);
    }

    // ── Case 2: Hotix is the client → received ────────────────────────────

    [Theory]
    [InlineData("SARL Dupont et Fils", "Hotix")]
    [InlineData("SARL Dupont et Fils", "HOTIX GROUPE")]
    [InlineData("SARL Dupont et Fils", "hotix")]
    public void ClientContainsHotix_FournisseurDoesNot_DirectionIsReceived(string fournisseur, string client)
    {
        var row = MakeRow(fournisseur, client);
        Assert.Equal("received", row.InvoiceDirection);
        Assert.False(row.HasDirectionAmbiguity);
    }

    // ── Case 3: neither side is Hotix → unset, manual required ────────────

    [Theory]
    [InlineData("SARL Dupont et Fils", "Entreprise Martin EURL")]
    [InlineData("Acme Corp", "Globex LLC")]
    [InlineData(null, "Entreprise Martin EURL")]
    public void NeitherContainsHotix_DirectionStaysUnset(string? fournisseur, string? client)
    {
        var row = MakeRow(fournisseur, client);
        Assert.Equal(string.Empty, row.InvoiceDirection);
        Assert.False(row.HasDirectionAmbiguity, "third-party invoice is ambiguous but NOT the Hotix→Hotix case");
    }

    // ── Case 4: both sides are Hotix → unset + explicit ambiguity flag ────

    [Theory]
    [InlineData("Hotix", "Hotix")]
    [InlineData("HOTIX SARL", "Hotix Groupe")]
    [InlineData("hotix international", "HOTIX")]
    public void BothContainHotix_DirectionStaysUnset_WithAmbiguityFlag(string fournisseur, string client)
    {
        var row = MakeRow(fournisseur, client);
        Assert.Equal(string.Empty, row.InvoiceDirection); // never silently locked
        Assert.True(row.HasDirectionAmbiguity, "Hotix→Hotix must be flagged as genuinely ambiguous");
        Assert.False(string.IsNullOrWhiteSpace(row.DirectionTooltip));
        Assert.Contains("Hotix", row.DirectionTooltip);
    }

    // ── Manual override clears the ambiguity flag ─────────────────────────

    [Fact]
    public void CycleDirection_ClearsAmbiguityFlag()
    {
        var row = MakeRow("Hotix", "Hotix");
        Assert.True(row.HasDirectionAmbiguity);

        row.CycleDirection(); // user decides manually → ambiguity resolved

        Assert.False(row.HasDirectionAmbiguity);
        Assert.Equal("received", row.InvoiceDirection);
    }

    // ── Pipeline integration: auto-detected direction does NOT push the row
    //    into "Incomplete" — IsIncomplete never reads the direction field. ──

    [Fact]
    public void CompleteInvoice_WithAutoDetectedDirection_IsNotIncomplete()
    {
        // Case 1 invoice with all fields present: supplier is Hotix → issued.
        var row = MakeRow("Hotix", "Entreprise Martin EURL");

        Assert.Equal("issued", row.InvoiceDirection);
        Assert.False(row.IsIncomplete, "a complete invoice must land in Results, not Incomplete");
    }

    [Fact]
    public void UnsetDirection_DoesNotMakeCompleteInvoice_Incomplete()
    {
        // Case 3: third-party invoice, direction unset — but all fields present.
        var row = MakeRow("SARL Dupont et Fils", "Entreprise Martin EURL");

        Assert.Equal(string.Empty, row.InvoiceDirection);
        Assert.False(row.IsIncomplete, "IsIncomplete must not depend on direction");
    }

    [Fact]
    public void MissingAmount_StillMakesInvoice_Incomplete()
    {
        // Sanity: IsIncomplete does respond to actual missing fields, just not
        // to the direction field.
        var result = new InvoiceResult
        {
            NumeroFacture = "FAC-X",
            Date = "2024-03-15",
            Fournisseur = "SARL Dupont et Fils",
            Client = "Entreprise Martin EURL",
            MontantHt = null, // missing
            MontantTva = "250.000",
            MontantTaxe = "0.000",
            MontantTtc = "1500.000",
            Confidence = 0.9,
            EngineUsed = "ocr",
        };
        var row = InvoiceRowViewModel.FromSuccess("C:\\tmp\\invoice.png", result);

        Assert.True(row.IsIncomplete, "a missing amount must still trigger Incomplete");
    }
}
