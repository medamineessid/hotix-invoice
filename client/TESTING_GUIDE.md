# Manual Testing Guide — HOTIX Invoice Client

This document captures **manual repro steps for scenarios that cannot be reliably
unit-tested** (UI, network, external process, OS integration). Run through these
steps after any significant change to the extraction pipeline, export logic, or
server lifecycle to catch regressions.

---

## 1. Locked Export File

**What it tests:** `ExcelWriter` crash path — the app must **not** shut down
when the target Excel file is open in another application.

**Repro steps:**

1. Open the HOTIX client.
2. Run a successful extraction (at least one result row).
3. Open the exported file location in Windows Explorer.
4. **Open the target .xlsx file in Excel** so it is locked.
5. Go back to HOTIX and click **Exporter vers Excel**.
6. Expected: A message box appears saying "Cannot write to the file. It may be
   open in another application (e.g. Excel)." The app continues running.
7. Close the file in Excel.
8. Click **Exporter vers Excel** again.
9. Expected: The export succeeds this time.

**Failure mode:** The app silently closes (crash via `HandleGlobalException` →
`Current.Shutdown()`) — **regression**.

---

## 2. Network Loss Mid-Extraction

**What it tests:** The extraction loop handles an abrupt server disconnection
gracefully without freezing the UI or crashing.

**Repro steps:**

1. Ensure the HOTIX server is running.
2. Drop several invoice files (3–5) into the client's drop zone.
3. **Kill the server mid-way** through processing (e.g. after 1–2 files have
   been processed). On Windows: Task Manager → end the `python.exe` process
   running `uvicorn`.
4. Expected:
   - The remaining files show an error row (red badge) with a message like
     "Connection refused" or "Server not available".
   - The UI does **not** freeze or crash.
   - The already-processed results remain visible.
5. Restart the server.
6. Re-drop the failed files.
7. Expected: They process normally.

**Failure mode:** The UI hangs indefinitely, the app crashes, or the error
message is uninformative.

---

## 3. Killed Local Server

**What it tests:** The client's server health-check polling and reconnection
behavior.

**Repro steps:**

1. Start the HOTIX application (server + client).
2. Verify the status indicator shows "Connecté au serveur" (green).
3. Kill the server process (Task Manager → end `python.exe` running `uvicorn`).
4. Expected within 45 seconds:
   - Status changes to "Serveur local arrêté" (red/orange).
   - The engine dropdown still reflects the last known configuration.
5. Restart the server manually (`venv\Scripts\python.exe -m uvicorn
   server.main:app --host 127.0.0.1 --port 8000`).
6. Expected within 45 seconds:
   - Status changes back to "Connecté au serveur" (green).
7. Start an extraction with the server down.
8. Expected: The app should show an error for each file, not freeze.

**Failure mode:** The app crashes, the status never updates, or extraction
hangs indefinitely.

---

## 4. Exhausted API Quota

**What it tests:** The quota-fallback banner appears mid-batch and subsequent
files switch to local OCR.

**Pre-requisite:** A configured Gemini API key that is either invalid or
exhausted.

**Repro steps:**

1. Configure the Gemini API key in `server/appsettings.json` (use a key that
   has exceeded its quota).
2. Start the server and client.
3. Drop several invoice files onto the client.
4. Expected:
   - The first file tries Gemini → gets a 429 (quota exceeded) → falls back
     to OCR.
   - An **orange warning banner** appears immediately: "Gemini quota exceeded
     — switching to local OCR for remaining files".
   - The per-file status text updates to show the quota message.
   - All remaining files process via local OCR without attempting Gemini.
5. Check the results grid.
6. Expected: Rows processed via OCR show "Local (hors ligne)" badge.
7. Clear results and run again.
8. Expected: The banner appears again (it is not permanently suppressed).

**Failure mode:** No banner appears, the app keeps trying Gemini and failing,
or the app crashes.

---

## 5. Gemini Quota → Grok Fallback (auto mode)

**What it tests:** In auto mode with both Gemini and Grok keys configured, a
Gemini quota hit should try Grok before falling to OCR.

