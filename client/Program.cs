using System.Text.Json;
using Hotix.InvoiceClient;

var settings = AppSettings.Load();

string inputFolder = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0]
    : settings.InputFolder ?? Directory.GetCurrentDirectory();

string outputFile = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
    ? args[1]
    : settings.OutputFile ?? Path.Combine(inputFolder, "hotix_invoice_results.xlsx");

string apiBaseUrl = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? args[2]
    : settings.ApiBaseUrl ?? "http://localhost:8000";

if (!Directory.Exists(inputFolder))
{
    Console.Error.WriteLine($"Input folder not found: {inputFolder}");
    return 1;
}

var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".pdf",
    ".jpg",
    ".jpeg",
    ".png",
    ".tif",
    ".tiff",
};

string[] files = Directory
    .EnumerateFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
    .Where(file => allowedExtensions.Contains(Path.GetExtension(file)))
    .OrderBy(file => file)
    .ToArray();

if (files.Length == 0)
{
    Console.WriteLine($"No invoice files found in {inputFolder}.");
    return 0;
}

using var httpClient = new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl.TrimEnd('/')),
    Timeout = TimeSpan.FromMinutes(10),
};

var invoiceClient = new InvoiceClient(httpClient);
var invoices = new List<InvoiceResult>();
var failures = new List<ExtractionFailure>();

foreach (string filePath in files)
{
    try
    {
        InvoiceResult result = await invoiceClient.ExtractAsync(filePath);
        invoices.Add(result);

        if (result.HasMissingFields)
        {
            failures.Add(new ExtractionFailure(Path.GetFileName(filePath), $"Missing fields: {result.MissingFieldsSummary}"));
        }

        Console.WriteLine($"Processed {Path.GetFileName(filePath)}");
    }
    catch (Exception exception)
    {
        failures.Add(new ExtractionFailure(Path.GetFileName(filePath), exception.Message));
        Console.Error.WriteLine($"Failed {Path.GetFileName(filePath)}: {exception.Message}");
    }
}

var writer = new ExcelWriter();
writer.Write(outputFile, invoices, failures);

Console.WriteLine($"Excel output written to {outputFile}");
if (failures.Count > 0)
{
    Console.WriteLine($"Logged {failures.Count} failed or partial extractions on the FailedExtractions sheet.");
}

return 0;

static class AppSettings
{
    private const string FileName = "appsettings.json";

    public static AppSettingsData Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        }

        if (!File.Exists(path))
        {
            return new AppSettingsData();
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettingsData>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new AppSettingsData();
    }
}

public sealed class AppSettingsData
{
    public string? ApiBaseUrl { get; set; }

    public string? InputFolder { get; set; }

    public string? OutputFile { get; set; }
}
