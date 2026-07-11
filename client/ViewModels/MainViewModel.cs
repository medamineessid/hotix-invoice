using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Hotix.InvoiceClient;

namespace Hotix.InvoiceClient.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly string[] AllowedExtensions =
        { ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hotix", "settings.json");

    private readonly HttpClient _apiHttpClient;
    private readonly InvoiceClient _invoiceClient;

    private string _selectedEngine = "auto";
    private bool _geminiAvailable;
    private string _geminiKeyInput = string.Empty;
    private bool _grokAvailable;
    private string _grokKeyInput = string.Empty;
    private bool _isSettingsPanelOpen;
    private DispatcherTimer? _engineStatusTimer;
    private DispatcherTimer? _processingTimer;
    private readonly Stopwatch _processingStopwatch = new();
    private bool _isServerRunning = true;
    private bool _isServerStarted;
    private bool _isServerStarting;
    private string _serverStartingStatus = string.Empty;
    private bool _internetAvailable;
    private InvoiceRowViewModel? _selectedRow;
    private CancellationTokenSource? _extractionCts;

    private string _selectedFolder = string.Empty;
    private bool _isExtracting;
    private bool _isProgressVisible;
    private int _processedFiles;
    private int _totalFiles;
    private bool _allFilesSelected;
    private bool _allRowsSelected;
    private bool _quotaFallbackBannerShown;
    private bool _showSummaryBanner;
    private string _summaryBannerText = string.Empty;
    private string _summaryBannerColor = "#2ECC71";
    private string? _saveConfirmationPath;
    private string? _lastExportSheetName;

    // Gemini REST API endpoint
    private const string GeminiApiBase = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

    // Grok (xAI) REST API endpoint
    private const string GrokApiBase = "https://api.x.ai/v1/chat/completions";
    private const string GrokModel = "grok-4.3";

    public MainViewModel()
    {
        string apiBaseUrl = Environment.GetEnvironmentVariable("HOTIX_API_BASE_URL")
            ?? $"http://{IPAddress.Loopback}:8000";

        _apiHttpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromMinutes(5),
        };

        _invoiceClient = new InvoiceClient(_apiHttpClient);

        DetectedFiles     = new ObservableCollection<FileItemViewModel>();
        Results           = new ObservableCollection<InvoiceRowViewModel>();
        IncompleteResults = new ObservableCollection<InvoiceRowViewModel>();

        DetectedFiles.CollectionChanged += (_, _) => NotifyFileCountChanged();

        BrowseFolderCommand    = new RelayCommand(_ => BrowseFolder());
        BrowseFilesCommand     = new RelayCommand(_ => BrowseFiles());
        StartExtractionCommand = new RelayCommand(async _ => await StartExtractionAsync(), _ => CanStartExtraction());
        CancelExtractionCommand = new RelayCommand(_ => CancelExtraction(), _ => IsExtracting);
        ExportExcelCommand     = new RelayCommand(_ => ExportExcel(), _ => CanExport());
        ClearCommand           = new RelayCommand(_ => ClearResults(), _ => CanClear());
        RerunCommand           = new RelayCommand(async p => await RerunRowAsync(p as InvoiceRowViewModel));
        RerunAllErrorsCommand  = new RelayCommand(async _ => await RerunAllErrorsAsync(), _ => Results.Any(r => r.HasError) && !IsExtracting);
        ToggleAllFilesCommand  = new RelayCommand(_ => ToggleAllFiles());
        ToggleAllRowsCommand   = new RelayCommand(_ => ToggleAllRows());
        ClearSelectedRowCommand = new RelayCommand(_ => SelectedRow = null);
        OpenSavedFolderCommand = new RelayCommand(_ => OpenSavedFolder(), _ => _saveConfirmationPath != null);
        OpenSavedFileCommand = new RelayCommand(_ => OpenSavedFile(), _ => _saveConfirmationPath != null);
        RetryServerCommand    = new RelayCommand(async _ => await RetryServerAsync(), _ => !IsServerStarting && !IsExtracting);
        ToggleSettingsCommand  = new RelayCommand(_ =>
        {
            var wizard = new global::Hotix.InvoiceClient.GeminiSetupWindow { DataContext = this };
            wizard.Owner = Application.Current.MainWindow;
            wizard.ShowDialog();
        });
        SaveGeminiKeyCommand   = new RelayCommand(async _ => await SaveGeminiKeyAsync());
        ClearGeminiKeyCommand  = new RelayCommand(async _ => await ClearGeminiKeyAsync());
        SaveGrokKeyCommand     = new RelayCommand(async _ => await SaveGrokKeyAsync());
        ClearGrokKeyCommand    = new RelayCommand(async _ => await ClearGrokKeyAsync());

        LoadSettings();
        LoadProviderKeysFromAppSettings();

        // Poll engine + internet status every 45 seconds on the UI thread
        _engineStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(45),
        };
        _engineStatusTimer.Tick += async (s, e) => await CheckEngineStatusAsync();
        _engineStatusTimer.Start();

        // Initial connectivity check
        _ = CheckEngineStatusAsync();

        // Re-evaluate WindowTitle when the UI language changes
        TranslationSource.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
                OnPropertyChanged(nameof(WindowTitle));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileItemViewModel>   DetectedFiles     { get; }
    public ObservableCollection<InvoiceRowViewModel> Results           { get; }
    public ObservableCollection<InvoiceRowViewModel> IncompleteResults { get; }

    public ICommand BrowseFolderCommand     { get; }
    public ICommand BrowseFilesCommand      { get; }
    public ICommand StartExtractionCommand  { get; }
    public ICommand CancelExtractionCommand { get; }
    public ICommand ExportExcelCommand      { get; }
    public ICommand ClearCommand            { get; }
    public ICommand RerunCommand            { get; }
    public ICommand RerunAllErrorsCommand   { get; }
    public ICommand ToggleAllFilesCommand   { get; }
    public ICommand ToggleAllRowsCommand    { get; }
    public ICommand ClearSelectedRowCommand { get; }
    public ICommand OpenSavedFolderCommand  { get; }
    public ICommand OpenSavedFileCommand    { get; }
    public ICommand RetryServerCommand     { get; }
    public ICommand ToggleSettingsCommand   { get; }
    public ICommand SaveGeminiKeyCommand    { get; }
    public ICommand ClearGeminiKeyCommand   { get; }
    public ICommand SaveGrokKeyCommand      { get; }
    public ICommand ClearGrokKeyCommand     { get; }

    public string SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetField(ref _selectedFolder, value))
            {
                OnPropertyChanged(nameof(HasSelectedFolder));
                RaiseCommandStateChanged();
            }
        }
    }

    public string SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (SetField(ref _selectedEngine, value))
            {
                SaveSettings();
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
            }
        }
    }

    public bool GeminiAvailable
    {
        get => _geminiAvailable;
        set
        {
            if (SetField(ref _geminiAvailable, value))
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
        }
    }

    public string GeminiKeyInput
    {
        get => _geminiKeyInput;
        set
        {
            if (SetField(ref _geminiKeyInput, value))
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
        }
    }

    public bool GrokAvailable
    {
        get => _grokAvailable;
        set
        {
            if (SetField(ref _grokAvailable, value))
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
        }
    }

    public string GrokKeyInput
    {
        get => _grokKeyInput;
        set
        {
            if (SetField(ref _grokKeyInput, value))
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
        }
    }

    public bool IsSettingsPanelOpen
    {
        get => _isSettingsPanelOpen;
        set => SetField(ref _isSettingsPanelOpen, value);
    }

    /// <summary>True if the local OCR server is currently running and healthy.</summary>
    public bool IsServerRunning
    {
        get => _isServerRunning;
        set
        {
            if (SetField(ref _isServerRunning, value))
            {
                OnPropertyChanged(nameof(ShowServerDiedOverlay));
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusDotColor));
                OnPropertyChanged(nameof(ServerStatusTooltip));
                RaiseCommandStateChanged();
            }
        }
    }

    /// <summary>True if the server has been started at least once (to distinguish "not yet started" from "crashed").</summary>
    public bool IsServerStarted
    {
        get => _isServerStarted;
        private set
        {
            if (SetField(ref _isServerStarted, value))
            {
                OnPropertyChanged(nameof(ShowServerDiedOverlay));
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusDotColor));
            }
        }
    }

    /// <summary>True if the server is currently starting up.</summary>
    public bool IsServerStarting
    {
        get => _isServerStarting;
        private set
        {
            if (SetField(ref _isServerStarting, value))
            {
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(ServerStatusDotColor));
                OnPropertyChanged(nameof(ServerStatusTooltip));
            }
        }
    }

    /// <summary>Status message shown while the server is starting.</summary>
    public string ServerStartingStatus
    {
        get => _serverStartingStatus;
        private set => SetField(ref _serverStartingStatus, value);
    }

    /// <summary>True when server was running but has since died — shows the crash overlay.</summary>
    public bool ShowServerDiedOverlay => _isServerStarted && !_isServerRunning;

    /// <summary>Display text for the server status indicator in the sidebar footer.</summary>
    public string ServerStatusText
    {
        get
        {
            if (_isServerStarting) return TranslationSource.Get("ServerStatusStarting");
            if (_isServerStarted && _isServerRunning) return TranslationSource.Get("ServerStatusActive");
            if (_isServerStarted && !_isServerRunning) return TranslationSource.Get("ServerStatusStopped");
            return TranslationSource.Get("ServerStatusInactive");
        }
    }

    /// <summary>Color key for the server status dot in the sidebar footer.</summary>
    public string ServerStatusDotColor
    {
        get
        {
            if (_isServerStarting) return "BrushAccent";
            if (_isServerStarted && _isServerRunning) return "BrushSuccess";
            if (_isServerStarted && !_isServerRunning) return "BrushError";
            return "BrushTextMuted";
        }
    }

    /// <summary>Tooltip for the server status indicator explaining the current state.</summary>
    public string ServerStatusTooltip
    {
        get
        {
            if (_isServerStarting) return TranslationSource.Get("ServerTooltipStarting");
            if (_isServerStarted && _isServerRunning) return TranslationSource.Get("ServerTooltipActive");
            if (_isServerStarted && !_isServerRunning) return TranslationSource.Get("ServerTooltipStopped");
            return TranslationSource.Get("ServerTooltipInactive");
        }
    }

    public bool HasSelectedFolder => !string.IsNullOrWhiteSpace(SelectedFolder);

    public bool IsExtracting
    {
        get => _isExtracting;
        private set
        {
            if (SetField(ref _isExtracting, value))
                RaiseCommandStateChanged();
        }
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set => SetField(ref _isProgressVisible, value);
    }

    public int ProcessedFiles
    {
        get => _processedFiles;
        private set
        {
            if (SetField(ref _processedFiles, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }
    }

    public int TotalFiles
    {
        get => _totalFiles;
        private set
        {
            if (SetField(ref _totalFiles, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }
    }

    public string ProgressText       => TranslationSource.Fmt("ProgressText", ProcessedFiles, TotalFiles);
    public double ProgressPercentage => TotalFiles == 0 ? 0.0 : (double)ProcessedFiles / TotalFiles * 100.0;
    public string SummaryText        => TranslationSource.Fmt("StatusBarSummary", Results.Count, IncompleteResults.Count);

    public bool AllFilesSelected
    {
        get => _allFilesSelected;
        set => SetField(ref _allFilesSelected, value);
    }

    public bool AllRowsSelected
    {
        get => _allRowsSelected;
        set => SetField(ref _allRowsSelected, value);
    }

    public string FileCountLabel
    {
        get
        {
            int total    = DetectedFiles.Count;
            int selected = DetectedFiles.Count(f => f.IsSelected);
            return TranslationSource.Fmt("FileCountLabel", total, selected);
        }
    }

    public bool ShowSummaryBanner
    {
        get => _showSummaryBanner;
        private set => SetField(ref _showSummaryBanner, value);
    }

    public string SummaryBannerText
    {
        get => _summaryBannerText;
        private set => SetField(ref _summaryBannerText, value);
    }

    public string SummaryBannerColor
    {
        get => _summaryBannerColor;
        private set => SetField(ref _summaryBannerColor, value);
    }

    public string? SaveConfirmationPath
    {
        get => _saveConfirmationPath;
        private set
        {
            if (SetField(ref _saveConfirmationPath, value))
            {
                OnPropertyChanged(nameof(ShowSaveConfirmation));
                OnPropertyChanged(nameof(SaveConfirmationText));
                (OpenSavedFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (OpenSavedFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool   ShowSaveConfirmation => _saveConfirmationPath != null;
    public string SaveConfirmationText => _saveConfirmationPath != null
        ? TranslationSource.Fmt("SaveConfirmationText", _saveConfirmationPath)
        : string.Empty;

    // Selected row drives the raw text preview panel
    public InvoiceRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetField(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(HasSelectedRow));
                OnPropertyChanged(nameof(PreviewRawText));
                OnPropertyChanged(nameof(PreviewFileName));
            }
        }
    }

    public bool    HasSelectedRow  => _selectedRow != null;
    public string  PreviewRawText  => _selectedRow?.RawText ?? string.Empty;
    public string  PreviewFileName => _selectedRow != null
        ? TranslationSource.Fmt("PreviewFileName", _selectedRow.FileName)
        : TranslationSource.Get("PreviewFileNameDefault");

    public bool HasErrors => Results.Any(r => r.HasError);

    /// <summary>Display text showing the resolved engine that will be used for extraction.</summary>
    public string ResolvedEngineDisplay
    {
        get
        {
            // Use in-memory fields directly (no disk I/O from LoadGeminiApiKey/LoadGrokApiKey)
            bool hasGemini = _internetAvailable && !string.IsNullOrEmpty(_geminiKeyInput);
            bool hasGrok = _internetAvailable && !string.IsNullOrEmpty(_grokKeyInput);

            return _selectedEngine switch
            {
                "gemini" => hasGemini
                    ? TranslationSource.Get("EngineBadgeGeminiReady")
                    : TranslationSource.Get("EngineBadgeGeminiNoKey"),
                "grok" => hasGrok
                    ? TranslationSource.Get("EngineBadgeGrokReady")
                    : TranslationSource.Get("EngineBadgeGrokNoKey"),
                "ocr" => TranslationSource.Get("EngineBadgeOcr"),
                "auto" => ResolveAutoEngineDisplay(hasGemini, hasGrok),
                _ => TranslationSource.Get("EngineBadgeAuto"),
            };
        }
    }

    private string ResolveAutoEngineDisplay(bool hasGemini, bool hasGrok)
    {
        if (hasGemini)
            return $"{TranslationSource.Get("EngineBadgeAuto")} → {TranslationSource.Get("EngineBadgeGeminiShort")}";
        if (hasGrok)
            return $"{TranslationSource.Get("EngineBadgeAuto")} → {TranslationSource.Get("EngineBadgeGrokShort")}";
        return $"{TranslationSource.Get("EngineBadgeAuto")} → {TranslationSource.Get("EngineBadgeOcr")}";
    }

    /// <summary>Window title including the build commit hash for build-identification.</summary>
    public string WindowTitle => $"{TranslationSource.Get("MainWindowTitle")} — {BuildInfo.CommitHash}";

    public async Task InitializeAsync()
    {
        await CheckEngineStatusAsync();
    }

    // ── Engine & Connectivity Status ──────────────────────────────────────

    private async Task CheckEngineStatusAsync()
    {
        // Check internet connectivity
        _internetAvailable = await CheckInternetAsync();
        OnPropertyChanged(nameof(InternetAvailable));
        OnPropertyChanged(nameof(ResolvedEngineDisplay));

        // Try to check via server if it's already running
        if (_isServerStarted && _isServerRunning)
        {
            try
            {
                using HttpResponseMessage response = await _apiHttpClient.GetAsync("/engine-status");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var status = JsonSerializer.Deserialize<JsonElement>(body);
                    GeminiAvailable = status.GetProperty("gemini_available").GetBoolean();
                    return;
                }
            }
            catch { /* server not reachable */ }
        }

        // If server is not running, we cannot determine engine status
        // Do NOT make live API calls to Gemini/Grok to avoid burning quota
        GeminiAvailable = false;
        GrokAvailable = false;
    }

    private static async Task<bool> CheckInternetAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("https://www.google.com/generate_204",
                HttpCompletionOption.ResponseContentRead);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether internet connectivity is currently available.</summary>
    public bool InternetAvailable => _internetAvailable;

    // ── Gemini Direct API (client-side) ───────────────────────────────────

    /// <summary>
    /// Loads the Gemini API key from memory or appsettings.json.
    /// </summary>
    private string? LoadGeminiApiKey()
    {
        // 1. Try in-memory input first (set during this session)
        if (!string.IsNullOrEmpty(_geminiKeyInput))
            return _geminiKeyInput;

        // 2. Fallback to appsettings.json
        try
        {
            string path = ResolveAppSettingsPath();
            if (File.Exists(path))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("gemini_api_key", out var el))
                {
                    string? key = el.GetString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        _geminiKeyInput = key;
                        return key;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    /// <summary>
    /// Loads the Grok API key from memory or appsettings.json.
    /// </summary>
    private string? LoadGrokApiKey()
    {
        // 1. Try in-memory input first (set during this session)
        if (!string.IsNullOrEmpty(_grokKeyInput))
            return _grokKeyInput;

        // 2. Fallback to appsettings.json
        try
        {
            string path = ResolveAppSettingsPath();
            if (File.Exists(path))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("grok_api_key", out var el))
                {
                    string? key = el.GetString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        _grokKeyInput = key;
                        return key;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    /// <summary>
    /// Tests whether a Gemini API key is valid by making a minimal API call.
    /// Returns (IsValid, ErrorMessage). When offline, returns (false, error).
    /// </summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateGeminiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, TranslationSource.Get("GeminiKeyEmpty"));

        bool hasInternet = await CheckInternetAsync();
        if (!hasInternet)
            return (false, TranslationSource.Get("GeminiNoInternet"));

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var body = new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } };
            var response = await client.PostAsJsonAsync(
                $"{GeminiApiBase}?key={apiKey}", body);

            if (response.IsSuccessStatusCode)
                return (true, null);

            string responseBody = await response.Content.ReadAsStringAsync();

            return (int)response.StatusCode switch
            {
                400 => (false, TranslationSource.Fmt("GeminiErrorPrefix", 400, ResponseBodySummary(responseBody))),
                401 => (false, TranslationSource.Fmt("GeminiErrorPrefix", 401, ResponseBodySummary(responseBody))),
                403 => (false, TranslationSource.Fmt("GeminiErrorPrefix", 403, ResponseBodySummary(responseBody))),
                429 => (false, TranslationSource.Fmt("GeminiErrorPrefix", 429, ResponseBodySummary(responseBody))),
                _   => (false, TranslationSource.Fmt("GeminiErrorPrefix", (int)response.StatusCode, responseBody)),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, TranslationSource.Get("GeminiTimeout"));
        }
        catch (HttpRequestException ex)
        {
            return (false, TranslationSource.Fmt("GeminiNetworkError", $"{ex.GetType().Name}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return (false, TranslationSource.Fmt("GeminiUnexpectedError", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Tests whether a Grok API key is valid by making a minimal API call.
    /// </summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateGrokKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, TranslationSource.Get("GeminiKeyEmpty"));

        bool hasInternet = await CheckInternetAsync();
        if (!hasInternet)
            return (false, TranslationSource.Get("GeminiNoInternet"));

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var body = new
            {
                model = GrokModel,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1
            };

            var response = await client.PostAsJsonAsync(GrokApiBase, body);

            if (response.IsSuccessStatusCode)
                return (true, null);

            string responseBody = await response.Content.ReadAsStringAsync();

            return (int)response.StatusCode switch
            {
                401 => (false, TranslationSource.Fmt("GrokErrorPrefix", 401, ResponseBodySummary(responseBody))),
                403 => (false, TranslationSource.Fmt("GrokErrorPrefix", 403, ResponseBodySummary(responseBody))),
                429 => (false, TranslationSource.Fmt("GrokErrorPrefix", 429, ResponseBodySummary(responseBody))),
                _   => (false, TranslationSource.Fmt("GrokErrorPrefix", (int)response.StatusCode, responseBody)),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, TranslationSource.Get("GeminiTimeout"));
        }
        catch (HttpRequestException ex)
        {
            return (false, TranslationSource.Fmt("GeminiNetworkError", $"{ex.GetType().Name}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return (false, TranslationSource.Fmt("GeminiUnexpectedError", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }



    private async Task<InvoiceResult> CallGeminiDirectlyAsync(string filePath, string apiKey)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        string base64Data = Convert.ToBase64String(fileBytes);
        string mimeType = GetMimeType(filePath);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = TranslationSource.Get("GeminiExtractionText") },
                        new { inline_data = new { mime_type = mimeType, data = base64Data } }
                    }
                }
            },
            generationConfig = new { response_mime_type = "application/json" }
        };

        using var geminiClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var response = await geminiClient.PostAsJsonAsync(
            $"{GeminiApiBase}?key={apiKey}", requestBody);

        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 429)
                throw new GeminiQuotaExceededException(TranslationSource.Fmt("GeminiApiError", 429, ResponseBodySummary(responseBody)));
            throw new GeminiApiException(TranslationSource.Fmt("GeminiApiError", (int)response.StatusCode, responseBody));
        }

        // Parse Gemini response JSON
        using var doc = JsonDocument.Parse(responseBody);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrEmpty(text))
            throw new GeminiApiException(TranslationSource.Get("GeminiEmptyResponse"));

        // Strip markdown fences if present
        text = text.Trim();
        if (text.StartsWith("```json")) text = text[7..];
        if (text.EndsWith("```")) text = text[..^3];
        text = text.Trim();

        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
        if (fields == null)
            throw new GeminiApiException(TranslationSource.Get("GeminiParseError"));

        static string? GetStringField(Dictionary<string, JsonElement> dict, string key)
        {
            return dict.TryGetValue(key, out var el) && el.ValueKind != JsonValueKind.Null
                ? el.GetString()
                : null;
        }

        return new InvoiceResult
        {
            NumeroFacture = GetStringField(fields, "numero_facture"),
            Date = GetStringField(fields, "date"),
            Fournisseur = GetStringField(fields, "fournisseur"),
            Client = GetStringField(fields, "client"),
            MontantHt = GetStringField(fields, "montant_ht"),
            MontantTva = GetStringField(fields, "montant_tva"),
            MontantTaxe = GetStringField(fields, "montant_taxe"),
            MontantTtc = GetStringField(fields, "montant_ttc"),
            Confidence = 0.95,
            RawText = TranslationSource.Get("GeminiDirectExtraction"),
            EngineUsed = "gemini",
        };
    }

    /// <summary>
    /// Extract invoice data via the Grok (xAI) API using an OpenAI-compatible chat completions call.
    /// </summary>
    private async Task<InvoiceResult> CallGrokDirectlyAsync(string filePath, string apiKey)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        string base64Data = Convert.ToBase64String(fileBytes);
        string mimeType = GetMimeType(filePath);
        string dataUri = $"data:{mimeType};base64,{base64Data}";

        var requestBody = new
        {
            model = GrokModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = TranslationSource.Get("GrokExtractionText") },
                        new { type = "image_url", image_url = new { url = dataUri } }
                    }
                }
            },
            response_format = new { type = "json_object" }
        };

        using var grokClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        grokClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var response = await grokClient.PostAsJsonAsync(GrokApiBase, requestBody);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // Log the exact response body for all non-2xx responses to avoid misclassifying real errors as quota
            string errorDetail = responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody;
            if ((int)response.StatusCode == 429)
                throw new GeminiQuotaExceededException(TranslationSource.Fmt("GrokApiError", 429, errorDetail));
            throw new GeminiApiException(TranslationSource.Fmt("GrokApiError", (int)response.StatusCode, errorDetail));
        }

        // Parse OpenAI-compatible response
        using var doc = JsonDocument.Parse(responseBody);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrEmpty(text))
            throw new GeminiApiException(TranslationSource.Get("GrokEmptyResponse"));

        // Strip markdown fences if present
        text = text.Trim();
        if (text.StartsWith("```json")) text = text[7..];
        if (text.EndsWith("```")) text = text[..^3];
        text = text.Trim();

        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
        if (fields == null)
            throw new GeminiApiException(TranslationSource.Get("GrokParseError"));

        static string? GetStringField(Dictionary<string, JsonElement> dict, string key)
        {
            return dict.TryGetValue(key, out var el) && el.ValueKind != JsonValueKind.Null
                ? el.GetString()
                : null;
        }

        return new InvoiceResult
        {
            NumeroFacture = GetStringField(fields, "numero_facture"),
            Date = GetStringField(fields, "date"),
            Fournisseur = GetStringField(fields, "fournisseur"),
            Client = GetStringField(fields, "client"),
            MontantHt = GetStringField(fields, "montant_ht"),
            MontantTva = GetStringField(fields, "montant_tva"),
            MontantTaxe = GetStringField(fields, "montant_taxe"),
            MontantTtc = GetStringField(fields, "montant_ttc"),
            Confidence = 0.95,
            RawText = TranslationSource.Get("GrokDirectExtraction"),
            EngineUsed = "grok",
        };
    }

    /// <summary>
    /// Extracts a short summary from a JSON error response body for display.
    /// </summary>
    private static string ResponseBodySummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                var message = errorEl.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? ""
                    : "";
                var status = errorEl.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString() ?? ""
                    : "";
                if (!string.IsNullOrEmpty(message))
                    return $"{status}: {message}";
            }
        }
        catch { }
        // Truncate raw body to a reasonable length
        return body.Length > 200 ? body[..200] + "…" : body;
    }

    private static string GetMimeType(string filePath) =>
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

    // ── Lazy Server Startup ──────────────────────────────────────────────

    /// <summary>
    /// Ensures the local OCR server is running. Starts it lazily if needed.
    /// Updates IsServerStarting/ServerStartingStatus for the UI.
    /// </summary>
    /// <summary>
    /// Ensures the local OCR server is running. Starts it lazily if needed.
    /// If the server is already being started by another call, waits up to
    /// 35 seconds for it to become ready before timing out.
    /// </summary>
    private async Task EnsureServerReadyAsync()
    {
        if (_isServerStarted && _isServerRunning)
            return;

        if (_isServerStarting)
        {
            // Already starting — wait for completion with a timeout
            Debug.WriteLine("[Hotix] EnsureServerReadyAsync: waiting for already-starting server...");
            var waitStart = Stopwatch.StartNew();
            while (waitStart.Elapsed < TimeSpan.FromSeconds(35))
            {
                await Task.Delay(500);
                if (_isServerStarted && _isServerRunning)
                {
                    Debug.WriteLine("[Hotix] EnsureServerReadyAsync: server became ready (waited {0}ms)", waitStart.ElapsedMilliseconds);
                    return;
                }
                // If the other server start attempt failed, _isServerStarting will be reset
                if (!_isServerStarting)
                {
                    Debug.WriteLine("[Hotix] EnsureServerReadyAsync: other server start failed, retrying...");
                    break; // Exit wait loop and try starting ourselves
                }
            }
            if (_isServerStarted && _isServerRunning)
                return;
            Debug.WriteLine("[Hotix] EnsureServerReadyAsync: wait timed out, will retry start");
        }

        IsServerStarting = true;
        ServerStartingStatus = TranslationSource.Get("ServerStartingOcr");
        IsServerRunning = false;
        (RetryServerCommand as RelayCommand)?.RaiseCanExecuteChanged();

        try
        {
            var progress = new Progress<string>(status =>
            {
                // Dispatch back to UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ServerStartingStatus = status;
                });
            });

            Debug.WriteLine("[Hotix] EnsureServerReadyAsync: starting server...");
            bool success = await App.StartServerAsync(progress);
            Debug.WriteLine("[Hotix] EnsureServerReadyAsync: server start result = {0}", success);

            if (success)
            {
                IsServerStarted = true;
                IsServerRunning = true;
                IsServerStarting = false;
                ServerStartingStatus = string.Empty;

                // Re-check engine status now that server is running
                await CheckEngineStatusAsync();
            }
            else
            {
                IsServerStarting = false;
                ServerStartingStatus = string.Empty;
                IsServerRunning = false;
                (RetryServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
                throw new InvalidOperationException(
                    TranslationSource.Get("ServerStartingFailed"));
            }

            (RetryServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (InvalidOperationException)
        {
            (RetryServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Hotix] EnsureServerReadyAsync: exception: {0}: {1}", ex.GetType().Name, ex.Message);
            IsServerStarting = false;
            ServerStartingStatus = string.Empty;
            (RetryServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            throw new InvalidOperationException(TranslationSource.Fmt("ServerStartFailPrefix", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    // ── Server Retry ──────────────────────────────────────────────────────

    private async Task RetryServerAsync()
    {
        try
        {
            await EnsureServerReadyAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, TranslationSource.Get("ErrorRetryTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Key Management ────────────────────────────────────────────────────

    private async Task ClearGeminiKeyAsync()
    {
        GeminiKeyInput = string.Empty;
        GeminiAvailable = false;

        try
        {
            // Clear from appsettings.json
            string appSettingsPath = ResolveAppSettingsPath();
            var settings = new { gemini_api_key = "", grok_api_key = _grokKeyInput, default_engine = SelectedEngine };
            await File.WriteAllTextAsync(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorClearKey", $"{ex.GetType().Name}: {ex.Message}"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveGeminiKeyAsync()
    {
        try
        {
            // Validate key via server endpoint (uses the server's Python environment
            // and reads from the same appsettings.json the server will use)
            if (_isServerStarted && _isServerRunning)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var payload = new { api_key = GeminiKeyInput };
                    var response = await client.PostAsJsonAsync(
                        "http://127.0.0.1:8000/validate-gemini-key", payload);
                    var body = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<JsonElement>(body);

                    bool valid = result.GetProperty("valid").GetBoolean();
                    if (!valid)
                    {
                        string? error = result.TryGetProperty("error", out var errEl)
                            ? errEl.GetString()
                            : null;
                        string msg = TranslationSource.Fmt("GeminiServerValidationFailed",
                            error ?? "unknown error");
                        MessageBox.Show(msg, TranslationSource.Get("GeminiValidationTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Server not reachable — still save the key, but warn the user
                }
                catch (TaskCanceledException)
                {
                    // Timeout — still save the key
                }
            }

            // Save to appsettings.json (same location server reads from)
            string appSettingsPath = ResolveAppSettingsPath();
            var settings = new { gemini_api_key = GeminiKeyInput, grok_api_key = _grokKeyInput, default_engine = SelectedEngine };
            await File.WriteAllTextAsync(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorSaveKey", $"{ex.GetType().Name}: {ex.Message}"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ClearGrokKeyAsync()
    {
        GrokKeyInput = string.Empty;
        GrokAvailable = false;

        try
        {
            // Clear from appsettings.json
            string appSettingsPath = ResolveAppSettingsPath();
            var settings = new { gemini_api_key = _geminiKeyInput, grok_api_key = "", default_engine = SelectedEngine };
            await File.WriteAllTextAsync(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorClearKey", $"{ex.GetType().Name}: {ex.Message}"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveGrokKeyAsync()
    {
        try
        {
            // Save to appsettings.json
            string appSettingsPath = ResolveAppSettingsPath();
            var settings = new { gemini_api_key = _geminiKeyInput, grok_api_key = GrokKeyInput, default_engine = SelectedEngine };
            await File.WriteAllTextAsync(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorSaveKey", $"{ex.GetType().Name}: {ex.Message}"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadProviderKeysFromAppSettings()
    {
        try
        {
            // Read from appsettings.json
            string appSettingsPath = ResolveAppSettingsPath();
            if (File.Exists(appSettingsPath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
                if (doc.RootElement.TryGetProperty("gemini_api_key", out var el))
                {
                    string? key = el.GetString();
                    if (!string.IsNullOrEmpty(key))
                        GeminiKeyInput = key;
                }
                if (doc.RootElement.TryGetProperty("grok_api_key", out var grokEl))
                {
                    string? key = grokEl.GetString();
                    if (!string.IsNullOrEmpty(key))
                        GrokKeyInput = key;
                }
                if (doc.RootElement.TryGetProperty("default_engine", out var engineEl))
                    SelectedEngine = engineEl.GetString() ?? "auto";
            }
        }
        catch
        {
            // Intentionally ignored: loading settings is best-effort.
        }
    }

    /// <summary>
    /// Resolves the full path to appsettings.json using the shared ServerPathResolver.
    /// Validates that server/main.py actually exists (not just the folder).
    /// </summary>
    public static string ResolveAppSettingsPath()
    {
        return ServerPathResolver.ResolveAppSettingsPath();
    }

    // ── Folder / File Selection ──────────────────────────────────────────

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = TranslationSource.Get("BrowseFolderTitle"),
            InitialDirectory = Directory.Exists(SelectedFolder)
                ? SelectedFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (!dialog.ShowDialog().GetValueOrDefault()) return;

        SelectedFolder = dialog.FolderName;
        LoadDetectedFiles();
    }

    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title            = TranslationSource.Get("BrowseFilesTitle"),
            Filter           = TranslationSource.Get("BrowseFilesFilter"),
            Multiselect      = true,
            InitialDirectory = Directory.Exists(SelectedFolder)
                ? SelectedFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (!dialog.ShowDialog().GetValueOrDefault()) return;

        string? folder = Path.GetDirectoryName(dialog.FileNames[0]);
        if (folder != null) SelectedFolder = folder;

        DetectedFiles.Clear();
        foreach (string file in dialog.FileNames.OrderBy(f => f))
        {
            var item = new FileItemViewModel(file);
            item.PropertyChanged += OnFileItemPropertyChanged;
            DetectedFiles.Add(item);
        }

        NotifyFileCountChanged();
        RaiseCommandStateChanged();
    }

    private void LoadDetectedFiles()
    {
        DetectedFiles.Clear();
        if (!Directory.Exists(SelectedFolder)) return;

        foreach (string file in Directory
            .EnumerateFiles(SelectedFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f))
        {
            var item = new FileItemViewModel(file);
            item.PropertyChanged += OnFileItemPropertyChanged;
            DetectedFiles.Add(item);
        }

        NotifyFileCountChanged();
        RaiseCommandStateChanged();
    }

    private void OnFileItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.IsSelected))
            NotifyFileCountChanged();
    }

    private void NotifyFileCountChanged()
    {
        OnPropertyChanged(nameof(FileCountLabel));
        RaiseCommandStateChanged();
    }

    private void ToggleAllFiles()
    {
        bool newState = !AllFilesSelected;
        AllFilesSelected = newState;
        foreach (var f in DetectedFiles) f.IsSelected = newState;
    }

    private void ToggleAllRows()
    {
        bool newState = !AllRowsSelected;
        AllRowsSelected = newState;
        foreach (var r in Results)           r.IsSelected = newState;
        foreach (var r in IncompleteResults) r.IsSelected = newState;
    }

    // ── Extraction ───────────────────────────────────────────────────────

    public string ExtractionStatusText => _extractionStatusText;
    private string _extractionStatusText = string.Empty;

    private bool CanStartExtraction() =>
        HasSelectedFolder && Directory.Exists(SelectedFolder) && !IsExtracting
        && DetectedFiles.Any(f => f.IsSelected);

    /// <summary>
    /// Adds a diagnostic trace line to Debug output and updates the status message.
    /// </summary>
    private void LogPipeline(string step)
    {
        Debug.WriteLine("[Hotix] " + step);
    }

    private async Task StartExtractionAsync()
    {
        if (!CanStartExtraction()) return;

        LogPipeline("Extraction button clicked — starting pipeline");
        LogPipeline($"Selected engine: {SelectedEngine}");

        IsExtracting      = true;
        IsProgressVisible = true;
        ShowSummaryBanner = false;
        SaveConfirmationPath = null;
        ProcessedFiles    = 0;
        _extractionStatusText = TranslationSource.Get("ExtractionPreparing");
        OnPropertyChanged(nameof(ExtractionStatusText));

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Results.Clear();
            IncompleteResults.Clear();
        });
        NotifySummaryChanged();

        string[] files = DetectedFiles.Where(f => f.IsSelected).Select(f => f.FilePath).ToArray();
        TotalFiles = files.Length;
        LogPipeline($"Input file count: {files.Length}");

        if (files.Length == 0)
        {
            LogPipeline("ERROR: No files selected — aborting extraction");
            ShowImmediateError(TranslationSource.Get("ExtractionNoFiles"));
            return;
        }

        _extractionCts = new CancellationTokenSource();

        try
        {
            // ── Decide extraction strategy ──
            LogPipeline("Pre-processing: loading keys and checking internet");
            string? geminiKey = LoadGeminiApiKey();
            string? grokKey = LoadGrokApiKey();
            _internetAvailable = await CheckInternetAsync();
            bool canUseCloud = _internetAvailable;
            bool hasGemini = canUseCloud && !string.IsNullOrEmpty(geminiKey);
            bool hasGrok = canUseCloud && !string.IsNullOrEmpty(grokKey);

            LogPipeline($"Internet: {_internetAvailable}, Gemini key: {!string.IsNullOrEmpty(geminiKey)}, Grok key: {!string.IsNullOrEmpty(grokKey)}");

            string selectedEngine = SelectedEngine;
            bool preferGemini = hasGemini && (selectedEngine == "auto" || selectedEngine == "gemini");
            bool preferGrok = hasGrok && (selectedEngine == "grok" || (selectedEngine == "auto" && !hasGemini));
            bool needServerFallback = false;

            LogPipeline($"Engine dispatch: selected={selectedEngine}, preferGemini={preferGemini}, preferGrok={preferGrok}, fallback={needServerFallback}");

            // Pre-flight check: ensure at least one engine is available
            if (!preferGemini && !preferGrok && selectedEngine != "ocr")
            {
                LogPipeline("PRE-FLIGHT: No cloud engine available, checking OCR server");
                // Will fall through to OCR path in foreach
            }

            foreach (string file in files)
            {
                if (_extractionCts.Token.IsCancellationRequested)
                {
                    LogPipeline("Extraction cancelled by user");
                    break;
                }

                string fileName = Path.GetFileName(file);
                _extractionStatusText = TranslationSource.Fmt("ExtractionProcessing", fileName);
                OnPropertyChanged(nameof(ExtractionStatusText));
                LogPipeline($"Processing file [{ProcessedFiles + 1}/{TotalFiles}]: {fileName}");

                // Start elapsed-time indicator so the user sees progress during long calls
                _processingStopwatch.Restart();
                _processingTimer?.Stop();
                _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _processingTimer.Tick += (s, e) =>
                {
                    var elapsed = _processingStopwatch.Elapsed;
                    _extractionStatusText = TranslationSource.Fmt("ExtractionProcessingElapsed",
                        fileName, (int)elapsed.TotalMinutes, elapsed.Seconds);
                    OnPropertyChanged(nameof(ExtractionStatusText));
                };
                _processingTimer.Start();

                InvoiceRowViewModel row;

                try
                {

                if (selectedEngine == "grok" && hasGrok)
                {
                    // Grok-only mode
                    LogPipeline($"Engine dispatch: Grok-only for {fileName}");
                    try
                    {
                        LogPipeline("HTTP request start (Grok)");
                        InvoiceResult result = await CallGrokDirectlyAsync(file, grokKey!);
                        LogPipeline("HTTP response received — success");
                        row = InvoiceRowViewModel.FromSuccess(file, result);
                    }
                    catch (GeminiApiException ex)
                    {
                        LogPipeline($"HTTP response error: {ex.Message}");
                        row = InvoiceRowViewModel.FromError(file, ex.Message);
                    }
                    catch (Exception ex2)
                    {
                        LogPipeline($"Grok exception: {ex2.GetType().Name}: {ex2.Message}");
                        row = InvoiceRowViewModel.FromError(file, TranslationSource.Fmt("ErrorGemini", $"{ex2.GetType().Name}: {ex2.Message}"));
                    }
                }
                else if (preferGemini && !needServerFallback)
                {
                    // Try Gemini first
                    LogPipeline($"Engine dispatch: Gemini-first for {fileName}");
                    try
                    {
                        LogPipeline("HTTP request start (Gemini)");
                        InvoiceResult result = await CallGeminiDirectlyAsync(file, geminiKey!);
                        LogPipeline("HTTP response received — parsing succeeded");
                        row = InvoiceRowViewModel.FromSuccess(file, result);
                    }
                    catch (GeminiQuotaExceededException)
                    {
                        LogPipeline("Gemini quota exceeded");

                        // Try Grok first when in auto mode, just like other
                        // Gemini failures do — Grok doesn't share quota with
                        // Gemini so it may still succeed.
                        if (selectedEngine == "auto" && hasGrok)
                        {
                            LogPipeline("Gemini quota — trying Grok before OCR");
                            try
                            {
                                InvoiceResult grokResult = await CallGrokDirectlyAsync(file, grokKey!);
                                LogPipeline("Grok succeeded after Gemini quota");
                                row = InvoiceRowViewModel.FromSuccess(file, grokResult);
                            }
                            catch
                            {
                                LogPipeline("Grok also failed after Gemini quota — falling back to OCR");
                                ShowQuotaFallbackBanner();
                                needServerFallback = true;
                                row = await ExtractViaServerAsync(file);
                            }
                        }
                        else
                        {
                            ShowQuotaFallbackBanner();
                            needServerFallback = true;
                            row = await ExtractViaServerAsync(file);
                        }
                    }
                    catch (Exception) when (selectedEngine == "auto")
                    {
                        LogPipeline("Gemini failed in auto mode — trying Grok or OCR");
                        if (hasGrok)
                        {
                            try
                            {
                                LogPipeline("HTTP request start (Grok fallback)");
                                InvoiceResult result = await CallGrokDirectlyAsync(file, grokKey!);
                                LogPipeline("Grok fallback succeeded");
                                row = InvoiceRowViewModel.FromSuccess(file, result);
                            }
                            catch
                            {
                                LogPipeline("Grok fallback also failed — trying OCR server");
                                await EnsureServerReadyAsync();
                                row = await ExtractViaServerAsync(file);
                            }
                        }
                        else
                        {
                            LogPipeline("No Grok key — falling back to OCR server");
                            await EnsureServerReadyAsync();
                            row = await ExtractViaServerAsync(file);
                        }
                    }
                    catch (GeminiApiException ex)
                    {
                        LogPipeline($"Gemini API error: {ex.Message}");
                        row = InvoiceRowViewModel.FromError(file, ex.Message);
                    }
                    catch (Exception ex2)
                    {
                        LogPipeline($"Gemini exception: {ex2.GetType().Name}: {ex2.Message}");
                        row = InvoiceRowViewModel.FromError(file, TranslationSource.Fmt("ErrorGemini", $"{ex2.GetType().Name}: {ex2.Message}"));
                    }
                }
                else if (preferGrok && !needServerFallback)
                {
                    // Try Grok (auto mode, no Gemini key available)
                    LogPipeline($"Engine dispatch: Grok-first for {fileName}");
                    try
                    {
                        LogPipeline("HTTP request start (Grok)");
                        InvoiceResult result = await CallGrokDirectlyAsync(file, grokKey!);
                        LogPipeline("HTTP response received — success");
                        row = InvoiceRowViewModel.FromSuccess(file, result);
                    }
                    catch (Exception) when (selectedEngine == "auto")
                    {
                        LogPipeline("Grok failed in auto mode — falling back to OCR server");
                        await EnsureServerReadyAsync();
                        row = await ExtractViaServerAsync(file);
                    }
                    catch (GeminiApiException ex)
                    {
                        LogPipeline($"Grok API error: {ex.Message}");
                        row = InvoiceRowViewModel.FromError(file, ex.Message);
                    }
                    catch (Exception ex2)
                    {
                        LogPipeline($"Grok exception: {ex2.GetType().Name}: {ex2.Message}");
                        row = InvoiceRowViewModel.FromError(file, TranslationSource.Fmt("ErrorGemini", $"{ex2.GetType().Name}: {ex2.Message}"));
                    }
                }
                else
                {
                    // OCR server path
                    LogPipeline($"Engine dispatch: OCR server for {fileName}");
                    await EnsureServerReadyAsync();
                    row = await ExtractViaServerAsync(file);
                }

                    LogPipeline($"Row created: success={!row.HasError}, engine={row.EngineUsed}");
                }
                finally
                {
                    _processingTimer?.Stop();
                    _processingTimer = null;
                }

                InvoiceRowViewModel captured = row;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Results.Add(captured);
                    if (captured.IsIncomplete) IncompleteResults.Add(captured);
                });

                LogPipeline("UI update triggered — ObservableCollection updated");

                ProcessedFiles += 1;
                NotifySummaryChanged();
                RaiseCommandStateChanged();
            }
        }
        catch (OperationCanceledException)
        {
            LogPipeline("Extraction cancelled (OperationCanceledException)");
        }
        catch (Exception ex)
        {
            LogPipeline($"UNHANDLED EXCEPTION in extraction pipeline: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _extractionCts?.Dispose();
            _extractionCts = null;
            IsExtracting   = false;
            IsProgressVisible = false;
            _extractionStatusText = string.Empty;
            OnPropertyChanged(nameof(ExtractionStatusText));
            OnPropertyChanged(nameof(HasErrors));
            (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            ShowExtractionSummary();
            LogPipeline($"Extraction complete — {Results.Count} results, {IncompleteResults.Count} incomplete");
        }
    }

    /// <summary>
    /// Shows a warning banner mid-batch when quota fallback activates, and updates
    /// the per-file progress text so the user knows remaining files will use local OCR.
    /// Only fires once per batch to avoid banner thrashing.
    /// </summary>
    private void ShowQuotaFallbackBanner()
    {
        if (_quotaFallbackBannerShown) return;
        _quotaFallbackBannerShown = true;

        SummaryBannerText = TranslationSource.Get("QuotaFallbackBanner");
        SummaryBannerColor = "#E67E22"; // Orange — warning, not error
        ShowSummaryBanner = true;
        OnPropertyChanged(nameof(SummaryBannerText));
        OnPropertyChanged(nameof(SummaryBannerColor));
        OnPropertyChanged(nameof(ShowSummaryBanner));

        // Update the in-progress status text next to the progress ring
        _extractionStatusText = TranslationSource.Get("ExtractionQuotaFallback");
        OnPropertyChanged(nameof(ExtractionStatusText));
    }

    /// <summary>Shows an error banner immediately without waiting for extraction to complete.</summary>
    private void ShowImmediateError(string message)
    {
        SummaryBannerText = message;
        SummaryBannerColor = "#C0392B";
        ShowSummaryBanner = true;

        IsExtracting = false;
        IsProgressVisible = false;
        _extractionStatusText = string.Empty;
        OnPropertyChanged(nameof(ExtractionStatusText));
    }

    /// <summary>Extract a file through the local OCR server (starts it lazily if needed).</summary>
    private async Task<InvoiceRowViewModel> ExtractViaServerAsync(string file)
    {
        try
        {
            await EnsureServerReadyAsync();

            InvoiceResult result = await _invoiceClient.ExtractAsync(file, "ocr", _extractionCts?.Token ?? CancellationToken.None);
            return InvoiceRowViewModel.FromSuccess(file, result);
        }
        catch (OperationCanceledException)
        {
            // If this was a real user cancellation, propagate it up to the pipeline
            if (_extractionCts?.Token.IsCancellationRequested == true)
                throw;

            // Otherwise it was an HTTP timeout (TaskCanceledException inherits from
            // OperationCanceledException when the HttpClient's Timeout fires).
            return InvoiceRowViewModel.FromError(file,
                TranslationSource.Fmt("ErrorTimeout", (int)_apiHttpClient.Timeout.TotalSeconds));
        }
        catch (InvoiceExtractionException ex)
        {
            return InvoiceRowViewModel.FromError(file, MapErrorMessage(ex));
        }
        catch (Exception ex)
        {
            return InvoiceRowViewModel.FromError(file, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelExtraction()
    {
        _extractionCts?.Cancel();
    }

    private async Task<InvoiceRowViewModel> ExtractRowViewModelAsync(string filePath)
    {
        try
        {
            await EnsureServerReadyAsync();
            InvoiceResult result = await _invoiceClient.ExtractAsync(filePath, SelectedEngine);
            return InvoiceRowViewModel.FromSuccess(filePath, result);
        }
        catch (InvoiceExtractionException ex)
        {
            return InvoiceRowViewModel.FromError(filePath, MapErrorMessage(ex));
        }
        catch (Exception ex)
        {
            return InvoiceRowViewModel.FromError(filePath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateRowInCollections(InvoiceRowViewModel row, InvoiceRowViewModel updated)
    {
        int idx = Results.IndexOf(row);
        if (idx >= 0) Results[idx] = updated;

        int idxInc = IncompleteResults.IndexOf(row);
        if (idxInc >= 0)
        {
            if (updated.IsIncomplete) IncompleteResults[idxInc] = updated;
            else IncompleteResults.RemoveAt(idxInc);
        }
        else if (updated.IsIncomplete)
        {
            IncompleteResults.Add(updated);
        }

        if (SelectedRow == row) SelectedRow = updated;
    }

    private async Task RerunRowAsync(InvoiceRowViewModel? row)
    {
        if (row is null || IsExtracting) return;

        // Use the stored full FilePath (set once at row creation) instead of
        // reconstructing from SelectedFolder, which may have changed since extraction.
        string filePath = row.FilePath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        InvoiceRowViewModel updated = await ExtractRowViewModelAsync(filePath);

        await Application.Current.Dispatcher.InvokeAsync(() => UpdateRowInCollections(row, updated));

        NotifySummaryChanged();
        OnPropertyChanged(nameof(HasErrors));
        (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RerunAllErrorsAsync()
    {
        var errorRows = Results.Where(r => r.HasError).ToList();
        if (errorRows.Count == 0) return;

        // Show progress feedback so the user sees something happening
        IsExtracting = true;
        IsProgressVisible = true;
        ShowSummaryBanner = false;
        SaveConfirmationPath = null;
        ProcessedFiles = 0;
        TotalFiles = errorRows.Count;
        _extractionStatusText = TranslationSource.Get("RerunErrorsProgress");
        OnPropertyChanged(nameof(ExtractionStatusText));

        try
        {
            foreach (var row in errorRows)
            {
                string fileName = row.FileName;
                _extractionStatusText = TranslationSource.Fmt("ExtractionProcessing", fileName);
                OnPropertyChanged(nameof(ExtractionStatusText));

                await RerunRowAsync(row);

                ProcessedFiles += 1;
                NotifySummaryChanged();
            }
        }
        finally
        {
            IsExtracting = false;
            IsProgressVisible = false;
            _extractionStatusText = string.Empty;
            OnPropertyChanged(nameof(ExtractionStatusText));
            OnPropertyChanged(nameof(HasErrors));
            (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            ShowExtractionSummary();
        }
    }

    private static string MapErrorMessage(InvoiceExtractionException ex)
    {
        if (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            return TranslationSource.Get("ErrorOcrFormat");

        if (ex.StatusCode == System.Net.HttpStatusCode.InternalServerError)
        {
            if (ex.ResponseBody.Contains("poppler", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("pdfinfo", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("pdftoppm", StringComparison.OrdinalIgnoreCase))
                return TranslationSource.Get("ErrorOcrPoppler");

            if (ex.ResponseBody.Contains("OcrEngineError", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("PaddleOCR", StringComparison.OrdinalIgnoreCase))
                return TranslationSource.Get("ErrorOcrEngine");

            return TranslationSource.Get("ErrorOcrInternal");
        }

        return TranslationSource.Fmt("ErrorOcrHttp", (int)ex.StatusCode);
    }

    private void ShowExtractionSummary()
    {
        int errors     = Results.Count(r => r.HasError);
        int incomplete = IncompleteResults.Count(r => !r.HasError);
        int success    = Results.Count - errors;

        SummaryBannerText  = TranslationSource.Fmt("SummaryBannerComplete", success, incomplete, errors);
        SummaryBannerColor = ResolveSummaryColor(errors, incomplete);
        ShowSummaryBanner  = true;
    }

    private static string ResolveSummaryColor(int errors, int incomplete)
    {
        if (errors > 0)    return "#C0392B";
        if (incomplete > 0) return "#E67E22";
        return "#2ECC71";
    }

    private bool CanExport() => Results.Count > 0 && !IsExtracting;

    private void ExportExcel()
    {
        if (!CanExport()) return;

        bool anySelected = Results.Any(r => r.IsSelected);
        var rowsToExport = anySelected ? Results.Where(r => r.IsSelected).ToList() : Results.ToList();
        string defaultDir = Directory.Exists(SelectedFolder) ? SelectedFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // ── Show export mode dialog ──
        var modeDialog = new global::Hotix.InvoiceClient.ExportDialog();
        modeDialog.Owner = Application.Current.MainWindow;
        bool? modeResult = modeDialog.ShowDialog();

        if (modeResult != true) return;

        if (modeDialog.SelectedMode == global::Hotix.InvoiceClient.ExportDialog.ExportMode.CreateNew)
        {
            // ── Option A: Create new workbook ──
            var saveDialog = new SaveFileDialog
            {
                Filter           = TranslationSource.Get("ExportExcelFilter"),
                FileName         = TranslationSource.Fmt("ExportFileName", DateTime.Today.ToString("yyyy-MM-dd")),
                InitialDirectory = defaultDir,
                Title            = TranslationSource.Get("ExportDialogTitle"),
            };

            if (!saveDialog.ShowDialog().GetValueOrDefault()) return;

            try
            {
                new ExcelWriter().Write(saveDialog.FileName, rowsToExport);
                SaveConfirmationPath = saveDialog.FileName;
            }
            catch (IOException ex)
            {
                var msg = TranslationSource.Fmt("ExportErrorFileOpen", ex.Message);
                MessageBox.Show(msg, TranslationSource.Get("ExportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            // ── Option B: Append to existing workbook ──
            var openDialog = new OpenFileDialog
            {
                Filter           = "Excel Workbook (*.xlsx)|*.xlsx",
                InitialDirectory = defaultDir,
                Title            = TranslationSource.Get("ExportAppendTitle"),
            };

            if (!openDialog.ShowDialog().GetValueOrDefault()) return;

            string existingPath = openDialog.FileName;

            // Check if the file has multiple worksheets
            List<string> sheetNames;
            try
            {
                sheetNames = ExcelWriter.GetWorksheetNames(existingPath);
            }
            catch (IOException ex)
            {
                var msg = TranslationSource.Fmt("ExportErrorFileOpen", ex.Message);
                MessageBox.Show(msg, TranslationSource.Get("ExportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? targetSheet = null;

            // If we have a remembered sheet from a previous append this session,
            // check if it still exists in the selected file
            if (_lastExportSheetName != null && sheetNames.Any(s => string.Equals(s, _lastExportSheetName, StringComparison.OrdinalIgnoreCase)))
            {
                targetSheet = _lastExportSheetName;
            }
            else if (sheetNames.Count > 1)
            {
                // Let the user choose which sheet to append to
                targetSheet = PromptForWorksheet(sheetNames);
                if (targetSheet == null) return; // User cancelled
            }
            else if (sheetNames.Count == 1)
            {
                targetSheet = sheetNames[0];
            }
            else
            {
                // No sheets — shouldn't happen with a valid .xlsx, but handle gracefully
                targetSheet = "Résultats";
            }

            _lastExportSheetName = targetSheet;

            try
            {
                new ExcelWriter().AppendToExisting(existingPath, rowsToExport, targetSheet);
                SaveConfirmationPath = existingPath;
            }
            catch (IOException ex)
            {
                var msg = TranslationSource.Fmt("ExportErrorFileOpen", ex.Message);
                MessageBox.Show(msg, TranslationSource.Get("ExportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }

    /// <summary>
    /// Shows a simple dialog listing worksheet names and returns the chosen name, or null if cancelled.
    /// </summary>
    private static string? PromptForWorksheet(List<string> sheetNames)
    {
        var dialog = new System.Windows.Window
        {
            Title = TranslationSource.Get("ExportSheetPickerTitle"),
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
            WindowStyle = System.Windows.WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = Application.Current.MainWindow,
            MinWidth = 340,
        };

        // Wrap in overlay + dialog style (matching the app's design)
        var overlay = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushOverlay"),
        };

        string? result = null;

        var listBox = new System.Windows.Controls.ListBox
        {
            FontSize = 14,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextPrimary"),
            BorderThickness = new System.Windows.Thickness(0),
        };

        foreach (var name in sheetNames)
        {
            listBox.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = name,
                Height = 36,
                Padding = new System.Windows.Thickness(16, 0, 16, 0),
            });
        }

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = TranslationSource.Get("ControlCancelBtn"),
            Style = (System.Windows.Style)Application.Current.FindResource("ButtonSecondaryStyle"),
            MinWidth = 80,
        };
        cancelBtn.Click += (_, _) => { dialog.Close(); };  // result stays null → cancellation

        var continueBtn = new System.Windows.Controls.Button
        {
            Content = TranslationSource.Get("ExportContinue"),
            Style = (System.Windows.Style)Application.Current.FindResource("ButtonPrimaryStyle"),
            MinWidth = 100,
            IsEnabled = false,
        };

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 16, 0, 0),
        };
        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(new System.Windows.Controls.TextBlock { Width = 12 }); // spacer
        buttonPanel.Children.Add(continueBtn);

        listBox.SelectionChanged += (_, _) =>
        {
            if (listBox.SelectedItem is System.Windows.Controls.ListBoxItem selected)
            {
                continueBtn.IsEnabled = true;
                result = (string)selected.Content;
            }
        };

        continueBtn.Click += (_, _) => { dialog.Close(); };

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = TranslationSource.Get("ExportSheetPickerLabel"),
            FontSize = 14,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextPrimary"),
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(listBox);
        panel.Children.Add(buttonPanel);

        var innerBorder = new System.Windows.Controls.Border
        {
            Style = (System.Windows.Style)Application.Current.FindResource("DialogStyle"),
            Width = 360,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(32),
            Child = new System.Windows.Controls.Border
            {
                Margin = new System.Windows.Thickness(28),
                Child = panel,
            },
        };

        overlay.Child = innerBorder;
        dialog.Content = overlay;

        _ = dialog.ShowDialog();
        return result;
    }

    private void OpenSavedFolder()
    {
        if (_saveConfirmationPath is null) return;
        string? dir = Path.GetDirectoryName(_saveConfirmationPath);
        if (dir != null) Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void OpenSavedFile()
    {
        if (_saveConfirmationPath is null) return;
        if (File.Exists(_saveConfirmationPath))
            Process.Start(new ProcessStartInfo(_saveConfirmationPath) { UseShellExecute = true });
    }

    private bool CanClear() => Results.Count > 0 && !IsExtracting;

    private void ClearResults()
    {
        if (!CanClear()) return;
        Results.Clear();
        IncompleteResults.Clear();
        SelectedRow          = null;
        ProcessedFiles       = 0;
        TotalFiles           = 0;
        IsProgressVisible    = false;
        ShowSummaryBanner    = false;
        SaveConfirmationPath = null;
        NotifySummaryChanged();
        OnPropertyChanged(nameof(HasErrors));
        RaiseCommandStateChanged();
    }

    public void SetFolderFromDrop(string folder)
    {
        SelectedFolder = folder;
        LoadDetectedFiles();
    }

    // ── Settings persistence ──────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));

            // Restore language preference
            if (doc.RootElement.TryGetProperty("language", out var langEl))
            {
                string? lang = langEl.GetString();
                if (lang == "en" || lang == "fr")
                    TranslationSource.Instance.CurrentCulture = lang;
            }

            // Restore engine selection
            if (doc.RootElement.TryGetProperty("engine", out var engineEl))
            {
                string? engine = engineEl.GetString();
                if (engine == "auto" || engine == "gemini" || engine == "grok" || engine == "ocr")
                    _selectedEngine = engine;
            }
        }
        catch { /* settings are best-effort */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
            {
                language = TranslationSource.Instance.CurrentCulture,
                engine = SelectedEngine,
            }));
        }
        catch
        {
            // Intentionally ignored: saving settings is best-effort.
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void NotifySummaryChanged() => OnPropertyChanged(nameof(SummaryText));

    private void RaiseCommandStateChanged()
    {
        (StartExtractionCommand  as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelExtractionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportExcelCommand      as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand            as RelayCommand)?.RaiseCanExecuteChanged();
        (RerunAllErrorsCommand   as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _engineStatusTimer?.Stop();
        _extractionCts?.Cancel();
        _extractionCts?.Dispose();
        _apiHttpClient.Dispose();
    }
}

// ── Custom Exceptions ─────────────────────────────────────────────────────

internal sealed class GeminiQuotaExceededException : Exception
{
    public GeminiQuotaExceededException(string message) : base(message) { }
}

internal sealed class GeminiApiException : Exception
{
    public GeminiApiException(string message) : base(message) { }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)    => _execute(parameter);
    public void RaiseCanExecuteChanged()      => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
