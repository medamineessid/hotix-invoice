using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hotix.InvoiceClient;

/// <summary>
/// Shared path resolver used by App.xaml.cs (to launch the server), MainViewModel.cs
/// (appsettings.json / pipeline.log) and MainWindow.xaml.cs.
///
/// All resolution is RELATIVE to the running executable's folder and walks UP the
/// directory tree, so the application works from any install/source location and is
/// never tied to a developer's absolute path (e.g. C:\hotix-invoice).
///
/// Layouts covered:
///   - Dev/source:  &lt;root&gt;\client\publish\Hotix.InvoiceClient.exe  → venv/server at &lt;root&gt;
///   - Installed:   {app}\client\Hotix.InvoiceClient.exe              → venv/server at {app}
/// </summary>
public static class ServerPathResolver
{
    /// <summary>
    /// Returns the list of paths probed while walking up from startDir: startDir itself,
    /// then each parent, up to maxLevels levels. Used both for resolution and for
    /// user-facing diagnostics ("which locations were checked").
    /// </summary>
    public static string[] UpwardCandidates(string startDir, string relativePath, int maxLevels = 6)
    {
        var candidates = new List<string>();
        string current = Path.GetFullPath(startDir);
        for (int i = 0; i <= maxLevels; i++)
        {
            candidates.Add(Path.Combine(current, relativePath));
            var parent = Directory.GetParent(current);
            if (parent == null)
                break;
            current = parent.FullName;
        }
        return candidates.ToArray();
    }

    /// <summary>
    /// Returns the first existing file found while walking up from startDir
    /// (checking startDir itself, then each parent, up to maxLevels levels), or null.
    /// </summary>
    public static string? FindUpwards(string startDir, string relativePath, int maxLevels = 6)
        => UpwardCandidates(startDir, relativePath, maxLevels).FirstOrDefault(File.Exists);

    /// <summary>Full path to the venv Python executable (venv\Scripts\python.exe), or null.</summary>
    public static string? ResolveVenvPython()
        => FindUpwards(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("venv", "Scripts", "python.exe"));

    /// <summary>Full path to server/main.py, or null.</summary>
    public static string? ResolveMainPy()
        => FindUpwards(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("server", "main.py"));

    /// <summary>
    /// Full path to the Poppler binaries folder (poppler\bin or poppler\Library\bin),
    /// or null. Callers should honor the POPPLER_PATH env var before calling this.
    /// </summary>
    public static string? ResolvePopplerDirectory()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (string relative in new[]
        {
            Path.Combine("poppler", "bin"),
            Path.Combine("poppler", "Library", "bin"),
        })
        {
            string? found = FindUpwards(appDir, relative);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Walks up from startDir and returns the first directory that contains the given
    /// relative path (file or directory), or null. Unlike FindUpwards this returns the
    /// probed base directory, not the found item itself.
    /// </summary>
    public static string? FindBaseDirUpwards(string startDir, string relativePath, int maxLevels = 6)
    {
        string current = Path.GetFullPath(startDir);
        for (int i = 0; i <= maxLevels; i++)
        {
            string candidate = Path.Combine(current, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return current;

            var parent = Directory.GetParent(current);
            if (parent == null)
                break;
            current = parent.FullName;
        }
        return null;
    }

    /// <summary>
    /// Returns the application root: the nearest ancestor of the executable containing
    /// the venv, server/main.py, or requirements.txt. The venv is checked first so the
    /// root stays the project/install root even when a server\ copy exists inside the
    /// publish output. Used for log files (crash.log / pipeline.log).
    /// </summary>
    public static string? ResolveProjectRoot()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (string marker in new[]
        {
            Path.Combine("venv", "Scripts", "python.exe"),
            Path.Combine("server", "main.py"),
            "requirements.txt",
        })
        {
            string? root = FindBaseDirUpwards(appDir, marker);
            if (root != null)
                return root;
        }
        return null;
    }

    /// <summary>Returns a writable path under %LOCALAPPDATA%\Hotix\logs. Never throws.</summary>
    public static string ResolveWritableFile(string fileName)
    {
        string logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hotix", "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, fileName);
    }

    /// <summary>
    /// Returns the directory containing server/main.py, or null if not found.
    /// </summary>
    public static string? ResolveServerDirectory()
    {
        string? mainPy = ResolveMainPy();
        if (string.IsNullOrEmpty(mainPy))
            return null;
        return Path.GetDirectoryName(mainPy);
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
        // Probe relative to the executable (walking up), never a hard-coded absolute path.
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (string relative in new[]
        {
            "appsettings.json",
            Path.Combine("server", "appsettings.json"),
        })
        {
            string? found = FindUpwards(appDir, relative);
            if (found != null)
                return found;
        }
        return null;
    }
}
