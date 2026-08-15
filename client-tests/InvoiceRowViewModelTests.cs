using Hotix.InvoiceClient;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Regression tests for the ItemsCount / DedupeItems desync (Bug 2).</summary>
public sealed class InvoiceRowViewModelTests
{
    [Fact]
    public void FromSuccess_WithExactDuplicateItems_KeepsCountInSyncWithDedupedList()
    {
        var result = new InvoiceResult
        {
            Items = new List<InvoiceItem>
            {
                new() { Designation = "Article A", Quantite = 2.0, Montant = 800.0 },
                new() { Designation = "Article A", Quantite = 2.0, Montant = 800.0 }, // exact duplicate
                new() { Designation = "Article B", Quantite = 1.0, Montant = 100.0 },
            },
        };

        var row = InvoiceRowViewModel.FromSuccess(@"C:\invoices\sample.png", result);

        // Dedupe removed the exact duplicate from the list…
        Assert.Equal(2, row.Items.Count);
        // …and ItemsCount (exported to Excel / items header) matches the deduped list.
        Assert.Equal(2, row.ItemsCount);
        Assert.Equal(row.Items.Count, row.ItemsCount);
        Assert.Equal("2", row.ItemsCountDisplay);
    }

    [Fact]
    public void FromSuccess_WithoutDuplicates_CountMatchesList()
    {
        var result = new InvoiceResult
        {
            Items = new List<InvoiceItem>
            {
                new() { Designation = "A", Quantite = 1.0, Montant = 10.0 },
                new() { Designation = "B", Quantite = 2.0, Montant = 20.0 },
            },
        };

        var row = InvoiceRowViewModel.FromSuccess(@"C:\invoices\sample.png", result);

        Assert.Equal(2, row.Items.Count);
        Assert.Equal(2, row.ItemsCount);
        Assert.Equal(row.Items.Count, row.ItemsCount);
    }

    [Fact]
    public void FromSuccess_NullItems_CountsZeroWithEmptyList()
    {
        var result = new InvoiceResult { Items = null };

        var row = InvoiceRowViewModel.FromSuccess(@"C:\invoices\sample.png", result);

        Assert.Empty(row.Items);
        Assert.Equal(0, row.ItemsCount);
        Assert.Equal("—", row.ItemsCountDisplay);
        Assert.False(row.HasItems);
    }

    [Fact]
    public void FromSuccess_DuplicateWithDifferentCase_IsDedupedConsistently()
    {
        var result = new InvoiceResult
        {
            Items = new List<InvoiceItem>
            {
                new() { Designation = "Article A", Quantite = 2.0, Montant = 800.0 },
                new() { Designation = "article a", Quantite = 2.0, Montant = 800.0 },
            },
        };

        var row = InvoiceRowViewModel.FromSuccess(@"C:\invoices\sample.png", result);

        Assert.Equal(1, row.Items.Count);
        Assert.Equal(1, row.ItemsCount);
    }
}
