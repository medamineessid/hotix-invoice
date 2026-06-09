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


@app.post("/extract", response_model=InvoiceExtractionResponse)
async def extract(file: UploadFile = File(...)) -> InvoiceExtractionResponse:
    """Extract invoice fields from an uploaded PDF or image file."""

    filename = file.filename or ""
    suffix = Path(filename).suffix.lower()
    if suffix not in SUPPORTED_SUFFIXES:
        raise HTTPException(status_code=400, detail=f"Unsupported file type: {suffix}")

    engine: PaddleOcrEngine = app.state.ocr_engine
    temp_path: Path | None = None

    try:
        file_bytes = await file.read()

        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
            temp_path = Path(tmp.name)
            tmp.write(file_bytes)

        logger.info("Received upload: %s", filename)

        poppler_path = os.getenv("POPPLER_PATH")
        pages = load_invoice_images(file_bytes, filename, poppler_path=poppler_path)

        all_lines = []
        for page_index, page_image in enumerate(pages):
            result = engine.recognize(page_image, page_index)
            all_lines.extend(result.lines)

        fields = extract_invoice_fields(all_lines)
        confidences = extract_field_confidences(all_lines)
        raw_text = extract_raw_text(all_lines)
        confidence = compute_confidence(confidences)

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
