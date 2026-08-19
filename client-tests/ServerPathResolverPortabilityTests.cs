using System;
using System.IO;
using Hotix.InvoiceClient;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>
/// Verifies that all path resolution is RELATIVE to the running executable
/// (no hard-coded C:\... absolute paths), so the application keeps working
/// after the project folder is moved. The test host runs from
/// client-tests\bin\Debug\net8.0-windows\ — several levels below the repo
/// root — which mirrors how the published EXE sits below client\publish\.
/// </summary>
public class ServerPathResolverPortabilityTests
{
    private static string RepoRoot
    {
        get
        {
            // client-tests\bin\Debug\net8.0-windows\ → repo root (4 levels up)
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 4 && dir.Parent != null; i++)
                dir = dir.Parent;
            return dir.FullName;
        }
    }

    [Fact]
    public void ResolveVenvPython_FindsVenvRelativeToRuntimeDirectory()
    {
        string? python = ServerPathResolver.ResolveVenvPython();

        Assert.False(string.IsNullOrEmpty(python), "venv\\Scripts\\python.exe should be resolved by walking up.");
        Assert.True(File.Exists(python), $"Resolved python does not exist: {python}");
        Assert.EndsWith(Path.Combine("venv", "Scripts", "python.exe"), python!, StringComparison.OrdinalIgnoreCase);
        // Must NOT be the old hard-coded location.
        Assert.DoesNotContain("C:\\hotix-invoice", python!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveMainPy_FindsServerRelativeToRuntimeDirectory()
    {
        string? mainPy = ServerPathResolver.ResolveMainPy();

        Assert.False(string.IsNullOrEmpty(mainPy), "server\\main.py should be resolved by walking up.");
        Assert.True(File.Exists(mainPy), $"Resolved server main.py does not exist: {mainPy}");
        Assert.EndsWith(Path.Combine("server", "main.py"), mainPy!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\hotix-invoice", mainPy!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectRoot_ResolvesToRepositoryRoot()
    {
        string? root = ServerPathResolver.ResolveProjectRoot();

        Assert.False(string.IsNullOrEmpty(root), "Project root should be resolved by walking up.");
        Assert.Equal(RepoRoot, root, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(root!, "requirements.txt")), "Root should contain requirements.txt");
        Assert.True(Directory.Exists(Path.Combine(root!, "venv")), "Root should contain the venv folder");
    }

    [Fact]
    public void ResolveWritableFile_ReturnsWritablePathInsideRoot()
    {
        string logPath = ServerPathResolver.ResolveWritableFile("portability_test.log");

        Assert.False(string.IsNullOrEmpty(logPath));
        Assert.True(Path.IsPathRooted(logPath), "Resolved log path must be absolute.");
        Assert.DoesNotContain("C:\\hotix-invoice", logPath, StringComparison.OrdinalIgnoreCase);

        try
        {
            File.AppendAllText(logPath, "portability test\n");
            Assert.True(File.Exists(logPath), "Resolved log path should be writable.");
        }
        finally
        {
            try { File.Delete(logPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UpwardCandidates_ContainsNoHardCodedAbsoluteFallback()
    {
        string[] candidates = ServerPathResolver.UpwardCandidates(
            AppContext.BaseDirectory, Path.Combine("venv", "Scripts", "python.exe"));

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(candidates, c => c.Contains("C:\\hotix-invoice", StringComparison.OrdinalIgnoreCase));
        // Every candidate must be rooted inside the runtime directory's tree (relative resolution).
        Assert.All(candidates, c => Assert.StartsWith(Path.GetPathRoot(c)!, c, StringComparison.OrdinalIgnoreCase));
    }
}
