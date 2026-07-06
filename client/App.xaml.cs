using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Sentry;

namespace Hotix.InvoiceClient;

public partial class App : Application
{
    public static Process? ServerProcess { get; private set; }
    private static string? _pythonPath;
    private static string? _workingDir;
    private static readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

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

        try

        {
            await StartupAsync();
        }
        catch (Exception ex)
        {
            HandleGlobalException(ex);
        }
    }

    private async Task StartupAsync()
    {
        try
        {
            await StartupCoreAsync();
        }
        catch (Exception ex)
        {
            // Log crash with basic System.IO only (no TranslationSource dependency)
            try
            {
                File.WriteAllText(@"C:\hotix-invoice\crash.log",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            }
            catch { }
            throw; // re-throw to be caught by HandleGlobalException
        }
    }

    private async Task StartupCoreAsync()
    {
        // 1. Path Discovery — find Python and server/main.py for later lazy start
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        _pythonPath = FindFile(new[]
        {
            Path.Combine(appDir, "venv", "Scripts", "python.exe"),
            @"C:\hotix-invoice\venv\Scripts\python.exe"
        });

        if (string.IsNullOrEmpty(_pythonPath))
        {
            MessageBox.Show(TranslationSource.Get("ErrorPythonNotFound"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(TranslationSource.Get("ErrorServerNotFound"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        _workingDir = Path.GetDirectoryName(mainPyPath)!;
        // If main.py is inside a 'server' folder, the root is one level up
        if (_workingDir.EndsWith("server", StringComparison.OrdinalIgnoreCase))
            _workingDir = Path.GetDirectoryName(_workingDir)!;

        // Cleanup on exit — safe to register even if server never starts
        AppDomain.CurrentDomain.ProcessExit += (s, args) => CleanupServer();
        Exit += (s, args) => CleanupServer();

        // 2. Show splash — stays open during initialization to keep the application alive
        //    (Closing the only window triggers OnLastWindowClose shutdown prematurely)
        var splash = new SplashScreen();
        splash.Show();
        splash.StatusMessage = TranslationSource.Get("SplashStatus");
        var splashStart = DateTime.UtcNow;

        // 3. Create ViewModel and check first-run (splash is still visible)
        var mainVm = new ViewModels.MainViewModel();
        await mainVm.InitializeAsync();

        // No longer shows the Gemini setup dialog on first run — it would
        // block the user from using the application. The user can configure
        // Gemini at any time via the ⚙ button in the main window's toolbar.

        // 4. Create and set the real main window BEFORE closing splash
        //    This ensures Application.Current.Windows is never empty
        var mainWindow = new MainWindow();
        mainWindow.DataContext = mainVm;
        Application.Current.MainWindow = mainWindow;

        // Ensure minimum brand impression before closing splash
        var splashElapsed = (DateTime.UtcNow - splashStart).TotalMilliseconds;
        if (splashElapsed < 800)
            await Task.Delay(800 - (int)splashElapsed);

        // Splash can close safely now — main window is ready to take over
        mainWindow.Show();   // Show first so Windows is never empty
        splash.Close();

    }

    /// <summary>
    /// Start the local OCR server and wait for it to become healthy.
    /// Reports progress via the optional IProgress{string} callback.
    /// Returns true if the server is healthy, false on failure.
    /// </summary>
    public static async Task<bool> StartServerAsync(IProgress<string>? progress = null)
    {
        // If the process is already running, check health quickly
        if (ServerProcess != null && !ServerProcess.HasExited)
        {
            try
            {
                using var response = await _healthClient.GetAsync("http://127.0.0.1:8000/health");
                if (response.IsSuccessStatusCode)
                {
                    progress?.Report(TranslationSource.Get("ServerStartingAlready"));
                    return true;
                }
            }
            catch { /* server not ready yet, will wait below */ }
        }

        if (string.IsNullOrEmpty(_pythonPath) || string.IsNullOrEmpty(_workingDir))
            return false;

        try
        {
            progress?.Report(TranslationSource.Get("ServerStartingOcr"));

            ServerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = "-m uvicorn server.main:app --host 127.0.0.1 --port 8000",
                    WorkingDirectory = _workingDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            ServerProcess.Start();

            // Poll /health until ready (max 30 seconds)
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                double elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed > 15)                    progress?.Report(TranslationSource.Get("ServerStartingModels"));
                    else if (elapsed > 5)
                        progress?.Report(TranslationSource.Get("ServerStartingAlmost"));
                    else
                        progress?.Report(TranslationSource.Get("ServerStartingOcr"));

                try
                {
                    using var response = await _healthClient.GetAsync("http://127.0.0.1:8000/health");
                    if (response.IsSuccessStatusCode)
                    {
                        progress?.Report(TranslationSource.Get("ServerStartingReady"));
                        return true;
                    }
                }
                catch { /* still polling */ }

                await Task.Delay(500);
            }

            // Timeout — clean up
            CleanupServer();
            return false;
        }
        catch
        {
            CleanupServer();
            return false;
        }
    }

    private static string FindFile(string[] paths) => paths.FirstOrDefault(File.Exists) ?? string.Empty;

    private static void CleanupServer()
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
        MessageBox.Show(TranslationSource.Fmt("ErrorUnexpected", ex.Message), TranslationSource.Get("ErrorSystemTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
