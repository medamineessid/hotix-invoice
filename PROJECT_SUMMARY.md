# HOTIX Invoice Extractor

Local Windows invoice extraction system with a Python OCR backend, a WPF desktop client, and an Inno Setup installer.

## Overview

The repository is split into three operational layers:

1. The installer prepares the machine, validates the environment, and publishes the app.
2. The Python server performs invoice ingestion, OCR, field extraction, and Gemini fallback.
3. The C# WPF client launches the server, presents the UI, manages file selection, and displays results.

The project is designed for local execution on Windows. The client and installer assume a Windows desktop environment, and the OCR pipeline depends on Python, PaddleOCR, and Poppler.

## Repository Layout

- `server/` contains the FastAPI service and extraction logic.
- `client/` contains the WPF application, ViewModels, converters, and window code-behind.
- `installer/` contains the Inno Setup script and supporting documentation.
- `scripts/` contains setup and startup automation.
- `README.md` is the end-user guide, but the code and installer are the source of truth.

## Architecture

The runtime flow is:

1. Install or validate Python, Poppler, and .NET prerequisites.
2. Publish the C# client.
3. Start the WPF app.
4. The app launches the Python server locally.
5. The app waits for `/health` before showing the main UI.
6. If Gemini is not configured, the first-run wizard appears.
7. The user selects files or folders and runs extraction.
8. The server returns normalized invoice fields and confidence scores.
9. The client renders results, incomplete items, previews, and export actions.

## Python Server

### [server/main.py](server/main.py)

This is the FastAPI entry point.

Responsibilities:

- create the OCR engine during app lifespan startup,
- provide `/health` and `/engine-status`,
- accept uploads at `/extract`,
- try Gemini first when requested or configured,
- fall back to OCR when Gemini is unavailable or disabled,
- normalize exceptions into HTTP responses.

Important functions:

- `lifespan`: initializes and tears down the OCR engine.
- `health`: returns a simple OK payload.
- `engine_status`: reports Gemini configuration and OCR availability.
- `_extract_first_page_bytes`: converts a page to PNG bytes for Gemini.
- `_run_gemini_extraction`: invokes Gemini and maps successful JSON into the API model.
- `_run_ocr_extraction`: runs PaddleOCR on all pages and assembles the result.
- `extract`: validates input types, loads invoice images, and routes to Gemini or OCR.

Important changes that were made:

- Package-relative imports replaced flat imports so `uvicorn server.main:app` works from the repo root.
- Gemini migrated from the deprecated `google.generativeai` SDK to `google.genai`.
- The hardcoded model `gemini-1.5-flash` was replaced with `gemini-3.5-flash`.
- CORS was narrowed from wildcard origins and methods to localhost-only origins and GET/POST.
- `engine_used: Literal["gemini", "ocr"]` field added to `InvoiceExtractionResponse` and set in both Gemini and OCR return paths, so the client knows which engine produced each result.

### [server/models.py](server/models.py)

Defines the API schema.

Models:

- `InvoiceExtractionResponse`: invoice fields, confidence, and raw OCR text.
- `HealthResponse`: health endpoint payload.
- `ErrorResponse`: structured error payload.

### [server/ingestion.py](server/ingestion.py)

Converts uploaded files into image pages.

Important function:

- `load_invoice_images`: handles PDFs through Poppler/pdf2image and image files through Pillow.

Important change:

- `.bmp` was added to supported extensions so the backend matches the README and client filters.

### [server/ocr_engine.py](server/ocr_engine.py)

Wraps PaddleOCR in a smaller app-specific abstraction.

Important types and methods:

- `OCRResult`: normalized output for one page.
- `OcrEngineError`: runtime OCR failure wrapper.
- `PaddleOcrEngine.__init__`: stores language and defers model loading.
- `PaddleOcrEngine.ocr`: lazy-loads PaddleOCR.
- `PaddleOcrEngine.recognize`: runs OCR on a PIL image.
- `PaddleOcrEngine._normalize_result`: converts raw OCR results into ordered lines.
- `PaddleOcrEngine._iter_detections`: normalizes PaddleOCR output shapes.
- `PaddleOcrEngine._parse_detection`: converts a detection into an OCR line.
- `PaddleOcrEngine._extract_text_and_confidence`: extracts text/confidence pairs.

Important changes:

- Malformed bounding boxes are no longer silently discarded without context; they now log a debug message.
- Removed `show_log=False` and `use_angle_cls=True` from the `PaddleOCR()` constructor, and `cls=True` from the `ocr()` call — these parameters were removed in PaddleOCR 3.x and caused runtime failures.

### [server/field_extractor.py](server/field_extractor.py)

Implements the heuristic field resolver.

Core data:

