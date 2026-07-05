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
    private bool _isSettingsPanelOpen;
    private DispatcherTimer? _engineStatusTimer;
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
    private bool _showSummaryBanner;
    private string _summaryBannerText = string.Empty;
    private string _summaryBannerColor = "#2ECC71";
    private string? _saveConfirmationPath;

    // Gemini REST API endpoint
    private const string GeminiApiBase = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent";

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
        RetryServerCommand    = new RelayCommand(async _ => await RetryServerAsync(), _ => !IsServerStarting && !IsExtracting);
        ToggleSettingsCommand  = new RelayCommand(_ =>
        {
            var wizard = new global::Hotix.InvoiceClient.GeminiSetupWindow { DataContext = this };
            wizard.Owner = Application.Current.MainWindow;
            wizard.ShowDialog();
        });
        SaveGeminiKeyCommand   = new RelayCommand(async _ => await SaveGeminiKeyAsync());
        ClearGeminiKeyCommand  = new RelayCommand(async _ => await ClearGeminiKeyAsync());

        LoadSettings();
        LoadGeminiKeyFromAppSettings();

        // Poll engine + internet status every 45 seconds on the UI thread
        _engineStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(45),
        };
        _engineStatusTimer.Tick += async (s, e) => await CheckEngineStatusAsync();
        _engineStatusTimer.Start();

        // Initial connectivity check
        _ = CheckEngineStatusAsync();
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
    public ICommand RetryServerCommand     { get; }
    public ICommand ToggleSettingsCommand   { get; }
    public ICommand SaveGeminiKeyCommand    { get; }
    public ICommand ClearGeminiKeyCommand   { get; }

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
        set => SetField(ref _selectedEngine, value);
    }

    public bool GeminiAvailable
    {
        get => _geminiAvailable;
        private set => SetField(ref _geminiAvailable, value);
    }

    public string GeminiKeyInput
    {
        get => _geminiKeyInput;
        set => SetField(ref _geminiKeyInput, value);
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

        // Fallback: check Gemini directly
        string? apiKey = LoadGeminiApiKey();
        if (!string.IsNullOrEmpty(apiKey) && _internetAvailable)
        {
            try
            {
                using var testClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var testBody = new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } };
                var testResponse = await testClient.PostAsJsonAsync(
                    $"{GeminiApiBase}?key={apiKey}", testBody);
                GeminiAvailable = testResponse.IsSuccessStatusCode;
            }
            catch
            {
                GeminiAvailable = false;
            }
        }
        else
        {
            GeminiAvailable = false;
        }
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
                400 => (false, TranslationSource.Get("GeminiError400")),
                401 => (false, TranslationSource.Get("GeminiError401")),
                403 => (false, TranslationSource.Get("GeminiError403")),
                429 => (false, TranslationSource.Get("GeminiError429")),
                _   => (false, TranslationSource.Fmt("GeminiErrorPrefix", (int)response.StatusCode, responseBody)),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, TranslationSource.Get("GeminiTimeout"));
        }
        catch (HttpRequestException ex)
        {
            return (false, TranslationSource.Fmt("GeminiNetworkError", ex.Message));
        }
        catch (Exception ex)
        {
            return (false, TranslationSource.Fmt("GeminiUnexpectedError", ex.Message));
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
                throw new GeminiQuotaExceededException(TranslationSource.Get("GeminiQuotaExceeded"));
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
    private async Task EnsureServerReadyAsync()
    {
        if (_isServerStarted && _isServerRunning)
            return;

        if (_isServerStarting)
        {
            // Already starting — wait for completion by polling health
            var waitCts = new CancellationTokenSource();
            try
            {
                while (!waitCts.IsCancellationRequested)
                {
                    await Task.Delay(500);
                    if (_isServerStarted && _isServerRunning)
                        return;
                }
            }
            finally
            {
                waitCts.Cancel();
            }
            return;
        }            IsServerStarting = true;
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

            bool success = await App.StartServerAsync(progress);

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
            IsServerStarting = false;
            ServerStartingStatus = string.Empty;
            (RetryServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            throw new InvalidOperationException(TranslationSource.Fmt("ServerStartFailPrefix", ex.Message));
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
            var settings = new { gemini_api_key = "", default_engine = SelectedEngine };
            await File.WriteAllTextAsync(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            await CheckEngineStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorClearKey", ex.Message), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveGeminiKeyAsync()
    {
        try
        {
            // Save to appsettings.json
            string appSettingsPath = ResolveAppSettingsPath();
            var settings = new { gemini_api_key = GeminiKeyInput, default_engine = SelectedEngine };
            await File.WriteAllTextAsync(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            await CheckEngineStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorSaveKey", ex.Message), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadGeminiKeyFromAppSettings()
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
                if (doc.RootElement.TryGetProperty("default_engine", out var engineEl))
                    SelectedEngine = engineEl.GetString() ?? "auto";
            }
        }
        catch
        {
            // Intentionally ignored: loading settings is best-effort.
        }
    }

    public static string ResolveAppSettingsPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] serverDirectories =
        {
            Path.Combine(appDir, "server"),
            Path.Combine(appDir, "..", "server"),
            @"C:\hotix-invoice\server",
        };

        string serverDirectory = serverDirectories.FirstOrDefault(Directory.Exists)
            ?? @"C:\hotix-invoice\server";

        return Path.Combine(serverDirectory, "appsettings.json");
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
        SaveSettings();
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

        SaveSettings();
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

    private bool CanStartExtraction() =>
        HasSelectedFolder && Directory.Exists(SelectedFolder) && !IsExtracting
        && DetectedFiles.Any(f => f.IsSelected);

    private async Task StartExtractionAsync()
    {
        if (!CanStartExtraction()) return;

        IsExtracting      = true;
        IsProgressVisible = true;
        ShowSummaryBanner = false;
        SaveConfirmationPath = null;
        ProcessedFiles    = 0;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Results.Clear();
            IncompleteResults.Clear();
        });
        NotifySummaryChanged();

        string[] files = DetectedFiles.Where(f => f.IsSelected).Select(f => f.FilePath).ToArray();
        TotalFiles = files.Length;
        _extractionCts = new CancellationTokenSource();

        try
        {
            // ── Decide extraction strategy ──
            string? apiKey = LoadGeminiApiKey();
            _internetAvailable = await CheckInternetAsync();
            bool useGeminiDirect = _internetAvailable && !string.IsNullOrEmpty(apiKey)
                && (SelectedEngine == "auto" || SelectedEngine == "gemini");
            bool needServerFallback = false;

            foreach (string file in files)
            {
                if (_extractionCts.Token.IsCancellationRequested) break;

                InvoiceRowViewModel row;

                if (useGeminiDirect && !needServerFallback)
                {
                    // Try Gemini directly from the client (no server needed)
                    try
                    {
                        InvoiceResult result = await CallGeminiDirectlyAsync(file, apiKey!);
                        row = InvoiceRowViewModel.FromSuccess(file, result);
                    }
                    catch (GeminiQuotaExceededException)
                    {
                        // Quota exceeded — switch to OCR server for all remaining files
                        needServerFallback = true;

                        // Process this file via server OCR instead
                        row = await ExtractViaServerAsync(file);
                    }
                    catch (Exception) when (SelectedEngine == "auto")
                    {
                        // Gemini failed in auto mode — fall back to OCR for this file
                        await EnsureServerReadyAsync();
                        row = await ExtractViaServerAsync(file);
                    }
                    catch (Exception ex2)
                    {
                        // Gemini failed in gemini-only mode — show error
                        row = InvoiceRowViewModel.FromError(file, TranslationSource.Fmt("ErrorGemini", ex2.Message));
                    }
                }
                else
                {
                    // OCR server path
                    row = await ExtractViaServerAsync(file);
                }

                InvoiceRowViewModel captured = row;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Results.Add(captured);
                    if (captured.IsIncomplete) IncompleteResults.Add(captured);
                });

                ProcessedFiles += 1;
                NotifySummaryChanged();
                RaiseCommandStateChanged();
            }
        }
        finally
        {
            _extractionCts?.Dispose();
            _extractionCts = null;
            IsExtracting   = false;
            IsProgressVisible = false;
            OnPropertyChanged(nameof(HasErrors));
            (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            ShowExtractionSummary();
        }
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
            throw;
        }
        catch (InvoiceExtractionException ex)
        {
            return InvoiceRowViewModel.FromError(file, MapErrorMessage(ex));
        }
        catch (Exception ex)
        {
            return InvoiceRowViewModel.FromError(file, ex.Message);
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
            return InvoiceRowViewModel.FromError(filePath, ex.Message);
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

        string filePath = Path.Combine(SelectedFolder, row.FileName);
        if (!File.Exists(filePath)) return;

        InvoiceRowViewModel updated = await ExtractRowViewModelAsync(filePath);

        await Application.Current.Dispatcher.InvokeAsync(() => UpdateRowInCollections(row, updated));

        NotifySummaryChanged();
        OnPropertyChanged(nameof(HasErrors));
        (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RerunAllErrorsAsync()
    {
        var errorRows = Results.Where(r => r.HasError).ToList();
        foreach (var row in errorRows)
            await RerunRowAsync(row);
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

        string defaultDir  = Directory.Exists(SelectedFolder) ? SelectedFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var saveDialog = new SaveFileDialog
        {
            Filter           = TranslationSource.Get("ExportExcelFilter"),
            FileName         = TranslationSource.Fmt("ExportFileName", DateTime.Today.ToString("yyyy-MM-dd")),
            InitialDirectory = defaultDir,
            Title            = TranslationSource.Get("ExportDialogTitle"),
        };

        if (!saveDialog.ShowDialog().GetValueOrDefault()) return;

        bool anySelected = Results.Any(r => r.IsSelected);
        var rowsToExport = anySelected ? Results.Where(r => r.IsSelected).ToList() : Results.ToList();

        new ExcelWriter().Write(saveDialog.FileName, rowsToExport);
        SaveConfirmationPath = saveDialog.FileName;
    }

    private void OpenSavedFolder()
    {
        if (_saveConfirmationPath is null) return;
        string? dir = Path.GetDirectoryName(_saveConfirmationPath);
        if (dir != null) Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
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
        SaveSettings();
        LoadDetectedFiles();
    }

    // ── Settings persistence ──────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("lastFolder", out var el))
            {
                string? folder = el.GetString();
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    _selectedFolder = folder;
                    OnPropertyChanged(nameof(SelectedFolder));
                    OnPropertyChanged(nameof(HasSelectedFolder));
                    LoadDetectedFiles();
                }
            }

            // Restore language preference
            if (doc.RootElement.TryGetProperty("language", out var langEl))
            {
                string? lang = langEl.GetString();
                if (lang == "en" || lang == "fr")
                    TranslationSource.Instance.CurrentCulture = lang;
            }
        }
        catch { /* settings are best-effort */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { lastFolder = SelectedFolder }));
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
