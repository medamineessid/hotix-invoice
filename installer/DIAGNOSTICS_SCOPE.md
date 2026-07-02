; HOTIX DIAGNOSTICS — Scope & Architecture
; Standalone WPF utility for post-install verification and repair
; ============================================================================

PROJECT OVERVIEW
================
HotixDiagnostics.exe is a small WPF application launched automatically after
Hotix installation (via [Run] section in Hotix.iss). It performs comprehensive
system checks and offers one-click repairs for common issues.

Purpose: Reduce support burden by enabling users to self-diagnose and fix
problems without manual intervention or contacting support.

ARCHITECTURE
============

1. PROJECT STRUCTURE
   ├── HotixDiagnostics/
   │   ├── HotixDiagnostics.csproj          (WPF .NET 8 project)
   │   ├── App.xaml / App.xaml.cs           (Entry point, minimal)
   │   ├── MainWindow.xaml / MainWindow.xaml.cs  (UI, check results display)
   │   ├── ViewModels/
   │   │   └── DiagnosticsViewModel.cs      (Business logic, check execution)
   │   ├── Models/
   │   │   ├── CheckResult.cs               (Single check result)
   │   │   └── DiagnosticsReport.cs         (Aggregated results)
   │   ├── Services/
   │   │   ├── PythonCheckService.cs        (Python detection + version)
   │   │   ├── VenvCheckService.cs          (venv integrity, pip list)
   │   │   ├── PaddleOCRCheckService.cs     (import test, model download status)
   │   │   ├── FastAPICheckService.cs       (server startup, port 8000)
   │   │   ├── GeminiCheckService.cs        (API key presence, connectivity)
   │   │   ├── FolderCheckService.cs        (required folders, write perms)
   │   │   └── InternetCheckService.cs      (connectivity test)
   │   ├── Repairs/
   │   │   ├── IRepairAction.cs             (Interface for repair actions)
   │   │   ├── RecreateVenvRepair.cs        (Rebuild venv + pip install)
   │   │   ├── ReinstallPythonRepair.cs     (Re-run Python installer)
   │   │   ├── FixPermissionsRepair.cs      (Grant write access to folders)
   │   │   └── DownloadModelsRepair.cs      (Pre-download PaddleOCR models)
   │   └── Converters/
   │       ├── CheckStatusToColorConverter.cs  (Green/Yellow/Red)
   │       └── CheckStatusToIconConverter.cs   (✓/⚠/✕)

2. DATA MODELS

   CheckResult.cs:
   ├── CheckName: string                    ("Python Installed", "venv Integrity", etc.)
   ├── Status: CheckStatus enum             (Success, Warning, Failed)
   ├── Message: string                      ("Python 3.12.6 found at C:\...", "venv missing", etc.)
   ├── Details: string                      (Optional detailed info for tooltips)
   ├── CanRepair: bool                      (True if repair action available)
   └── RepairAction: IRepairAction          (Null if CanRepair=false)

   DiagnosticsReport.cs:
   ├── Timestamp: DateTime
   ├── Checks: List<CheckResult>
   ├── OverallStatus: CheckStatus           (Failed if any check failed, Warning if any warning, Success if all pass)
   ├── SummaryText: string                  ("All systems operational" / "3 issues detected")
   └── LogPath: string                      (Path to detailed log file)

