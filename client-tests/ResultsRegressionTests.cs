using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

public sealed class ResultsRegressionTests
{
    [Fact]
    public void RunText_BindsTwoWayByDefault_RequiresExplicitOneWayForTranslations()
    {
        var metadata = Run.TextProperty.GetMetadata(typeof(Run));

        Assert.True(metadata is FrameworkPropertyMetadata);
        Assert.True(((FrameworkPropertyMetadata)metadata).BindsTwoWayByDefault);
    }

    [Fact]
    public void MainWindow_LocalizedRunBindings_AreExplicitlyOneWay()
    {
        string projectRoot = FindProjectRoot();
        string xaml = File.ReadAllText(Path.Combine(projectRoot, "client", "MainWindow.xaml"));

        MatchCollection localizedRuns = Regex.Matches(
            xaml,
            "<Run\\s+Text=\"\\{Binding\\s+Source=\\{x:Static\\s+ui:TranslationSource\\.Instance\\}[^\\\"]*\\}\"",
            RegexOptions.CultureInvariant);

        Assert.NotEmpty(localizedRuns);
        Assert.All(localizedRuns, match =>
            Assert.Contains("Mode=OneWay", match.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void RowDetails_CloseAffordance_IsVisibleAndBoundToClearSelection()
    {
        string projectRoot = FindProjectRoot();
        string xaml = File.ReadAllText(Path.Combine(projectRoot, "client", "MainWindow.xaml"));

        const string closeBinding = "Content=\"{Binding Source={x:Static ui:TranslationSource.Instance}, Path=[CloseBtn], Mode=OneWay}\"";
        const string clearCommand = "Command=\"{Binding DataContext.ClearSelectedRowCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}\"";
        Assert.Equal(2, CountOccurrences(xaml, closeBinding));
        Assert.Equal(2, CountOccurrences(xaml, clearCommand));
    }

    [Fact]
    public void AutomaticGrokFallback_CatchesQuotaBeforeGeneralException()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(projectRoot, "client", "ViewModels", "MainViewModel.cs"));
        int grokBranch = source.IndexOf("LogPipeline($\"Engine dispatch: Grok-first for", StringComparison.Ordinal);
        Assert.True(grokBranch >= 0);

        int quotaCatch = source.IndexOf("catch (CloudQuotaExceededException ex)", grokBranch, StringComparison.Ordinal);
        int generalAutoCatch = source.IndexOf("catch (Exception ex2) when (selectedEngine == \"auto\")", grokBranch, StringComparison.Ordinal);
        Assert.True(quotaCatch >= 0);
        Assert.True(generalAutoCatch > quotaCatch);
    }

    [Fact]
    public void CloudRequestGates_SerializeConfiguredProviderRequests()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(projectRoot, "client", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("_geminiRequestGate.WaitAsync()", source, StringComparison.Ordinal);
        Assert.Contains("_grokRequestGate.WaitAsync()", source, StringComparison.Ordinal);
        Assert.Contains("does not turn a normal multi-file import into a spurious per-file OCR fallback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeSelectedFiles_ProduceThreeDistinctInputPaths_EvenWithSameInvoiceNumber()
    {
        string root = Path.Combine(Path.GetTempPath(), "hotix-regression");
        string[] selected =
        {
            Path.Combine(root, "invoice-a.png"),
            Path.Combine(root, "invoice-b.png"),
            Path.Combine(root, "invoice-c.png"),
        };

        string[] snapshot = MainViewModel.DistinctInputFilePaths(selected).ToArray();

        Assert.Equal(3, snapshot.Length);
        Assert.Equal(selected, snapshot);
    }

    [Fact]
    public void EquivalentFilePathSpellings_HaveTheSameIdentity()
    {
        string relative = Path.Combine(".", "invoice.png");
        string absolute = Path.GetFullPath(relative);

        Assert.Equal(
            MainViewModel.NormalizeFilePathForComparison(relative),
            MainViewModel.NormalizeFilePathForComparison(absolute),
            StringComparer.OrdinalIgnoreCase);

        Assert.Single(MainViewModel.DistinctInputFilePaths(new[] { relative, absolute }));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindProjectRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "client", "MainWindow.xaml")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Hotix project root.");
    }
}
