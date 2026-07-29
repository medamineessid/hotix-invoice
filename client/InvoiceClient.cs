using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hotix.InvoiceClient;

public sealed class InvoiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;

    public InvoiceClient(HttpClient httpClient) => _httpClient = httpClient;

    /// <summary>
    /// Returns the extracted result, or throws <see cref="InvoiceExtractionException"/> on non-200.
    /// </summary>
    public async Task<InvoiceResult> ExtractAsync(string filePath, string engine = "auto", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Fichier introuvable : {filePath}", filePath);

        await using FileStream fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(filePath));

        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", Path.GetFileName(filePath));

        using HttpResponseMessage response = await _httpClient.PostAsync($"/extract?engine={engine}", form, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvoiceExtractionException(response.StatusCode, body);

        InvoiceResult? result = JsonSerializer.Deserialize<InvoiceResult>(body, JsonOptions);
        if (result is null)
            throw new InvalidOperationException($"Réponse vide pour {filePath}.");

        return result;
    }

    private static string GetContentType(string filePath) =>
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
}

public sealed class InvoiceExtractionException(HttpStatusCode statusCode, string responseBody)
    : Exception($"HTTP {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