3. CHECK SERVICES (Each returns CheckResult)

   PythonCheckService:
   - Detect Python via py.exe, python.exe, registry (same logic as installer)
   - Extract version via `python --version`
   - Verify 3.8+ (PaddlePaddle requirement)
   - Status: Success if 3.8+, Failed if missing or too old
   - Repair: ReinstallPythonRepair (re-run bundled installer)

   VenvCheckService:
   - Check if {app}\venv exists
   - Check if {app}\venv\Scripts\python.exe exists
   - Run `pip list` to verify pip is functional
   - Check for critical packages: paddleocr, fastapi, google-genai
   - Status: Success if all present, Warning if venv exists but pip fails, Failed if missing
   - Repair: RecreateVenvRepair (delete venv, recreate, pip install)

   PaddleOCRCheckService:
   - Try `import paddleocr` from venv Python
   - Check if PaddleOCR model cache exists (~/.paddleocr/)
   - Estimate model download status (if cache is empty, models not yet downloaded)
   - Status: Success if import works, Warning if import works but models not cached, Failed if import fails
   - Repair: DownloadModelsRepair (run a small test extraction to trigger model download)

   FastAPICheckService:
   - Attempt to start the FastAPI server (uvicorn) in a subprocess
   - Try to connect to http://127.0.0.1:8000/health within 10 seconds
   - If successful, gracefully shut down the server
   - Status: Success if /health responds, Failed if server won't start or port unreachable
   - Repair: None (user should check logs; this is a symptom of deeper issues)

   GeminiCheckService:
   - Check if {app}\server\appsettings.json exists
   - Parse JSON, check for "gemini_api_key" field
   - If key present, attempt a simple API call (e.g., list models) with 5s timeout
   - Status: Success if key present and API responds, Warning if key missing (optional), Failed if key present but API unreachable
   - Repair: None (user must manually enter key via app settings)

   FolderCheckService:
   - Verify {app}\server exists and is readable
   - Verify {app}\client exists and is readable
   - Verify {app}\venv exists (if venv check passed) and is writable
   - Check write permissions on {app} (for logs, temp files)
   - Status: Success if all readable/writable, Failed if any missing or permission denied
   - Repair: FixPermissionsRepair (attempt to grant current user write access via icacls)

   InternetCheckService:
   - Use same InternetGetConnectedState from wininet.dll as installer
   - Status: Success if connected, Failed if not
   - Repair: None (user must fix network)

4. REPAIR ACTIONS (Implement IRepairAction interface)

   IRepairAction interface:
   ├── Name: string                         ("Recreate Virtual Environment", etc.)
   ├── Description: string                  (User-facing explanation)
   ├── ExecuteAsync(): Task<bool>           (Perform repair, return success)
   └── LogOutput: string                    (Captured output for debugging)

   RecreateVenvRepair:
   - Delete {app}\venv recursively
   - Run `python -m venv {app}\venv`
   - Run `pip install --upgrade pip`
   - Run `pip install -r {app}\requirements.txt` (with 3 retries, same as installer)
   - Return true if all steps succeed
   - Log all output to {app}\repair.log

   ReinstallPythonRepair:
   - Locate bundled Python installer (check {app}\installer\vendor\python-3.12.6-amd64.exe)
   - If not found, offer to download from python.org (requires internet)
   - Run installer with /quiet /norestart PrependPath=1
   - Verify new Python is on PATH
   - Return true if successful

   FixPermissionsRepair:
   - Use `icacls {app} /grant %USERNAME%:F /T` to grant full control
   - Requires admin privileges (should already have them from installer)
   - Return true if successful

   DownloadModelsRepair:
   - Run a minimal PaddleOCR test: `from paddleocr import PaddleOCR; ocr = PaddleOCR(lang='fr')`
   - This triggers automatic model download to ~/.paddleocr/
   - May take 5-10 minutes depending on internet speed
   - Show progress dialog with cancel button
   - Return true if models downloaded successfully

5. UI LAYOUT (MainWindow.xaml)

   ┌─────────────────────────────────────────────────────────────┐
   │ HOTIX DIAGNOSTICS                                    [_][□][X]│
   ├─────────────────────────────────────────────────────────────┤
   │                                                               │
   │  Status: ⚠ 2 issues detected                                │
   │                                                               │
   │  ┌─────────────────────────────────────────────────────────┐ │
   │  │ Check Name              Status    Message               │ │
   │  ├─────────────────────────────────────────────────────────┤ │
   │  │ ✓ Python Installed      Success   Python 3.12.6        │ │
   │  │ ✓ Virtual Environment   Success   venv OK              │ │
   │  │ ⚠ PaddleOCR Models      Warning   Not cached yet       │ │
   │  │ ✕ FastAPI Server        Failed    Port 8000 in use     │ │
   │  │ ⚠ Gemini API Key        Warning   Not configured       │ │
   │  │ ✓ Folders & Permissions Success   All writable         │ │
   │  │ ✓ Internet Connection   Success   Connected            │ │
   │  └─────────────────────────────────────────────────────────┘ │
   │                                                               │
   │  [Repair Selected Issues]  [Refresh]  [View Log]  [Close]   │
   │                                                               │
   └─────────────────────────────────────────────────────────────┘

   Details:
   - Each row is a CheckResult
   - Color-coded: Green (Success), Yellow (Warning), Red (Failed)
   - Icon: ✓ / ⚠ / ✕
   - Clicking a row shows Details in a tooltip or expandable panel
   - "Repair Selected Issues" button is enabled only if at least one check has CanRepair=true
   - "Refresh" re-runs all checks
   - "View Log" opens {app}\diagnostics.log in Notepad
   - Progress bar during repair execution

