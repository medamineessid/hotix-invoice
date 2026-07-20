using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

namespace HotixDiagnostics;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { await RunAllChecksAsync(); }
            catch (Exception ex) { ShowError(ex); }
        };
        Closed += (_, _) => _httpClient.Dispose();
    }

    private void ShowError(Exception ex)
    {
        try
        {
            StatusSummary.Text = $"Erreur : {ex.Message}";
        }
        catch { /* best-effort */ }
    }

    private async Task RunAllChecksAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusSummary.Text = "Vérification en cours...";
        CheckList.ItemsSource = null;

        var results = new List<CheckResult>();

        // Run all 3 checks in parallel
        var tasks = new[]
        {
            CheckPopplerAsync(),
            CheckVenvAsync(),
            CheckServerAsync(),
        };

        var checkResults = await Task.WhenAll(tasks);

        int passed = checkResults.Count(r => r.Status == CheckStatus.Passed);
        int total = checkResults.Length;

        foreach (var result in checkResults)
            results.Add(result);

        CheckList.ItemsSource = results;
        StatusSummary.Text = $"{passed}/{total} vérifications réussies";
        RefreshButton.IsEnabled = true;
    }

    private async Task<CheckResult> CheckPopplerAsync()
    {
        var result = new CheckResult
        {
            CheckName = "Poppler (PDF support)",
            Details = "",
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pdfinfo",
                Arguments = "-v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Poppler's -v goes to stderr, not stdout
            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Status = CheckStatus.Failed;
                result.Message = "Impossible de lancer pdfinfo";
                return result;
            }

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                // Extract version from first line: "pdfinfo version X.Y.Z"
                var firstLine = stderr.Split('\n')[0].Trim();
                result.Status = CheckStatus.Passed;
                result.Message = firstLine;
                result.Details = stderr.Trim();
            }
            else
            {
                result.Status = CheckStatus.Failed;
                result.Message = "pdfinfo introuvable — Poppler n'est pas dans le PATH";
                result.Details = $"Exit code: {process.ExitCode}\n{stderr.Trim()}";
            }
        }
        catch (Win32Exception)
        {
            result.Status = CheckStatus.Failed;
            result.Message = "pdfinfo introuvable — Poppler n'est pas installé ou n'est pas dans le PATH";
            result.Details = "Assurez-vous que Poppler est installé et que le dossier bin est ajouté au PATH système.";
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Failed;
            result.Message = $"Erreur inattendue : {ex.Message}";
            result.Details = ex.ToString();
        }

        return result;
    }

    private async Task<CheckResult> CheckVenvAsync()
    {
        var result = new CheckResult
        {
            CheckName = "Environnement Python (venv)",
            Details = "",
        };

        try
        {
            // Diagnostics lives at {app}\diagnostics\HotixDiagnostics.exe
            // Venv lives at {app}\venv\Scripts\python.exe
            var diagDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var appDir = Path.GetFullPath(Path.Combine(diagDir ?? "", ".."));
            var venvPython = Path.Combine(appDir, "venv", "Scripts", "python.exe");

            if (!File.Exists(venvPython))
            {
                result.Status = CheckStatus.Failed;
                result.Message = "Environnement virtuel introuvable";
                result.Details = $"Chemin vérifié : {venvPython}";
                return result;
            }

            // Verify pip works
            var psi = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-m pip list --format=columns 2>&1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Status = CheckStatus.Warning;
                result.Message = $"venv trouvé mais pip injoignable : {venvPython}";
                return result;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                result.Status = CheckStatus.Warning;
                result.Message = $"venv trouvé mais pip ne répond pas (code {process.ExitCode})";
                result.Details = stderr.Trim();
                return result;
            }

            // Check for critical packages
            var required = new[] { "paddleocr", "fastapi", "uvicorn", "paddlepaddle" };
            var missing = required.Where(pkg => !output.Contains(pkg, StringComparison.OrdinalIgnoreCase)).ToList();

            if (missing.Count == 0)
            {
                result.Status = CheckStatus.Passed;
                result.Message = $"Tous les paquets installés ({venvPython})";
                result.Details = output.Trim();
            }
            else
            {
                result.Status = CheckStatus.Warning;
                result.Message = $"Paquets manquants : {string.Join(", ", missing)}";
                result.Details = output.Trim();
            }
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Failed;
            result.Message = $"Erreur : {ex.Message}";
            result.Details = ex.ToString();
        }

        return result;
    }

    private async Task<CheckResult> CheckServerAsync()
    {
        var result = new CheckResult
        {
            CheckName = "Serveur OCR (port 8000)",
            Details = "",
        };

        try
        {
            using var response = await _httpClient.GetAsync("http://127.0.0.1:8000/health");

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                result.Status = CheckStatus.Passed;
                result.Message = "Serveur OCR opérationnel (HTTP " + (int)response.StatusCode + ")";
                result.Details = $"Response: {body.Trim()}";
            }
            else
            {
                result.Status = CheckStatus.Warning;
                result.Message = "Serveur OCR joint mais réponse inattendue (HTTP " + (int)response.StatusCode + ")";
                result.Details = await response.Content.ReadAsStringAsync();
            }
        }
        catch (HttpRequestException)
        {
            result.Status = CheckStatus.Warning;
            result.Message = "Serveur OCR non joignable — peut être normal si l'application n'est pas lancée";
            result.Details = "Le serveur est généralement démarré automatiquement lors du lancement de l'application Hotix.";
        }
        catch (TaskCanceledException)
        {
            result.Status = CheckStatus.Warning;
            result.Message = "Serveur OCR injoignable après 10s — peut être normal si l'application n'est pas lancée";
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Failed;
            result.Message = $"Erreur : {ex.Message}";
            result.Details = ex.ToString();
        }

        return result;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try { await RunAllChecksAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public enum CheckStatus
{
    Pending,
    Passed,
    Warning,
    Failed,
}

public class CheckResult : INotifyPropertyChanged
{
    private CheckStatus _status;

    public string CheckName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    public CheckStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(DetailIndicator));
            }
        }
    }

    public string StatusColor => Status switch
    {
        CheckStatus.Passed => "#2ECC71",
        CheckStatus.Warning => "#F39C12",
        CheckStatus.Failed => "#E74C3C",
        _ => "#95A5A6",
    };

    public string StatusIcon => Status switch
    {
        CheckStatus.Passed => "✓",
        CheckStatus.Warning => "⚠",
        CheckStatus.Failed => "✕",
        _ => "?",
    };

    public string DetailIndicator => string.IsNullOrEmpty(Details) ? "" : "⋯";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
