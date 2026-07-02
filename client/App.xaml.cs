using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Sentry;

namespace Hotix.InvoiceClient;

public partial class App : Application
{
    public static Process? ServerProcess { get; private set; }
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1) };
public App()
{
    SentrySdk.Init(o =>
    {
        o.Dsn = "https://154c8274aa22e3a02b159304b92a5df6@o4511656088567808.ingest.de.sentry.io/4511656096497744";
    });
}
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handling
        AppDomain.CurrentDomain.UnhandledException += (s, args) => HandleGlobalException((Exception)args.ExceptionObject);
        DispatcherUnhandledException += (s, args) => { HandleGlobalException(args.Exception); args.Handled = true; };

        // 1. Path Discovery
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string pythonPath = FindFile(new[]
        {
            Path.Combine(appDir, "venv", "Scripts", "python.exe"),
            @"C:\hotix-invoice\venv\Scripts\python.exe"
        });

        if (string.IsNullOrEmpty(pythonPath))
        {
            MessageBox.Show("L'environnement Python est introuvable. Veuillez relancer l'installation.", "Erreur Fatale", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        string mainPyPath = FindFile(new[]
        {
            Path.Combine(appDir, "server", "main.py"),
            Path.Combine(appDir, "..", "server", "main.py"),
            @"C:\hotix-invoice\server\main.py"
        });

        if (string.IsNullOrEmpty(mainPyPath))
        {
            MessageBox.Show("Le serveur OCR est introuvable. Veuillez relancer l'installation.", "Erreur Fatale", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        // 2. Start Python Server
        try
        {
            string workingDir = Path.GetDirectoryName(mainPyPath)!;
            // If main.py is inside a 'server' folder, the root is one level up
            if (workingDir.EndsWith("server", StringComparison.OrdinalIgnoreCase))
                workingDir = Path.GetDirectoryName(workingDir)!;

            ServerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"-m uvicorn server.main:app --host 127.0.0.1 --port 8000",
                    WorkingDirectory = workingDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            ServerProcess.Start();
            
            // Cleanup on exit
            AppDomain.CurrentDomain.ProcessExit += (s, args) => CleanupServer();
            Exit += (s, args) => CleanupServer();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Échec du démarrage du serveur : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        // 3. Wait for Server with Splash
        var splash = new SplashScreen();
        splash.Show();

        bool healthy = false;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            splash.StatusMessage = GetStatusMessage(stopwatch.Elapsed.TotalSeconds);
            try
            {
                using var response = await _httpClient.GetAsync("http://127.0.0.1:8000/health");
                if (response.IsSuccessStatusCode)
                {
                    healthy = true;
                    break;
                }
            }
            catch { /* polling */ }
            await Task.Delay(500);
        }

        if (healthy)
        {
            splash.StatusMessage = "Connexion établie";
            await Task.Delay(500);
            splash.Close();

            // First-run wizard check
            var mainVm = new ViewModels.MainViewModel();
            await mainVm.InitializeAsync(); // Load initial balance/health

            if (IsFirstRun())
            {
                 var wizard = new GeminiSetupWindow { DataContext = mainVm };
                 wizard.ShowDialog();
            }

            var mainWindow = new MainWindow();
            mainWindow.DataContext = mainVm;
            Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
        }
        else
        {
            CleanupServer();
            MessageBox.Show("Le serveur OCR n'a pas pu démarrer dans le délai imparti. Vérifiez les logs.", "Délai Dépassé", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private string GetStatusMessage(double elapsedSeconds)
    {
        if (elapsedSeconds < 5) return "Démarrage du serveur OCR...";
        if (elapsedSeconds < 15) return "Chargement des modèles...";
        return "Presque prêt...";
    }

    private string FindFile(string[] paths) => paths.FirstOrDefault(File.Exists) ?? string.Empty;

    private void CleanupServer()
    {
        if (ServerProcess == null || ServerProcess.HasExited) return;
        try
        {
            ServerProcess.Kill();
            ServerProcess.WaitForExit(3000);
        }
        catch { /* ignored */ }
    }

    private void HandleGlobalException(Exception ex)
    {
        SentrySdk.CaptureException(ex);
        CleanupServer();
        MessageBox.Show($"Une erreur inattendue est survenue : {ex.Message}", "Erreur Système", MessageBoxButton.OK, MessageBoxImage.Error);
        Current.Shutdown();
    }

    private bool IsFirstRun()
    {
        try
        {
            string appSettingsPath = ViewModels.MainViewModel.ResolveAppSettingsPath();
            if (!File.Exists(appSettingsPath)) return true;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appSettingsPath));
            if (doc.RootElement.TryGetProperty("gemini_api_key", out var el))
                return string.IsNullOrWhiteSpace(el.GetString());
            return true;
        }
        catch { return true; }
    }
}