- `FIELD_ORDER`: canonical invoice field order.
- `FIELD_ALIASES`: label variants for invoice fields.
- `NUMERIC_FIELDS`: monetary fields.
- `TEXT_FIELDS`: textual invoice fields.
- `FieldSelection`: the best selected candidate for a field.

Core functions:

- `extract_invoice_fields`: returns the normalized invoice fields.
- `extract_field_confidences`: returns the confidence per field.
- `extract_raw_text`: returns OCR text in reading order.
- `compute_confidence`: averages populated field confidences.
- `_extract_field_selections`: orchestrates selection for all fields.
- `_select_best_selection_for_field`: scores all anchors for one field.
- `_selection_from_same_line`: handles inline label/value patterns.
- `_selection_from_next_line`: handles label/value on adjacent lines.
- `_contains_any_alias`: checks normalized label aliases.
- `_extract_inline_value`: extracts a value from the same line as the label.
- `_find_next_candidate`: finds the nearest plausible follow-up OCR line.
- `_clean_candidate_value`: normalizes field-specific candidate values.
- `_score_candidate`: scores a candidate by geometry and label proximity.
- `_is_plausible_field_value`: rejects obviously invalid values.
- `_nearest_amount_line`: locates a nearby amount-like line.
- `_normalize_supplier_or_client`: cleans entity names.
- `_score_field_candidate`: field-specific scoring helper.
- `_has_numeric_content`: checks whether a string looks numeric.
- `_is_label_only_line`: identifies label-only OCR lines.
- `_select_preferred_amount_candidate`: resolves competing monetary candidates.
- `_extract_field_from_block`: block-based fallback extraction.
- `_find_best_block`: selects the best OCR block for a field.
- `_group_lines_by_page`: groups OCR lines by page.
- `_fallback_field_search`: last-resort heuristic search.
- `_rank_candidate_lines`: ranks candidate lines when structure is weak.

Design note:

The extractor uses layered heuristics rather than simple regexes. That is necessary because invoice OCR is noisy, labels vary widely, and many documents place values either on the same line as the label or on the next line.

### [server/gemini_extractor.py](server/gemini_extractor.py)

Handles Gemini-based extraction.

Important functions:

- `GeminiExtractionError`: domain-specific failure type.
- `load_gemini_api_key`: reads the API key from the environment or appsettings.json.
- `extract_with_gemini`: sends the invoice image to Gemini and parses the JSON response.

Important changes:

- `google.generativeai` was replaced with `google.genai`.
- `gemini-1.5-flash` was replaced with `gemini-3.5-flash`.
- `base64` import was removed because it was unused.
- Error handling was preserved in French so existing UX messages remain stable.

### [server/utils.py](server/utils.py)

Shared geometry and text helpers.

Important types:

- `BoundingBox`: geometric helper used for OCR candidate scoring.
- `OCRLine`: a single OCR line with text, geometry, confidence, and page metadata.

Important functions that remain:

- `normalize_text`
- `collapse_text`
- `looks_like_latin_text`
- `normalize_text_for_output`
- `extract_amount`
- `clean_amount`
- `extract_date`
- `clean_date`

Unused helpers removed:

- `contains_keyword`
- `clean_invoice_number`
- `normalize_supplier_client`
- `sort_by_reading_order`

Reasoning:

The removed helpers had zero call sites and only added maintenance noise.

### [server/verify_system.py](server/verify_system.py)

Added because `setup.ps1` expected a verification step that did not exist.

Checks performed:

- imports `paddleocr`,
- verifies `google.genai` or `google.generativeai` is installed,
- verifies `pdfinfo` is on PATH,
- runs `pdfinfo -v`,
- exits with a clear failure message when a check fails.

## C# Client

### [client/App.xaml.cs](client/App.xaml.cs)

Application bootstrapper.

Important methods:

- `OnStartup`: resolves paths, starts the OCR server, waits for health, shows splash, handles first-run wizard, and opens the main window.
- `GetStatusMessage`: maps elapsed time to user-visible splash text.
- `FindFile`: generic path discovery helper.
- `CleanupServer`: stops the Python process.
- `HandleGlobalException`: catches unhandled exceptions and reports them.
- `IsFirstRun`: checks whether Gemini is configured.

Important changes:

- The app now reads the server appsettings path through the shared resolver in `MainViewModel`.
- It no longer uses a separate hardcoded `C:\hotix-invoice\server\appsettings.json` copy.

### [client/MainWindow.xaml.cs](client/MainWindow.xaml.cs)

Main window code-behind.

Important methods:

