using System.Collections.Concurrent;
using System.Reflection;
using Hotix.InvoiceClient;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

// The stress test hammers the shared TranslationSource singleton with fr/en
// flips; it must never run alongside other test classes (which assert exact
// English strings). DisableParallelization runs this collection serially,
// after the parallel collections, so the flips cannot leak into other tests.
[CollectionDefinition("Shared-state", DisableParallelization = true)]
public sealed class SharedStateTestCollection
{
}

/// <summary>
/// Regression tests for the culture-switch race that made
/// OcrServer500Generic_MapsToErrorOcrInternal flaky. TranslationSource is a
/// shared singleton and the test classes run in parallel (xUnit default), so
/// before TranslationSource was made thread-safe a reader could observe
/// CurrentCulture == "en" while the string dictionary was still French — the
/// `_currentCulture` write and the `_strings` swap were non-atomic (file I/O
/// in between), so an English-string assertion intermittently received the
/// French value.
///
/// The stress test samples the (_currentCulture, _strings) pair while a
/// writer repeatedly re-opens the vulnerable fr→en transition. The reader
/// acquires the SAME monitor the production setter now uses (via the
/// reflected _sync object), so on fixed code it only ever observes completed,
/// consistent states — no false positives — while on the buggy code (which
/// held no lock) it can catch culture "en" paired with the French dictionary.
/// </summary>
[Collection("Shared-state")]
public sealed class TranslationSourceConcurrencyTests
{
    private const string Key = "ErrorOcrInternal";
    private const string EnValue = "Internal OCR server error";
    private const string FrValue = "Erreur interne serveur OCR";

    private static readonly Type SourceType = typeof(TranslationSource);
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Sample the (culture, strings) pair atomically under the production monitor.</summary>
    private static (string Culture, string Value) SampleUnderLock()
    {
        object sync = SourceType.GetField("_sync", PrivateFlags)!.GetValue(TranslationSource.Instance)!;
        lock (sync)
        {
            var culture = (string)SourceType.GetField("_currentCulture", PrivateFlags)!
                .GetValue(TranslationSource.Instance)!;
            var strings = (Dictionary<string, string>)SourceType.GetField("_strings", PrivateFlags)!
                .GetValue(TranslationSource.Instance)!;
            return (culture, strings.TryGetValue(Key, out string? value) ? value : "");
        }
    }

    private static void ResetToFrench()
    {
        // Restore the initial vulnerable state (culture "fr" + French dict).
        // Must run under the SAME monitor the production setter and the reader
        // use, otherwise this unlocked mutation would interleave with the
        // reader's two field reads and fabricate false violations.
        object sync = SourceType.GetField("_sync", PrivateFlags)!.GetValue(TranslationSource.Instance)!;
        lock (sync)
        {
            SourceType.GetField("_currentCulture", PrivateFlags)!.SetValue(TranslationSource.Instance, "fr");
            SourceType.GetMethod("LoadCulture", PrivateFlags)!.Invoke(TranslationSource.Instance, new object[] { "fr" });
        }
    }

    [Fact]
    public void CultureSwitch_IsAtomicWithStringReads_UnderConcurrentAccess()
    {
        var violations = new ConcurrentBag<string>();
        const int readers = 8;
        const int iterations = 500;

        var start = new ManualResetEventSlim(false);
        var threads = new List<Thread>();

        // Writer: each iteration re-opens the fr→en transition the bug lived in.
        threads.Add(new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < iterations; i++)
            {
                ResetToFrench();
                TranslationSource.Instance.CurrentCulture = "en";
            }
        }));

        for (int r = 0; r < readers; r++)
        {
            threads.Add(new Thread(() =>
            {
                start.Wait();
                for (int i = 0; i < iterations; i++)
                {
                    var (culture, value) = SampleUnderLock();
                    if (value != EnValue && value != FrValue)
                        violations.Add($"torn dictionary state: '{value}'");
                    else if (culture == "en" && value != EnValue)
                        violations.Add($"culture='{culture}' but value='{value}'");
                    else if (culture == "fr" && value != FrValue)
                        violations.Add($"culture='{culture}' but value='{value}'");
                }
            }));
        }

        foreach (var t in threads) t.Start();
        start.Set();
        foreach (var t in threads) t.Join();

        // Leave the singleton in the English state the rest of the suite expects.
        TranslationSource.Instance.CurrentCulture = "en";

        Assert.True(violations.IsEmpty,
            $"observed culture/dictionary desync ({(violations.Count)}): " +
            string.Join(" | ", violations.Take(5)));
    }

    [Fact]
    public void CultureFlip_ThenRead_IsConsistent()
    {
        // Deterministic companion: after a completed flip the dictionary
        // always matches the reported culture.
        TranslationSource.Instance.CurrentCulture = "fr";
        Assert.Equal(FrValue, TranslationSource.Instance[Key]);

        TranslationSource.Instance.CurrentCulture = "en";
        Assert.Equal(EnValue, TranslationSource.Instance[Key]);
    }
}
