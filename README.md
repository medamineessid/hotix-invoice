# HOTIX — Extraction de Factures

Système local d'extraction automatique de champs de factures scannées (PDF et images) via OCR, avec interface graphique Windows.

---

## Architecture

```
hotix-invoice/
├── server/        ← Python logic, extraction engines & appsettings.json
├── client/        ← WPF C# application code
├── venv/          ← Python 3.12 environment
├── scripts/       ← setup.ps1 and start.bat
├── requirements.txt
└── README.md```

- The **Python server** runs locally and exposes a `POST /extract` API.
- The **C# WPF client** is the graphical interface the user interacts with.
- The Python server is launched automatically by the application. No manual server management is required.

---

## For IT — One-Time Machine Setup

### Step 1 — Install Python 3.12

Download and install from the official site:
👉 https://www.python.org/downloads/release/python-3126/

> **Important:** During installation, check **"Add Python to PATH"**.

Verify it worked — open a terminal and run:
```
python --version
```
Expected output: `Python 3.12.x`

---

### Step 2 — Install .NET 8 Runtime

Download and install the **.NET 8 Desktop Runtime** (x64):
👉 https://dotnet.microsoft.com/en-us/download/dotnet/8.0

> Choose **".NET Desktop Runtime 8.x"** — not the SDK, not ASP.NET.

---

### Step 3 — Install Poppler for Windows

Poppler is required to process PDF files.

1. Download the latest Poppler for Windows:
   👉 https://github.com/oschwartz10612/poppler-windows/releases/latest

2. Extract the zip to `C:\poppler`

3. Add Poppler to the system PATH:
   - Open **Start** → search **"Environment Variables"**
   - Click **"Environment Variables..."**
   - Under **System variables**, find **Path** → click **Edit**
   - Click **New** → type `C:\poppler\Library\bin`
   - Click **OK** on all dialogs

4. Verify — open a **new** terminal and run:
   ```
   pdfinfo -v
   ```
   Expected output: `pdfinfo version x.xx`

---

### Step 4 — Clone or Download the Project

**Option A — Git:**
```
git clone https://github.com/medamineessid/hotix-invoice.git
cd hotix-invoice
```

**Option B — Download ZIP:**
1. Go to https://github.com/medamineessid/hotix-invoice
2. Click **Code** → **Download ZIP**
3. Extract to `C:\hotix-invoice`

---

### Step 5 — Run the Setup Script

Open **PowerShell** in the project folder and run:

```powershell
.\scripts\setup.ps1
```

This will:
- Create the Python virtual environment
- Install all Python dependencies (~500 MB, internet required)
- Download the PaddleOCR French language models
- Verify the full environment
- Build the C# client

> This only needs to be done **once per machine**.

---

### Step 6 — Create a Desktop Shortcut (Optional)

Right-click `client\bin\Release\net8.0-windows\Hotix.InvoiceClient.exe` → Send to → Desktop (create shortcut). Rename the shortcut to HOTIX.

---

## For End Users — Daily Use

### Starting the Application

Double-click the **HOTIX** shortcut on the desktop.

A **splash screen** will appear while the OCR server initializes. Once ready, the main application window will open automatically. The server now runs silently in the background with no visible console window.

> The status bar in the top-right corner indicates **"Serveur OCR actif"** when the system is ready. If the server process stops unexpectedly, a red error screen will prompt you to restart.

---

### Extracting Invoices

1. Click **Ajouter...** and select your invoice files or a folder
   - Or drag and drop a folder directly onto the window
   - Supported formats: PDF, JPG, PNG, TIF, BMP

2. **Select the Extraction Engine** in the control panel:
   - **Automatique**: Tries Gemini Vision first, falls back to local OCR if needed.
   - **Gemini Vision**: High accuracy cloud-based extraction (requires API key).
   - **OCR local**: Standard local extraction (no internet required).

3. Click **Lancer l'extraction** (or press `F5`)

4. Wait for the progress bar to complete


---

### Reading the Results

Results appear in the **Résultats** tab with the following columns:

| Column | Description |
|---|---|
| N° Facture | Invoice number |
| Date | Invoice date (YYYY-MM-DD) |
| Fournisseur | Supplier name |
| Client | Client name |
| Montant HT | Amount before tax |
| TVA | VAT amount |
| Taxe | Stamp duty or other tax |
| TTC | Total including tax |
| Confiance | Extraction confidence (green = high, red = low) |
| Fichier | Source file name |

- Fields shown in **grey italic** were not found in the invoice.
- Invoices with missing fields appear in the **Extractions Incomplètes** tab.

### Configuring Gemini API Key

To use Gemini Vision for better accuracy:
1. Click the gear icon **⚙** next to the engine selector.
2. Enter your **Gemini API key** (visit [Google AI Studio](https://aistudio.google.com/app/apikey) to get a free key).
3. Click **Enregistrer**. A green indicator signifies the key is active.

---

### Exporting to Excel

1. Select the rows you want to export (or leave all selected for everything)
2. Click **Exporter en Excel** (or press `Ctrl+E`)
3. Choose where to save the file
4. Click the **"Ouvrir le dossier"** link in the status bar to open the saved location

---

### Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `F5` | Start extraction |
| `Escape` | Cancel extraction in progress |
| `Ctrl+E` | Export to Excel |

---

## Troubleshooting

**"Le serveur OCR s'est arrêté de façon inattendue"**
The background process failed. Close the app and relaunch via the HOTIX shortcut. This can happen if another application is using port 8000.

**"Clé API Gemini non configurée"**
You selected Gemini but haven't provided a key. Use the gear icon **⚙** to set it up or switch back to "OCR local".

**PDF files fail with "Poppler manquant"**
Poppler is not installed or not on PATH. Redo Step 3 of the IT setup.

**Extraction is slow on first run**
PaddleOCR downloads its language models on first use. This is a one-time download requiring internet access.

**Fields are missing or incorrect**
Check the raw OCR text in the preview panel (click the row). If the text itself is garbled, the scan quality is too low — try rescanning at higher resolution (300 DPI minimum).

---

## Re-running Failed Extractions

- Right-click any row → **Relancer l'extraction**
- Or click **Relancer les erreurs** in the summary banner to retry all failed files at once
