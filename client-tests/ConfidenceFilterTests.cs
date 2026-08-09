using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Pins the simplified binary confidence filter: "All" shows every
/// invoice, "To check" reuses the exact old low-bucket threshold (confidence
/// &lt; 0.40 — the same value the previous "low" segment used, not a new
/// threshold). The fine-grained percentage stays visible in the grid; the
/// toolbar only carries this coarse toggle.</summary>
public sealed class ConfidenceFilterTests
{
    // ── All mode: everything visible regardless of confidence ──────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.39)]
    [InlineData(0.40)]   // boundary — still visible in All mode
    [InlineData(0.75)]
    [InlineData(0.99)]
    public void AllMode_ShowsEveryConfidence(double confidence)
    {
        Assert.True(MainViewModel.MatchesConfidenceFilter(lowOnly: false, confidence));
    }

    // ── To-check mode: reuses the exact old "low" threshold (< 0.40) ───────

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.10)]
    [InlineData(0.39)]
    public void ToCheckMode_ShowsLowConfidence(double confidence)
    {
        Assert.True(MainViewModel.MatchesConfidenceFilter(lowOnly: true, confidence));
    }

    [Theory]
    [InlineData(0.40)]   // boundary: 0.40 was NOT low before, must not be now
    [InlineData(0.41)]
    [InlineData(0.75)]
    [InlineData(0.99)]
    public void ToCheckMode_HidesMediumAndHighConfidence(double confidence)
    {
        Assert.False(MainViewModel.MatchesConfidenceFilter(lowOnly: true, confidence));
    }

    // ── Error rows surface as confidence 0.0 in the filter path (HasError
    //    maps to 0.0 in FilterByDirection), so they land in "To check". ─────

    [Fact]
    public void ErrorRowConfidenceZero_IsIncludedInToCheck()
    {
        // FilterByDirection uses 0.0 for error rows; 0.0 < 0.40 → visible.
        Assert.True(MainViewModel.MatchesConfidenceFilter(lowOnly: true, 0.0));
    }
}
