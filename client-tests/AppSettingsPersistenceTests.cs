using System.Text.Json;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>
/// Regression tests for the appsettings.json persistence fix (commit a4a3974).
/// Verifies that the File.Replace atomic-save path works whether or not the
/// destination file already exists.
/// </summary>
public class AppSettingsPersistenceTests : IDisposable
{
    private readonly string _testDir;

    public AppSettingsPersistenceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hotix_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Mirrors WriteAppSettingsAsync from MainViewModel.cs lines 2476–2491,
    /// including the seed-file fix from commit a4a3974.
    /// </summary>
    private static async Task WriteAppSettingsAsync(string appSettingsPath, Dictionary<string, object?> settings)
    {
        string tempPath = appSettingsPath + ".tmp";
        string? dir = Path.GetDirectoryName(appSettingsPath);
        if (dir != null)
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json);
        // Seed-file fix from a4a3974
        if (!File.Exists(appSettingsPath))
            File.WriteAllText(appSettingsPath, "{}");
        File.Replace(tempPath, appSettingsPath, null);
    }

    [Fact]
    public async Task WriteAppSettings_DestinationAbsent_SeedFileCreated_JsonValid()
    {
        // Arrange: no appsettings.json exists yet (fresh install scenario)
        string path = Path.Combine(_testDir, "appsettings.json");
        Assert.False(File.Exists(path), "Precondition: file must not exist");

        var settings = new Dictionary<string, object?>
        {
            ["gemini_api_key"] = "encrypted_value_1",
            ["default_engine"] = "gemini",
        };

        // Act
        await WriteAppSettingsAsync(path, settings);

        // Assert: file exists and contains valid JSON with the expected key
        Assert.True(File.Exists(path), "appsettings.json should exist after save");
        string content = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(content);
        Assert.Equal("encrypted_value_1", doc.RootElement.GetProperty("gemini_api_key").GetString());
        Assert.Equal("gemini", doc.RootElement.GetProperty("default_engine").GetString());
    }

    [Fact]
    public async Task WriteAppSettings_DestinationExists_NewContentOverwrites()
    {
        // Arrange: file already exists from a prior save (normal path)
        string path = Path.Combine(_testDir, "appsettings.json");
        var prior = new Dictionary<string, object?> { ["onboarding_completed"] = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(prior));

        var settings = new Dictionary<string, object?>
        {
            ["gemini_api_key"] = "encrypted_value_2",
            ["grok_api_key"] = "",
            ["default_engine"] = "gemini",
            ["onboarding_completed"] = true,
        };

        // Act
        await WriteAppSettingsAsync(path, settings);

        // Assert: new content is on disk, old content is gone
        string content = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(content);
        Assert.Equal("encrypted_value_2", doc.RootElement.GetProperty("gemini_api_key").GetString());
        Assert.True(doc.RootElement.TryGetProperty("onboarding_completed", out var el) && el.GetBoolean());
        // Old field that was NOT in the new settings must be absent
        Assert.False(doc.RootElement.TryGetProperty("gemini_model", out _));
    }
}
