using System;
using System.IO;
using System.Linq;

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
    /// Returns the full path to appsettings.json inside the resolved server directory.
    /// Falls back to @"C:\hotix-invoice\server\appsettings.json" if main.py is not found.
    /// </summary>
    public static string ResolveAppSettingsPath()
    {
        string? serverDir = ResolveServerDirectory();
        if (serverDir == null)
            return @"C:\hotix-invoice\server\appsettings.json";
        return Path.Combine(serverDir, "appsettings.json");
    }
}
