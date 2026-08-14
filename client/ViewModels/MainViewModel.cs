using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Sentry;
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

    // ── Shared HttpClient instances (reuse across calls instead of per-call new) ──
    private static readonly HttpClient _httpQuickClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly HttpClient _httpShortClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly HttpClient _httpCloudClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Cloud providers may reject a burst of concurrent vision requests even when
    // the API key is valid. Serialize requests per provider so batch concurrency
    // does not turn a normal multi-file import into a spurious per-file OCR fallback.
    private static readonly SemaphoreSlim _geminiRequestGate = new(1, 1);
    private static readonly SemaphoreSlim _grokRequestGate = new(1, 1);

    private string _selectedEngine = "auto";
    private bool _geminiAvailable;
    private string _geminiKeyInput = string.Empty;
    private bool _grokAvailable;
    private string _grokKeyInput = string.Empty;
    private string _geminiModel = string.Empty;
    private string _grokModel = string.Empty;
    private bool _isSettingsPanelOpen;
    private DispatcherTimer? _engineStatusTimer;
    private readonly Stopwatch _processingStopwatch = new();
    private bool _isServerRunning = true;
    private bool _isServerStarted;
    private bool _isServerStarting;
    private string _serverStartingStatus = string.Empty;
    private bool _internetAvailable;
    private InvoiceRowViewModel? _selectedRow;
    private bool _clearingSelection;
    private CancellationTokenSource? _extractionCts;
    private ImageSource? _previewImageSource;
    private string _previewStatusMessage = string.Empty;
    private double _previewZoomLevel = 1.0;
    private bool _previewShowRawText;
    private bool _isPreviewLoading;
    private CancellationTokenSource? _previewLoadCts;
    private string? _lastPreviewFilePath;
    // Image cache: filePath → frozen BitmapImage (cleared when too large)
    private readonly Dictionary<string, BitmapImage> _previewImageCache = new();
    private const int MaxPreviewCacheEntries = 50;
    // Target width (px) the preview image is fit to on first load so a large
    // scan is immediately visible instead of showing a blank corner.
    private const double PreviewFitTargetWidth = 720.0;
    private double _previewNaturalWidth;
    private double _previewNaturalHeight;
    private string _directionFilter = "all";
    private bool _confidenceFilterLowOnly;
    private ListCollectionView? _resultsView;
    private ListCollectionView? _incompleteView;

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
    private const int DefaultBatchConcurrency = 4;
    private int _batchConcurrency = DefaultBatchConcurrency;
    private readonly object _batchLock = new();
    private bool _geminiDisabled;
    private bool _grokDisabled;
    private string _geminiDisabledReason = string.Empty;
    private string _grokDisabledReason = string.Empty;
    // Session-scope memory for the quota dialog: when the user picks "continue
    // with OCR" and asks to remember it, we don't re-prompt on later batches.
    private bool _geminiOcrChosenForSession;
    private bool _grokOcrChosenForSession;