- `MainWindow`: initializes the window and attaches lifecycle handlers.
- `OnLoaded`: initializes the shared ViewModel.
- `OnClosing`: disposes the shared ViewModel.
- `TitleBar_MouseLeftButtonDown`: supports drag and maximize behavior.
- `MinimizeButton_Click`: minimizes the window.
- `MaximizeButton_Click`: toggles maximize.
- `CloseButton_Click`: closes the window.
- `Window_DragOver`: handles drag/drop preview.
- `Window_Drop`: sends dropped folders into the ViewModel.
- `AddButton_Click`: opens the add-files/add-folder context menu.

Important change:

- The window no longer constructs its own `MainViewModel`. It uses the single instance created in `App.xaml.cs`.

### [client/MainWindow.xaml](client/MainWindow.xaml)

Main UI layout and styles.

Main UI areas:

- custom title bar,
- control panel,
- file list panel,
- summary banner,
- results tabs,
- raw preview panel,
- status bar,
- server failure overlay.

Important changes:

- The inline Gemini settings panel was removed.
- The preview close button now uses only the command path.
- The gear icon now opens the Gemini setup dialog.
- The confidence badges still use the three-tier color converter.
- A `"Local (hors ligne)"` badge now appears beside the confidence indicator for OCR-extracted rows, driven by the `IsLocalOcr` binding. Gemini rows show no badge.

Style details:

- red action buttons use `RedButtonStyle`,
- neutral buttons use `GrayButtonStyle`,
- window control buttons are constrained variants,
- data grid cells use a shared text style,
- progress bars and checkboxes use dedicated styles.

### [client/ViewModels/MainViewModel.cs](client/ViewModels/MainViewModel.cs)

The largest and most important client file.

Responsibilities:

- store current file/folder selection,
- manage extraction commands,
- track progress and server health,
- fetch engine status,
- persist Gemini settings,
- persist last folder selection,
- manage selected row preview,
- export results,
- support reruns and selection toggles.

Important commands:

- `BrowseFolderCommand`
- `BrowseFilesCommand`
- `StartExtractionCommand`
- `CancelExtractionCommand`
- `ExportExcelCommand`
- `ClearCommand`
- `RerunCommand`
- `RerunAllErrorsCommand`
- `ToggleAllFilesCommand`
- `ToggleAllRowsCommand`
- `OpenSavedFolderCommand`
- `ToggleSettingsCommand`
- `SaveGeminiKeyCommand`
- `ClearSelectedRowCommand`

Important methods:

- `InitializeAsync`
- `CheckEngineStatusAsync`
- `SaveGeminiKeyAsync`
- `LoadGeminiKeyFromAppSettings`
- `BrowseFolder`
- `BrowseFiles`
- `SetFolderFromDrop`
- `LoadSettings`
- `SaveSettings`
- `Dispose`

Important changes:

- The ViewModel became the single source of truth for the app lifecycle.
- It now exposes the shared appsettings resolver for Gemini storage.
- `ToggleSettingsCommand` no longer toggles an inline panel; it opens the Gemini popup dialog.
- `ClearSelectedRowCommand` was added to support the preview close button cleanly.
- The appsettings path logic is centralized so App.xaml.cs and the wizard use the same path resolution.
- A `DispatcherTimer` (not `System.Threading.Timer`) polls `/engine-status` every 45 seconds on the UI thread, so `GeminiAvailable` reflects current state without risk of `InvalidOperationException` from cross-thread property sets.

### [client/ViewModels/InvoiceRowViewModel.cs](client/ViewModels/InvoiceRowViewModel.cs)

Presentation model for one invoice row.

Important methods:

- `FromSuccess`: converts a successful `InvoiceResult` to the view model.
- `FromError`: creates a row for failed extraction.
- `ToInvoiceResult`: removed because it was unused.
- `SetField`: property helper that raises notifications.
- `OnDerivedFieldChanges`: updates dependent properties.

Important properties:

- file display,
- extracted fields,
- missing-field flags,
- confidence display,
- tooltip text,
- selection state,
- error state,
- `EngineUsed` ("gemini" or "ocr") and computed `IsLocalOcr` for XAML binding.

### [client/InvoiceClient.cs](client/InvoiceClient.cs)

HTTP client wrapper used by the ViewModel.

Important method:

- `ExtractAsync`: uploads a file to `/extract` using multipart form data and returns a parsed `InvoiceResult`.

Important change:

- `.bmp` now maps to `image/bmp`.

### [client/GeminiSetupWindow.xaml](client/GeminiSetupWindow.xaml)

Popup wizard used for Gemini configuration.

It contains:

- explanatory text,
- a PasswordBox for the key,
- a help link,
- ignore and save buttons,
- a message label for status.

### [client/GeminiSetupWindow.xaml.cs](client/GeminiSetupWindow.xaml.cs)

Handles the popup wizard behavior.

Important methods:

