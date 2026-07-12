using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hotix.InvoiceClient;

/// <summary>
/// Shared server directory resolver used by both App.xaml.cs (to launch the server)
/// and MainViewModel.cs (to read/write appsettings.json).
/// 
/// Validates that server/main.py actually exists — not just that a folder named "server"
/// exists on disk. This prevents the two path-resolution methods from diverging when
/// multiple candidate "server" folders exist (e.g., in the build output vs. the project root).
/// </summary>
public static class ServerPathResolver
{
    private static readonly string[] MainPyCandidates = new[]
    {
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server", "main.py"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "server", "main.py"),
        @"C:\hotix-invoice\server\main.py",
    };

    /// <summary>
    /// Returns the directory containing server/main.py, or null if not found.
    /// </summary>
    public static string? ResolveServerDirectory()
    {
        string? mainPy = MainPyCandidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrEmpty(mainPy))
            return null;

        // mainPy is known non-null here because of the guard above
        string dir = Path.GetDirectoryName(mainPy)!;
        return dir;
    }

    /// <summary>
    /// Returns the full path to the user-writable appsettings.json in %LOCALAPPDATA%\Hotix\.
    /// This location is always writable without elevation, unlike the install directory (Program Files).
    /// Automatically migrates from the old install-directory location on first read.
    /// </summary>
    public static string ResolveAppSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string newPath = Path.Combine(localAppData, "Hotix", "appsettings.json");

        // Migrate from old location on first access (best-effort)
        if (!File.Exists(newPath))
        {
            TryMigrateFromOldLocation(newPath);
        }

        return newPath;
    }

    /// <summary>
    /// Migrates settings from the old install-directory location (server/appsettings.json)
    /// to the new user-writable location.
    /// </summary>
    private static void TryMigrateFromOldLocation(string newPath)
    {
        string? oldPath = GetOldAppSettingsPath();
        if (oldPath == null || !File.Exists(oldPath))
        {
            Debug.WriteLine("[Hotix] No existing appsettings.json found at old location to migrate.");
            return;
        }

        try
        {
            string content = File.ReadAllText(oldPath);
            // Validate it's valid JSON before copying
            JsonDocument.Parse(content);

            string newDir = Path.GetDirectoryName(newPath)!;
            Directory.CreateDirectory(newDir);
            File.WriteAllText(newPath, content);

            Debug.WriteLine($"[Hotix] Migrated appsettings.json from {oldPath} to {newPath}");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Hotix] Failed to migrate appsettings.json: {ex.GetType().Name}: {ex.Message}");
            // Non-critical — user can re-enter their API keys
        }
    }

    /// <summary>
    /// Returns the old install-directory path for appsettings.json, or null if unresolved.
    /// </summary>
    private static string? GetOldAppSettingsPath()
    {
        // Try old project-root-relative paths
        string[] oldCandidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "server", "appsettings.json"),
            @"C:\hotix-invoice\server\appsettings.json",
        };

        foreach (string candidate in oldCandidates)
        {
            string fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }
}