// Gemini REST API endpoint
    private const string GeminiApiBaseTemplate = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";
    private const string GeminiDefaultModel = "gemini-2.5-flash";

    // Grok (xAI) REST API endpoint
    private const string GrokApiBase = "https://api.x.ai/v1/chat/completions";
    private const string GrokDefaultModel = "grok-4.3";

    // ── Gemini structured output schema (matches InvoiceItem + tax_summary) ──
    // Uses the v1beta OpenAPI 3.0 Schema subset: uppercase types, camelCase field names.
    // Confirmed supported on gemini-2.5-flash and later 1.5+ models.
    private static readonly object GeminiResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            numero_facture = new { type = "STRING", nullable = true },
            date = new { type = "STRING", nullable = true },
            fournisseur = new { type = "STRING", nullable = true },
            client = new { type = "STRING", nullable = true },
            montant_ht = new { type = "NUMBER", nullable = true },
            montant_tva = new { type = "NUMBER", nullable = true },
            montant_taxe = new { type = "NUMBER", nullable = true },
            montant_ttc = new { type = "NUMBER", nullable = true },
            items = new
            {
                type = "ARRAY",
                nullable = true,
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        designation = new { type = "STRING", nullable = true },
                        quantite = new { type = "NUMBER", nullable = true },
                        unit = new { type = "STRING", nullable = true },
                        prix_unitaire = new { type = "NUMBER", nullable = true },
                        tva_rate = new { type = "NUMBER", nullable = true },
                        montant = new { type = "NUMBER", nullable = true }
                    }
                }
            },
            tax_summary = new
            {
                type = "ARRAY",
                nullable = true,
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        rate = new { type = "NUMBER", nullable = true },
                        base_ht = new { type = "NUMBER", nullable = true },
                        tax_amount = new { type = "NUMBER", nullable = true }
                    }
                }
            }
        }
    };

    public MainViewModel()
    {
        string apiBaseUrl = Environment.GetEnvironmentVariable("HOTIX_API_BASE_URL")
            ?? $"http://{IPAddress.Loopback}:8000";

        _apiHttpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromMinutes(15),
        };

        _invoiceClient = new InvoiceClient(_apiHttpClient);

        DetectedFiles     = new ObservableCollection<FileItemViewModel>();
        Results           = new ObservableCollection<InvoiceRowViewModel>();
        IncompleteResults = new ObservableCollection<InvoiceRowViewModel>();

        DetectedFiles.CollectionChanged += (_, _) => NotifyFileCountChanged();
        Results.CollectionChanged += (_, _) => RefreshFilteredViews();
        IncompleteResults.CollectionChanged += (_, _) => RefreshFilteredViews();
        
        _resultsView = new ListCollectionView(Results) { Filter = FilterByDirection };
        _incompleteView = new ListCollectionView(IncompleteResults) { Filter = FilterByDirection };

        BrowseFolderCommand    = new RelayCommand(_ => BrowseFolder());
        BrowseFilesCommand     = new RelayCommand(_ => BrowseFiles());
        RemoveFileCommand      = new RelayCommand(p => RemoveFile(p as FileItemViewModel));
        StartExtractionCommand = new RelayCommand(async _ => await StartExtractionAsync(), _ => CanStartExtraction());
        CancelExtractionCommand = new RelayCommand(_ => CancelExtraction(), _ => IsExtracting);
        ExportExcelCommand     = new RelayCommand(_ => ExportExcel(), _ => CanExport());
        ClearCommand           = new RelayCommand(_ => ClearResults(), _ => CanClear());
        RerunCommand           = new RelayCommand(async p => await RerunRowAsync(p as InvoiceRowViewModel), _ => !IsExtracting);
        RerunAllErrorsCommand  = new RelayCommand(async _ => await RerunAllErrorsAsync(), _ => Results.Any(r => r.HasError) && !IsExtracting);
        ToggleAllFilesCommand  = new RelayCommand(_ => ToggleAllFiles());
        ToggleAllRowsCommand   = new RelayCommand(_ => ToggleAllRows());
        ClearSelectedRowCommand = new RelayCommand(_ => { _clearingSelection = true; SelectedRow = null; });
        CycleRowDirectionCommand = new RelayCommand(p =>
        {
            if (p is InvoiceRowViewModel row)
            {
                row.CycleDirection();
                // Re-evaluate filter in case row no longer matches.
                // SafeRefreshView commits any in-flight DataGrid edit first,
                // so Refresh() cannot throw (Sentry DOTNET-J).
                RefreshFilteredViews();
            }
        });
        SetDirectionFilterCommand = new RelayCommand(p =>
        {
            if (p is string filter)
                DirectionFilter = filter;
        });
        SetConfidenceFilterCommand = new RelayCommand(p =>
        {
            // Binary toggle: "low" (or "true") selects the "To check" bucket,
            // anything else shows all invoices. Old 3-value enum simplified to
            // a single low-confidence switch — no per-bucket logic remains.
            if (p is string filter)
                ConfidenceFilterLowOnly = string.Equals(filter, "low", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(filter, "true", StringComparison.OrdinalIgnoreCase);
            else if (p is bool b)
                ConfidenceFilterLowOnly = b;
        });
        ToggleItemsExpandedCommand = new RelayCommand(p =>
        {
            if (p is InvoiceRowViewModel row)
                row.ToggleItemsExpanded();
        });
        OpenSavedFolderCommand = new RelayCommand(_ => OpenSavedFolder(), _ => _saveConfirmationPath != null);
        OpenSavedFileCommand = new RelayCommand(_ => OpenSavedFile(), _ => _saveConfirmationPath != null);
        RetryServerCommand    = new RelayCommand(async _ => await RetryServerAsync(), _ => !IsServerStarting && !IsExtracting);
        ToggleSettingsCommand  = new RelayCommand(_ => OpenSettingsForProvider(null));
        SaveGeminiKeyCommand   = new RelayCommand(async _ => await SaveGeminiKeyAsync());
        ClearGeminiKeyCommand  = new RelayCommand(async _ => await ClearGeminiKeyAsync(), _ => HasGeminiKey);
        SaveGrokKeyCommand     = new RelayCommand(async _ => await SaveGrokKeyAsync());
        ClearGrokKeyCommand    = new RelayCommand(async _ => await ClearGrokKeyAsync(), _ => HasGrokKey);
        ClearActiveKeyCommand  = new RelayCommand(async _ => await ClearActiveKeyAsync(), _ => HasActiveKey);
        PreviewZoomInCommand = new RelayCommand(_ => PreviewZoomLevel *= 1.25);
        PreviewZoomOutCommand = new RelayCommand(_ => PreviewZoomLevel /= 1.25);
        PreviewFitWidthCommand = new RelayCommand(_ => FitPreviewToWidth());
        PreviewFitPageCommand = new RelayCommand(_ => FitPreviewToPage());
        ShowPreviewImageCommand = new RelayCommand(p => ShowPreviewImage(p as InvoiceRowViewModel));

        LoadSettings();
        LoadProviderKeysFromAppSettings();
        // Ensure in-memory key fields are populated from disk BEFORE any UI
        // binding or engine-status check reads them. LoadProviderKeysFromAppSettings
        // reads from appsettings.json, and LoadGeminiApiKey/LoadGrokApiKey serve
        // as a second-chance fallback that also caches into the same fields.
        LoadGeminiApiKey();
        LoadGrokApiKey();
        LoadBatchConcurrencyFromAppSettings();

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
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(GeminiKeyStatusText));
                OnPropertyChanged(nameof(GrokKeyStatusText));
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileItemViewModel>   DetectedFiles     { get; }
    public ObservableCollection<InvoiceRowViewModel> Results           { get; }
    public ObservableCollection<InvoiceRowViewModel> IncompleteResults { get; }

    // Canonical paths already added as result rows. This is the authoritative
    // de-duplication guard: a HashSet.Add is atomic, so two rows racing for the
    // same file can never both be inserted (the check-then-add on Results alone
    // would still be safe on the UI thread, but this removes any doubt).
    private readonly HashSet<string> _addedResultPaths = new(StringComparer.OrdinalIgnoreCase);

    public ICommand BrowseFolderCommand     { get; }
    public ICommand BrowseFilesCommand      { get; }
    public ICommand RemoveFileCommand       { get; }
    public ICommand StartExtractionCommand  { get; }
    public ICommand CancelExtractionCommand { get; }
    public ICommand ExportExcelCommand      { get; }
    public ICommand ClearCommand            { get; }
    public ICommand RerunCommand            { get; }
    public ICommand RerunAllErrorsCommand   { get; }
    public ICommand ToggleAllFilesCommand   { get; }
    public ICommand ToggleAllRowsCommand    { get; }
    public ICommand ClearSelectedRowCommand { get; }
    public ICommand CycleRowDirectionCommand { get; }
    public ICommand SetDirectionFilterCommand { get; }
    public ICommand SetConfidenceFilterCommand { get; }
    public ICommand ToggleItemsExpandedCommand { get; }
    public ICommand OpenSavedFolderCommand  { get; }
    public ICommand OpenSavedFileCommand    { get; }
    public ICommand RetryServerCommand     { get; }
    public ICommand ToggleSettingsCommand   { get; }
    public ICommand SaveGeminiKeyCommand    { get; }
    public ICommand ClearGeminiKeyCommand   { get; }
    public ICommand SaveGrokKeyCommand      { get; }
    public ICommand ClearGrokKeyCommand     { get; }
    public ICommand ClearActiveKeyCommand   { get; }
    public ICommand PreviewZoomInCommand     { get; }
    public ICommand PreviewZoomOutCommand    { get; }
    public ICommand PreviewFitWidthCommand   { get; }
    public ICommand PreviewFitPageCommand    { get; }
    public ICommand ShowPreviewImageCommand  { get; }

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

    public bool HasGeminiKey => !string.IsNullOrEmpty(_geminiKeyInput);

    public bool HasGrokKey => !string.IsNullOrEmpty(_grokKeyInput);

    /// <summary>Badge text on the main screen showing the Gemini key configuration status.</summary>
    public string GeminiKeyStatusText => TranslationSource.Fmt("ApiKeyStatusGemini",
        HasGeminiKey ? TranslationSource.Get("ApiKeyConfigured") : TranslationSource.Get("ApiKeyNotConfigured"));

    /// <summary>Badge text on the main screen showing the Grok key configuration status.</summary>
    public string GrokKeyStatusText => TranslationSource.Fmt("ApiKeyStatusGrok",
        HasGrokKey ? TranslationSource.Get("ApiKeyConfigured") : TranslationSource.Get("ApiKeyNotConfigured"));

    /// <summary>Tracks which provider tab is active in the Gemini/Grok setup window.</summary>
    private string _activeKeyProvider = "gemini";

    /// <summary>Set by GeminiSetupWindow when the user switches between Gemini/Grok tabs.</summary>
    public string ActiveKeyProvider
    {
        get => _activeKeyProvider;
        set
        {
            if (SetField(ref _activeKeyProvider, value))
            {
                OnPropertyChanged(nameof(HasActiveKey));
                (ClearActiveKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the currently-active provider has a stored key.</summary>
    public bool HasActiveKey => _activeKeyProvider == "gemini"
        ? HasGeminiKey
        : HasGrokKey;

    public string GeminiKeyInput
    {
        get => _geminiKeyInput;
        set
        {
            if (SetField(ref _geminiKeyInput, value))
            {
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
                OnPropertyChanged(nameof(HasGeminiKey));
                OnPropertyChanged(nameof(HasActiveKey));
                OnPropertyChanged(nameof(GeminiKeyStatusText));
                if (!string.IsNullOrEmpty(value))
                    _geminiOcrChosenForSession = false; // new key → re-arm the quota dialog
                (ClearGeminiKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearActiveKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
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
            {
                OnPropertyChanged(nameof(ResolvedEngineDisplay));
                OnPropertyChanged(nameof(HasGrokKey));
                OnPropertyChanged(nameof(HasActiveKey));
                OnPropertyChanged(nameof(GrokKeyStatusText));
                if (!string.IsNullOrEmpty(value))
                    _grokOcrChosenForSession = false; // new key → re-arm the quota dialog
                (ClearGrokKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearActiveKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Selected Gemini model (empty = use default).</summary>
    public string GeminiModel
    {
        get => _geminiModel;
        set => SetField(ref _geminiModel, value);
    }

    /// <summary>Selected Grok model (empty = use default).</summary>
    public string GrokModel
    {
        get => _grokModel;
        set => SetField(ref _grokModel, value);
    }

    /// <summary>Resolves the Gemini model to use for API calls.</summary>
    private string ResolvedGeminiModel => !string.IsNullOrEmpty(_geminiModel) ? _geminiModel : GeminiDefaultModel;

    /// <summary>Resolves the Grok model to use for API calls.</summary>
    private string ResolvedGrokModel => !string.IsNullOrEmpty(_grokModel) ? _grokModel : GrokDefaultModel;

    private string GeminiApiBase => string.Format(GeminiApiBaseTemplate, ResolvedGeminiModel);
    private void LoadBatchConcurrencyFromAppSettings()
    {
        int concurrency = DefaultBatchConcurrency;

        string? envValue = Environment.GetEnvironmentVariable("HOTIX_BATCH_CONCURRENCY");
        if (int.TryParse(envValue, out int parsedEnv) && parsedEnv > 0)
            concurrency = parsedEnv;

        try
        {
            string path = ResolveAppSettingsPath();
            if (File.Exists(path))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("batch_concurrency", out var concurrencyEl)
                    && concurrencyEl.TryGetInt32(out int parsedFile)
                    && parsedFile > 0)
                {
                    concurrency = parsedFile;
                }
            }
        }
        catch
        {
            // Best-effort settings read.
        }

        _batchConcurrency = Math.Clamp(concurrency, 1, 16);
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
        set => SetField(ref _showSummaryBanner, value);
    }

    public string SummaryBannerText
    {
        get => _summaryBannerText;
        set => SetField(ref _summaryBannerText, value);
    }

    public string SummaryBannerColor
    {
        get => _summaryBannerColor;
        set => SetField(ref _summaryBannerColor, value);
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

    // ── Preview Panel (image + fields + raw text) ────────────────────────

    public InvoiceRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            // Guard against spurious null from inactive DataGrid bindings
            // (e.g. switching tabs empties the other grid, which pushes null
            // through TwoWay SelectedItem). Only explicit clears via
            // ClearSelectedRowCommand (which sets _clearingSelection) or
            // genuinely switching to a new non-null row are allowed.
            if (value == null && _selectedRow != null && !_clearingSelection)
                return;
            _clearingSelection = false;

            if (SetField(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(HasSelectedRow));
                OnPropertyChanged(nameof(PreviewRawText));
                OnPropertyChanged(nameof(PreviewFileName));
                // Reset view to image+fields when selection changes
                PreviewShowRawText = false;
                if (_selectedRow != null && _selectedRow.FilePath != _lastPreviewFilePath)
                    PreviewZoomLevel = 1.0;
                // Load the source image asynchronously (cache-aware)
                _ = LoadPreviewImageAsync();
            }
        }
    }

    public bool    HasSelectedRow  => _selectedRow != null;
    public string  PreviewRawText  => _selectedRow?.RawText ?? string.Empty;
    public string  PreviewFileName => _selectedRow != null
        ? TranslationSource.Fmt("PreviewFileName", _selectedRow.FileName)
        : TranslationSource.Get("PreviewFileNameDefault");

    // ── Invoice Direction (Received / Issued) Filter ────────────────────

    /// <summary>Filtered view of Results for direction-based filtering.</summary>
    public ListCollectionView? ResultsView => _resultsView;

    /// <summary>Filtered view of IncompleteResults for direction-based filtering.</summary>
    public ListCollectionView? IncompleteView => _incompleteView;

    /// <summary>
    /// Current direction filter: "all", "received", or "issued".
    /// Changing it refreshes both filtered views.
    /// </summary>
    public string DirectionFilter
    {
        get => _directionFilter;
        set
        {
            if (SetField(ref _directionFilter, value))
            {
                OnPropertyChanged(nameof(DirectionFilter));
                OnPropertyChanged(nameof(DirectionFilterDisplay));
                RefreshFilteredViews();
            }
        }
    }

    /// <summary>Human-readable label for the current filter.</summary>
    public string DirectionFilterDisplay => _directionFilter switch
    {
        "all"      => TranslationSource.Get("DirectionFilterAll"),
        "received" => TranslationSource.Get("DirectionFilterReceived"),
        "issued"   => TranslationSource.Get("DirectionFilterIssued"),
        _          => TranslationSource.Get("DirectionFilterAll"),
    };

    /// <summary>Confidence threshold for the "To check" filter bucket. This is
    /// the SAME threshold the old "low" bucket used (&lt;40%) — reused verbatim,
    /// not a new value. It mirrors ConfidenceToColorConverter's low bucket and
    /// the InfoConfidence popup text.</summary>
    private const double LowConfidenceThreshold = 0.40;

    /// <summary>Binary confidence filter: false = show all invoices, true =
    /// show only low-confidence ones ("À vérifier" / "To check", &lt;40%). The
    /// fine-grained percentage stays visible and sortable in the grid's
    /// Confidence column — the toolbar only needs the coarse toggle.</summary>
    public bool ConfidenceFilterLowOnly
    {
        get => _confidenceFilterLowOnly;
        set
        {
            if (SetField(ref _confidenceFilterLowOnly, value))
            {
                OnPropertyChanged(nameof(ConfidenceFilterLowOnly));
                RefreshFilteredViews();
            }
        }
    }

    /// <summary>Core confidence-filter predicate — kept static and internal so
    /// it is unit-testable without instantiating the heavy ViewModel (same
    /// pattern as GetStringField). Returns true when the row stays visible.</summary>
    internal static bool MatchesConfidenceFilter(bool lowOnly, double confidence)
        => !lowOnly || confidence < LowConfidenceThreshold;

    /// <summary>Filter predicate used by ResultsView and IncompleteView.
    /// Direction and confidence are independent dimensions — both apply.</summary>
    private bool FilterByDirection(object obj)
    {
        if (obj is not InvoiceRowViewModel row) return false;

        if (_directionFilter != "all"
            && !string.Equals(row.InvoiceDirection, _directionFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        // All mode: short-circuit before touching Confidence (matches old
        // behavior where the string filter "all" returned immediately).
        if (!_confidenceFilterLowOnly)
            return true;

        double conf = row.HasError ? 0.0 : row.Confidence;
        return MatchesConfidenceFilter(_confidenceFilterLowOnly, conf);
    }

    /// <summary>Refresh both filtered views when collections change.</summary>
    private void RefreshFilteredViews()
    {
        SafeRefreshView(_resultsView);
        SafeRefreshView(_incompleteView);
    }

    /// <summary>
    /// Refreshes a filtered view, first committing/cancelling any in-flight
    /// DataGrid edit transaction (AddNew/EditItem) so Refresh() cannot throw
    /// InvalidOperationException ("Refresh not allowed during AddNew or
    /// EditItem"). Residual failures are logged, never swallowed silently.
    /// </summary>
    private static void SafeRefreshView(ListCollectionView? view)
    {
        if (view == null) return;
        try
        {
            if (view.IsAddingNew) view.CommitNew();
            if (view.IsEditingItem) view.CommitEdit();
            view.Refresh();
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"[Hotix] SafeRefreshView: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Image source for the preview panel (source doc page).</summary>
    public ImageSource? PreviewImageSource
    {
        get => _previewImageSource;
        private set => SetField(ref _previewImageSource, value);
    }

    /// <summary>
    /// Status message shown in the preview panel when no image can be displayed
    /// yet (e.g. the server is still starting up for a PDF preview).
    /// </summary>
    public string PreviewStatusMessage
    {
        get => _previewStatusMessage;
        private set => SetField(ref _previewStatusMessage, value);
    }

    /// <summary>Zoom level for the preview image (0.25x – 3.0x).</summary>
    public double PreviewZoomLevel
    {
        get => _previewZoomLevel;
        set
        {
            if (SetField(ref _previewZoomLevel, Math.Clamp(value, 0.25, 3.0)))
                OnPropertyChanged(nameof(PreviewZoomPercent));
        }
    }

    /// <summary>Formatted zoom percentage for display.</summary>
    public string PreviewZoomPercent => $"{(int)(_previewZoomLevel * 100)}%";

    /// <summary>True while the preview image is being loaded.</summary>
    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => SetField(ref _isPreviewLoading, value);
    }

    /// <summary>Natural pixel dimensions of the currently loaded preview image.</summary>
    public double PreviewNaturalWidth => _previewNaturalWidth;
    public double PreviewNaturalHeight => _previewNaturalHeight;
    public bool PreviewShowRawText
    {
        get => _previewShowRawText;
        set => SetField(ref _previewShowRawText, value);
    }

    /// <summary>
    /// Loads the source document image for the preview panel.
    /// For image files (PNG, JPG, BMP, TIFF): loads directly from disk.
    /// For PDF files: calls the server /preview endpoint to render page 1.
    /// Uses an image cache and CancellationToken to avoid redundant loads.
    /// </summary>
    private async Task LoadPreviewImageAsync()
    {
        // Cancel any in-flight preview load
        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        _previewLoadCts = new CancellationTokenSource();
        var ct = _previewLoadCts.Token;

        if (_selectedRow == null || string.IsNullOrEmpty(_selectedRow.FilePath))
        {
            Debug.WriteLine($"[LoadPreviewImageAsync] SKIP — SelectedRow null or empty file path");
            IsPreviewLoading = false;
            PreviewImageSource = null;
            PreviewStatusMessage = string.Empty;
            _lastPreviewFilePath = null;
            return;
        }

        string filePath = _selectedRow.FilePath;
        Debug.WriteLine($"[LoadPreviewImageAsync] START — {DateTime.Now:HH:mm:ss.fff} — {filePath}");

        // Skip if same file already loaded
        if (filePath == _lastPreviewFilePath && _previewImageSource != null)
        {
            Debug.WriteLine($"[LoadPreviewImageAsync] SKIP — same file already loaded: {filePath}");
            IsPreviewLoading = false;
            return;
        }

        // Check cache first
        if (_previewImageCache.TryGetValue(filePath, out var cached))
        {
            PreviewImageSource = cached;
            PreviewStatusMessage = string.Empty;
            IsPreviewLoading = false;
            _lastPreviewFilePath = filePath;
            _previewNaturalWidth = cached.Width;
            _previewNaturalHeight = cached.Height;
            OnPropertyChanged(nameof(PreviewNaturalWidth));
            OnPropertyChanged(nameof(PreviewNaturalHeight));
            FitPreviewToAvailableWidth(cached.Width);
            return;
        }

        if (!File.Exists(filePath))
        {
            IsPreviewLoading = false;
            PreviewImageSource = null;
            PreviewStatusMessage = TranslationSource.Get("PreviewImageMissing");
            _lastPreviewFilePath = null;
            return;
        }

        IsPreviewLoading = true;
        PreviewStatusMessage = string.Empty;

        try
        {
            ct.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            BitmapImage bitmap;
            if (ext == ".pdf")
            {
                if (!_isServerStarted || !_isServerRunning)
                {
                    IsPreviewLoading = false;
                    PreviewImageSource = null;
                    if (_isServerStarting)
                    {
                        PreviewStatusMessage = TranslationSource.Get("PreviewServerStarting");
                    }
                    else
                    {
                        PreviewStatusMessage = TranslationSource.Get("PreviewServerUnavailable");
                        // Lazy-start the server so the PDF preview can succeed;
                        // the readiness hook inside EnsureServerReadyAsync
                        // reloads the preview automatically once it is up.
                        _ = StartServerForPreviewAsync();
                    }
                    _lastPreviewFilePath = null;
                    return;
                }

                // Register the file path via POST /preview/register to get a
                // short-lived token, then fetch the preview by token.
                var registerPayload = new StringContent(
                    JsonSerializer.Serialize(new { file_path = filePath }),
                    System.Text.Encoding.UTF8,
                    "application/json");
                using var registerResp = await _apiHttpClient.PostAsync(
                    "/preview/register", registerPayload, ct);
                ct.ThrowIfCancellationRequested();

                if (!registerResp.IsSuccessStatusCode)
                {
                    IsPreviewLoading = false;
                    PreviewImageSource = null;
                    PreviewStatusMessage = TranslationSource.Fmt("PreviewServerError", (int)registerResp.StatusCode);
                    _lastPreviewFilePath = null;
                    Debug.WriteLine($"[LoadPreviewImageAsync] /preview/register failed: {(int)registerResp.StatusCode}");
                    return;
                }

                var registerBody = await registerResp.Content.ReadAsStringAsync(ct);
                var tokenDoc = JsonSerializer.Deserialize<JsonElement>(registerBody);
                string token = tokenDoc.GetProperty("token").GetString() ?? "";
                if (string.IsNullOrEmpty(token))
                {
                    IsPreviewLoading = false;
                    PreviewImageSource = null;
                    PreviewStatusMessage = TranslationSource.Get("PreviewLoadError");
                    _lastPreviewFilePath = null;
                    return;
                }

                using var response = await _apiHttpClient.GetAsync(
                    $"/preview?token={Uri.EscapeDataString(token)}", ct);
                ct.ThrowIfCancellationRequested();

                if (!response.IsSuccessStatusCode)
                {
                    IsPreviewLoading = false;
                    PreviewImageSource = null;
                    PreviewStatusMessage = TranslationSource.Fmt("PreviewServerError", (int)response.StatusCode);
                    _lastPreviewFilePath = null;
                    Debug.WriteLine($"[LoadPreviewImageAsync] /preview failed: {(int)response.StatusCode}");
                    return;
                }

                byte[] imageBytes = await response.Content.ReadAsByteArrayAsync(ct);
                ct.ThrowIfCancellationRequested();
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(imageBytes);
                bitmap.EndInit();
            }
            else
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            ct.ThrowIfCancellationRequested();
            bitmap.Freeze();

            // Cache the result (with bounded size)
            if (_previewImageCache.Count >= MaxPreviewCacheEntries)
            {
                // Remove oldest entry
                var firstKey = _previewImageCache.Keys.First();
                _previewImageCache.Remove(firstKey);
            }
            _previewImageCache[filePath] = bitmap;

            _previewNaturalWidth = bitmap.Width;
            _previewNaturalHeight = bitmap.Height;
            PreviewImageSource = bitmap;
            PreviewStatusMessage = string.Empty;
            IsPreviewLoading = false;
            _lastPreviewFilePath = filePath;
            OnPropertyChanged(nameof(PreviewNaturalWidth));
            OnPropertyChanged(nameof(PreviewNaturalHeight));
            FitPreviewToAvailableWidth(bitmap.Width);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a new preview request — log for diagnostics
            Debug.WriteLine($"[LoadPreviewImageAsync] CANCELLED — {DateTime.Now:HH:mm:ss.fff} — {filePath}");
        }
        catch (Exception ex)
        {
            IsPreviewLoading = false;
            PreviewImageSource = null;
            PreviewStatusMessage = TranslationSource.Get("PreviewLoadError");
            _lastPreviewFilePath = null;
            Debug.WriteLine($"[LoadPreviewImageAsync] failed for {filePath}: {ex}");
            SentrySdk.CaptureException(ex);
        }
    }

    /// <summary>Starts the local OCR server on demand for a PDF preview without
    /// letting a startup failure crash the preview pipeline. On success the
    /// readiness hook inside EnsureServerReadyAsync reloads the preview.</summary>
    private async Task StartServerForPreviewAsync()
    {
        try
        {
            await EnsureServerReadyAsync();
        }
        catch
        {
            // Preview status stays on "PreviewServerUnavailable".
        }
    }

    private void FitPreviewToWidth()
    {
        // This is a hint — the actual fit is calculated in XAML via the ScrollViewer.
        // We set zoom to 1.0 and let the user adjust.
        PreviewZoomLevel = 1.0;
    }

    private void FitPreviewToPage()
    {
        PreviewZoomLevel = 1.0;
    }

    /// <summary>Opens the invoice image in a full-window viewer so it can be
    /// read comfortably. Used by the "View invoice" button in the row details.
    /// Selecting the row first guarantees the preview image is loaded (the row
    /// details are only visible when the row is already selected, but this
    /// re-arms the load in case selection and the expanded row diverge).</summary>
    private void ShowPreviewImage(InvoiceRowViewModel? row)
    {
        if (row != null)
            SelectedRow = row;

        PreviewShowRawText = false;
        _ = OpenImageViewerAsync(row);
    }

    /// <summary>Loads the preview image (the exact same pipeline the side panel
    /// uses) and opens it in the full-window viewer. If the image cannot be
    /// resolved, shows a clear message instead of silently doing nothing.</summary>
    private async Task OpenImageViewerAsync(InvoiceRowViewModel? row)
    {
        try
        {
            if (row != null && row.FilePath != _selectedRow?.FilePath)
                return;

            if (_previewImageSource == null)
                await LoadPreviewImageAsync();

            var source = _previewImageSource;
            if (source == null)
            {
                string reason = string.IsNullOrWhiteSpace(PreviewStatusMessage)
                    ? TranslationSource.Get("PreviewLoadError")
                    : PreviewStatusMessage;
                MessageBox.Show(Application.Current.MainWindow, reason,
                    TranslationSource.Get("PreviewBtnViewImage"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var viewer = new ImageViewerWindow(source, row?.FileName ?? PreviewFileName)
            {
                Owner = Application.Current.MainWindow,
            };
            viewer.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow,
                TranslationSource.Get("PreviewLoadError") + "\n\n" + ex.Message,
                TranslationSource.Get("PreviewBtnViewImage"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Sets the preview zoom so a freshly loaded image fits within a
    /// comfortable reading width instead of rendering at natural pixel size (a
    /// large scan would otherwise show only a blank corner). Clamped by the
    /// existing zoom bounds (0.25x-3.0x).</summary>
    private void FitPreviewToAvailableWidth(double naturalWidth)
    {
        if (naturalWidth <= 1)
            return;
        // PreviewZoomLevel's setter clamps to the 0.25x-3.0x range.
        PreviewZoomLevel = PreviewFitTargetWidth / naturalWidth;
    }

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
    public string WindowTitle => $"{TranslationSource.Get("MainWindowTitle")} — v{BuildInfo.AppVersion} ({BuildInfo.CommitHash})";

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
            // Use shared instance directly (no 'using' — static client is disposed on shutdown)
            var client = _httpQuickClient;
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
    /// Loads the Gemini API key from memory or appsettings.json (decrypting if stored encrypted).
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
                    string? stored = el.GetString();
                    if (!string.IsNullOrEmpty(stored))
                    {
                        // Try DPAPI decryption first, fall back to plaintext for old format
                        string? decrypted = DecryptString(stored) ?? stored;
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            _geminiKeyInput = decrypted;
                            return decrypted;
                        }
                    }
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    /// <summary>
    /// Loads the Grok API key from memory or appsettings.json (decrypting if stored encrypted).
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
                    string? stored = el.GetString();
                    if (!string.IsNullOrEmpty(stored))
                    {
                        // Try DPAPI decryption first, fall back to plaintext for old format
                        string? decrypted = DecryptString(stored) ?? stored;
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            _grokKeyInput = decrypted;
                            return decrypted;
                        }
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
            var client = _httpShortClient; // reuse shared instance (no 'using' — static)
            string apiUrl = string.Format(GeminiApiBaseTemplate, GeminiDefaultModel);
            var body = new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } };
            var response = await client.PostAsJsonAsync(
                $"{apiUrl}?key={apiKey}", body);

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
            return (false, TranslationSource.Fmt("GeminiNetworkError", ex.GetType().Name));
        }
        catch (Exception ex)
        {
            return (false, TranslationSource.Fmt("GeminiUnexpectedError", ex.GetType().Name));
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
            // Grok validation sets per-call Authorization header, so use a fresh client
            // to avoid polluting the shared _httpShortClient's DefaultRequestHeaders.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var body = new
            {
                model = GrokDefaultModel,
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
            return (false, TranslationSource.Fmt("GeminiNetworkError", ex.GetType().Name));
        }
        catch (Exception ex)
        {
            return (false, TranslationSource.Fmt("GeminiUnexpectedError", ex.GetType().Name));
        }
    }



    /// <summary>Logs malformed JSON returned by a cloud provider for post-mortem
    /// diagnostics. Writes truncated raw text to Debug output and a Sentry
    /// breadcrumb so future failures are diagnosable without reproducing blind.</summary>
    private static void LogMalformedJsonText(string provider, string rawText, JsonException jex, string filePath)
    {
        string truncated = rawText.Length > 2000 ? rawText[..2000] + "…" : rawText;
        Debug.WriteLine($"[Hotix] {provider} JSON parse failed for {Path.GetFileName(filePath)}: {jex.Message}");
        Debug.WriteLine($"[Hotix] {provider} raw text (first 2000 chars): {truncated}");
        SentrySdk.AddBreadcrumb(
            message: $"{provider} returned malformed JSON",
            category: provider.ToLowerInvariant(),
            level: Sentry.BreadcrumbLevel.Warning,
            data: new Dictionary<string, string>
            {
                { "file", Path.GetFileName(filePath) },
                { "error", jex.Message },
                { "raw_text_preview", rawText.Length > 500 ? rawText[..500] + "…" : rawText }
            });
    }

    /// <summary>Core Gemini retry loop, factored out of the HTTP path so it is
    /// unit-testable without network. fetchBody returns the raw response body
    /// for ONE attempt (HTTP status already checked by the caller). Malformed
    /// JSON (JsonException — including a truncated items array) triggers ONE
    /// retry with a fresh fetch, then throws a clear error carrying the raw
    /// text for diagnostics. Empty text and null-fields are NOT retried (they
    /// are API-level errors, not malformed output).</summary>
    internal static async Task<Dictionary<string, JsonElement>?> FetchGeminiFieldsWithRetryAsync(
        Func<Task<string>> fetchBody, string filePath)
    {
        JsonException? lastJsonError = null;
        string? lastRawText = null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            string responseBody = await fetchBody().ConfigureAwait(false);
            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                if (string.IsNullOrEmpty(text))
                    throw new CloudApiException(TranslationSource.Get("GeminiEmptyResponse"));

                // Strip markdown fences if present
                text = text.Trim();
                if (text.StartsWith("```json")) text = text[7..];
                if (text.EndsWith("```")) text = text[..^3];
                text = text.Trim();

                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                if (fields == null)
                    throw new CloudApiException(TranslationSource.Get("GeminiParseError"));

                return fields;
            }
            catch (JsonException jex)
            {
                // Log/carry the INNER invoice JSON (the actual malformed payload,
                // not the API envelope) — this is the diagnostic value of the raw
                // text and matches the pre-refactor behavior.
                string rawText = text ?? responseBody;
                LogMalformedJsonText("Gemini", rawText, jex, filePath);
                lastJsonError = jex;
                lastRawText = rawText;
                if (attempt == 0)
                {
                    Debug.WriteLine($"[Hotix] Gemini malformed JSON — retrying (attempt 2/2)...");
                    continue;
                }
                // Both attempts failed — surface a clear error with the raw text for diagnostics
                throw new CloudApiException(
                    TranslationSource.Fmt("GeminiParseErrorWithDetail", jex.Message),
                    statusCode: null,
                    responseBody: rawText);
            }
        }

        // Unreachable — both attempts either returned or threw above
        throw new CloudApiException(
            TranslationSource.Fmt("GeminiParseErrorWithDetail", lastJsonError?.Message ?? "unknown"),
            statusCode: null,
            responseBody: lastRawText);
    }

    /// <summary>Returns true if the Gemini model supports responseSchema in
    /// generationConfig. Schema support was added in gemini-1.5 and is present
    /// in all later models (2.x, 3.x, etc.). We default to including schema and
    /// only exclude known-incompatible pre-1.5 models (gemini-1.0-pro, etc.).</summary>
    private static bool SupportsResponseSchema(string model)
    {
        // Known incompatible: gemini-1.0-pro and other pre-1.5 models.
        if (model.StartsWith("gemini-1.0", StringComparison.OrdinalIgnoreCase))
            return false;
        // All other models (gemini-1.5+, gemini-2.x, gemini-3.x, etc.) support it.
        return true;
    }

    /// <summary>Safely reads a string field from a JsonElement dictionary, handling
    /// all value kinds without throwing. Gemini/Grok occasionally return numeric
    /// values for fields like montant_ht instead of quoted strings — the bare
    /// <c>el.GetString()</c> call used previously threw InvalidOperationException
    /// for non-String kinds, crashing extraction entirely for that invoice.</summary>
    internal static string? GetStringField(Dictionary<string, JsonElement> dict, string key)
    {
        if (!dict.TryGetValue(key, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            // Number → raw JSON text (e.g. "3800.00", "3800", "3.8e3").
            // Compatible with downstream decimal.TryParse(…, NumberStyles.Any, InvariantCulture)
            // which handles all these formats after comma→dot and space-stripping.
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null, // Object, Array, Undefined, Null
        };
    }

    private async Task<InvoiceResult> CallGeminiDirectlyAsync(string filePath, string apiKey)
    {
        await _geminiRequestGate.WaitAsync();
        try
        {
            if (GeminiDisabled)
                throw new CloudQuotaExceededException(
                    _geminiDisabledReason,
                    HttpStatusCode.TooManyRequests);

            return await CallGeminiDirectlyCoreAsync(filePath, apiKey);
        }
        finally
        {
            _geminiRequestGate.Release();
        }
    }

    private async Task<InvoiceResult> CallGeminiDirectlyCoreAsync(string filePath, string apiKey)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        string base64Data = Convert.ToBase64String(fileBytes);
        string mimeType = GetMimeType(filePath);

        // Determine whether to include responseSchema (supported on 1.5+ models).
        // gemini-1.0-pro and earlier models will get response_mime_type only.
        bool includeSchema = SupportsResponseSchema(ResolvedGeminiModel);
        if (!includeSchema)
            Debug.WriteLine($"[Hotix] Gemini model '{ResolvedGeminiModel}' does not support responseSchema — omitting from generationConfig");

        var generationConfigObj = includeSchema
            ? new { responseMimeType = "application/json", responseSchema = GeminiResponseSchema }
            : (object)new { responseMimeType = "application/json" };

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
            generationConfig = generationConfigObj
        };

        string geminiApiUrl = string.Format(GeminiApiBaseTemplate, ResolvedGeminiModel);

        // ── Retry loop: malformed JSON from an LLM is often non-deterministic,
        //     so a single retry (fresh API call) frequently recovers. We do NOT
        //     retry on auth/quota/network errors — those should fail immediately.
        //     The loop itself lives in FetchGeminiFieldsWithRetryAsync (factored
        //     out so the retry+logging behavior is unit-testable offline). ──
        Dictionary<string, JsonElement>? fields = await FetchGeminiFieldsWithRetryAsync(
            async () =>
            {
                var geminiClient = _httpCloudClient; // reuse shared instance
                var response = await geminiClient.PostAsJsonAsync(
                    $"{geminiApiUrl}?key={apiKey}", requestBody);

                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429)
                        throw new CloudQuotaExceededException(TranslationSource.Fmt("GeminiApiError", 429, ResponseBodySummary(responseBody)), response.StatusCode, responseBody);
                    throw new CloudApiException(TranslationSource.Fmt("GeminiApiError", (int)response.StatusCode, responseBody), response.StatusCode, responseBody);
                }

                return responseBody;
            },
            filePath);

            // Parse items array (BUG 1 fix)
            List<InvoiceItem> items = new();
            if (fields.TryGetValue("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemEl in itemsEl.EnumerateArray())
                {
                    if (itemEl.ValueKind != JsonValueKind.Object) continue;
                    items.Add(new InvoiceItem
                    {
                        Designation = itemEl.TryGetProperty("designation", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                        Quantite = itemEl.TryGetProperty("quantite", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDouble() : null,
                        Unit = itemEl.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                        PrixUnitaire = itemEl.TryGetProperty("prix_unitaire", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null,
                        TvaRate = itemEl.TryGetProperty("tva_rate", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : null,
                        Montant = itemEl.TryGetProperty("montant", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : null,
                    });
                }
            }

            // Parse tax_summary array (per-rate TVA breakdown)
            List<TaxSummaryRow> taxSummary = new();
            if (fields.TryGetValue("tax_summary", out var taxEl) && taxEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var rowEl in taxEl.EnumerateArray())
                {
                    if (rowEl.ValueKind != JsonValueKind.Object) continue;
                    taxSummary.Add(new TaxSummaryRow
                    {
                        Rate = rowEl.TryGetProperty("rate", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null,
                        BaseHt = rowEl.TryGetProperty("base_ht", out var bh) && bh.ValueKind == JsonValueKind.Number ? bh.GetDouble() : null,
                        TaxAmount = rowEl.TryGetProperty("tax_amount", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetDouble() : null,
                    });
                }
            }

            // Reconcile amounts — derive montant_taxe when HT+TVA+TTC are consistent (BUG 2 fix)
            string? htGemini = GetStringField(fields, "montant_ht");
            string? tvaGemini = GetStringField(fields, "montant_tva");
            string? ttcGemini = GetStringField(fields, "montant_ttc");
            string? taxeGemini = GetStringField(fields, "montant_taxe");
            if (!string.IsNullOrEmpty(htGemini) && !string.IsNullOrEmpty(tvaGemini)
                && !string.IsNullOrEmpty(ttcGemini) && string.IsNullOrEmpty(taxeGemini)
                && decimal.TryParse(htGemini.Replace(",", ".").Replace(" ", ""),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var htDec)
                && decimal.TryParse(tvaGemini.Replace(",", ".").Replace(" ", ""),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tvaDec)
                && decimal.TryParse(ttcGemini.Replace(",", ".").Replace(" ", ""),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ttcDec))
            {
                decimal computedTaxe = ttcDec - htDec - tvaDec;
                taxeGemini = computedTaxe.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            }

            return new InvoiceResult
            {
                NumeroFacture = GetStringField(fields, "numero_facture"),
                Date = GetStringField(fields, "date"),
                Fournisseur = GetStringField(fields, "fournisseur"),
                Client = GetStringField(fields, "client"),
                MontantHt = htGemini,
                MontantTva = tvaGemini,
                MontantTaxe = taxeGemini,
                MontantTtc = ttcGemini,
                Confidence = items.Count > 0 ? 0.90 : 0.95,
                RawText = TranslationSource.Get("GeminiDirectExtraction"),
                EngineUsed = "gemini",
                Items = items.Count > 0 ? items : null,
                TaxSummary = taxSummary.Count > 0 ? taxSummary : null,
            };
    }

    /// <summary>
    /// Extract invoice data via the Grok (xAI) API using an OpenAI-compatible chat completions call.
    /// </summary>
    private async Task<InvoiceResult> CallGrokDirectlyAsync(string filePath, string apiKey)
    {
        await _grokRequestGate.WaitAsync();
        try
        {
            if (GrokDisabled)
                throw new CloudQuotaExceededException(
                    _grokDisabledReason,
                    HttpStatusCode.TooManyRequests);

            return await CallGrokDirectlyCoreAsync(filePath, apiKey);
        }
        finally
        {
            _grokRequestGate.Release();
        }
    }

    private async Task<InvoiceResult> CallGrokDirectlyCoreAsync(string filePath, string apiKey)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        string base64Data = Convert.ToBase64String(fileBytes);
        string mimeType = GetMimeType(filePath);
        string dataUri = $"data:{mimeType};base64,{base64Data}";

        var requestBody = new
        {
            model = ResolvedGrokModel,
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

        // Grok API calls set per-call Authorization header, so use a fresh client
        // to avoid polluting the shared _httpCloudClient's DefaultRequestHeaders.

        // ── Retry loop: malformed JSON from an LLM is often non-deterministic,
        //     so a single retry (fresh API call) frequently recovers. We do NOT
        //     retry on auth/quota/network errors — those should fail immediately. ──
        for (int grokAttempt = 0; grokAttempt < 2; grokAttempt++)
        {
            using var grokClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            grokClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var response = await grokClient.PostAsJsonAsync(GrokApiBase, requestBody);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Log the exact response body for all non-2xx responses to avoid misclassifying real errors as quota
                string errorDetail = responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody;
                if ((int)response.StatusCode == 429)
                    throw new CloudQuotaExceededException(TranslationSource.Fmt("GrokApiError", 429, errorDetail), response.StatusCode, responseBody);
                throw new CloudApiException(TranslationSource.Fmt("GrokApiError", (int)response.StatusCode, errorDetail), response.StatusCode, responseBody);
            }

            // Parse the whole response inside the retry scope so malformed
            // top-level JSON (JsonDocument.Parse of the API envelope) also gets
            // the single retry — LLM malformed output is non-deterministic, so
            // a fresh API call frequently recovers.
            Dictionary<string, JsonElement>? fields;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var text = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrEmpty(text))
                    throw new CloudApiException(TranslationSource.Get("GrokEmptyResponse"));

                // Strip markdown fences if present
                text = text.Trim();
                if (text.StartsWith("```json")) text = text[7..];
                if (text.EndsWith("```")) text = text[..^3];
                text = text.Trim();

                fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                if (fields == null)
                    throw new CloudApiException(TranslationSource.Get("GrokParseError"));
            }
            catch (JsonException jex)
            {
                LogMalformedJsonText("Grok", responseBody, jex, filePath);
                if (grokAttempt == 0)
                {
                    Debug.WriteLine($"[Hotix] Grok malformed JSON — retrying (attempt 2/2)...");
                    continue;
                }
                // Both attempts failed — surface a clear error with the raw text for diagnostics
                throw new CloudApiException(
                    TranslationSource.Fmt("GrokParseErrorWithDetail", jex.Message),
                    statusCode: null,
                    responseBody: responseBody);
            }

            // Parse items array (BUG 1 fix)
            List<InvoiceItem> itemsGrok = new();
            if (fields.TryGetValue("items", out var itemsElGrok) && itemsElGrok.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemEl in itemsElGrok.EnumerateArray())
                {
                    if (itemEl.ValueKind != JsonValueKind.Object) continue;
                    itemsGrok.Add(new InvoiceItem
                    {
                        Designation = itemEl.TryGetProperty("designation", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                        Quantite = itemEl.TryGetProperty("quantite", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDouble() : null,
                        Unit = itemEl.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                        PrixUnitaire = itemEl.TryGetProperty("prix_unitaire", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null,
                        TvaRate = itemEl.TryGetProperty("tva_rate", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : null,
                        Montant = itemEl.TryGetProperty("montant", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : null,
                    });
                }
            }

            // Parse tax_summary array (per-rate TVA breakdown)
            List<TaxSummaryRow> taxSummaryGrok = new();
            if (fields.TryGetValue("tax_summary", out var taxElGrok) && taxElGrok.ValueKind == JsonValueKind.Array)
            {
                foreach (var rowEl in taxElGrok.EnumerateArray())
                {
                    if (rowEl.ValueKind != JsonValueKind.Object) continue;
                    taxSummaryGrok.Add(new TaxSummaryRow
                    {
                        Rate = rowEl.TryGetProperty("rate", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null,
                        BaseHt = rowEl.TryGetProperty("base_ht", out var bh) && bh.ValueKind == JsonValueKind.Number ? bh.GetDouble() : null,
                        TaxAmount = rowEl.TryGetProperty("tax_amount", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetDouble() : null,
                    });
                }
            }

            // Reconcile amounts — derive montant_taxe when HT+TVA+TTC are consistent (BUG 2 fix)
            string? htGrok = GetStringField(fields, "montant_ht");
            string? tvaGrok = GetStringField(fields, "montant_tva");
            string? ttcGrok = GetStringField(fields, "montant_ttc");
            string? taxeGrok = GetStringField(fields, "montant_taxe");
            if (!string.IsNullOrEmpty(htGrok) && !string.IsNullOrEmpty(tvaGrok)
                && !string.IsNullOrEmpty(ttcGrok) && string.IsNullOrEmpty(taxeGrok)
                && decimal.TryParse(htGrok.Replace(",", ".").Replace(" ", ""),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var htDec)
                && decimal.TryParse(tvaGrok.Replace(",", ".").Replace(" ", ""),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tvaDec)
                && decimal.TryParse(ttcGrok.Replace(",", ".").Replace(" ", ""),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ttcDec))
            {
                decimal computedTaxe = ttcDec - htDec - tvaDec;
                taxeGrok = computedTaxe.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            }

            return new InvoiceResult
            {
                NumeroFacture = GetStringField(fields, "numero_facture"),
                Date = GetStringField(fields, "date"),
                Fournisseur = GetStringField(fields, "fournisseur"),
                Client = GetStringField(fields, "client"),
                MontantHt = htGrok,
                MontantTva = tvaGrok,
                MontantTaxe = taxeGrok,
                MontantTtc = ttcGrok,
                Confidence = itemsGrok.Count > 0 ? 0.90 : 0.95,
                RawText = TranslationSource.Get("GrokDirectExtraction"),
                EngineUsed = "grok",
                Items = itemsGrok.Count > 0 ? itemsGrok : null,
                TaxSummary = taxSummaryGrok.Count > 0 ? taxSummaryGrok : null,
            };
        }

        // Unreachable — retained to satisfy compiler definite-assignment rules
        throw new InvalidOperationException("Unreachable: Grok retry loop exhausted");
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
            // Already starting — wait for completion. The wait must be LONGER than
            // App.StartServerAsync's own startup timeout (ServerStartTimeout,
            // default 90s) so concurrent callers (batch concurrency can reach
            // EnsureServerReadyAsync from multiple ExtractViaServerAsync tasks) do
            // NOT time out early and re-enter App.StartServerAsync, which would
            // kill the in-progress server process.
            Debug.WriteLine("[Hotix] EnsureServerReadyAsync: waiting for already-starting server...");
            var waitStart = Stopwatch.StartNew();
            while (waitStart.Elapsed < App.ServerStartTimeout + TimeSpan.FromSeconds(5))
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

                // Server is now ready — reload the preview for the selected row
                // (it may have been skipped earlier because the server wasn't running).
                if (SelectedRow != null)
                    Application.Current.Dispatcher.InvokeAsync(() => { _ = LoadPreviewImageAsync(); });

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
                    TranslationSource.Fmt("ServerStartingFailed", App.ServerLogPath));
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
            throw new InvalidOperationException(TranslationSource.Fmt("ServerStartFailPrefix", ex.GetType().Name));
        }
    }

    // ── Server Retry ──────────────────────────────────────────────────────

    private async Task RetryServerAsync()
    {
        // Reset state before retrying — force a fresh health check
        IsExtracting = false;
        IsServerRunning = false;
        ShowSummaryBanner = false;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Results.Clear();
            IncompleteResults.Clear();
            _addedResultPaths.Clear();
        });
        NotifySummaryChanged();

        try
        {
            await EnsureServerReadyAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ErrorMessageTranslator.ToUserMessage(ex), TranslationSource.Get("ErrorRetryTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Key Management ────────────────────────────────────────────────────

    private async Task ClearGeminiKeyAsync()
    {
        // Confirmation dialog
        var confirm = TranslationSource.Get("GeminiClearConfirm");
        var title = TranslationSource.Get("GeminiClearTitle");
        if (MessageBox.Show(confirm, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        GeminiKeyInput = string.Empty;
        GeminiAvailable = false;

        try
        {
            var settings = ReadAppSettings();
            settings["gemini_api_key"] = "";
            await WriteAppSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorClearKey", ErrorMessageTranslator.ToUserMessage(ex)), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task SaveGeminiKeyAsync()
    {
        try
        {
            // Validate key via server endpoint (uses the server's Python environment
            // and reads from the same appsettings.json the server will use)
            if (_isServerStarted && _isServerRunning)
            {
                try
                {
                    var client = _httpShortClient; // reuse shared instance (no 'using' — static)
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
                        // Note: do NOT return here — the key must still be persisted.
                        // The message above explicitly promises "the key will still be
                        // saved". Returning early silently erased the user's key.
                        // Continue to the save block below.
                    }
                }
                catch (HttpRequestException)
                {
                    // Server not reachable — still save the key, but warn the user
                    MessageBox.Show(
                        TranslationSource.Get("GeminiServerUnreachable"),
                        TranslationSource.Get("GeminiValidationTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (TaskCanceledException)
                {
                    // Timeout — still save the key
                    MessageBox.Show(
                        TranslationSource.Get("GeminiServerUnreachable"),
                        TranslationSource.Get("GeminiValidationTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // Save to appsettings.json with DPAPI-encrypted key
            var settings = ReadAppSettings();
            settings["gemini_api_key"] = EncryptString(GeminiKeyInput);
            settings["grok_api_key"] = string.IsNullOrEmpty(_grokKeyInput)
                ? "" : EncryptString(_grokKeyInput);
            settings["default_engine"] = SelectedEngine;
            settings["gemini_model"] = _geminiModel;
            settings["grok_model"] = _grokModel;
            settings["batch_concurrency"] = _batchConcurrency;
            await WriteAppSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorSaveKey", ErrorMessageTranslator.ToUserMessage(ex)), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ClearGrokKeyAsync()
    {
        // Confirmation dialog
        var confirm = TranslationSource.Get("GrokClearConfirm");
        var title = TranslationSource.Get("GrokClearTitle");
        if (MessageBox.Show(confirm, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        GrokKeyInput = string.Empty;
        GrokAvailable = false;

        try
        {
            var settings = ReadAppSettings();
            settings["grok_api_key"] = "";
            await WriteAppSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorClearKey", ErrorMessageTranslator.ToUserMessage(ex)), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Clears the key for whichever provider tab is active in the setup window.</summary>
    private async Task ClearActiveKeyAsync()
    {
        if (_activeKeyProvider == "gemini")
            await ClearGeminiKeyAsync();
        else
            await ClearGrokKeyAsync();
    }

    public async Task SaveGrokKeyAsync()
    {
        try
        {
            // Validate key via server endpoint
            if (_isServerStarted && _isServerRunning)
            {
                try
                {
                    var client = _httpShortClient; // reuse shared instance (no 'using' — static)
                    var payload = new { api_key = GrokKeyInput };
                    var response = await client.PostAsJsonAsync(
                        "http://127.0.0.1:8000/validate-grok-key", payload);
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
                        // Note: do NOT return here — the key must still be persisted.
                        // The message above explicitly promises "the key will still be
                        // saved". Returning early silently erased the user's key.
                        // Continue to the save block below.
                    }
                }
                catch (HttpRequestException)
                {
                    MessageBox.Show(
                        TranslationSource.Get("GeminiServerUnreachable"),
                        TranslationSource.Get("GeminiValidationTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (TaskCanceledException)
                {
                    MessageBox.Show(
                        TranslationSource.Get("GeminiServerUnreachable"),
                        TranslationSource.Get("GeminiValidationTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // Save to appsettings.json with DPAPI-encrypted key
            var settings = ReadAppSettings();
            settings["grok_api_key"] = EncryptString(GrokKeyInput);
            settings["gemini_api_key"] = string.IsNullOrEmpty(_geminiKeyInput)
                ? "" : EncryptString(_geminiKeyInput);
            settings["default_engine"] = SelectedEngine;
            settings["batch_concurrency"] = _batchConcurrency;
            await WriteAppSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(TranslationSource.Fmt("ErrorSaveKey", ErrorMessageTranslator.ToUserMessage(ex)), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                bool migrated = false;

                if (doc.RootElement.TryGetProperty("gemini_api_key", out var el))
                {
                    string? stored = el.GetString();
                    if (!string.IsNullOrEmpty(stored))
                    {
                        string? decrypted = DecryptString(stored);
                        if (decrypted != null)
                        {
                            GeminiKeyInput = decrypted; // New encrypted format
                        }
                        else
                        {
                            // Old plaintext — use directly and re-save encrypted (migration)
                            GeminiKeyInput = stored;
                            migrated = true;
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("grok_api_key", out var grokEl))
                {
                    string? stored = grokEl.GetString();
                    if (!string.IsNullOrEmpty(stored))
                    {
                        string? decrypted = DecryptString(stored);
                        if (decrypted != null)
                        {
                            GrokKeyInput = decrypted; // New encrypted format
                        }
                        else
                        {
                            // Old plaintext — use directly and re-save encrypted (migration)
                            GrokKeyInput = stored;
                            migrated = true;
                        }
                    }
                }

                // One-time migration: re-save with encryption if old plaintext was found
                if (migrated)
                {
                    var settings = ReadAppSettings();
                    if (!string.IsNullOrEmpty(GeminiKeyInput))
                        settings["gemini_api_key"] = EncryptString(GeminiKeyInput);
                    if (!string.IsNullOrEmpty(GrokKeyInput))
                        settings["grok_api_key"] = EncryptString(GrokKeyInput);
                    _ = WriteAppSettingsAsync(settings); // fire-and-forget

                    // Notify the user that their keys were secured
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        SummaryBannerText = TranslationSource.Get("KeySecuredNotification");
                        SummaryBannerColor = "#2ECC71";
                        ShowSummaryBanner = true;
                    });
                }

                if (doc.RootElement.TryGetProperty("default_engine", out var engineEl))
                    SelectedEngine = engineEl.GetString() ?? "auto";

                // Load model selections
                if (doc.RootElement.TryGetProperty("gemini_model", out var geminiModelEl))
                    GeminiModel = geminiModelEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("grok_model", out var grokModelEl))
                    GrokModel = grokModelEl.GetString() ?? "";
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

    // ── DPAPI Encryption / Decryption ────────────────────────────────────

    /// <summary>
    /// Encrypts a string using Windows DPAPI with CurrentUser scope.
    /// The encrypted result is Base64-encoded for JSON storage.
    /// Only the current Windows user can decrypt it.
    /// </summary>
    private static string EncryptString(string plaintext)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a DPAPI-encrypted Base64 string.
    /// Returns null if the value is not valid encrypted data
    /// (e.g. old plaintext from before encryption was added).
    /// </summary>
    private static string? DecryptString(string ciphertext)
    {
        try
        {
            byte[] encrypted = Convert.FromBase64String(ciphertext);
            byte[] bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // Not valid encrypted data — caller can treat as plaintext
        }
    }

    // ── Crash-safe Settings IO ───────────────────────────────────────────

    /// <summary>
    /// Reads the current appsettings.json into a mutable Dictionary.
    /// Returns an empty dictionary if the file doesn't exist or can't be parsed.
    /// </summary>
    private static Dictionary<string, object?> ReadAppSettings()
    {
        try
        {
            string path = ResolveAppSettingsPath();
            if (!File.Exists(path))
                return new Dictionary<string, object?>();

            var doc = JsonDocument.Parse(File.ReadAllText(path));
            var result = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.True => (object?)true,
                    JsonValueKind.False => (object?)false,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i)
                        ? (object?)i
                        : prop.Value.GetRawText(),
                    _ => prop.Value.GetRawText()
                };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// Atomically writes settings to appsettings.json via a temp file.
    /// If the process crashes mid-write, the original file is never corrupted.
    /// </summary>
    private static async Task WriteAppSettingsAsync(Dictionary<string, object?> settings)
    {
        string appSettingsPath = ResolveAppSettingsPath();
        string tempPath = appSettingsPath + ".tmp";
        string dir = Path.GetDirectoryName(appSettingsPath)!;
        Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json);
        File.Replace(tempPath, appSettingsPath, null); // atomic replace (no backup)
    }

    /// <summary>Static copy of AllowedExtensions used by Window_Drop in the code-behind.</summary>
    public static readonly HashSet<string> AllowedExtensionsStatic = new(AllowedExtensions, StringComparer.OrdinalIgnoreCase);

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

        // APPEND — use shared method (same logic as drag-and-drop)
        AddValidatedFilePaths(dialog.FileNames);
    }

    private void RemoveFile(FileItemViewModel? file)
    {
        if (file == null) return;
        file.PropertyChanged -= OnFileItemPropertyChanged;
        DetectedFiles.Remove(file);
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

    private Task SetExtractionStatusAsync(string status)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _extractionStatusText = status;
            OnPropertyChanged(nameof(ExtractionStatusText));
        }).Task;
    }

    private Task AddExtractionResultAsync(InvoiceRowViewModel row)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Defense-in-depth: one input file must produce at most one result
            // row.  If the same file is ever submitted twice (duplicate in the
            // selection, a race, or a retry), deduplicate by canonical path so
            // the user never sees an "extra extraction".
            string key = NormalizeFilePathForComparison(row.FilePath);
            if (!_addedResultPaths.Add(key))
            {
                LogPipeline($"Skipped duplicate result for {row.FileName}");
                return;
            }

            Results.Add(row);
            if (row.IsIncomplete) IncompleteResults.Add(row);

            ProcessedFiles += 1;
            NotifySummaryChanged();
            RaiseCommandStateChanged();
        }).Task;
    }

    private Task ShowQuotaFallbackBannerAsync()
    {
        lock (this)
        {
            if (_quotaFallbackBannerShown)
                return Task.CompletedTask;

            _quotaFallbackBannerShown = true;
        }

        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SummaryBannerText = TranslationSource.Get("QuotaFallbackBanner");
            SummaryBannerColor = "#E67E22";
            ShowSummaryBanner = true;
            OnPropertyChanged(nameof(SummaryBannerText));
            OnPropertyChanged(nameof(SummaryBannerColor));
            OnPropertyChanged(nameof(ShowSummaryBanner));

            _extractionStatusText = TranslationSource.Get("ExtractionQuotaFallback");
            OnPropertyChanged(nameof(ExtractionStatusText));
        }).Task;
    }

    /// <summary>
    /// Shows a banner when the selected engine's quota is reached while the engine
    /// is explicit (no automatic fallback to OCR/Grok). Gives the user a clear,
    /// actionable message instead of a silent error row.
    /// </summary>
    private Task ShowQuotaExceededBannerAsync(bool isGemini)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SummaryBannerText = TranslationSource.Get(isGemini ? "QuotaExceededGeminiBanner" : "QuotaExceededGrokBanner");
            SummaryBannerColor = "#C0392B";
            ShowSummaryBanner = true;
            OnPropertyChanged(nameof(SummaryBannerText));
            OnPropertyChanged(nameof(SummaryBannerColor));
            OnPropertyChanged(nameof(ShowSummaryBanner));
        }).Task;
    }

    private static bool IsPermanentCloudFailure(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.Gone
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests;
    }

    private bool TryMarkGeminiDisabled(string reason)
    {
        lock (this)
        {
            if (_geminiDisabled)
                return false;

            _geminiDisabled = true;
            _geminiDisabledReason = reason;
            return true;
        }
    }

    private bool TryMarkGrokDisabled(string reason)
    {
        lock (this)
        {
            if (_grokDisabled)
                return false;

            _grokDisabled = true;
            _grokDisabledReason = reason;
            return true;
        }
    }

    /// <summary>
    /// Opens the Gemini/Grok setup window, optionally on a specific provider tab
    /// ("gemini" or "grok"). Used by the quota dialog's "enter a new key" action.
    /// </summary>
    private void OpenSettingsForProvider(string? provider)
    {
        var wizard = new global::Hotix.InvoiceClient.GeminiSetupWindow(provider) { DataContext = this };
        wizard.Owner = Application.Current.MainWindow;
        wizard.ShowDialog();
    }

    /// <summary>
    /// On the FIRST availability failure (quota or high demand) for a provider in a
    /// batch, shows the interactive dialog on the UI thread (the extraction loop may
    /// be on a background thread — hence the Dispatcher hop). Returns the user's
    /// choice; choosing "Stop" also cancels the running batch.
    ///
    /// Depending on the user's choice:
    ///  - "Enter a new key": opens the settings window on the provider's tab; if a
    ///    new key was actually saved, the provider's disabled flag is cleared so the
    ///    remaining files of the batch retry it.
    ///  - "Continue with OCR": keeps the current fallback behavior; if "remember for
    ///    this session" is ticked, the dialog is suppressed for later batches too.
    ///  - "Stop": cancels the batch so no file is silently handed to local OCR.
    /// </summary>
    private async Task<QuotaDialogChoice> HandleQuotaExceededAsync(bool isGemini, string provider)
    {
        QuotaDialogChoice result = QuotaDialogChoice.ContinueWithOcr;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new QuotaExceededDialog(isGemini) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true)
                return;

            result = dialog.Choice;

            switch (dialog.Choice)
            {
                case QuotaDialogChoice.EnterNewKey:
                    OpenSettingsForProvider(provider);
                    if (isGemini ? HasGeminiKey : HasGrokKey)
                    {
                        lock (this)
                        {
                            if (isGemini)
                            {
                                _geminiDisabled = false;
                                _geminiDisabledReason = string.Empty;
                            }
                            else
                            {
                                _grokDisabled = false;
                                _grokDisabledReason = string.Empty;
                            }
                        }
                    }
                    break;

                case QuotaDialogChoice.Stop:
                    _extractionCts?.Cancel();
                    break;

                case QuotaDialogChoice.ContinueWithOcr:
                    if (dialog.RememberForSession)
                    {
                        if (isGemini)
                            _geminiOcrChosenForSession = true;
                        else
                            _grokOcrChosenForSession = true;
                    }
                    break;
            }
        });

        return result;
    }

    /// <summary>
    /// Falls back to local OCR for one file after a cloud provider became
    /// unavailable (quota or high demand). The user is asked — once per batch per
    /// provider — whether to continue with OCR, enter a new key, or stop. If the
    /// user chooses "Stop", the batch is cancelled and an error row is returned
    /// instead of running OCR. Otherwise the local OCR extraction runs.
    /// </summary>
    private async Task<InvoiceRowViewModel> FallbackToOcrWithConsentAsync(
        string file,
        CancellationToken ct,
        string reason,
        bool isGemini,
        string provider,
        bool firstProviderFailure)
    {
        if (firstProviderFailure && !(isGemini ? _geminiOcrChosenForSession : _grokOcrChosenForSession))
        {
            QuotaDialogChoice choice = await HandleQuotaExceededAsync(isGemini, provider);
            if (choice == QuotaDialogChoice.Stop)
                return InvoiceRowViewModel.FromError(file, TranslationSource.Get("ExtractionStoppedByUser"));
        }

        return await ExtractViaServerAsync(file, ct, reason);
    }

    private bool GeminiDisabled
    {
        get { lock (this) { return _geminiDisabled; } }
    }

    private bool GrokDisabled
    {
        get { lock (this) { return _grokDisabled; } }
    }

    private string GeminiDisabledReason
    {
        get { lock (this) { return _geminiDisabledReason; } }
    }

    private string GrokDisabledReason
    {
        get { lock (this) { return _grokDisabledReason; } }
    }

    private async Task<InvoiceRowViewModel> ProcessInvoiceAsync(
        string file,
        string selectedEngine,
        string? geminiKey,
        string? grokKey,
        bool hasGemini,
        bool hasGrok,
        CancellationToken ct = default)
    {
        string fileName = Path.GetFileName(file);
        LogPipeline($"Invoice started: {fileName}");
        await SetExtractionStatusAsync(TranslationSource.Fmt("ExtractionProcessing", fileName));

        var stopwatch = Stopwatch.StartNew();
        InvoiceRowViewModel row;

        try
        {
            if (selectedEngine == "grok" && hasGrok)
            {
                if (GrokDisabled)
                {
                    LogPipeline($"Grok skipped (cached failure) for {fileName}");
                    row = InvoiceRowViewModel.FromError(file, GrokDisabledReason);
                }
                else
                {
                    LogPipeline($"Engine dispatch: Grok-only for {fileName}");
                    try
                    {
                        LogPipeline("HTTP request start (Grok)");
                        InvoiceResult result = await CallGrokDirectlyAsync(file, grokKey!);
                        LogPipeline("HTTP response received — success");
                        row = InvoiceRowViewModel.FromSuccess(file, result);
                    }
                    catch (CloudQuotaExceededException ex)
                    {
                        bool firstGrokQuota = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(ex));
                        LogPipeline($"Grok skipped (cached failure) for {fileName}");

                        // Interactive quota dialog — shown once per batch per provider.
                        if (firstGrokQuota && !_grokOcrChosenForSession)
                            await HandleQuotaExceededAsync(isGemini: false, "grok");

                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                    catch (CloudApiException ex) when (ex.StatusCode.HasValue && IsPermanentCloudFailure(ex.StatusCode.Value))
                    {
                        TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(ex));
                        LogPipeline($"Grok skipped (cached failure) for {fileName}");
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                    catch (CloudApiException ex)
                    {
                        LogPipeline($"HTTP response error: {ex.Message}");
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                    catch (Exception ex2)
                    {
                        LogPipeline($"Grok exception: {ex2.GetType().Name}: {ex2.Message}");
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex2, ocrServerContext: false));
                    }
                }
            }
            else if (hasGemini && (selectedEngine == "auto" || selectedEngine == "gemini") && !GeminiDisabled)
            {
                LogPipeline($"Engine dispatch: Gemini-first for {fileName}");
                try
                {
                    LogPipeline("HTTP request start (Gemini)");
                    InvoiceResult result = await CallGeminiDirectlyAsync(file, geminiKey!);
                    LogPipeline("HTTP response received — parsing succeeded");
                    row = InvoiceRowViewModel.FromSuccess(file, result);
                }
                catch (CloudQuotaExceededException ex)
                {
                    bool firstGeminiQuota = TryMarkGeminiDisabled(ErrorMessageTranslator.ToUserMessage(ex));
                    LogPipeline("Gemini quota exceeded");

                    // Interactive dialog — shown once per batch per provider. The
                    // user chooses whether to continue with OCR, enter a new key,
                    // or stop (never silently fall back to OCR).
                    QuotaDialogChoice choice = QuotaDialogChoice.ContinueWithOcr;
                    if (firstGeminiQuota && !_geminiOcrChosenForSession)
                        choice = await HandleQuotaExceededAsync(isGemini: true, "gemini");

                    if (choice == QuotaDialogChoice.Stop)
                    {
                        row = InvoiceRowViewModel.FromError(file, TranslationSource.Get("ExtractionStoppedByUser"));
                    }
                    else if (selectedEngine == "auto" && hasGrok && !GrokDisabled)
                    {
                        LogPipeline("Gemini quota — trying Grok before OCR");
                        try
                        {
                            InvoiceResult grokResult = await CallGrokDirectlyAsync(file, grokKey!);
                            LogPipeline("Grok succeeded after Gemini quota");
                            row = InvoiceRowViewModel.FromSuccess(file, grokResult);
                        }
                        catch (CloudQuotaExceededException grokEx)
                        {
                            bool firstGrokQuota = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(grokEx));
                            LogPipeline("Grok also failed after Gemini quota — falling back to OCR");
                            row = await FallbackToOcrWithConsentAsync(file, ct, ErrorMessageTranslator.ToUserMessage(grokEx), isGemini: false, provider: "grok", firstProviderFailure: firstGrokQuota);
                        }
                        catch (CloudApiException grokEx) when (grokEx.StatusCode.HasValue && IsPermanentCloudFailure(grokEx.StatusCode.Value))
                        {
                            bool firstGrokQuota = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(grokEx));
                            LogPipeline("Grok also failed after Gemini quota — falling back to OCR");
                            row = await FallbackToOcrWithConsentAsync(file, ct, ErrorMessageTranslator.ToUserMessage(grokEx), isGemini: false, provider: "grok", firstProviderFailure: firstGrokQuota);
                        }
                        catch (Exception grokEx)
                        {
                            LogPipeline("Grok also failed after Gemini quota — falling back to OCR");
                            await ShowQuotaFallbackBannerAsync();
                            row = await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(grokEx, ocrServerContext: false));
                        }
                    }
                    else if (selectedEngine == "gemini")
                    {
                        // Explicit engine — no automatic fallback. Show a clear
                        // quota message instead of a bare error row.
                        await ShowQuotaExceededBannerAsync(isGemini: true);
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                    else
                    {
                        // Auto mode, no Grok available. The user was already asked
                        // above (choice != Stop), so proceed with the OCR fallback.
                        await ShowQuotaFallbackBannerAsync();
                        row = await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                }
                catch (CloudApiException ex) when (ex.StatusCode.HasValue && IsPermanentCloudFailure(ex.StatusCode.Value))
                {
                    bool firstGeminiFailure = TryMarkGeminiDisabled(ErrorMessageTranslator.ToUserMessage(ex));
                    LogPipeline($"Gemini unavailable (HTTP {(int)ex.StatusCode.Value}) for {fileName}");

                    if (selectedEngine == "auto")
                    {
                        // High demand / overload / quota: ask the user before
                        // silently handing the file to local OCR.
                        QuotaDialogChoice choice = QuotaDialogChoice.ContinueWithOcr;
                        if (firstGeminiFailure && !_geminiOcrChosenForSession)
                            choice = await HandleQuotaExceededAsync(isGemini: true, "gemini");

                        if (choice == QuotaDialogChoice.Stop)
                        {
                            row = InvoiceRowViewModel.FromError(file, TranslationSource.Get("ExtractionStoppedByUser"));
                        }
                        else if (hasGrok && !GrokDisabled)
                        {
                            try
                            {
                                LogPipeline("HTTP request start (Grok fallback)");
                                InvoiceResult result = await CallGrokDirectlyAsync(file, grokKey!);
                                LogPipeline("Grok fallback succeeded");
                                row = InvoiceRowViewModel.FromSuccess(file, result);
                            }
                            catch (CloudQuotaExceededException grokEx)
                            {
                                bool firstGrokQuota = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(grokEx));
                                LogPipeline("Grok fallback also failed — trying OCR server");
                                row = await FallbackToOcrWithConsentAsync(file, ct, ErrorMessageTranslator.ToUserMessage(grokEx), isGemini: false, provider: "grok", firstProviderFailure: firstGrokQuota);
                            }
                            catch (CloudApiException grokEx) when (grokEx.StatusCode.HasValue && IsPermanentCloudFailure(grokEx.StatusCode.Value))
                            {
                                bool firstGrokQuota = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(grokEx));
                                LogPipeline("Grok fallback also failed — trying OCR server");
                                row = await FallbackToOcrWithConsentAsync(file, ct, ErrorMessageTranslator.ToUserMessage(grokEx), isGemini: false, provider: "grok", firstProviderFailure: firstGrokQuota);
                            }
                            catch (Exception grokEx)
                            {
                                LogPipeline("Grok fallback also failed — trying OCR server");
                                row = await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(grokEx, ocrServerContext: false));
                            }
                        }
                        else
                        {
                            LogPipeline("No Grok key — falling back to OCR server");
                            row = await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(ex));
                        }
                    }
                    else
                    {
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                }
                catch (CloudApiException ex)
                {
                    LogPipeline($"Cloud API error: {ex.Message}");
                    row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                }
                catch (Exception ex2)
                {
                    LogPipeline($"Gemini exception: {ex2.GetType().Name}: {ex2.Message}");
                    row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex2, ocrServerContext: false));
                }
            }
            else if (hasGrok && (selectedEngine == "auto" || selectedEngine == "grok") && !GrokDisabled)
            {
                LogPipeline($"Engine dispatch: Grok-first for {fileName}");
                try
                {
                    LogPipeline("HTTP request start (Grok)");
                    InvoiceResult result = await CallGrokDirectlyAsync(file, grokKey!);
                    LogPipeline("HTTP response received — success");
                    row = InvoiceRowViewModel.FromSuccess(file, result);
                }
                catch (CloudQuotaExceededException ex)
                {
                    bool firstGrokQuota = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(ex));
                    LogPipeline("Grok quota exceeded");

                    // Interactive dialog — shown once per batch per provider. The
                    // user chooses whether to continue with OCR, enter a new key,
                    // or stop (never silently fall back to OCR).
                    QuotaDialogChoice choice = QuotaDialogChoice.ContinueWithOcr;
                    if (firstGrokQuota && !_grokOcrChosenForSession)
                        choice = await HandleQuotaExceededAsync(isGemini: false, "grok");

                    if (choice == QuotaDialogChoice.Stop)
                    {
                        row = InvoiceRowViewModel.FromError(file, TranslationSource.Get("ExtractionStoppedByUser"));
                    }
                    else if (selectedEngine == "auto")
                    {
                        row = await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                    else
                    {
                        // Explicit engine — no automatic fallback. Show a clear
                        // quota message instead of a bare error row.
                        await ShowQuotaExceededBannerAsync(isGemini: false);
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                }
                catch (CloudApiException ex) when (ex.StatusCode.HasValue && IsPermanentCloudFailure(ex.StatusCode.Value))
                {
                    bool firstGrokFailure = TryMarkGrokDisabled(ErrorMessageTranslator.ToUserMessage(ex));
                    LogPipeline($"Grok unavailable (HTTP {(int)ex.StatusCode.Value}) for {fileName}");

                    if (selectedEngine == "auto")
                    {
                        QuotaDialogChoice choice = QuotaDialogChoice.ContinueWithOcr;
                        if (firstGrokFailure && !_grokOcrChosenForSession)
                            choice = await HandleQuotaExceededAsync(isGemini: false, "grok");

                        row = choice == QuotaDialogChoice.Stop
                            ? InvoiceRowViewModel.FromError(file, TranslationSource.Get("ExtractionStoppedByUser"))
                            : await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                    else
                    {
                        row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                    }
                }
                catch (CloudApiException ex)
                {
                    LogPipeline($"Grok API error: {ex.Message}");
                    row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                }
                catch (Exception ex2) when (selectedEngine == "auto")
                {
                    LogPipeline("Grok failed in auto mode — falling back to OCR server");
                    row = await ExtractViaServerAsync(file, ct, ErrorMessageTranslator.ToUserMessage(ex2, ocrServerContext: false));
                }
                catch (Exception ex2)
                {
                    LogPipeline($"Grok exception: {ex2.GetType().Name}: {ex2.Message}");
                    row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex2, ocrServerContext: false));
                }
            }
            else
            {
                LogPipeline($"Engine dispatch: OCR server for {fileName}");
                row = await ExtractViaServerAsync(file, ct);
            }
        }
        finally
        {
            stopwatch.Stop();
            LogPipeline($"Invoice completed: {fileName}");
            LogPipeline($"Invoice duration: {fileName} took {stopwatch.Elapsed.TotalSeconds:F2}s");
            await SetExtractionStatusAsync(string.Empty);
        }

        LogPipeline($"Row created: success={!row.HasError}, engine={row.EngineUsed}");
        return row;
    }

    private async Task StartExtractionAsync()
    {
        if (!CanStartExtraction()) return;

        LogPipeline("Extraction button clicked — starting pipeline");
        LogPipeline($"Selected engine: {SelectedEngine}");
        LogPipeline("Batch started");
        LogPipeline($"Concurrency level: {_batchConcurrency}");

        IsExtracting      = true;
        IsProgressVisible = true;
        ShowSummaryBanner = false;
        SaveConfirmationPath = null;
        ProcessedFiles    = 0;
        _extractionStatusText = TranslationSource.Get("ExtractionPreparing");
        OnPropertyChanged(nameof(ExtractionStatusText));
        _quotaFallbackBannerShown = false;
        _geminiDisabled = false;
        _grokDisabled = false;
        _geminiDisabledReason = string.Empty;
        _grokDisabledReason = string.Empty;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Results.Clear();
            IncompleteResults.Clear();
            _addedResultPaths.Clear();
        });
        NotifySummaryChanged();

        // Snapshot canonical paths once. File selection can be populated from a
        // folder drop, a multi-select dialog, or individual drops; equivalent
        // relative/absolute spellings must still represent one input file.
        string[] files = DistinctInputFilePaths(
                DetectedFiles.Where(f => f.IsSelected).Select(f => f.FilePath))
            .ToArray();
        TotalFiles = files.Length;
        LogPipeline($"Input file count: {files.Length}");

        if (files.Length == 0)
        {
            LogPipeline("ERROR: No files selected — aborting extraction");
            ShowImmediateError(TranslationSource.Get("ExtractionNoFiles"));
            return;
        }

        _extractionCts = new CancellationTokenSource();

        // ── Decide extraction strategy ──
        LogPipeline("Pre-processing: loading keys and checking internet");
        string? geminiKey = LoadGeminiApiKey();
        string? grokKey = LoadGrokApiKey();
        _internetAvailable = await CheckInternetAsync();
        bool hasGemini = _internetAvailable && !string.IsNullOrEmpty(geminiKey);
        bool hasGrok = _internetAvailable && !string.IsNullOrEmpty(grokKey);

        LogPipeline($"Internet: {_internetAvailable}, Gemini key: {!string.IsNullOrEmpty(geminiKey)}, Grok key: {!string.IsNullOrEmpty(grokKey)}");

        string selectedEngine = SelectedEngine;
        LogPipeline($"Engine dispatch: selected={selectedEngine}, gemini={hasGemini}, grok={hasGrok}");

        // Determine if we need the server NOW (for OCR or fallback)
        bool needsServerNow = selectedEngine == "ocr"
            || (selectedEngine == "auto" && !hasGemini && !hasGrok)
            || (selectedEngine == "gemini" && !hasGemini)
            || (selectedEngine == "grok" && !hasGrok);

        // ── Start server in background ONLY if it may actually be needed ──
        // Explicit cloud engines (gemini/grok with a valid key) never fall back
        // to OCR, so the server must NOT be started (or its status shows
        // "Starting..." even though nothing uses it). Auto mode with available
        // cloud engines also skips pre-start — the lazy fallback path in
        // ProcessInvoiceAsync calls EnsureServerReadyAsync() on demand when
        // OCR is actually required.  OCR fallback paths also lazily ensure
        // the server via EnsureServerReadyAsync().
        Task? serverTask = null;
        bool serverMayBeNeeded = needsServerNow;
        if (serverMayBeNeeded)
        {
            LogPipeline("PRE-FLIGHT: Starting server in background for potential fallback");
            serverTask = Task.Run(async () =>
            {
                try
                {
                    await EnsureServerReadyAsync();
                }
                catch (Exception ex)
                {
                    LogPipeline($"Background server startup failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
        else
        {
            LogPipeline("PRE-FLIGHT: Cloud API available — server not needed, skipping startup");
        }

        if (needsServerNow)
        {
            LogPipeline("PRE-FLIGHT: Server needed for extraction — waiting for it");
            try
            {
                // Wait for the background server to be ready
                if (serverTask != null)
                    await serverTask;

                // ── Pre-flight health check ──
                using var healthResponse = await _apiHttpClient.GetAsync(
                    "http://127.0.0.1:8000/health",
                    _extractionCts.Token);
                if (!healthResponse.IsSuccessStatusCode)
                {
                    IsServerRunning = false;
                    throw new InvalidOperationException(
                        TranslationSource.Get("ServerHealthCheckFailed"));
                }
            }
            catch (InvalidOperationException)
            {
                _extractionCts?.Dispose();
                _extractionCts = null;
                IsExtracting = false;
                IsProgressVisible = false;
                _extractionStatusText = string.Empty;
                OnPropertyChanged(nameof(ExtractionStatusText));
                OnPropertyChanged(nameof(HasErrors));
                SummaryBannerText = TranslationSource.Get("ServerHealthCheckFailed");
                SummaryBannerColor = "#C0392B";
                ShowSummaryBanner = true;
                return;
            }
            catch (Exception ex)
            {
                LogPipeline($"Server startup failed — aborting batch: {ex.GetType().Name}: {ex.Message}");
                IsServerRunning = false;
                _extractionCts?.Dispose();
                _extractionCts = null;
                IsExtracting = false;
                IsProgressVisible = false;
                _extractionStatusText = string.Empty;
                OnPropertyChanged(nameof(ExtractionStatusText));
                OnPropertyChanged(nameof(HasErrors));
                SummaryBannerText = ErrorMessageTranslator.ToUserMessage(ex);
                SummaryBannerColor = "#C0392B";
                ShowSummaryBanner = true;
                return;
            }
        }
        else
        {
            LogPipeline("PRE-FLIGHT: Cloud API available — server not needed, skipping startup");
        }

        var batchStopwatch = Stopwatch.StartNew();

        try
        {
            var semaphore = new SemaphoreSlim(_batchConcurrency, _batchConcurrency);
            try
            {
                Task[] tasks = files.Select(async file =>
                {
                    bool entered = false;
                    try
                    {
                        var extractionCts = _extractionCts;
                        if (extractionCts == null) return;

                        await semaphore.WaitAsync(extractionCts.Token);
                        entered = true;

                        if (extractionCts.Token.IsCancellationRequested)
                            return;

                        var extractionToken = _extractionCts?.Token ?? CancellationToken.None;
                        InvoiceRowViewModel row;
                        try
                        {
                            row = await ProcessInvoiceAsync(
                                file,
                                selectedEngine,
                                geminiKey,
                                grokKey,
                                hasGemini,
                                hasGrok,
                                extractionToken);
                        }
                        catch (OperationCanceledException)
                        {
                            LogPipeline("Invoice task cancelled by user");
                            return;
                        }
                        catch (Exception ex)
                        {
                            LogPipeline($"Invoice task failed: {ex.GetType().Name}: {ex.Message}");
                            row = InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
                        }

                        // Keep result insertion outside the extraction catch. If a
                        // UI/view notification fails after a row was inserted, the
                        // old broad catch created a second error row for the same
                        // file. One input path must have one final result row.
                        await AddExtractionResultAsync(row);
                        LogPipeline("UI update triggered — ObservableCollection updated");
                    }
                    finally
                    {
                        if (entered)
                            semaphore.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            finally
            {
                semaphore.Dispose();
            }

            batchStopwatch.Stop();
            LogPipeline("Batch completed");
            LogPipeline($"Total duration: {batchStopwatch.Elapsed.TotalSeconds:F2}s");
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

    /// <summary>Long timeout for OCR extraction calls (15 min).  OCR is serialized
    /// server-side by an asyncio.Semaphore(1), so the 4th concurrent file may wait
    /// ~6 minutes in the server queue before processing even starts.
    /// HttpClient.Timeout (5 min) is too short for that scenario.
    /// We create a linked token with a longer timeout instead of relying on the
    /// shared HttpClient's timeout.</summary>
    private static readonly TimeSpan OcrExtractionTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Extract a file through the local OCR server (starts it lazily if needed).</summary>
    private async Task<InvoiceRowViewModel> ExtractViaServerAsync(
        string file,
        CancellationToken ct = default,
        string? cloudFallbackReason = null)
    {
        LogPipeline("OCR started");
        CancellationTokenSource? linkedCts = null;
        try
        {
            // Lazy server ensure: the server may not have been pre-started
            // (cloud-only batch), so make sure it is up before calling OCR.
            await EnsureServerReadyAsync();

            // Create a linked token that includes the passed cancellation token
            // and a longer timeout for the OCR call.
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(OcrExtractionTimeout);

            InvoiceResult result = await _invoiceClient.ExtractAsync(file, "ocr", linkedCts.Token);
            LogPipeline("OCR completed");
            var row = InvoiceRowViewModel.FromSuccess(file, result);
            row.GeminiFallbackReason = cloudFallbackReason;
            return row;
        }
        catch (OperationCanceledException) when (
            _extractionCts?.Token.IsCancellationRequested == true)
        {
            // Real user cancellation — propagate up to the pipeline
            LogPipeline("OCR cancelled by user");
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout (either the linked CTS timed out or HttpClient.Timeout fired)
            LogPipeline("OCR completed (timeout)");
            return InvoiceRowViewModel.FromError(file,
                TranslationSource.Fmt("ErrorTimeout", (int)OcrExtractionTimeout.TotalSeconds));
        }
        catch (InvoiceExtractionException ex)
        {
            LogPipeline("OCR completed (error)");
            return InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
        }
        catch (Exception ex)
        {
            LogPipeline($"OCR completed (exception: {ex.GetType().Name})");
            return InvoiceRowViewModel.FromError(file, ErrorMessageTranslator.ToUserMessage(ex));
        }
        finally
        {
            linkedCts?.Dispose();
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
            // Server readiness is ensured by the batch-level call before the loop.
            InvoiceResult result = await _invoiceClient.ExtractAsync(filePath, SelectedEngine);
            return InvoiceRowViewModel.FromSuccess(filePath, result);
        }
        catch (InvoiceExtractionException ex)
        {
            return InvoiceRowViewModel.FromError(filePath, ErrorMessageTranslator.ToUserMessage(ex));
        }
        catch (Exception ex)
        {
            return InvoiceRowViewModel.FromError(filePath, ErrorMessageTranslator.ToUserMessage(ex));
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

    /// <summary>
    /// Rerun a single row (called from context menu). Guards against concurrent extraction.
    /// </summary>
    private async Task RerunRowAsync(InvoiceRowViewModel? row)
    {
        if (row is null || IsExtracting) return;
        await RerunRowCoreAsync(row);
    }

    /// <summary>
    /// Rerun a single row without guard (called from RerunAllErrorsAsync where IsExtracting is already set).
    /// </summary>
    private async Task RerunRowCoreAsync(InvoiceRowViewModel row)
    {
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

        _extractionCts = new CancellationTokenSource();

        try
        {
            foreach (var row in errorRows)
            {
                if (_extractionCts.Token.IsCancellationRequested)
                {
                    LogPipeline("Rerun errors cancelled by user");
                    break;
                }

                string fileName = row.FileName;
                _extractionStatusText = TranslationSource.Fmt("ExtractionProcessing", fileName);
                OnPropertyChanged(nameof(ExtractionStatusText));

                await RerunRowCoreAsync(row);

                ProcessedFiles += 1;
                NotifySummaryChanged();
            }
        }
        finally
        {
            _extractionCts?.Dispose();
            _extractionCts = null;
            IsExtracting = false;
            IsProgressVisible = false;
            _extractionStatusText = string.Empty;
            OnPropertyChanged(nameof(ExtractionStatusText));
            OnPropertyChanged(nameof(HasErrors));
            (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            ShowExtractionSummary();
        }
    }

    private void ShowExtractionSummary()
    {
        int errors     = Results.Count(r => r.HasError);
        int incomplete = IncompleteResults.Count(r => !r.HasError);
        int success    = Results.Count - errors - incomplete;

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
        var baseRows = anySelected ? Results.Where(r => r.IsSelected).ToList() : Results.ToList();
        string defaultDir = Directory.Exists(SelectedFolder) ? SelectedFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // ── Show export dialog with filter + destination options ──
        var exportDialog = new global::Hotix.InvoiceClient.ExportDialog();
        exportDialog.Owner = Application.Current.MainWindow;
        bool? dialogResult = exportDialog.ShowDialog();

        if (dialogResult != true) return;

        // ── Apply filter ──
        var rowsToExport = exportDialog.SelectedFilter switch
        {
            global::Hotix.InvoiceClient.ExportDialog.FilterMode.ResultsOnly =>
                baseRows.Where(r => !r.IsIncomplete).ToList(),
            global::Hotix.InvoiceClient.ExportDialog.FilterMode.MissingOnly =>
                baseRows.Where(r => r.IsIncomplete).ToList(),
            _ => baseRows.ToList(), // Both
        };

        bool markMissing = exportDialog.SelectedFilter == global::Hotix.InvoiceClient.ExportDialog.FilterMode.Both;
        bool includeItems = exportDialog.IncludeItemsInExport;

        if (rowsToExport.Count == 0)
        {
            MessageBox.Show(
                TranslationSource.Get("ExportNoRows"),
                TranslationSource.Get("ExportTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (exportDialog.SelectedDestination == global::Hotix.InvoiceClient.ExportDialog.DestinationMode.CreateNew)
        {
            // ── Option A: Create new workbook ──
            var saveDialog = new SaveFileDialog
            {
                Filter           = TranslationSource.Get("ExportExcelFilter"),
                FileName         = TranslationSource.Fmt("ExportFileName", DateTime.Today.ToString("yyyy-MM-dd")),
                InitialDirectory = defaultDir,
                Title            = TranslationSource.Get("ExportDialogTitle"),
            };

            if (saveDialog.ShowDialog() != true) return;

            try
            {
                new ExcelWriter().Write(saveDialog.FileName, rowsToExport, markMissing, includeItems);
                SaveConfirmationPath = saveDialog.FileName;
            }
            catch (Exception ex)
            {
                ShowExportError(ex);
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
            List<string> sheetNames;
            try
            {
                sheetNames = ExcelWriter.GetWorksheetNames(existingPath);
            }
            catch (Exception ex)
            {
                ShowExportError(ex);
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
                new ExcelWriter().AppendToExisting(existingPath, rowsToExport, targetSheet, markMissing, includeItems);
                SaveConfirmationPath = existingPath;
            }
            catch (Exception ex)
            {
                ShowExportError(ex);
                return;
            }
        }
    }

    /// <summary>Shows a user-facing message for any export failure. File-lock
    /// errors keep their specific "open in another app" hint; schema drift gets
    /// its own message; anything else goes through the translator. The export
    /// path never crashes the app — a failed export is recoverable.</summary>
    private static void ShowExportError(Exception ex)
    {
        string msg = ex switch
        {
            IOException => TranslationSource.Fmt("ExportErrorFileOpen", ex.Message),
            ExcelSchemaMismatchException => TranslationSource.Get("ExportSchemaMismatch"),
            _ => ErrorMessageTranslator.ToUserMessage(ex),
        };
        MessageBox.Show(msg, TranslationSource.Get("ExportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // Confirm before destructive action
        var result = MessageBox.Show(
            TranslationSource.Fmt("ClearConfirmMessage", $"{Results.Count} résultat(s)"),
            TranslationSource.Get("ClearConfirmTitle") ?? "Effacer les résultats",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        Results.Clear();
        IncompleteResults.Clear();
        _addedResultPaths.Clear();
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

    /// <summary>
    /// Adds validated file paths to the DetectedFiles collection with deduplication.
    /// Extracted from BrowseFiles() so drag-and-drop can reuse the same logic.
    /// </summary>
    public void AddValidatedFilePaths(IEnumerable<string> filePaths)
    {
        foreach (string rawFile in filePaths.OrderBy(f => f))
        {
            string file = NormalizeFilePathForComparison(rawFile);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                continue;
            if (DetectedFiles.Any(f => string.Equals(
                    NormalizeFilePathForComparison(f.FilePath), file,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            var item = new FileItemViewModel(file);
            item.PropertyChanged += OnFileItemPropertyChanged;
            DetectedFiles.Add(item);
        }
        NotifyFileCountChanged();
        RaiseCommandStateChanged();
    }

    /// <summary>
    /// Returns a stable absolute path for file identity comparisons. This is
    /// deliberately path-based rather than invoice-number-based: two different
    /// files may contain the same invoice number, but one file must produce one
    /// extraction task and one result row.
    /// </summary>
    internal static string NormalizeFilePathForComparison(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        string fullPath = Path.GetFullPath(filePath);
        string? root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// De-duplicates selected input paths by canonical file identity while
    /// preserving their first-seen order. Invoice numbers are intentionally not
    /// part of this key: different files may legitimately share one number.
    /// </summary>
    internal static IEnumerable<string> DistinctInputFilePaths(IEnumerable<string> filePaths)
        => filePaths
            .Select(NormalizeFilePathForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Handles a folder being dropped via drag-and-drop (append mode).
    /// Non-destructively adds all supported files from the folder to the existing list,
    /// unlike BrowseFolder() which deliberately clears and replaces for the "choose source" action.
    /// </summary>
    public void SetFolderFromDrop(string folder)
    {
        SelectedFolder = folder;

        if (!Directory.Exists(folder))
            return;

        var files = Directory
            .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

        AddValidatedFilePaths(files);
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
        // Cancel only — disposal is owned by the per-batch finally block
        // to avoid ObjectDisposedException racing with background tasks
        // that may still be reading _extractionCts.Token.
        _extractionCts?.Cancel();
        _apiHttpClient.Dispose();
        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        _previewLoadCts = null;
        _previewImageCache.Clear();
        _httpQuickClient.Dispose();
        _httpShortClient.Dispose();
        _httpCloudClient.Dispose();
        // Request gates are intentionally not disposed here: extraction tasks
        // may still be unwinding after cancellation, and disposing a semaphore
        // while a waiter is active would turn normal shutdown into an exception.
    }
}

// ── Custom Exceptions ─────────────────────────────────────────────────────

internal sealed class CloudQuotaExceededException : Exception
{
    public CloudQuotaExceededException(string message, HttpStatusCode? statusCode = null, string? responseBody = null) : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }
}

internal sealed class CloudApiException : Exception
{
    public CloudApiException(string message, HttpStatusCode? statusCode = null, string? responseBody = null) : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }
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
