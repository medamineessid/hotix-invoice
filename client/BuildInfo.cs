// This file is overwritten at build time by the GenerateBuildInfo MSBuild target.
// When git is not available, the fallback values below are used instead.

namespace Hotix.InvoiceClient;

internal static class BuildInfo
{
    /// <summary>Short git commit hash, or a UTC timestamp fallback when git is unavailable.</summary>
    public const string CommitHash = "unknown";

    /// <summary>UTC timestamp of this build.</summary>
    public const string BuildTimestamp = "1970-01-01 00:00:00 UTC";
}
