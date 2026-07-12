"""FastAPI application for HOTIX invoice extraction."""

from __future__ import annotations

import asyncio
import logging
import os
import tempfile
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

import sentry_sdk
from sentry_sdk.integrations.fastapi import FastApiIntegration
from sentry_sdk.integrations.logging import LoggingIntegration

from .models import InvoiceExtractionResponse
from .ingestion import IngestionError, load_invoice_images
from .ocr_engine import PaddleOcrEngine, OcrEngineError
from .field_extractor import (
    extract_invoice_fields,
    extract_field_confidences,
    extract_raw_text,
    compute_confidence,
)

from typing import Literal
from fastapi import Query
from .gemini_extractor import extract_with_gemini, GeminiExtractionError, load_gemini_api_key, load_gemini_model

logging.basicConfig(
    level=os.getenv("HOTIX_LOG_LEVEL", "INFO"),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger(__name__)

# Initialize Sentry for error tracking
sentry_dsn = os.getenv("SENTRY_DSN")
if sentry_dsn:
    sentry_sdk.init(
        dsn=sentry_dsn,
        integrations=[
            FastApiIntegration(),
            LoggingIntegration(level=logging.INFO, event_level=logging.ERROR),
        ],
        traces_sample_rate=0.1,
        environment=os.getenv("HOTIX_ENV", "production"),
    )
    logger.info("Sentry initialized with DSN: %s", sentry_dsn[:20] + "...")

SUPPORTED_SUFFIXES = {".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}


@asynccontextmanager
async def lifespan(_: FastAPI):
    app.state.ocr_engine = PaddleOcrEngine()

    # Pre-warm PaddleOCR so model loading doesn't block the first real request
    logger.info("Pre-warming PaddleOCR model...")
    try:
        await asyncio.to_thread(lambda: app.state.ocr_engine.ocr)
        logger.info("PaddleOCR model warmed up successfully")
    except Exception as exc:
        logger.warning("Failed to pre-warm PaddleOCR model: %s", exc)

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
    allow_origins=["http://127.0.0.1:8000", "http://localhost:8000"],
    allow_credentials=True,
    allow_methods=["GET", "POST"],
    allow_headers=["*"],
)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/validate-grok-key")
async def validate_grok_key(request: dict) -> dict:
    """Validate a Grok API key by making one lightweight chat completion call."""
    api_key = request.get("api_key", "")
    if not api_key:
        return {"valid": False, "error": "No API key provided"}

    try:
        import httpx
        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        }
        # Use the currently configured Grok model, or default
        grok_model = "grok-4.3"  # default
        import json as _json
        settings_path = Path(__file__).parent / "appsettings.json"
        if settings_path.exists():
            try:
                with open(settings_path, 'r', encoding='utf-8') as _f:
                    _data = _json.load(_f)
                    _m = _data.get("grok_model", "")
                    if _m:
                        grok_model = _m
            except Exception:
                pass
        body = {
            "model": grok_model,
            "messages": [{"role": "user", "content": "ping"}],
            "max_tokens": 1,
        }
        async with httpx.AsyncClient(timeout=10) as client:
            response = await client.post(
                "https://api.x.ai/v1/chat/completions",
                headers=headers,
                json=body,
            )
        if response.status_code == 200:
            return {"valid": True}
        error_body = response.text[:300]
        logger.warning("Grok key validation failed (HTTP %s): %s", response.status_code, error_body)
        return {"valid": False, "error": f"xAI API error ({response.status_code}): {error_body}"}
    except Exception as exc:
        error_str = str(exc)[:500]
        logger.warning("Grok key validation failed: %s", error_str)
        return {"valid": False, "error": error_str}


@app.post("/validate-gemini-key")
async def validate_gemini_key(request: dict) -> dict:
    """Validate a Gemini API key by making one lightweight generateContent call."""
    api_key = request.get("api_key", "")
    if not api_key:
        return {"valid": False, "error": "No API key provided"}

    try:
        import google.genai as genai
        client = genai.Client(api_key=api_key)
        # Use the currently configured model, or default if none selected
        validate_model = load_gemini_model()
        response = client.models.generate_content(
            model=validate_model,
            contents=["ping"],
        )
        if response and response.text:
            return {"valid": True}
        return {"valid": False, "error": "Empty response from Gemini"}
    except Exception as exc:
        error_str = str(exc)[:500]
        logger.warning("Key validation failed: %s", error_str)
        return {"valid": False, "error": error_str}


