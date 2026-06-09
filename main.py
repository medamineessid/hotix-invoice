"""FastAPI application exposing the HOTIX invoice extraction endpoint."""

from __future__ import annotations

import logging
import os
from typing import Annotated

from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from field_extractor import compute_confidence, extract_field_confidences, extract_invoice_fields, extract_raw_text
from ingestion import IngestionError, load_invoice_images
from models import ErrorResponse, HealthResponse, InvoiceExtractionResponse
from ocr_engine import OcrEngineError, PaddleOcrEngine


logging.basicConfig(
    level=os.getenv("HOTIX_LOG_LEVEL", "INFO"),
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)

LOGGER = logging.getLogger(__name__)

app = FastAPI(
    title="HOTIX Invoice Extraction API",
    version="1.0.0",
    description="Local invoice extraction service using PaddleOCR and geometry-based field matching.",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.state.ocr_engine = PaddleOcrEngine(language="fr")
app.state.poppler_path = os.getenv("POPPLER_PATH")


@app.exception_handler(IngestionError)
async def handle_ingestion_error(_, exc: IngestionError) -> JSONResponse:
    """Convert ingestion failures into HTTP 422 responses."""

    return JSONResponse(status_code=422, content=ErrorResponse(detail=str(exc)).model_dump())


@app.exception_handler(OcrEngineError)
async def handle_ocr_error(_, exc: OcrEngineError) -> JSONResponse:
    """Convert OCR failures into HTTP 422 responses."""

    return JSONResponse(status_code=422, content=ErrorResponse(detail=str(exc)).model_dump())


@app.get("/health")
async def health() -> HealthResponse:
    """Return a simple readiness response."""

    return HealthResponse(status="ok")


@app.post(
    "/extract",
    responses={
        400: {"model": ErrorResponse, "description": "Invalid request or empty file."},
        422: {"model": ErrorResponse, "description": "Ingestion or OCR failure."},
        500: {"model": ErrorResponse, "description": "Unexpected server error."},
    },
)
async def extract(file: Annotated[UploadFile, File(...)] ) -> InvoiceExtractionResponse:
    """Extract invoice fields from an uploaded PDF or image file."""

    if not file.filename:
        raise HTTPException(status_code=400, detail="A file name is required.")

    file_bytes = await file.read()
    if not file_bytes:
        raise HTTPException(status_code=400, detail="The uploaded file is empty.")

    try:
        images = load_invoice_images(file_bytes, file.filename, poppler_path=app.state.poppler_path)
        ocr_engine: PaddleOcrEngine = app.state.ocr_engine

        ocr_lines = []
        for page_index, image in enumerate(images):
            page_result = ocr_engine.recognize(image, page_index=page_index)
            ocr_lines.extend(page_result.lines)

        field_values = extract_invoice_fields(ocr_lines)
        field_scores = extract_field_confidences(ocr_lines)
        confidence = compute_confidence(field_scores)
        raw_text = extract_raw_text(ocr_lines)

        return InvoiceExtractionResponse(
            numero_facture=field_values["numero_facture"],
            date=field_values["date"],
            fournisseur=field_values["fournisseur"],
            client=field_values["client"],
            montant_ht=field_values["montant_ht"],
            montant_tva=field_values["montant_tva"],
            montant_taxe=field_values["montant_taxe"],
            montant_ttc=field_values["montant_ttc"],
            confidence=confidence,
            raw_text=raw_text,
        )
    except (IngestionError, OcrEngineError) as exc:
        LOGGER.exception("Extraction failed for %s", file.filename)
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except HTTPException:
        raise
    except Exception as exc:  # pragma: no cover - guard against unexpected runtime failures
        LOGGER.exception("Unexpected extraction error for %s", file.filename)
        raise HTTPException(status_code=500, detail="Unexpected extraction error.") from exc
    finally:
        await file.close()