6. EXECUTION FLOW

   On Launch:
   1. Initialize DiagnosticsViewModel
   2. Run all 7 checks in parallel (or sequential if dependencies exist)
   3. Aggregate results into DiagnosticsReport
   4. Display results in MainWindow
   5. If OverallStatus != Success, highlight "Repair Selected Issues" button

   On "Repair Selected Issues":
   1. Identify all checks with CanRepair=true and Status != Success
   2. Show confirmation dialog: "This will attempt to fix the following issues: [list]"
   3. Execute repairs sequentially (some depend on others, e.g., venv before pip)
   4. Show progress dialog with cancel button
   5. After each repair, log result
   6. On completion, offer to re-run diagnostics
   7. If user clicks "Refresh", go back to step 2 above

   On "View Log":
   1. Open {app}\diagnostics.log in default text editor (Notepad)

7. LOGGING

   All checks and repairs log to {app}\diagnostics.log with timestamps:
   ```
   2025-01-15 14:32:10 | [CHECK] Python Installed
   2025-01-15 14:32:10 | [RESULT] Success: Python 3.12.6 at C:\Python312\python.exe
   2025-01-15 14:32:11 | [CHECK] Virtual Environment
   2025-01-15 14:32:11 | [RESULT] Success: venv at C:\Program Files\Hotix\venv
   2025-01-15 14:32:15 | [REPAIR] RecreateVenvRepair started
   2025-01-15 14:32:15 | [REPAIR] Deleting old venv...
   2025-01-15 14:32:20 | [REPAIR] Creating new venv...
   2025-01-15 14:32:45 | [REPAIR] Installing dependencies...
   2025-01-15 14:33:20 | [REPAIR] Success
   ```

8. DEPENDENCIES

   - System.Net.Http (for internet connectivity test)
   - System.Diagnostics (for subprocess execution)
   - System.IO (for file operations)
   - System.Text.Json (for appsettings.json parsing)
   - No external NuGet packages (keep it lightweight)

9. ERROR HANDLING

   - All service methods wrap in try-catch and return CheckResult with Failed status + exception message
   - Repair actions catch exceptions and return false with logged error
   - UI remains responsive during long operations (async/await)
   - User can cancel repairs mid-execution
   - If a repair fails, log the error and suggest manual intervention

10. FUTURE ENHANCEMENTS (Out of scope for v1)

    - Export diagnostics report as PDF
    - Auto-run on app startup (optional, configurable)
    - Remote diagnostics (send report to support)
    - Scheduled health checks (background task)
    - Rollback failed repairs (undo venv recreation if it fails)

IMPLEMENTATION PRIORITY
=======================
1. Models (CheckResult, DiagnosticsReport)
2. Services (PythonCheckService, VenvCheckService, etc.)
3. Repairs (RecreateVenvRepair, etc.)
4. ViewModel (DiagnosticsViewModel orchestrating checks + repairs)
5. UI (MainWindow.xaml + code-behind)
6. Integration (launch from installer, logging)

ESTIMATED EFFORT
================
- Models: 1-2 hours
- Services: 4-6 hours (most complex: FastAPI startup, PaddleOCR import)
- Repairs: 2-3 hours
- ViewModel: 2-3 hours
- UI: 2-3 hours
- Testing & refinement: 3-4 hours
- Total: ~15-20 hours for a production-ready v1

TESTING CHECKLIST
=================
- [ ] All checks pass on a clean install
- [ ] All checks fail gracefully when components are missing
- [ ] Repairs execute without errors
- [ ] Repairs are idempotent (can run multiple times safely)
- [ ] Logging captures all output
- [ ] UI remains responsive during long operations
- [ ] Cancel button works during repairs
- [ ] Refresh re-runs all checks correctly
- [ ] View Log opens file in editor
- [ ] Installer launches diagnostics automatically
- [ ] Diagnostics can be run manually from Start Menu