@app.get("/engine-status")
async def engine_status() -> dict[str, bool]:
    """Check availability of extraction engines.

    NOTE: gemini_available is inferred from key presence rather than a live API
    call, because the client polls this endpoint every 45 seconds and a real
    generate_content call would burn quota on every poll.
    """
    key = load_gemini_api_key()
    gemini_key_configured = bool(key)

    return {
        "gemini_available": gemini_key_configured,
        "gemini_key_configured": gemini_key_configured,
        "ocr_available": hasattr(app.state, "ocr_engine") and app.state.ocr_engine is not None,
    }


async def _extract_first_page_bytes(page) -> bytes:
    from io import BytesIO
    import aiofiles
    from anyio import Path as AsyncPath

    img_buf = BytesIO()
    page.save(img_buf, format="PNG")

    async with aiofiles.tempfile.NamedTemporaryFile(delete=False, suffix=".png") as tmp:
        await tmp.write(img_buf.getvalue())
        tmp_name = tmp.name

    try:
        async with aiofiles.open(tmp_name, "rb") as f:
            return await f.read()
    finally:
        await AsyncPath(tmp_name).unlink()


async def _run_gemini_extraction(
    pages: list, filename: str, engine: str
) -> tuple[InvoiceExtractionResponse | None, str | None]:
    """
    Try Gemini extraction. Returns (response, None) on success
    or (None, error_reason) on failure when falling back is allowed.
    When engine='gemini' (non-auto), raises HTTPException on failure.
    """
    try:
        image_data = await _extract_first_page_bytes(pages[0])
        fields = extract_with_gemini(image_data, "image/png")
        logger.info("Extraction via Gemini Vision successful for %s", Path(filename).name)
        return (
            InvoiceExtractionResponse(
                **fields,
                confidence=0.95,
                raw_text="Extraction via Gemini Vision",
                engine_used="gemini",
            ),
            None,
        )
    except GeminiExtractionError as exc:
        if engine == "gemini":
            logger.error("Gemini extraction failed: %s", exc)
            raise HTTPException(status_code=503, detail=str(exc))
        logger.warning("Gemini failed, falling back to OCR: %s", exc)
        return (None, str(exc))
    except Exception as exc:
        if engine == "gemini":
            logger.exception("Gemini unexpected error")
            raise HTTPException(status_code=503, detail="Gemini service unavailable")
        logger.warning("Gemini unexpected error, falling back to OCR: %s", exc)
        return (None, str(exc))


def _run_ocr_extraction(
    pages: list, filename: str, ocr_engine: PaddleOcrEngine
) -> InvoiceExtractionResponse:
    all_lines = []
    for page_index, page_image in enumerate(pages):
        result = ocr_engine.recognize(page_image, page_index)
        all_lines.extend(result.lines)

    fields = extract_invoice_fields(all_lines)
    confidences = extract_field_confidences(all_lines)
    raw_text = extract_raw_text(all_lines)
    confidence = compute_confidence(confidences)

    field_names = [k for k, v in fields.items() if v is not None]
    logger.info("Extraction via OCR successful for %s. Fields: %s", Path(filename).name, field_names)

    return InvoiceExtractionResponse(
        **fields,
        confidence=confidence,
        raw_text=raw_text,
        engine_used="ocr",
    )


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

    ocr_engine: PaddleOcrEngine = app.state.ocr_engine

    try:
        file_bytes = await file.read()
        poppler_path = os.getenv("POPPLER_PATH")
        pages = load_invoice_images(file_bytes, filename, poppler_path=poppler_path)
        
        if not pages:
             raise IngestionError("Aucune page trouvée dans le fichier")

        # --- Gemini Path ---
        gemini_fallback_reason: str | None = None
        if engine in ("gemini", "auto"):
            res, gemini_fallback_reason = await _run_gemini_extraction(pages, filename, engine)
            if res is not None:
                return res

        # --- OCR Path ---
        result = await asyncio.to_thread(_run_ocr_extraction, pages, filename, ocr_engine)
        if gemini_fallback_reason:
            result.gemini_fallback_reason = gemini_fallback_reason
        return result

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