- `GetApiKey_Click`: opens the Google AI Studio key page.
- `Ignore_Click`: closes the dialog.
- `Save_Click`: sends the key to the ViewModel save command.

Important change:

- The manual appsettings write path was removed so the dialog reuses the ViewModel’s single save path.

### [client/Converters](client/Converters)

Small UI converters used throughout the window.

Notable one:

- `ConfidenceToColorConverter`: defines the three badge tiers, green above 0.75, orange from 0.40 to 0.75, and red below 0.40.

### [client/SplashScreen.xaml](client/SplashScreen.xaml)

Startup splash UI while the server is booting.

### [client/ExcelWriter.cs](client/ExcelWriter.cs)

Excel export helper for result output.

Important change:

- Added `"Moteur"` column (position 11) with French labels `"Gemini (cloud)"` or `"OCR local"` mapped from the `EngineUsed` property.

### [client/InvoiceResult.cs](client/InvoiceResult.cs)

Client-side model matching the server extraction response.

Important change:

- Added `EngineUsed` property with `[JsonPropertyName("engine_used")]` to deserialize the new engine transparency field from the server.

## Installer

### [installer/Hotix.iss](installer/Hotix.iss)

The installer script handles machine readiness and installation flow.

Key responsibilities:

- detect Python through PATH and registry,
- verify Python version,
- verify internet connectivity,
- verify disk space,
- validate requirements.txt,
- install Python when needed,
- create the virtual environment,
- install Python dependencies,
- roll back on failure,
- launch the app after install.

Important changes:

- Python version gate is back to 3.8+.
- Disk threshold is now 2200 MB with explicit arithmetic.
- pip install retries now inspect stderr for permanent vs transient failure signatures.
- `WizardForm.StatusLabel` is used for visible step feedback.
- `InternetGetConnectedState` is declared from wininet.dll and wrapped in a helper.

### [installer/InternetGetConnectedStateTest.iss](installer/InternetGetConnectedStateTest.iss)

Minimal standalone test script for the WinINet connectivity API.

## Scripts

### [scripts/setup.ps1](scripts/setup.ps1)

Setup automation.

It:

- checks Python,
- checks Poppler,
- checks .NET Desktop Runtime,
- creates the venv,
- installs Python packages,
- runs the verification script,
- publishes the client.

Important fix:

- the referenced verifier script now exists at `server/verify_system.py`.

### [scripts/start.ps1](scripts/start.ps1)

Preferred launch path.

It:

- starts the OCR server,
- waits for `/health`,
- launches the published WPF client,
- stops the server when the client exits.

### [scripts/start.bat](scripts/start.bat)

Batch launcher for end users who want a simple double-click entrypoint.

Important change:

- it now points to `client/publish/Hotix.InvoiceClient.exe` so it matches the published output.

## Files Removed or Simplified

These items were removed because they were unused or misleading:

- unused server helper functions in `utils.py`,
- unused `ToInvoiceResult()` in `InvoiceRowViewModel`,
- the duplicate inline Gemini settings panel in `MainWindow.xaml`,
- the duplicate `MainViewModel` instance in `MainWindow.xaml.cs`,
- deprecated Gemini SDK usage,
- stale publish artifacts from git tracking.

## What Went Wrong During the Work

Several things were initially inconsistent:

- server imports were written as if the modules were top-level,
- the app created more than one ViewModel instance,
- Gemini used a deprecated SDK and a dead model id,
- the installer expected a missing verifier script,
- the installer claimed an unsupported disk threshold,
- the documentation and launcher paths did not match the real publish path,
- some helper functions and model conversion methods were no longer used,
- build outputs were tracked in git.

Each of those issues had to be corrected rather than worked around, because they affected startup, correctness, or maintainability.

## Current Status

The current codebase now has:

- a functioning Python server startup path,
- a single shared client ViewModel,
- Gemini integration on `google.genai`,
- BMP support across the stack,
- a real system verification script,
- installer checks that are at least internally consistent,
- launch scripts that match the published client path,
- engine transparency with `engine_used` field in API response, "Local (hors ligne)" badge in the UI, and "Moteur" column in Excel export,
- automatic Gemini status polling every 45 seconds via `DispatcherTimer` (UI-thread-safe),
- PaddleOCR 3.7.0 compatibility after removing deprecated `show_log`, `use_angle_cls`, and `cls` parameters,
- `paddlepaddle==3.2.0` pinned per PaddleOCR's official installation docs.

## Notes for Maintenance

The most important invariants to preserve are:


1. `server.main` must stay importable as a package module.
2. The client must keep a single ViewModel instance for the whole app lifetime.
3. Gemini key saving must go through one path only.
4. Installer logic should stay explicit about disk, network, and Python version checks.
5. Published output paths in scripts and docs should match the actual build output.
