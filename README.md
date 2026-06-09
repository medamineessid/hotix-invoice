# HOTIX Invoice Extraction

Local invoice extraction system for scanned PDFs and images.

## Repository Layout

- `main.py` - FastAPI app exposing `POST /extract`
- `models.py` - Pydantic request and response models
- `ingestion.py` - PDF/image ingestion and conversion to PIL images
- `ocr_engine.py` - PaddleOCR wrapper returning OCR lines with bounding boxes
- `field_extractor.py` - Keyword and geometry-based field extraction
- `utils.py` - Shared normalization and bounding-box helpers
- `client/` - .NET client project that scans a folder and writes Excel output

## Python Server Setup

1. Activate the virtual environment.
2. Install dependencies:

```bash
pip install -r requirements.txt
```

3. Make sure Poppler is installed and available on `PATH`.

If Poppler is installed in a custom folder, set `POPPLER_PATH` to that directory before starting the API.

## Run the FastAPI Server

```bash
uvicorn main:app --reload
```

## API Endpoint

- `POST /extract`
- `multipart/form-data`
- Body field: `file`

Supported uploads:

- PDF
- JPG
- JPEG
- PNG
- TIF
- TIFF

## Python Verification

```bash
python verify_system.py
python test_ocr.py
```

## C# Client

The C# client lives in `client/` and includes:

- `Program.cs`
- `InvoiceClient.cs`
- `InvoiceResult.cs`
- `ExcelWriter.cs`
- `Hotix.InvoiceClient.csproj`

Run it from the `client` folder with `dotnet run`, or pass an input folder, output file, and optional API URL:

```bash
dotnet run -- "C:\Invoices" "C:\Invoices\hotix_invoice_results.xlsx" "http://localhost:8000"
```

## Response Shape

```json
{
  "numero_facture": "FAC-2026-001",
  "date": "2026-05-01",
  "fournisseur": "ABC SARL",
  "client": "HOTIX",
  "montant_ht": "1000.000",
  "montant_tva": "190.000",
  "montant_taxe": "0.000",
  "montant_ttc": "1190.000",
  "confidence": 0.94,
  "raw_text": "..."
}
```
