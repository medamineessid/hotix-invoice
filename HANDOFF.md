# HOTIX Invoice Extraction - Copilot Brief

Use this as the next conversion brief for a local invoice extraction system.

## Goal

Build a fully local pipeline where a C# .NET client scans a folder of invoice files, sends each file to a local Python FastAPI server, receives extracted invoice data as JSON, and writes the results to Excel.

## Target Fields

- `numero_facture` / N° Facture
- `date`
- `fournisseur`
- `client`
- `montant_ht`
- `montant_tva`
- `montant_taxe`
- `montant_ttc`

## Python Server Architecture

- `main.py` - FastAPI app with a single `POST /extract` endpoint
- `ingestion.py` - accepts file bytes and returns a list of PIL images
- `ocr_engine.py` - runs PaddleOCR on a PIL image and returns text, bounding boxes, and confidence
- `field_extractor.py` - takes OCR output and returns the 8 fields using heuristics and label proximity
- `models.py` - Pydantic request and response models

## API Contract

- `POST /extract`
- `Content-Type: multipart/form-data`
- Body field: `file`

Response:

```json
{
  "numero_facture": "string or null",
  "date": "string or null",
  "fournisseur": "string or null",
  "client": "string or null",
  "montant_ht": "string or null",
  "montant_tva": "string or null",
  "montant_taxe": "string or null",
  "montant_ttc": "string or null",
  "confidence": 0.0,
  "raw_text": "full OCR text for debugging"
}
```

## Extraction Rules

- Do not use pure regex for extraction.
- Run PaddleOCR first to get text plus bounding boxes.
- Detect invoice keywords in French/Tunisian layouts.
- For each label, locate the nearest value using bounding-box geometry.
- Prefer values to the right of the label, then below it.
- Use regex only as a secondary validation layer.
- Return `null` when a field is not found. Never guess.
- Ignore Arabic text and extract French fields only.

## Keyword Hints

- `numero_facture`: `N° Facture`, `Numéro`, `Facture N°`
- `date`: `Date`, `Date de facturation`, `Date facture`
- `fournisseur`: `Fournisseur`, `Vendeur`, `Émetteur`
- `client`: `Client`, `Acheteur`, `Destinataire`
- `montant_ht`: `Montant HT`, `Total HT`, `HT`
- `montant_tva`: `TVA`, `Montant TVA`
- `montant_taxe`: `Taxe`, `Montant Taxe`
- `montant_ttc`: `TTC`, `Montant TTC`, `Total TTC`, `Net à payer`

## Parsing Rules

- Amounts must be normalized as strings with three decimals, such as `1000.000`.
- Handle comma or dot decimal separators, including French locale input.
- Strip currency symbols such as `DT`, `TND`, `€`, `$`, and `£`.
- Dates should be normalized to `YYYY-MM-DD` when possible.
- Support `DD/MM/YYYY`, `DD-MM-YYYY`, and French month names.

## C# Client Architecture

- `Program.cs` - entry point, reads folder path from args or config
- `InvoiceClient.cs` - HttpClient wrapper that posts each file to `/extract`
- `InvoiceResult.cs` - model matching the JSON response
- `ExcelWriter.cs` - writes `List<InvoiceResult>` to `.xlsx` using ClosedXML

## Client Flow

1. Read all `.pdf`, `.jpg`, `.jpeg`, and `.png` files from the input folder.
2. POST each file to `http://localhost:8000/extract`.
3. Deserialize the JSON response into `InvoiceResult`.
4. Write one Excel row per invoice after all files are processed.
5. Log failed extractions or null-heavy results separately.

## Environment

- Windows 11
- Python 3.12.6
- PowerShell
- Project folder: `C:\hotix-invoice`
- Python venv: `C:\hotix-invoice\venv`
- Poppler must be on `PATH` for `pdf2image`

## Constraints

- Fully local only, no cloud services.
- No paid libraries.
- No training data or fine-tuning.
- Must handle variable invoice layouts.
- French invoices are primary; some files may contain Arabic text, which should be ignored.

## Next Conversion Priority

Implement or align the codebase to this exact architecture, with the extraction logic centered on OCR bounding-box proximity rather than regex-first matching.
