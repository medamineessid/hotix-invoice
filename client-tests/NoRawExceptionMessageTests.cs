using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>
/// Permanent regression guard: fails if any UI-bound property in client/
/// receives a raw .NET exception message directly (the bug class that showed
/// "Aucune connexion n'a pu être établie …" to end users).
///
/// The only file allowed to read <c>ex.Message</c> into a user-facing string
/// is <c>ErrorMessageTranslator.cs</c> — every other site must go through
/// <see cref="ErrorMessageTranslator.ToUserMessage"/>. This test scans the
/// client source tree on every CI run (it is part of the dotnet test step in
/// .github/workflows/build-check.yml).
/// </summary>
public sealed class NoRawExceptionMessageTests
{
    /// <summary>Direct assignment: <c>SomeProperty = ex.Message;</c>.</summary>
    private static readonly Regex DirectAssignment =
        new(@"=\s*(ex|exception)\w*\.Message\s*;", RegexOptions.Compiled);

    /// <summary>Interpolated assignment: <c>SomeProperty = $"...{ex.Message}"</c>.</summary>
    private static readonly Regex InterpolatedUiAssignment =
        new(@"=\s*\$""[^""\r\n]*\{(ex|exception)\w*\.Message\}", RegexOptions.Compiled);

    [Fact]
    public void NoUiProperty_IsAssignedRawExceptionMessage()
    {
        var violations = new List<string>();

        foreach (string file in EnumerateClientSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (DirectAssignment.IsMatch(lines[i]))
                    violations.Add($"{file}:{i + 1}: {lines[i].Trim()}");
                if (InterpolatedUiAssignment.IsMatch(lines[i]))
                    violations.Add($"{file}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "Raw .NET exception messages must never be assigned directly to a UI property. " +
            "Route the exception through ErrorMessageTranslator.ToUserMessage(...) instead. Found:\n" +
            string.Join("\n", violations));
    }

    private static IEnumerable<string> EnumerateClientSources()
    {
        string root = FindRepoRoot();
        string clientDir = Path.Combine(root, "client");

        foreach (string file in Directory.EnumerateFiles(clientDir, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/")) continue;
            // The translator itself is the one place ex.Message is allowed.
            if (normalized.EndsWith("/ErrorMessageTranslator.cs", StringComparison.Ordinal)) continue;
            yield return file;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "client")) &&
                Directory.Exists(Path.Combine(dir.FullName, "client-tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root from " + AppContext.BaseDirectory);
    }
}
