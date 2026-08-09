using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>Pins the Gemini malformed-JSON retry behavior. The real bug: the
/// LLM occasionally returns JSON truncated mid-array (e.g. inside "items"),
/// which JsonDocument.Parse/Deserialize rejects with JsonException. The retry
/// loop must (1) retry ONCE with a fresh fetch, (2) log the raw truncated text
/// for post-mortem diagnostics, and (3) if both attempts fail, throw
/// GeminiParseErrorWithDetail carrying the raw text — never silently swallow
/// it. All tests are offline: the fetch is injected, no HTTP is made.</summary>
public sealed class GeminiJsonRetryTests
{
    public GeminiJsonRetryTests()
    {
        TranslationSource.Instance.CurrentCulture = "en";
    }

    /// <summary>Builds a Gemini API envelope whose embedded invoice JSON is
    /// truncated mid-"items" array — the exact shape that triggered the bug.</summary>
    private static string EnvelopeWith(string invoiceJson) =>
        $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{JsonSerializer.Serialize(invoiceJson)}}}]}}}}]}}";

    private const string ValidInvoiceJson =
        "{\"numero_facture\":\"FAC-1\",\"date\":\"2024-03-15\",\"fournisseur\":\"Hotix\",\"client\":\"Acme\"," +
        "\"montant_ht\":\"100\",\"montant_tva\":\"20\",\"montant_taxe\":\"0\",\"montant_ttc\":\"120\"," +
        "\"items\":[{\"designation\":\"A\",\"quantite\":1},{\"designation\":\"B\",\"quantite\":2}]}";

    // Truncated inside the items array: the closing "]} of the array and the
    // object's closing brace are cut off → invalid JSON at the exact bug site.
    private const string TruncatedInvoiceJson =
        "{\"numero_facture\":\"FAC-1\",\"date\":\"2024-03-15\",\"fournisseur\":\"Hotix\",\"client\":\"Acme\"," +
        "\"montant_ht\":\"100\",\"montant_tva\":\"20\",\"montant_taxe\":\"0\",\"montant_ttc\":\"120\"," +
        "\"items\":[{\"designation\":\"A\",\"quantite\":1}";

    private static string TruncatedEnvelope => EnvelopeWith(TruncatedInvoiceJson);

    /// <summary>Captures Debug.WriteLine output for the duration of the action
    /// (LogMalformedJsonText writes the raw text there). In .NET Core/5+
    /// Debug.WriteLine is forwarded through DebugProvider to Trace.Listeners,
    /// so a TraceListener alone captures it.</summary>
    private static string CaptureDebug(Action action)
    {
        var sb = new StringBuilder();
        var listener = new TextWriterTraceListener(new StringWriter(sb));
        try
        {
            Trace.Listeners.Add(listener);
            action();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Flush();
            listener.Dispose();
        }
        return sb.ToString();
    }

    [Fact]
    public async Task TruncatedItemsArray_TriggersOneRetry_ThenSucceeds()
    {
        int fetchCount = 0;
        var fetch = new Func<Task<string>>(() =>
        {
            fetchCount++;
            // First fetch returns the truncated JSON (the bug); the retry's
            // fresh fetch returns valid JSON — proving recovery actually works.
            return Task.FromResult(fetchCount == 1 ? TruncatedEnvelope : EnvelopeWith(ValidInvoiceJson));
        });

        var fields = await MainViewModel.FetchGeminiFieldsWithRetryAsync(fetch, "invoice.png");

        Assert.Equal(2, fetchCount); // retry happened
        Assert.NotNull(fields);
        Assert.Equal("FAC-1", MainViewModel.GetStringField(fields!, "numero_facture"));
    }

    [Fact]
    public void TruncatedItemsArray_RawText_IsLoggedForDiagnostics()
    {
        var fetch = new Func<Task<string>>(() => Task.FromResult(TruncatedEnvelope)); // always truncated

        // The await runs inside the (async void) lambda — CaptureDebug is
        // synchronous, and the injected Task.FromResult fetch completes without
        // yielding, so the log is fully written before CaptureDebug returns.
        // NOTE: this only works because every injected fetch in this file
        // completes synchronously; a test that injects a genuinely yielding
        // fetch (e.g. Task.Delay) must await it under the listener instead.
        string debugOutput = CaptureDebug(async () =>
        {
            try { await MainViewModel.FetchGeminiFieldsWithRetryAsync(fetch, "invoice.png"); }
            catch { /* expected — we only inspect the log */ }
        });

        // The raw truncated text must be logged (LogMalformedJsonText writes
        // "raw text (first 2000 chars): ..."), so a post-mortem is possible
        // without reproducing the failure blind.
        Assert.Contains("raw text", debugOutput);
        Assert.Contains("\"items\"", debugOutput);   // the truncation site
        Assert.Contains("designation", debugOutput); // evidence of real payload
    }

    [Fact]
    public async Task TwoConsecutiveFailures_ThrowErrorWithRawText_NotSilent()
    {
        var fetch = new Func<Task<string>>(() => Task.FromResult(TruncatedEnvelope));

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            MainViewModel.FetchGeminiFieldsWithRetryAsync(fetch, "invoice.png"));

        // The error must surface the parse detail (GeminiParseErrorWithDetail)
        // and carry the raw truncated body for diagnostics — never be swallowed.
        Assert.Contains("JSON", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.ResponseBody));
        Assert.Contains("\"items\"", ex.ResponseBody);
    }

    [Fact]
    public async Task ValidEnvelope_NoRetry_Succeeds()
    {
        int fetchCount = 0;
        var fetch = new Func<Task<string>>(() =>
        {
            fetchCount++;
            return Task.FromResult(EnvelopeWith(ValidInvoiceJson));
        });

        var fields = await MainViewModel.FetchGeminiFieldsWithRetryAsync(fetch, "invoice.png");

        Assert.Equal(1, fetchCount); // no retry for valid output
        Assert.NotNull(fields);
    }

    [Fact]
    public async Task MarkdownFencedJson_IsAccepted()
    {
        var fenced = EnvelopeWith("```json\n" + ValidInvoiceJson + "\n```");
        var fetch = new Func<Task<string>>(() => Task.FromResult(fenced));

        var fields = await MainViewModel.FetchGeminiFieldsWithRetryAsync(fetch, "invoice.png");

        Assert.NotNull(fields);
        Assert.Equal("FAC-1", MainViewModel.GetStringField(fields!, "numero_facture"));
    }
}
