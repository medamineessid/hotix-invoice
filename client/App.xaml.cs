using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
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
    // Fast 1s client for the high-frequency /health polling loop.
    private static readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    // Slower 5s client for kill/reuse DECISIONS.  A genuinely booting server
    // (socket accepting, but mid-PaddleOCR-model-load) can take >1s to answer
    // /health — using the fast client here would misclassify it as a dead
    // orphan and kill it mid-boot (violates: slow-but-successful startup must
    // not be killed).  Polling keeps the fast client; only decisions use this.
    private static readonly HttpClient _probeClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Startup timeout for the local OCR server. Configurable via the
    /// HOTIX_SERVER_START_TIMEOUT_SECONDS env var (default 90s). PaddleOCR
    /// model warm-up on a true cold start can take 30-90s alone, so this must
    /// not be set too low — a slow-but-successful boot must never be killed.</summary>
    public static TimeSpan ServerStartTimeout => TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("HOTIX_SERVER_START_TIMEOUT_SECONDS"), out var seconds)
            && seconds >= 10
            ? seconds
            : 90.0);

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
        AppDomain.CurrentDomain.UnhandledException += (s, args) => HandleGlobalException((Exception)args.ExceptionObject, isFatal: true);
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
                string crashPath = ServerPathResolver.ResolveWritableFile("crash.log");
                File.WriteAllText(crashPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            }
            catch { }
            throw; // re-throw to be caught by HandleGlobalException
        }
    }

    private async Task StartupCoreAsync()
    {
        // [SEL-DIAG] TEMPORARY: capture WPF data-binding warnings/errors to
        // pipeline.log so a silently-broken SelectedItem write-back is visible.
        // Remove this block (and PipelineTraceListener) after diagnosis.
        try
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new PipelineTraceListener());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        }
        catch { /* diagnostics must never break startup */ }

        // 1. Path Discovery — find Python and server/main.py for later lazy start.
        //    Resolved relative to the executable (walking up the tree), never a
        //    hard-coded absolute path, so the app works from any location.
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        _pythonPath = ServerPathResolver.ResolveVenvPython();

        LogServerLine($"Startup path discovery: executable directory={appDir}");
        LogServerLine($"Resolved project root={ServerPathResolver.ResolveProjectRoot() ?? "<not found>"}");
        LogServerLine($"Resolved Python={_pythonPath ?? "<not found>"}; exists={_pythonPath != null && File.Exists(_pythonPath)}");

        if (string.IsNullOrEmpty(_pythonPath))
        {
            // Robust diagnostics: list every location probed and whether it exists.
            string[] pythonCandidates = ServerPathResolver.UpwardCandidates(
                appDir, Path.Combine("venv", "Scripts", "python.exe"));
            string pythonDetail = string.Join(Environment.NewLine, pythonCandidates.Select(c =>
                (File.Exists(c) ? "[present]  " : "[missing]  ") + c));

            MessageBox.Show(
                TranslationSource.Get("ErrorPythonNotFound") + Environment.NewLine + Environment.NewLine +
                TranslationSource.Get("ErrorLocationsChecked") + Environment.NewLine + pythonDetail,
                TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        string? serverDir = ServerPathResolver.ResolveServerDirectory();

        LogServerLine($"Resolved server directory={serverDir ?? "<not found>"}; exists={serverDir != null && Directory.Exists(serverDir)}");
        string? resolvedMainPy = ServerPathResolver.ResolveMainPy();
        LogServerLine($"Resolved main.py={resolvedMainPy ?? "<not found>"}; exists={resolvedMainPy != null && File.Exists(resolvedMainPy)}");
        string? resolvedPoppler = ResolvePopplerPath();
        LogServerLine($"Resolved Poppler={resolvedPoppler ?? "<not found>"}; exists={resolvedPoppler != null && Directory.Exists(resolvedPoppler)}");

        if (serverDir == null)
        {
            string[] serverCandidates = ServerPathResolver.UpwardCandidates(
                appDir, Path.Combine("server", "main.py"));
            string serverDetail = string.Join(Environment.NewLine, serverCandidates.Select(c =>
                (File.Exists(c) ? "[present]  " : "[missing]  ") + c));

            MessageBox.Show(
                TranslationSource.Get("ErrorServerNotFound") + Environment.NewLine + Environment.NewLine +
                TranslationSource.Get("ErrorLocationsChecked") + Environment.NewLine + serverDetail,
                TranslationSource.Get("ErrorFatalTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        // Working directory is one level above the server folder (project root)
        _workingDir = Path.GetDirectoryName(serverDir)!;
        LogServerLine($"Server working directory={_workingDir}; exists={Directory.Exists(_workingDir)}");

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
        // ── Kill any existing server process before starting a new one ──
        // HasExited can lag — the process may have crashed without the OS
        // having fully reaped it yet, leaving the port locked.
        if (ServerProcess != null)
        {
            if (!ServerProcess.HasExited)
            {
                try
                {
                    using var response = await _probeClient.GetAsync("http://127.0.0.1:8000/health");
                    if (response.IsSuccessStatusCode)
                    {
                        progress?.Report(TranslationSource.Get("ServerStartingAlready"));
                        return true;
                    }
                }
                catch { /* server not ready yet — will kill and restart below */ }

                // Health check failed — kill the unresponsive/stale process
                LogServerLine("Existing server process is unhealthy — killing before restart");
                try
                {
                    ServerProcess.Kill();
                    ServerProcess.WaitForExit(2000);
                }
                catch { /* process already gone */ }
            }
            ServerProcess.Dispose();
            ServerProcess = null;
        }

        // ── Port 8000 probe: reuse a live server, kill a dead orphan ──
        // A previous Hotix session can leave a python.exe holding the port
        // (client crashed, wrapper killed but not the child, stale process
        // from a prior install, etc.).  If we skip this, uvicorn fails to
        // bind with WinError 10048 and the app wastes the full startup
        // timeout.  Distinguish the two cases explicitly:
        //   • port bound AND /health answers → live healthy server → reuse it
        //   • port bound AND /health fails     → dead orphan → kill and retry
        if (IsPortListening(8000))
        {
            bool healthy = false;
            try
            {
                using var probe = await _probeClient.GetAsync("http://127.0.0.1:8000/health");
                healthy = probe.IsSuccessStatusCode;
            }
            catch { /* not answering within 5s — treat as orphan */ }

            if (healthy)
            {
                LogServerLine("PORT 8000 is held by a live healthy Hotix server — reusing it (no restart)");
                progress?.Report(TranslationSource.Get("ServerStartingAlready"));
                return true;
            }

            LogServerLine("PORT 8000 is bound but did not answer /health within 5s — classifying as dead orphan");
            int? orphanPid = FindOwningPid(8000);
            if (orphanPid.HasValue)
            {
                LogServerLine($"PORT 8000 is held by a dead orphan (PID {orphanPid.Value}) — killing it before restart");
                KillProcessById(orphanPid.Value);
                await Task.Delay(700); // give the OS time to release the socket
            }
            else
            {
                LogServerLine("PORT 8000 is bound by an unknown process and is not responding to /health — attempting start anyway");
            }
        }
        else
        {
            LogServerLine("Port 8000 is free — starting a fresh server process");
        }

        if (string.IsNullOrEmpty(_pythonPath) || string.IsNullOrEmpty(_workingDir))
        {
            LogServerLine("STARTUP BLOCKED: Python executable or server working directory was not resolved.");
            return false;
        }

        try
        {
            progress?.Report(TranslationSource.Get("ServerStartingOcr"));

            LogServerLine($"Starting Python command: \"{_pythonPath}\" -m uvicorn server.main:app --host 127.0.0.1 --port 8000");
            LogServerLine($"Python working directory: {_workingDir}");

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
                    if (checkProcess == null)
                        throw new InvalidOperationException("Python pre-check could not be started.");

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

                    LogServerLine("Pre-check passed: Python executable started successfully.");
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

            // Force UTF-8 for Python's stdio so PaddleX/PaddleOCR log lines
            // (non-ASCII) render correctly in the captured log instead of
            // mojibake on Windows legacy codepages (cp1252/cp850).
            ServerProcess.StartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

            // Drain stdout/stderr to prevent pipe-buffer deadlock
            ServerProcess.OutputDataReceived += (s, e) => { if (e.Data != null) LogServerLine(e.Data); };
            ServerProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) LogServerLine(e.Data); };

            try
            {
                if (!ServerProcess.Start())
                    throw new InvalidOperationException("Process.Start returned false.");
            }
            catch (Exception ex)
            {
                LogServerLine($"PROCESS START FAILED: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            LogServerLine($"Process.Start succeeded; process ID={ServerProcess.Id}");
            ServerProcess.BeginOutputReadLine();
            ServerProcess.BeginErrorReadLine();

            LogServerLine($"Server process started, polling /health (max {ServerStartTimeout.TotalSeconds:0}s)...");

            // Poll /health until ready (ServerStartTimeout, default 90s)
            // Progress thresholds based on real user logs:
            //   0-15s:   "Starting OCR server..."    — Python/uvicorn startup + imports
            //   15-35s:  "Almost ready..."            — FastAPI lifespan, PaddleOCR init
            //   35-60s:  "Loading models from cache..." — PaddleOCR models loading
            //   60-90s:  "Loading models..."          — long pole (first-time download)
            //
            // On a true cold start (no cached models) PaddleOCR needs to download
            // models from HuggingFace/ModelScope, which can add 30-60+ seconds.
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < ServerStartTimeout)
            {
                if (ServerProcess.HasExited)
                {
                    LogServerLine($"SERVER PROCESS EXITED before readiness check; exit code={ServerProcess.ExitCode}");
                    string exitLogTail = GetLogTail(20) ?? "<no captured server output>";
                    throw new InvalidOperationException(
                        TranslationSource.Fmt("ServerStartingFailed", ServerLogPath) +
                        $"\n\nPython exited before the OCR server became ready (exit code {ServerProcess.ExitCode})." +
                        $"\n\nLast log lines:\n{exitLogTail}");
                }

                double elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed > 55)
                    progress?.Report(TranslationSource.Get("ServerStartingModels"));
                else if (elapsed > 30)
                    progress?.Report(TranslationSource.Get("ServerStartingCache"));
                else if (elapsed > 12)
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
            LogServerLine($"TIMEOUT: Server did not become healthy within {ServerStartTimeout.TotalSeconds:0} seconds.");
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
            bool portInUse = false;
            // Check for WSAEADDRINUSE (errno 10048) — port already bound
            if (logTail != null)
            {
                string lower = logTail.ToLowerInvariant();
                portInUse = lower.Contains("10048") || lower.Contains("address already in use");
            }
            if (!portInUse && ex.Message != null)
            {
                portInUse = ex.Message.Contains("10048") ||
                    (ex.InnerException?.Message?.Contains("10048") == true);
            }
            CleanupServer();
            if (portInUse)
            {
                throw new InvalidOperationException(
                    TranslationSource.Get("ServerPortInUse"));
            }
            string timeoutDetail = string.IsNullOrEmpty(logTail)
                ? $"Log file: {logPath}"
                : $"Log file: {logPath}\n\nLast log lines:\n{logTail}";
            throw new InvalidOperationException(
                TranslationSource.Fmt("ServerStartingFailed", logPath) +
                $"\n\n{timeoutDetail}");
        }
    }

    /// <summary>
    /// Resolves the Poppler binary directory for PDF support by checking
    /// POPPLER_PATH env var first, then walking up from the executable
    /// (poppler\bin or poppler\Library\bin). Never uses a hard-coded path.
    /// </summary>
    private static string? ResolvePopplerPath()
    {
        // Check POPPLER_PATH env var first (user/system-level override)
        string? envPath = Environment.GetEnvironmentVariable("POPPLER_PATH");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            return envPath;

        return ServerPathResolver.ResolvePopplerDirectory();
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(ep => ep.Port == port);
        }
        catch
        {
            return false; // best effort
        }
    }

    /// <summary>Find the PID owning a socket bound to the given local port via netstat.
    ///
    /// Locale-independent: matches only on the LOCAL-ADDRESS column suffix.  The
    /// state column ("LISTENING" / "ECOUTE" on French Windows / …) is localized
    /// and must never be used as a filter — that silently returns null on
    /// non-English systems and the orphan would never be killed.  A non-listening
    /// row's local address is an ephemeral port, so the ":port" suffix match on
    /// the local column alone isolates the owning socket.
    /// </summary>
    private static int? FindOwningPid(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            string suffix = ":" + port;
            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                // netstat -ano columns: Proto  LocalAddr  ForeignAddr  State  PID
                // (state column is localized — never inspected here).
                if (parts.Length >= 5 &&
                    parts[1].EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[^1], out int pid) && pid > 0)
                {
                    return pid;
                }
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private static void KillProcessById(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc != null && !proc.HasExited)
            {
                LogServerLine($"Terminating orphaned server process PID {pid}");
                proc.Kill();
                proc.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            LogServerLine("Failed to kill orphan PID " + pid + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void CleanupServer()
    {
        if (ServerProcess == null)
            return;
        if (!ServerProcess.HasExited)
        {
            try
            {
                LogServerLine("Cleaning up server process...");
                ServerProcess.Kill();
                ServerProcess.WaitForExit(3000);
                LogServerLine("Server process terminated");
                // Brief wait for OS to fully release the port
                System.Threading.Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                LogServerLine("Cleanup exception (ignored): " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        ServerProcess.Dispose();
        ServerProcess = null;
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

    private int _consecutiveDispatcherFailures;
    private DateTime _lastDispatcherFailure;

    private void HandleGlobalException(Exception ex, bool isFatal = false)
    {
        // 1. Crash log FIRST — a paper trail even if Sentry or the message box
        //    themselves fail (copies the StartupAsync pattern).
        try
        {
            string crashPath = ServerPathResolver.ResolveWritableFile("crash.log");
            File.WriteAllText(crashPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
        }
        catch { /* best-effort */ }

        // 2. Sentry — isolated so a Sentry failure can't suppress the dialog.
        try { SentrySdk.CaptureException(ex); } catch { /* best-effort */ }

        // 3. Always surface a translated message (never the raw .NET text).
        //    Guarded too: if the dialog itself throws, the Dispatcher handler
        //    would never reach args.Handled = true and the app would still die.
        try
        {
            MessageBox.Show(ErrorMessageTranslator.ToUserMessage(ex), TranslationSource.Get("ErrorSystemTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* best-effort */ }

        // 4. Only truly fatal paths shut the app down. Dispatcher exceptions
        //    (args.Handled = true) keep the app running: a failed export or
        //    preview must not kill the whole application. But a genuinely
        //    wedged app (5+ consecutive dispatcher failures) escalates to
        //    fatal so it exits instead of spinning forever.
        if (isFatal)
        {
            CleanupServer();
            Current.Shutdown();
        }
        else
        {
            // Only escalate to shutdown when dispatcher failures are genuinely
            // CONSECUTIVE (a burst), not spread out over a long session.  A
            // single rare failure (e.g. a one-off layout hiccup) must never
            // accumulate with an unrelated failure minutes/hours later and kill
            // the app while the user is away.
            var now = DateTime.UtcNow;
            if (_lastDispatcherFailure == default || (now - _lastDispatcherFailure) > TimeSpan.FromSeconds(30))
                _consecutiveDispatcherFailures = 0;
            _lastDispatcherFailure = now;
            if (++_consecutiveDispatcherFailures >= 5)
            {
                _consecutiveDispatcherFailures = 0;
                CleanupServer();
                Current.Shutdown();
            }
        }
    }

    // [SEL-DIAG] TEMPORARY: appends WPF binding-trace lines to pipeline.log.
    // Remove together with the wiring block in StartupCoreAsync after diagnosis.
    private sealed class PipelineTraceListener : TraceListener
    {
        private readonly StringBuilder _buffer = new();
        public override void Write(string? message) => _buffer.Append(message);
        public override void WriteLine(string? message)
        {
            _buffer.Append(message);
            Flush(_buffer.ToString());
            _buffer.Clear();
        }
        private static void Flush(string line)
        {
            try
            {
                File.AppendAllText(
                    ServerPathResolver.ResolveWritableFile("pipeline.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [BIND] {line}{Environment.NewLine}");
            }
            catch { /* diagnostics must never break the app */ }
        }
    }
}
