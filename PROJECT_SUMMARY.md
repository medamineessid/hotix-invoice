# HOTIX Invoice Extractor — Technical Summary

Local Windows invoice extraction system: Python OCR backend (FastAPI + PaddleOCR + Gemini/Grok AI), WPF desktop client (C#, .NET 8, MVVM), and Inno Setup installer. Monitored via Sentry.

---

## Architecture Overview

```
┌────────────────────────────────────────────────────────────┐
│                    End User (Windows)                      │
├────────────────────────────────────────────────────────────┤
│                                                             │
│   ┌──────────────────────────────┐  ┌──────────────────┐   │
│   │   Hotix.InvoiceClient.exe    │  │ HotixDiagnostics  │   │
│   │   (WPF C#, .NET 8)           │  │ (Post-install)    │   │
│   │   - Design System Premium    │  │                   │   │
│   │   - 11 Theme Dictionaries    │  │  7 check services │   │
│   │   - MVVM (MainViewModel)     │  │  4 repair actions │   │
│   │   - EN/FR translation        │  │                   │   │
│   └─────────────┬────────────────┘  └──────────────────┘   │
│                 │ HTTP (127.0.0.1:8000)                     │
│   ┌─────────────▼──────────────────────────────────────┐   │
│   │              Python Server (FastAPI)                │   │
│   │  ┌──────────┐  ┌──────────┐  ┌──────────────────┐  │   │
│   │  │  Gemini  │  │   Grok   │  │  PaddleOCR       │  │   │
│   │  │  Vision  │  │  Vision  │  │  (hors ligne)    │  │   │
│   │  └──────────┘  └──────────┘  └──────────────────┘  │   │
│   └─────────────────────────────────────────────────────┘   │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Runtime Flow

1. **Startup**: App launches → shows splash → spawns Python process (uvicorn)
2. **Health check**: Polls `/health` up to 90 seconds, reports progress
3. **Preflight**: Runs `server/verify_system.py` (Python, Poppler, packages)
4. **Onboarding**: First-run spotlight tour (5 steps)
5. **Extraction**: File selection → engine selection → `/extract` → results grid
6. **Fallback chain**: Auto mode = Gemini → Grok → OCR local (cascade)
7. **Export**: Filter (Results/Missing/Both) → Excel via ClosedXML

---

## Repository Layout

| Directory | Purpose | Key Files |
|---|---|---|
| `server/` | Python FastAPI service | `main.py`, `field_extractor.py`, `ocr_engine.py`, `gemini_extractor.py` |
| `client/` | WPF C# desktop app | `MainWindow.xaml`, `MainViewModel.cs`, `Themes/*.xaml` |
| `client/ViewModels/` | MVVM ViewModels | `MainViewModel.cs` (~1200 lines), `InvoiceRowViewModel.cs` |
| `client/Themes/` | Design system (11 files) | `Colors.xaml`, `Brushes.xaml`, `Animations.xaml`, `ButtonStyles.xaml` |
| `client/Resources/` | i18n translations | `strings.json` (EN), `strings.fr.json` (FR) |
| `client/Converters/` | WPF value converters | `ConfidenceToColorConverter.cs` (3 tiers) |
| `client/HotixDiagnostics/` | Diagnostic WPF tool | `MainWindow.xaml.cs` (3 basic checks) |
| `installer/` | Inno Setup installer | `Hotix.iss` (600+ lines Pascal), vendor/ |
| `scripts/` | Automation scripts | `setup.ps1`, `start.ps1`, `start.bat` |
| `server/tests/` | Python unit tests | `test_field_extractor.py`, `test_ingestion.py`, `test_ocr_engine.py`, `test_utils.py` |

---

## Python Server Details

### `server/main.py` — FastAPI Entry Point

Lifespan-managed OCR engine with memory-aware recycling.

**Endpoints:**
- `GET /health` → `{"status": "ok"}`
- `GET /engine-status` → `{"gemini_available": bool, "ocr_available": bool}`
- `POST /extract` → `InvoiceExtractionResponse` (multipart file upload)
- `POST /validate-gemini-key` → Validates key via real API call
- `POST /validate-grok-key` → Validates key via x.ai API call
- `POST /admin/recycle-engine` → Force-recycle OCR engine (diagnostics)

**Key design decisions:**
- **OCR engine recycling**: After 25 requests, releases and recreates PaddleOCR to bound memory (configurable via `OCR_ENGINE_RECYCLE_INTERVAL`)
- **Semaphore**: `_ocr_semaphore = asyncio.Semaphore(1)` serializes OCR ops (PaddleOCR not thread-safe)
- **Pre-warming**: PaddleOCR model loaded during lifespan startup (not on first request)
- **Sentry**: Integrated with DSN from `SENTRY_DSN` env var
- **CORS**: Locked to `127.0.0.1:8000` and `localhost:8000`, GET/POST only
- **90s health timeout**: Server startup reports phase-specific progress messages
- **POPPLER_PATH env var**: Passed to server process to override Poppler binary location
- **Engine transparency**: `engine_used: Literal["gemini", "grok", "ocr"]` in response

**Memory management:**
```python
# Auto-recycle every 25 requests
if app.state.ocr_request_counter >= OCR_ENGINE_RECYCLE_INTERVAL:
    async with app.state.ocr_recycle_lock:
        await asyncio.to_thread(_recycle_ocr_engine, app.state)
```

### `server/field_extractor.py` — Heuristic Field Extraction

Layered heuristics for noisy invoice OCR. 30+ functions covering:

- Label-value association (same-line, next-line, block-based)
- Geometric scoring (vertical/horizontal proximity, alignment)
- Cross-field validation & amount reconciliation
- French/Tunisian/English field aliases
- Numeric field cleaning and date normalization

**Key functions:**
- `extract_invoice_fields()` → normalized field dict
- `cross_validate_fields()` → consistency checks (HT+TVA+Taxe ≈ TTC)
- `reconcile_amounts()` → fills missing amounts from tax derivation
- `detect_amount_collision()` → flags duplicate amount candidates
- `compute_confidence()` → weighted average over populated fields

**Design note:** Uses layered heuristics (not simple regex) because invoice OCR is noisy, labels vary widely, and values appear on same line or next line depending on layout.

### `server/gemini_extractor.py` — Gemini/Grok API Client

- `extract_with_gemini()` → sends invoice image to Gemini API, parses JSON response
- `load_gemini_api_key()` → reads from `SENTRY_GEMINI_KEY` env or `appsettings.json`
- `load_gemini_model()` → reads configured model from settings
- SDK: `google.genai` (migrated from deprecated `google.generativeai`)
- Error messages in French for UX consistency

### `server/ocr_engine.py` — PaddleOCR Wrapper

- `PaddleOcrEngine` — lazy-loads PaddleOCR on first `ocr()` call
- Supports French language (`lang='fr'`)
- PaddleOCR 3.7.0+ compatibility (removed deprecated `show_log`, `use_angle_cls`, `cls`)
- `OCRResult` — normalized output per page (text, bbox, confidence)
- Pinned `paddlepaddle==3.2.0` per official docs

### `server/ingestion.py` — File Loading

- `load_invoice_images()` → handles PDF (via Poppler/pdf2image) and images (via Pillow)
- Supported extensions: `.pdf`, `.jpg`, `.jpeg`, `.png`, `.bmp`, `.tif`, `.tiff`

### `server/utils.py` — Geometry & Text Helpers

- `BoundingBox`, `OCRLine` — geometric data types
- `normalize_text()`, `collapse_text()`, `extract_amount()`, `clean_amount()`, `extract_date()`, `clean_date()`

### `server/verify_system.py` — Pre-flight Check

Validates: PaddleOCR import, google.genai availability, pdfinfo on PATH.

### `server/generate_test_invoices.py` — Test Data Generator

Creates `invoices/ocr_data/synthetic_*.json` for accuracy scoring (`score_accuracy.py`).

### Synced Root-Level `ocr_engine.py`

A minimal wrapper kept at repo root for backward compatibility. Delegates to `server.ocr_engine`.

---

## C# Client Details

### `client/Hotix.InvoiceClient.csproj` — Build Configuration

- Target: `net8.0-windows`
- WPF with implicit usings, nullable enabled
- NuGet: `ClosedXML 0.104.0` (Excel), `Sentry 6.6.0` (error monitoring)
- **BuildInfo.g.cs**: Auto-generated at compile time from `git rev-parse --short HEAD` (includes CommitHash, BuildTimestamp, AppVersion)
- **CopyAssets**: After build, copies `server/*.py`, `appsettings.json`, and `Resources/*.json` to output directory

### `client/App.xaml.cs` — Application Bootstrap

- Initializes Sentry DSN
- Resolves Python path (venv/Scripts/python.exe or C:\hotix-invoice\venv)
- Spawns Python server with health polling (90s timeout, phase-specific progress)
- Shows splash screen → waits for `/health` → opens main window
- Cleanup on exit (kills Python process)
- Global exception handler (Sentry + MessageBox)

### `client/MainWindow.xaml` — Main UI (2,600 lines)

**Layout:** 3-row Grid (titlebar 40px → content * → statusbar auto)
**Columns:** Sidebar 240px | Content * | Preview 420px (collapsible via GridSplitter)

**Sections:**
1. **Custom Title Bar** — Drag, minimize, maximize, close, language toggle (FR/EN)
2. **Sidebar** — Navigation (Extraction/About), file list, app version, server status dot
3. **Control Panel** — Engine selector (Auto/Gemini/Grok/OCR), settings ⚙ button
4. **Step Cards** — Step 1 (folder selection), Step 2 (add files)
5. **Summary Banner** — Extraction results (success count, error count, rerun all errors)
6. **Results Tabs** — "Résultats" + "Extractions Incomplètes" with fade transitions (150ms)
7. **DataGrid** — 10 columns with sort arrows, pastel confidence badges, engine badge
8. **Preview Panel** — Raw OCR text of selected row (collapsible)
9. **Status Bar** — Server status, progress bar, save path with "Open folder" link
10. **Server Failure Overlay** — Red error screen with retry button
11. **Onboarding Overlay** — Spotlight + callout (5 steps, first-run only)
12. **Drag-Over Overlay** — Visual feedback when files dragged onto window
13. **Update Notification** — Dismissible bar when new GitHub release available

### `client/MainWindow.xaml.cs` — Code-Behind (~600 lines)

**Key methods:**
- `OnLoaded`: Initializes ViewModel, sets language radio state, subscribes to PropertyChanged for animations
- `OnContentRendered`: Shows onboarding (after 500ms delay), checks for updates
- `OnClosing`: Disposes ViewModel, unsubscribes event handlers
- `TitleBar_MouseLeftButtonDown`: Drag + double-click maximize
- `Window_DragOver` / `Window_Drop`: Drag-and-drop folder support
- `AddButton_Click`: Context menu (Add Files / Add Folder)
- `ResultsGrid_LoadingRow`: Staggered row animation (opacity 0→1 + slide 6px→0, 40ms stagger)
- `ShowAboutDialog()`: Styled About dialog with engine badges (PaddleOCR, Gemini, Grok)
- `TabResults_Click` / `TabIncomplete_Click`: Tab switching with fade (150ms)
- `CheckForUpdateAsync()`: GitHub Releases API check (daily cache)
- Onboarding: `CheckOnboarding()`, `ShowOnboardingStep()`, `CompleteOnboarding()` with spotlight positioning and callout clamping

### `client/ViewModels/MainViewModel.cs` — Central ViewModel (~1200 lines)

**Commands (19 total):**
`BrowseFolder`, `BrowseFiles`, `StartExtraction`, `CancelExtraction`, `ExportExcel`, `Clear`, `Rerun`, `RerunAllErrors`, `ToggleAllFiles`, `ToggleAllRows`, `ClearSelectedRow`, `OpenSavedFolder`, `OpenSavedFile`, `RetryServer`, `ToggleSettings`, `SaveGeminiKey`, `ClearGeminiKey`, `SaveGrokKey`, `ClearGrokKey`

**Engine Management:**
- `SelectedEngine`: "auto" | "gemini" | "grok" | "ocr"
- `GeminiAvailable` / `GrokAvailable`: reflects key presence
- `GeminiModel` / `GrokModel`: user-selected model IDs
- `ResolvedEngineDisplay`: shows effective engine to user
- 45-second `DispatcherTimer` polling `/engine-status` (UI-thread-safe)
- Gemini REST API: `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`
- Grok REST API: `https://api.x.ai/v1/chat/completions`
- Batch concurrency: configurable via `HOTIX_BATCH_CONCURRENCY` env or `appsettings.json`

**Extraction Flow:**
1. Validates engine selection (key required for cloud, internet check)
2. Files processed with configurable concurrency (default 4, max 16)
3. Per-file progress: which file currently processing
4. Auto-fallback on cloud failure: shows "Local (hors ligne)" badge + tooltip reason
5. Results split into `Results` and `IncompleteResults` based on field completeness
6. Summary banner: `ShowSummaryBanner` triggers fade-in animation

**Settings persistence:** `%APPDATA%\Hotix\settings.json` (engine, language, update_last_check)

**Key bug fixes:**
- Single ViewModel instance (was duplicating in MainWindow.xaml.cs)
- Appsettings path centralized via `ResolveAppSettingsPath()`
- `ClearSelectedRowCommand` added for preview close button
- `ClearConfirmMessage` translation key added (fixed DOTNET-B FormatException)
- `ToggleSettingsCommand` opens dialog (not inline panel)

### `client/ViewModels/InvoiceRowViewModel.cs`

Presentation model for one invoice row. Implements `INotifyPropertyChanged`.

**Properties:**
- 8 extracted fields + confidence + error state + selection + tooltip
- `IsLocalOcr` computed from `EngineUsed` (drives XAML badge binding)
- `HasMissingFields` computed for incomplete tab filtering

**Methods:**
- `FromSuccess(InvoiceResult)` → maps API response to VM
- `FromError(filename, error)` → creates error row with tooltip
- `SetField()` → updates property with notification + derived field recalc

### `client/ViewModels/FileItemViewModel.cs`

Simple model for detected files. Properties: `FileName`, `FullPath`, `IsSelected`, `FileType`.

### `client/Themes/` — Premium Design System (11 files)

| File | Purpose |
|---|---|
| `Colors.xaml` | 30-color palette: warm backgrounds (#F8F7F4), branded accent (#D9472B), functional (success/warning/error/info), pastel confidence badges, interaction states, shadows |
| `Brushes.xaml` | SolidColorBrushes + 5 DropShadowEffects (Apple-style: soft, minimal opacity) + input/button brushes |
| `Typography.xaml` | Type scale (11-32px), 5 weights, 3 font families (Inter, Helvetica, Segoe UI fallback), 9 named text styles |
| `Spacing.xaml` | 8-point grid system (4, 8, 12, 16, 24, 32, 40, 48), padding presets, corner radii (8/12/16/9999), row/grid lengths |
| `Animations.xaml` | 14 storyboards: window fade-in, dialog open/close (scale 0.95→1.0), card/button hover lift (-1px), button press (scale 0.97), panel enter (fade + slide 8px), row fade, badge fade, notification slide, shimmer, spin. Durations: 150-250ms, CubicEase |
| `ButtonStyles.xaml` | 4 styles: Primary (red fill), Secondary (white+border), Danger (red outline), Icon (transparent). Shared `TemplateButtonBase` with hover/press/disabled triggers. `TargetType="ButtonBase"` for ToggleButton compatibility |
| `InputStyles.xaml` | TextBox, PasswordBox, ComboBox, CheckBox, RadioButton — all 44px height, radius 12, red focus ring |
| `CardStyles.xaml` | Card with radius 16, hover lift 1px, step badge style |
| `DataGridStyles.xaml` | Column header with sort arrow, minimal grid lines, 48px row height, pastel confidence badge, engine badge, virtualization enabled |
| `DialogStyles.xaml` | Modal with fade + scale animation, title/body/footer styles |
| `NavigationStyles.xaml` | Sidebar 240px, nav item active (soft red bg), icon/text styles, footer with status dot |

### `client/Converters/`

| Converter | Purpose |
|---|---|
| `ConfidenceToColorConverter` | 3 tiers: ≥75% green (#2E7D32), ≥40% orange (#E65100), <40% red (#C62828) |
| `InverseBooleanToVisibilityConverter` | true→Collapsed, false→Visible |
| `NullToPlaceholderConverter` | null→"—" (dash) |
| `StringToColorBrushConverter` | Hex string → SolidColorBrush |

### `client/Resources/`

- `strings.json` — English translations (~100+ keys)
- `strings.fr.json` — French translations (~100+ keys)
- Managed by `TranslationSource` (singleton with PropertyChanged)
- Language toggle in title bar (FR/EN radio buttons)

### Other Client Files

- `InvoiceClient.cs` — HTTP wrapper for `/extract` (multipart upload)
- `InvoiceResult.cs` — Client model with `[JsonPropertyName]` attributes, includes `EngineUsed`
- `ExcelWriter.cs` — ClosedXML export with "Moteur" column (EngineUsed), append mode, "[MISSING]" text
- `TranslationSource.cs` — Singleton i18n with `Fmt()` (string.Format wrapper), culture switching
- `ServerPathResolver.cs` — Path discovery (server dir, appsettings, poppler)
- `GeminiSetupWindow.xaml/.cs` — API key management, model selection, key visibility toggle, key validation
- `ExportDialog.xaml/.cs` — Export with 3 filter modes (ResultsOnly/MissingOnly/Both), append checkbox
- `SplashScreen.xaml/.cs` — Startup animation with dependency property for status message
- `Controls/ProgressRing.cs` — Custom spinning progress indicator
- `HotixDiagnostics/` — Standalone WPF diagnostic tool (3 checks: Poppler, venv, server health)

---

## Installer (`installer/Hotix.iss`)

600+ lines of Pascal Script. Production-ready Inno Setup 6.3+ installer.

### Feature Summary (9 critical items verified)

1. **Multi-method Python detection**: PATH → registry → bundled installer
2. **Pip retry**: 3 attempts with exponential backoff (1s, 2s, 4s), `--default-timeout=60`
3. **Progress feedback**: `WizardForm.StatusLabel` updates between steps
4. **Logging**: `SaveStringToFile` with timestamps to `{app}\install.log`
5. **Internet check**: `InternetGetConnectedState` from `wininet.dll`
6. **Requirements validation**: `FileExists` check on `requirements.txt`
7. **Python version check**: Parses `python --version`, accepts 3.8+
8. **Disk space**: 2,200 MB minimum (966 MB venv + 500 MB pip overhead + safety buffer)
9. **Rollback**: Full venv removal on failure (clean state for retry)

### Install Flow

```
Pre-flight → Python detection → venv creation → pip install (3 retries) → completion
     │              │                 │                  │                    │
     ├ Internet     ├ py.exe         ├ python -m venv   ├ --default-timeout=60 ├ Launch app
     ├ Disk space   ├ python.exe     └ pip upgrade      └ log stderr          ├ Desktop shortcut
     └ requirements ├ registry                                               └ Start menu entry
                    └ bundled 3.12
```

### Compilation

```powershell
iscc.exe installer/Hotix.iss
# Output: installer/HotixSetup_1.0.0.exe (~50 MB)
```

---

## Major Bug Fixes & Changes (Chronological)

| Fix | File(s) | Description | Status |
|---|---|---|---|
| DOTNET-A | `ButtonStyles.xaml` | `Cursor="NotAllowed"` → `"No"` (invalid WPF enum) | ✅ Fixed |
| DOTNET-B | `strings.json`, `strings.fr.json` | Added `ClearConfirmMessage` key (FormatException) | ✅ Fixed |
| DOTNET-C | `ButtonStyles.xaml` | `TargetType="Button"` → `"ButtonBase"` chain (StaticResourceException) | ✅ Fixed |
| StaticResource | `ButtonStyles.xaml` | TemplateButtonBase, Primary/Secondary/Danger styles → ButtonBase | ✅ Fixed |
| Empty Add button | `MainWindow.xaml` | Removed non-functional button from empty state | ✅ Fixed |
| Single ViewModel | `MainWindow.xaml.cs` | Use shared instance from App.xaml.cs | ✅ Fixed |
| Gemini SDK | `server/gemini_extractor.py` | `google.generativeai` → `google.genai` | ✅ Fixed |
| Model version | `server/gemini_extractor.py` | `gemini-1.5-flash` → `gemini-3.5-flash` | ✅ Fixed |
| CORS | `server/main.py` | Wildcard → localhost only, GET/POST only | ✅ Fixed |
| PaddleOCR 3.x | `server/ocr_engine.py` | Removed deprecated params (`show_log`, `use_angle_cls`, `cls`) | ✅ Fixed |
| Engine transparency | API + Client | `engine_used` field, badge, Excel "Moteur" column | ✅ Fixed |
| BMP support | `ingestion.py`, `InvoiceClient.cs` | Added `.bmp` to extensions, `image/bmp` MIME | ✅ Fixed |
| Preflight check | `server/verify_system.py` | Existed as referenced by setup.ps1 | ✅ Fixed |
| Cursor fix | `ButtonStyles.xaml` | `NotAllowed` → `No` (caused XamlParseException) | ✅ Fixed |
| Gemini validation | `server/main.py` | `POST /validate-gemini-key` + `POST /validate-grok-key` | ✅ Fixed |
| OCR recycling | `server/main.py` | Auto-recycle every 25 requests, semaphore-protected | ✅ Fixed |
| Amount reconciliation | `server/field_extractor.py` | Cross-field validation, tax derivation | ✅ Fixed |
| Update checker | `MainWindow.xaml.cs` | GitHub Releases API, daily cache, dismissible notification | ✅ Fixed |
| About dialog | `MainWindow.xaml.cs` | Styled dialog with engine badges | ✅ Fixed |
| Onboarding | `MainWindow.xaml.cs` | 5-step spotlight tour, persisted to appsettings | ✅ Fixed |
| Language toggle | `MainWindow.xaml` | FR/EN radio in title bar, persisted to settings | ✅ Fixed |
| Export filters | `ExportDialog.xaml/.cs` | 3 modes: Results/Missing/Both, append checkbox | ✅ Fixed |
| Staggered rows | `MainWindow.xaml.cs` | 40ms stagger, opacity + slide animation | ✅ Fixed |
| Tab transitions | `MainWindow.xaml.cs` | 150ms fade between Results/Incomplete tabs | ✅ Fixed |
| Drag-over overlay | `MainWindow.xaml.cs` | Fade-in on drag, collapse on drop | ✅ Fixed |
| Summary banner | `MainWindow.xaml.cs` | Fade-in via ViewModel property change | ✅ Fixed |
| BuildInfo | `Hotix.InvoiceClient.csproj` | Auto-generated git hash on compile | ✅ Fixed |
| Sentry DSN | `App.xaml.cs`, `server/main.py` | Client + server Sentry integration | ✅ Fixed |

---

## Current Status

✅ **Build**: 0 errors, 0 warnings (Debug + Release)
✅ **Tests**: Python pytest suite passes (field_extractor, ingestion, ocr_engine, utils)
✅ **Installer**: Compiles successfully (~50 MB)
✅ **Sentry**: 3 critical bugs fixed (DOTNET-A, B, C)
✅ **Server**: Pre-warmed PaddleOCR, 90s health timeout, auto-recycling
✅ **Client**: Premium design system, animations, onboarding, multi-engine
✅ **i18n**: English + French translations, one-click toggle

---

## Maintenance Notes

1. **`server.main` must stay importable as a package module** — imports use relative `from .models` syntax
2. **Single ViewModel** — `App.xaml.cs` creates one `MainViewModel`, passes to `MainWindow`
3. **Gemini key save path** — must go through `ServerPathResolver.ResolveAppSettingsPath()` only
4. **Button TargetType** — All named styles use `ButtonBase` (not `Button`) for ToggleButton compatibility
5. **BuildInfo.g.cs** — Auto-generated; .gitignore in obj/ directory
6. **Published paths** — Scripts and docs must match actual build output (`client/publish/`)
7. **PaddleOCR version** — Keep `paddlepaddle==3.2.0` pinned (required by PaddleOCR 3.7.0)
