using System.IO;

namespace Hotix.InvoiceClient;

/// <summary>
/// Shared helpers used across the OCR server client and the direct cloud
/// (Gemini / Grok) extraction paths.
/// </summary>
public static class ExtractionUtils
{
    /// <summary>Maps a file path's extension to its MIME/content type.</summary>
    public static string GetMimeType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".pdf"  => "application/pdf",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png"  => "image/png",
            ".bmp"  => "image/bmp",
            ".tif"  => "image/tiff",
            ".tiff" => "image/tiff",
            _       => "application/octet-stream",
        };

    /// <summary>
    /// Removes the ```json … ``` markdown fences that LLM responses are often
    /// wrapped in, returning the trimmed inner content.
    /// </summary>
    public static string StripJsonFences(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```json")) text = text[7..];
        if (text.EndsWith("```")) text = text[..^3];
        return text.Trim();
    }
}
