using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

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
    private readonly DispatcherTimer _healthTimer;

    private string _selectedFolder = string.Empty;
    private bool _isServerHealthy;
    private bool _isExtracting;
    private bool _isProgressVisible;
    private int _processedFiles;
    private int _totalFiles;
    private bool _allFilesSelected = true;
    private bool _allRowsSelected;
    private string? _saveConfirmationPath;
    private bool _showSummaryBanner;
    private string _summaryBannerText = string.Empty;
    private string _summaryBannerColor = "#2ECC71";
    private InvoiceRowViewModel? _selectedRow;
    private CancellationTokenSource? _extractionCts;

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
        StartExtractionCommand = new RelayCommand(async _ => await StartExtractionAsync(), _ => CanStartExtraction());
        CancelExtractionCommand = new RelayCommand(_ => CancelExtraction(), _ => IsExtracting);
        ExportExcelCommand     = new RelayCommand(_ => ExportExcel(), _ => CanExport());
        ClearCommand           = new RelayCommand(_ => ClearResults(), _ => CanClear());
        RerunCommand           = new RelayCommand(async p => await RerunRowAsync(p as InvoiceRowViewModel));
        RerunAllErrorsCommand  = new RelayCommand(async _ => await RerunAllErrorsAsync(), _ => Results.Any(r => r.HasError) && !IsExtracting);
        ToggleAllFilesCommand  = new RelayCommand(_ => ToggleAllFiles());
        ToggleAllRowsCommand   = new RelayCommand(_ => ToggleAllRows());
        OpenSavedFolderCommand = new RelayCommand(_ => OpenSavedFolder(), _ => _saveConfirmationPath != null);

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _healthTimer.Tick += async (_, _) => await CheckServerHealthAsync();

        LoadSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileItemViewModel>   DetectedFiles     { get; }
    public ObservableCollection<InvoiceRowViewModel> Results           { get; }
    public ObservableCollection<InvoiceRowViewModel> IncompleteResults { get; }

    public ICommand BrowseFolderCommand     { get; }
    public ICommand StartExtractionCommand  { get; }
    public ICommand CancelExtractionCommand { get; }
    public ICommand ExportExcelCommand      { get; }
    public ICommand ClearCommand            { get; }
    public ICommand RerunCommand            { get; }
    public ICommand RerunAllErrorsCommand   { get; }
    public ICommand ToggleAllFilesCommand   { get; }
    public ICommand ToggleAllRowsCommand    { get; }
    public ICommand OpenSavedFolderCommand  { get; }

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

    public bool HasSelectedFolder => !string.IsNullOrWhiteSpace(SelectedFolder);

    public bool IsServerHealthy
    {
        get => _isServerHealthy;
        private set => SetField(ref _isServerHealthy, value);
    }

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

    public string ProgressText       => $"{ProcessedFiles} / {TotalFiles} fichiers traités";
    public double ProgressPercentage => TotalFiles == 0 ? 0.0 : (double)ProcessedFiles / TotalFiles * 100.0;
    public string SummaryText        => $"{Results.Count} factures traitées — {IncompleteResults.Count} incomplètes";

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
            return $"{total} fichier{(total != 1 ? "s" : "")} détecté{(total != 1 ? "s" : "")}, {selected} sélectionné{(selected != 1 ? "s" : "")}";
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
        ? $"Fichier enregistré : {_saveConfirmationPath}"
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
    public string  PreviewFileName => _selectedRow != null ? $"Texte brut OCR — {_selectedRow.FileName}" : "Texte brut OCR";

    public bool HasErrors => Results.Any(r => r.HasError);

    public async Task InitializeAsync()
    {
        await CheckServerHealthAsync();
        _healthTimer.Start();
    }

    private async Task CheckServerHealthAsync()
    {
        try
        {
            using HttpResponseMessage response = await _apiHttpClient.GetAsync("/health");
            IsServerHealthy = response.IsSuccessStatusCode;
        }
        catch
        {
            IsServerHealthy = false;
        }
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Sélectionner le dossier des factures",
            InitialDirectory = Directory.Exists(SelectedFolder)
                ? SelectedFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (!dialog.ShowDialog().GetValueOrDefault()) return;

        SelectedFolder = dialog.FolderName;
        SaveSettings();
        LoadDetectedFiles();
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
            foreach (string file in files)
            {
                if (_extractionCts.Token.IsCancellationRequested) break;

                InvoiceRowViewModel row;
                try
                {
                    InvoiceResult result = await _invoiceClient.ExtractAsync(file, _extractionCts.Token);
                    row = InvoiceRowViewModel.FromSuccess(file, result);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvoiceExtractionException ex)
                {
                    row = InvoiceRowViewModel.FromError(file, MapErrorMessage(ex));
                }
                catch (Exception ex)
                {
                    row = InvoiceRowViewModel.FromError(file, ex.Message);
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
            OnPropertyChanged(nameof(HasErrors));
            (RerunAllErrorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            ShowExtractionSummary();
        }
    }

    private void CancelExtraction()
    {
        _extractionCts?.Cancel();
    }

    private async Task RerunRowAsync(InvoiceRowViewModel? row)
    {
        if (row is null || IsExtracting) return;

        string filePath = Path.Combine(SelectedFolder, row.FileName);
        if (!File.Exists(filePath)) return;

        InvoiceRowViewModel updated;
        try
        {
            InvoiceResult result = await _invoiceClient.ExtractAsync(filePath);
            updated = InvoiceRowViewModel.FromSuccess(filePath, result);
        }
        catch (InvoiceExtractionException ex)
        {
            updated = InvoiceRowViewModel.FromError(filePath, MapErrorMessage(ex));
        }
        catch (Exception ex)
        {
            updated = InvoiceRowViewModel.FromError(filePath, ex.Message);
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
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
        });

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
            return "Format non supporté";

        if (ex.StatusCode == System.Net.HttpStatusCode.InternalServerError)
        {
            if (ex.ResponseBody.Contains("poppler", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("pdfinfo", StringComparison.OrdinalIgnoreCase)
                || ex.ResponseBody.Contains("pdftoppm", StringComparison.OrdinalIgnoreCase))
                return "Poppler manquant — PDF non supporté";

            return "Erreur serveur OCR";
        }

        return "Erreur serveur OCR";
    }

    private void ShowExtractionSummary()
    {
        int errors     = Results.Count(r => r.HasError);
        int incomplete = IncompleteResults.Count(r => !r.HasError);
        int success    = Results.Count - errors;

        SummaryBannerText  = $"Extraction terminée — {success} réussies, {incomplete} incomplètes, {errors} erreurs. Vérifiez les résultats avant d'exporter.";
        SummaryBannerColor = errors > 0 ? "#C0392B" : incomplete > 0 ? "#E67E22" : "#2ECC71";
        ShowSummaryBanner  = true;
    }

    private bool CanExport() => Results.Count > 0 && !IsExtracting;

    private void ExportExcel()
    {
        if (!CanExport()) return;

        string defaultDir  = Directory.Exists(SelectedFolder) ? SelectedFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var saveDialog = new SaveFileDialog
        {
            Filter           = "Classeur Excel (*.xlsx)|*.xlsx",
            FileName         = $"Hotix_Export_{DateTime.Today:yyyy-MM-dd}.xlsx",
            InitialDirectory = defaultDir,
            Title            = "Exporter les résultats en Excel",
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
        catch { }
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
        _healthTimer.Stop();
        _extractionCts?.Cancel();
        _extractionCts?.Dispose();
        _apiHttpClient.Dispose();
    }
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
