"""FastAPI application for HOTIX invoice extraction."""

from __future__ import annotations

import logging
import os
import tempfile
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from models import InvoiceExtractionResponse
from ingestion import IngestionError, load_invoice_images
from ocr_engine import PaddleOcrEngine, OcrEngineError
from field_extractor import (
    extract_invoice_fields,
    extract_field_confidences,
    extract_raw_text,
    compute_confidence,
)

# Import gemini_extractor from the root directory
import sys
from typing import Literal
from fastapi import Query
sys.path.append(str(Path(__file__).parent.parent))
from gemini_extractor import extract_with_gemini, GeminiExtractionError, load_gemini_api_key

logging.basicConfig(
    level=os.getenv("HOTIX_LOG_LEVEL", "INFO"),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger(__name__)

SUPPORTED_SUFFIXES = {".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}


@asynccontextmanager
async def lifespan(_: FastAPI):
    app.state.ocr_engine = PaddleOcrEngine()
    logger.info("HOTIX extraction service started")
    yield
    logger.info("HOTIX extraction service stopped")


app = FastAPI(
    title="HOTIX Invoice Extraction API",
    version="1.0.0",
    description="Extract invoice fields from scanned PDFs and images using OCR",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/engine-status")
async def engine_status() -> dict[str, bool]:
    """Check availability of extraction engines."""
    key = load_gemini_api_key()
    gemini_key_configured = bool(key)
    
    gemini_available = False
    if gemini_key_configured:
        try:
            # Test ping for Gemini (simple generation with small prompt)
            import google.generativeai as genai
            genai.configure(api_key=key)
            model = genai.GenerativeModel("gemini-1.5-flash")
            # Minimal prompt to verify key and quota
            model.generate_content("ping", request_options={"timeout": 10})
            gemini_available = True
        except Exception:
            gemini_available = False

    return {
        "gemini_available": gemini_available,
        "gemini_key_configured": gemini_key_configured,
        "ocr_available": hasattr(app.state, "ocr_engine") and app.state.ocr_engine is not None,
    }


@app.post("/extract", response_model=InvoiceExtractionResponse)
async def extract(
    file: UploadFile = File(...),
    engine: Literal["auto", "gemini", "ocr"] = Query(default="auto")
) -> InvoiceExtractionResponse:
    """Extract invoice fields from an uploaded PDF or image file."""

    filename = file.filename or ""
    raw_suffix = Path(filename).suffix.lower()
    if raw_suffix not in SUPPORTED_SUFFIXES:
        raise HTTPException(status_code=400, detail=f"Unsupported file type: {raw_suffix}")

    safe_suffix = raw_suffix
    ocr_engine: PaddleOcrEngine = app.state.ocr_engine

    try:
        file_bytes = await file.read()
        poppler_path = os.getenv("POPPLER_PATH")
        pages = load_invoice_images(file_bytes, filename, poppler_path=poppler_path)
        
        if not pages:
             raise IngestionError("Aucune page trouvée dans le fichier")

        # --- Gemini Path ---
        if engine in ("gemini", "auto"):
            try:
                # Convert first page to bytes for Gemini
                with tempfile.NamedTemporaryFile(delete=False, suffix=".png") as tmp:
                    pages[0].save(tmp.name, format="PNG")
                    with open(tmp.name, "rb") as f:
                        image_data = f.read()
                os.unlink(tmp.name)

                fields = extract_with_gemini(image_data, "image/png")
                logger.info("Extraction via Gemini Vision successful for %s", Path(filename).name)
                
                return InvoiceExtractionResponse(
                    **fields,
                    confidence=0.95,
                    raw_text="Extraction via Gemini Vision",
                )
            except GeminiExtractionError as exc:
                if engine == "gemini":
                    logger.error("Gemini extraction failed: %s", exc)
                    raise HTTPException(status_code=503, detail=str(exc))
                else:
                    logger.warning("Gemini failed, falling back to OCR: %s", exc)
                    # Proceed to OCR path
            except Exception as exc:
                if engine == "gemini":
                    logger.exception("Gemini unexpected error")
                    raise HTTPException(status_code=503, detail="Gemini service unavailable")
                else:
                    logger.warning("Gemini unexpected error, falling back to OCR: %s", exc)

        # --- OCR Path ---
        all_lines = []
        for page_index, page_image in enumerate(pages):
            result = ocr_engine.recognize(page_image, page_index)
            all_lines.extend(result.lines)

        fields = extract_invoice_fields(all_lines)
        confidences = extract_field_confidences(all_lines)
        raw_text = extract_raw_text(all_lines)
        confidence = compute_confidence(confidences)

        # Log field names as requested (not content)
        field_names = [k for k, v in fields.items() if v is not None]
        logger.info("Extraction via OCR successful for %s. Fields: %s", Path(filename).name, field_names)

        return InvoiceExtractionResponse(
            **fields,
            confidence=confidence,
            raw_text=raw_text,
        )

    except (IngestionError, OcrEngineError) as exc:
        logger.exception("Extraction failed for %s", filename)
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except HTTPException:
        raise
    except Exception as exc:
        logger.exception("Unexpected error for %s", filename)
        raise HTTPException(status_code=500, detail="Internal server error") from exc
    finally:
        await file.close()
        if temp_path and temp_path.exists():
            try:
                temp_path.unlink()
            except OSError:
                pass
