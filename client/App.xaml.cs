using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

    // ── Server logging ──────────────────────────────────────────────
    private static readonly object _serverLogLock = new();
    /// <summary>Full path to the server log file. Public so callers can surface it in error messages.</summary>
    public static string ServerLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hotix", "logs", "server.log");

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

        string? serverDir = ServerPathResolver.ResolveServerDirectory();

        if (serverDir == null)
        {
            MessageBox.Show(TranslationSource.Get("ErrorServerNotFound"), TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        // Working directory is one level above the server folder (project root)
        _workingDir = Path.GetDirectoryName(serverDir)!;

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

            // ── Pre-check: verify system environment ──
            LogServerLine("Running system pre-check: " + _pythonPath + " -m server.verify_system");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = "-m server.verify_system",
                    WorkingDirectory = _workingDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // Pass the POPPLER_PATH env var so the pre-check can find pdfinfo
                string? preCheckPopplerDir = ResolvePopplerPath();
                if (preCheckPopplerDir != null)
                    psi.EnvironmentVariables["POPPLER_PATH"] = preCheckPopplerDir;

                using (var checkProcess = Process.Start(psi))
                {
                    if (checkProcess != null)
                    {
                        string checkStdout = await checkProcess.StandardOutput.ReadToEndAsync();
                        string checkStderr = await checkProcess.StandardError.ReadToEndAsync();
                        checkProcess.WaitForExit(15_000);

                        if (checkProcess.ExitCode != 0)
                        {
                            string checkOutput = (checkStdout + checkStderr).Trim();
                            LogServerLine("Pre-check FAILED (exit " + checkProcess.ExitCode + "): " + checkOutput);
                            throw new InvalidOperationException(
                                TranslationSource.Fmt("ServerStartingFailed", ServerLogPath) +
                                "\n\nSystem pre-check failed:\n" + checkOutput);
                        }

                        LogServerLine("Pre-check passed");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw; // rethrow our own pre-check failure
            }
            catch (Exception ex)
            {
                // Pre-check itself crashed (e.g. file not found) — log and continue
                LogServerLine("Pre-check crashed (continuing anyway): " + ex.GetType().Name + ": " + ex.Message);
            }

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

            // Resolve Poppler path for PDF support and pass to server
            // This is needed because the installer sets POPPLER_PATH via [Registry] HKLM
            // but that env var may not be visible to processes started by the installer itself.
            string? popplerDir = ResolvePopplerPath();
            if (popplerDir != null)
                ServerProcess.StartInfo.EnvironmentVariables["POPPLER_PATH"] = popplerDir;

            // Drain stdout/stderr to prevent pipe-buffer deadlock
            ServerProcess.OutputDataReceived += (s, e) => { if (e.Data != null) LogServerLine(e.Data); };
            ServerProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) LogServerLine(e.Data); };

            ServerProcess.Start();
            ServerProcess.BeginOutputReadLine();
            ServerProcess.BeginErrorReadLine();

            LogServerLine("Server process started, polling /health...");

            // Poll /health until ready (max 30 seconds)
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                double elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed > 15)
                    progress?.Report(TranslationSource.Get("ServerStartingModels"));
                else if (elapsed > 5)
                    progress?.Report(TranslationSource.Get("ServerStartingAlmost"));
                else
                    progress?.Report(TranslationSource.Get("ServerStartingOcr"));

                try
                {
                    using var response = await _healthClient.GetAsync("http://127.0.0.1:8000/health");
                    if (response.IsSuccessStatusCode)
                    {
                        LogServerLine("Server is healthy and ready.");
                        progress?.Report(TranslationSource.Get("ServerStartingReady"));
                        return true;
                    }
                }
                catch { /* still polling */ }

                await Task.Delay(500);
            }

            // Timeout — capture diagnostics before cleaning up
            LogServerLine("TIMEOUT: Server did not become healthy within 30 seconds.");
            string logTail = GetLogTail(20);
            string logPath = ServerLogPath;
            LogServerLine($"Log file location: {logPath}");

            CleanupServer();

            // Surface the last log lines so the timeout is diagnosable
            string timeoutDetail = string.IsNullOrEmpty(logTail)
                ? $"Log file: {logPath}"
                : $"Log file: {logPath}\n\nLast log lines:\n{logTail}";
            throw new InvalidOperationException(
                TranslationSource.Fmt("ServerStartingFailed", logPath) +
                $"\n\n{timeoutDetail}");
        }
        catch (Exception ex)
        {
            LogServerLine($"UNHANDLED ERROR starting server: {ex.GetType().Name}: {ex.Message}");
            string logTail = GetLogTail(20);
            string logPath = ServerLogPath;
            string timeoutDetail = string.IsNullOrEmpty(logTail)
                ? $"Log file: {logPath}"
                : $"Log file: {logPath}\n\nLast log lines:\n{logTail}";
            CleanupServer();
            throw new InvalidOperationException(
                TranslationSource.Fmt("ServerStartingFailed", logPath) +
                $"\n\n{timeoutDetail}");
        }
    }

    /// <summary>
    /// Resolves the Poppler binary directory for PDF support by checking
    /// the same candidate locations as ServerPathResolver.
    /// </summary>
    private static string? ResolvePopplerPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(appDir, "poppler", "bin"),
            Path.Combine(appDir, "..", "poppler", "bin"),
            @"C:\hotix-invoice\poppler\bin",
        };
        return candidates.FirstOrDefault(Directory.Exists);
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

    /// <summary>
    /// Appends a timestamped line to %LOCALAPPDATA%\Hotix\logs\server.log.
    /// Thread-safe (locked). Rotates the file at 5MB by moving to .old.
    /// Silently ignores all I/O errors — logging is best-effort only.
    /// </summary>
    private static void LogServerLine(string line)
    {
        try
        {
            string logPath = ServerLogPath;
            string logDir = Path.GetDirectoryName(logPath)!;
            Directory.CreateDirectory(logDir);

            lock (_serverLogLock)
            {
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    // Rotate: rename current → .old, start fresh
                    string oldPath = logPath + ".old";
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);
                    File.Move(logPath, oldPath);
                }

                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch { /* best-effort logging */ }
    }

    /// <summary>
    /// Returns the last N lines from the server log file, or null if the file
    /// doesn't exist or can't be read. Used to surface startup errors in the UI.
    /// </summary>
    private static string? GetLogTail(int lineCount)
    {
        try
        {
            string logPath = ServerLogPath;
            if (!File.Exists(logPath))
                return null;

            // Read from the end (efficient for large files)
            var lines = new List<string>();
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            // Read all lines (the log will be small under normal startup)
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);

                // If we've accumulated more entries than needed, drop the oldest
                if (lines.Count > lineCount)
                    lines.RemoveAt(0);
            }

            return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
        }
        catch { return null; }
    }

    private void HandleGlobalException(Exception ex)
    {
        SentrySdk.CaptureException(ex);
        CleanupServer();
        MessageBox.Show(TranslationSource.Fmt("ErrorUnexpected", ex.Message), TranslationSource.Get("ErrorSystemTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        Current.Shutdown();
    }
}