**Pre-requisite:** Both Gemini and Grok API keys configured.

**Repro steps:**

1. Configure both keys.
2. Use an exhausted/quota-exceeded Gemini key, but a valid Grok key.
3. Drop invoice files.
4. Expected:
   - The first file gets a Gemini quota error → falls to Grok → succeeds.
   - The row shows the Grok engine badge (not "Local (hors ligne)").
   - **No** quota-fallback banner appears because Grok succeeded.
5. Repeat with both keys exhausted:
   - After Gemini quota → Grok also fails → quota-fallback banner appears →
     subsequent files go to OCR.

**Failure mode:** The quota hit goes straight to OCR even though Grok was
available (inconsistent fallback order).

---

## 6. Oversized / Garbled OCR Numbers

**What it tests:** The `extract_amount` function handles gargantuan decimal
strings from garbled OCR without crashing.

**Repro steps:**

1. Prepare a fake invoice image or PDF that, when OCR'd, produces an
   extremely long number (e.g. "999999999999999999999999999999999999999.99").
   - Simplest approach: edit a real invoice scan in an image editor to
     replace a price with a long string of digits.
2. Drop this file onto the client.
3. Expected:
   - The server processes the file without an HTTP 500 error.
   - The amount field for that row shows "N/A" or an error indicator.
   - The app does **not** crash.
4. Also test with a file that has mixed comma/dot separators:
   - "1.234.567,89" or "1,234,567.89"
5. Expected: These parse correctly if they follow either French or English
   convention.

**Failure mode:** The server returns a 500 error, or the WPF app crashes.

---

## 7. Zero-byte / Corrupt File Drop

**What it tests:** Dropping an unsupported or malformed file does not crash
the app.

**Repro steps:**

1. Create a zero-byte file: `type nul > bad.txt`
2. Create a zero-byte `.pdf`: `type nul > empty.pdf`
3. Create a file with random bytes: `fsutil file createnew random.bin 1024`
4. Drop each file individually onto the client.
5. Expected for each:
   - The file is either rejected (not added to the list) or shows an error row.
   - The app does **not** freeze or crash.
6. Drop a mix of valid invoices and these bad files together.
7. Expected:
   - Valid files process normally.
   - Bad files show error rows.
   - The extraction does **not** abort for the bad files.

**Failure mode:** The app crashes, hangs, or valid files are blocked by bad
files.

---

## 8. Multi-Page Invoice

**What it tests:** Multi-page PDFs and multi-frame TIFFs are handled.

**Repro steps:**

1. Drop a 2–3 page PDF invoice onto the client.
2. Expected: All pages are processed and fields are extracted from the most
   relevant page.
3. Drop a multi-frame TIFF file.
4. Expected: Same as above.
5. Check the raw OCR text or confidence to verify multiple pages were
   inspected.

**Failure mode:** Only the first page is read, or the extraction shows
incomplete data.

---

## 9. Rapid Click / Double-Click Export

**What it tests:** The export button is debounced or re-entrant.

**Repro steps:**

1. Run a successful extraction with several files.
2. Rapidly double-click the **Exporter vers Excel** button.
3. Expected: Only one export dialog appears, or the second click is ignored.
   The app does not show two save dialogs or attempt two simultaneous writes
   to the same file.

**Failure mode:** Two export processes run in parallel, possibly corrupting
the output file or crashing.

---

## 10. Language Switch Mid-Session

**What it tests:** Switching the UI language mid-session updates the title bar,
banners, and grid headers.

**Repro steps:**

1. Start the app in French (default).
2. Note the window title: "Hotix Invoice Extractor — <hash>".
3. Switch to English via the language toggle.
4. Expected:
   - The window title updates to include the English-translated title.
   - All UI strings in the main window update immediately.
   - The commit hash suffix remains visible.
5. Run an extraction and verify the results grid headers are in the selected
   language.
6. Switch back to French.
7. Expected: All strings update again.

**Failure mode:** The title bar loses the commit hash, or strings don't update
until restart.
