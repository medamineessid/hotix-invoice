"""FastAPI application for HOTIX invoice extraction."""

from __future__ import annotations

import asyncio
import io
import json
import logging
import os
import random
import time
from collections import defaultdict
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, File, HTTPException, Query, Request, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response

import sentry_sdk
from sentry_sdk.integrations.fastapi import FastApiIntegration
from sentry_sdk.integrations.logging import LoggingIntegration

from .models import HealthResponse, InvoiceExtractionResponse, InvoiceItem, ApiKeyValidationRequest
from .ingestion import IngestionError, load_invoice_images
from .ocr_engine import PaddleOcrEngine, OcrEngineError
from .field_extractor import (
    extract_invoice_fields,
    extract_field_confidences,
    extract_raw_text,
    extract_item_table,
    cross_validate_fields,
    compute_confidence,
)
from .utils import reconcile_amounts, detect_amount_collision

from typing import Literal
from .gemini_extractor import extract_with_gemini, GeminiExtractionError, load_gemini_api_key, load_gemini_model, _get_settings_path

# Server-wide semaphore that serialises OCR operations.  PaddleOCR is not
# guaranteed thread-safe, and even if it were, concurrent PDF-to-image
# conversions (poppler/pdftoppm) can overwhelm the system.  A single
# permit ensures correctness and predictable memory usage.
_ocr_semaphore = asyncio.Semaphore(1)

# ── OCR Engine Recycling ─────────────────────────────────────────────────────
# PaddleOCR can accumulate memory over repeated calls.  Recycling the engine
# after a fixed number of files releases and re-allocates the underlying
# models, bounding the peak memory footprint.
OCR_ENGINE_RECYCLE_INTERVAL: int = 25

# Maximum uploaded file size (50 MB).  Exceeding this raises HTTP 413 Payload Too Large.
MAX_FILE_SIZE_BYTES: int = 50 * 1024 * 1024

# Try to import psutil for RSS memory measurement; fall back gracefully.
try:
    import psutil as _psutil
except ImportError:
    _psutil = None  # type: ignore[assignment]

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


# ── Rate Limiter (in-memory sliding window) ─────────────────────────────────


class SimpleRateLimiter:
    """In-memory sliding-window rate limiter per client IP.

    Tracks request timestamps per IP within a rolling time window.
    Does not use Redis or external dependencies.
    """

    def __init__(self, max_requests: int = 10, window_seconds: int = 60):
        self.max_requests = max_requests
        self.window = window_seconds
        self._requests: dict[str, list[float]] = defaultdict(list)

    def is_allowed(self, client_ip: str) -> bool:
        now = time.time()
        cutoff = now - self.window
        # Prune expired timestamps
        self._requests[client_ip] = [t for t in self._requests[client_ip] if t > cutoff]
        if len(self._requests[client_ip]) >= self.max_requests:
            return False
        self._requests[client_ip].append(now)
        return True

    def cleanup(self) -> None:
        """Remove expired entries for all IPs (prevent unbounded memory growth)."""
        now = time.time()
        cutoff = now - self.window
        stale_ips = []
        for ip, timestamps in self._requests.items():
            self._requests[ip] = [t for t in timestamps if t > cutoff]
            if not self._requests[ip]:
                stale_ips.append(ip)
        for ip in stale_ips:
            del self._requests[ip]


# Global rate limiter instances for different endpoint groups
_rate_limiter_default = SimpleRateLimiter(max_requests=10, window_seconds=60)
_rate_limiter_validate = SimpleRateLimiter(max_requests=5, window_seconds=60)


def _get_client_ip(request: Request) -> str:
    """Extract client IP from request, handling proxies."""
    forwarded = request.headers.get("X-Forwarded-For", "")
    if forwarded:
        return forwarded.split(",")[0].strip()
    return request.client.host if request.client else "unknown"


def _get_rss_mb() -> float | None:
    """Return current process RSS in MB, or None if psutil is not available."""
    if _psutil is None:
        return None
    try:
        return _psutil.Process().memory_info().rss / (1024 * 1024)
    except Exception:
        return None


def _recycle_ocr_engine(app_state) -> float | None:
    """Recycle the OCR engine: release the old instance and create a fresh one.

    Logs RSS before and after the swap so the memory impact is visible in logs.
    Returns the RSS delta in MB, or None if measurement is unavailable.
    """
    rss_before = _get_rss_mb()
    rss_after: float | None = None

    logger.info(
        "Recycling OCR engine after %d requests (RSS before: %s)",
        app_state.ocr_request_counter,
        f"{rss_before:.1f} MB" if rss_before is not None else "N/A",
    )

    # Release the old engine
    old = getattr(app_state, "ocr_engine", None)
    if old is not None:
        del old

    # Create a fresh engine
    app_state.ocr_engine = PaddleOcrEngine()
    app_state.ocr_request_counter = 0

    rss_after = _get_rss_mb()
    logger.info(
        "OCR engine recycled (RSS after: %s, delta: %s)",
        f"{rss_after:.1f} MB" if rss_after is not None else "N/A",
        f"{rss_after - rss_before:.1f} MB" if (rss_before is not None and rss_after is not None) else "N/A",
    )

    return (rss_after - rss_before) if (rss_before is not None and rss_after is not None) else None


@asynccontextmanager
async def lifespan(_: FastAPI):
    app.state.ocr_engine = PaddleOcrEngine()
    app.state.ocr_request_counter = 0
    app.state.ocr_recycle_lock = asyncio.Lock()

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


@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    """Return detailed component-level health status.

    Checks:
    - OCR engine: instantiated and model loaded
    - Poppler (pdftoppm): binary path configured and exists
    - Gemini: API key configured
    - Grok: API key configured (via appsettings.json)
    """
    ocr_ready = hasattr(app.state, "ocr_engine") and app.state.ocr_engine is not None
    ocr_model_loaded = ocr_ready and getattr(app.state.ocr_engine, "_ocr", None) is not None

    poppler_path = os.getenv("POPPLER_PATH", "")
    poppler_available = bool(poppler_path) and Path(poppler_path).exists()

    gemini_configured = bool(load_gemini_api_key())

    # Check Grok key in appsettings.json
    grok_configured = False
    settings_path = _get_settings_path()
    if settings_path.exists():
        try:
            with open(settings_path, 'r', encoding='utf-8') as _f:
                _data = json.load(_f)
                if _data.get("grok_api_key"):
                    grok_configured = True
        except Exception:
            pass

    status = "ok" if ocr_ready else "degraded"

    return HealthResponse(
        status=status,
        ocr_ready=ocr_ready,
        ocr_model_loaded=ocr_model_loaded,
        poppler_available=poppler_available,
        gemini_configured=gemini_configured,
        grok_configured=grok_configured,
        version="1.0.0",
    )


PREVIEW_ROOT = Path(os.getenv("HOTIX_PREVIEW_ROOT", "./previews")).resolve()


@app.get("/preview")
async def preview(path: str = Query(...)) -> Response:
    """Return the first page of a document as a PNG image for preview.

    For PDFs, renders page 1 via pdf2image. For image files (PNG, JPG, BMP,
    TIFF), returns the raw file bytes with the correct MIME type.

    This endpoint is separate from the extraction pipeline — it is a
    UI-only utility for the preview panel.

    Security: the requested path is resolved against PREVIEW_ROOT and rejected
    if it escapes the sandbox (path traversal protection).
    """
    # Reject absolute paths and obvious traversal
    if path.startswith(("/", "\\")) or ".." in path:
        raise HTTPException(status_code=403, detail="Accès refusé")

    raw = PREVIEW_ROOT / path
    try:
        filepath = raw.resolve()
        if not filepath.is_relative_to(PREVIEW_ROOT):
            raise HTTPException(status_code=403, detail="Accès refusé")
    except (ValueError, RuntimeError):
        raise HTTPException(status_code=403, detail="Accès refusé")

    if not filepath.exists():
        raise HTTPException(status_code=404, detail="Fichier introuvable")

    suffix = filepath.suffix.lower()
    if suffix not in SUPPORTED_SUFFIXES:
        raise HTTPException(status_code=400, detail=f"Type de fichier non supporté : {suffix}")

    if suffix == ".pdf":
        # Render PDF page 1 to PNG using the same pipeline as extraction
        try:
            file_bytes = filepath.read_bytes()
            poppler_path = os.getenv("POPPLER_PATH")
            pages = load_invoice_images(file_bytes, filepath.name, poppler_path=poppler_path)
            if not pages:
                raise HTTPException(status_code=422, detail="Le PDF ne contient aucune page")
            buf = io.BytesIO()
            pages[0].save(buf, format="PNG")
            return Response(content=buf.getvalue(), media_type="image/png")
        except Exception as exc:
            logger.exception("Preview render failed for %s", filepath)
            raise HTTPException(status_code=500, detail=f"Échec du rendu de l'aperçu : {exc}")

    # Image file — return raw bytes with matching MIME type
    mime_map = {
        ".png": "image/png",
        ".jpg": "image/jpeg",
        ".jpeg": "image/jpeg",
        ".bmp": "image/bmp",
        ".tif": "image/tiff",
        ".tiff": "image/tiff",
    }
    try:
        return Response(content=filepath.read_bytes(), media_type=mime_map.get(suffix, "application/octet-stream"))
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"Échec de la lecture du fichier : {exc}")


@app.post("/validate-grok-key")
async def validate_grok_key(
    request: ApiKeyValidationRequest,
    fastapi_request: Request,
) -> dict:
    # Rate limit: 5 req/min per IP
    client_ip = _get_client_ip(fastapi_request)
    if not _rate_limiter_validate.is_allowed(client_ip):
        raise HTTPException(
            status_code=429,
            detail="Trop de requêtes. Réessayez dans une minute.",
        )
    """Validate a Grok API key by making one lightweight chat completion call.
    Retries up to 3 times with exponential backoff on transient errors (429, 503, timeout).
    """
    api_key = request.api_key

    # Resolve Grok model once (outside retry loop — doesn't change between attempts)
    grok_model = "grok-4.3"  # default
    settings_path = _get_settings_path()
    if settings_path.exists():
        try:
            with open(settings_path, 'r', encoding='utf-8') as _f:
                _data = json.load(_f)
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
    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
    }

    max_attempts = 3
    for attempt in range(1, max_attempts + 1):
        try:
            import httpx
            async with httpx.AsyncClient(timeout=10) as client:
                response = await client.post(
                    "https://api.x.ai/v1/chat/completions",
                    headers=headers,
                    json=body,
                )
            if response.status_code == 200:
                return {"valid": True}
            # Transient server errors — retry
            if response.status_code in (429, 503) and attempt < max_attempts:
                delay = min(2 ** attempt, 10) + random.uniform(0, 1)
                logger.warning(
                    "Grok API error %s (attempt %d/%d), retrying in %.1fs...",
                    response.status_code, attempt, max_attempts, delay,
                )
                await asyncio.sleep(delay)
                continue
            error_body = response.text[:300]
            logger.warning(
                "Grok key validation failed (HTTP %s): %s",
                response.status_code, error_body,
            )
            return {"valid": False, "error": f"xAI API error ({response.status_code}): {error_body}"}
        except httpx.TimeoutException:
            if attempt < max_attempts:
                delay = min(2 ** attempt, 10) + random.uniform(0, 1)
                logger.warning(
                    "Grok API timeout (attempt %d/%d), retrying in %.1fs...",
                    attempt, max_attempts, delay,
                )
                await asyncio.sleep(delay)
                continue
            logger.warning("Grok key validation failed after %d attempts: timeout", max_attempts)
            return {"valid": False, "error": "xAI API timeout après 3 tentatives"}
        except Exception as exc:
            error_str = str(exc)[:500]
            # Check for timeout-like errors in exception message
            if "timeout" in error_str.lower() and attempt < max_attempts:
                delay = min(2 ** attempt, 10) + random.uniform(0, 1)
                logger.warning(
                    "Grok API timeout-like error (attempt %d/%d), retrying in %.1fs...",
                    attempt, max_attempts, delay,
                )
                await asyncio.sleep(delay)
                continue
            logger.warning("Grok key validation failed: %s", error_str)
            return {"valid": False, "error": error_str}

    return {"valid": False, "error": "Échec de validation après 3 tentatives"}


@app.post("/validate-gemini-key")
async def validate_gemini_key(
    request: ApiKeyValidationRequest,
    fastapi_request: Request,
) -> dict:
    # Rate limit: 5 req/min per IP
    client_ip = _get_client_ip(fastapi_request)
    if not _rate_limiter_validate.is_allowed(client_ip):
        raise HTTPException(
            status_code=429,
            detail="Trop de requêtes. Réessayez dans une minute.",
        )
    """Validate a Gemini API key by making one lightweight generateContent call."""
    api_key = request.api_key

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


@app.post("/admin/recycle-engine")
async def admin_recycle_engine() -> dict:
    """Force-recycle the OCR engine on demand (for diagnostics/testing).

    Acquires both _ocr_semaphore (to ensure no extraction is in progress)
    and ocr_recycle_lock (to prevent concurrent recycling with the
    interval-triggered path).
    """
    async with _ocr_semaphore:
        async with app.state.ocr_recycle_lock:
            rss_delta = await asyncio.to_thread(_recycle_ocr_engine, app.state)
    logger.info("Manual engine recycle triggered via /admin/recycle-engine")
    return {
        "status": "ok",
        "engine_recycled": True,
        "rss_delta_mb": round(rss_delta, 1) if rss_delta is not None else None,
    }


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


async def _prepare_pages_for_gemini(pages: list) -> bytes:
    """Prepare pages for Gemini by sending the first page (or stacking up to 2).

    For single-page documents, returns the page as PNG bytes.
    For multi-page documents, stacks the first 2 pages vertically with a
    20px white margin, giving Gemini context across page boundaries.
    """
    from io import BytesIO
    from PIL import Image

    if len(pages) == 1:
        buf = BytesIO()
        pages[0].save(buf, format="PNG")
        return buf.getvalue()

    # Stack up to 2 pages vertically with a white margin
    pages_to_send = pages[:2]
    w = max(p.width for p in pages_to_send)
    margin = 20
    total_h = sum(p.height for p in pages_to_send) + margin * (len(pages_to_send) - 1)
    combined = Image.new("RGB", (w, total_h), (255, 255, 255))

    y = 0
    for p in pages_to_send:
        combined.paste(p, (0, y))
        y += p.height + margin

    buf = BytesIO()
    combined.save(buf, format="PNG")
    return buf.getvalue()


async def _run_gemini_extraction(
    pages: list, filename: str, engine: str
) -> tuple[InvoiceExtractionResponse | None, str | None]:
    """
    Try Gemini extraction. Returns (response, None) on success
    or (None, error_reason) on failure when falling back is allowed.
    When engine='gemini' (non-auto), raises HTTPException on failure.
    """
    try:
        image_data = await _prepare_pages_for_gemini(pages)
        result = await extract_with_gemini(image_data, "image/png")
        # Separate items from the flat fields
        gemini_items = result.pop("items", [])
        fields = result  # remaining keys are the 8 flat fields
        # Cross-field validation before reconciliation
        gemini_issues = cross_validate_fields(fields)
        # Reconcile monetary amounts (compute missing, flag mismatches)
        fields, computed_fields, has_mismatch = reconcile_amounts(fields, {})
        # Compute confidence: base on field completeness, penalize for mismatches/collisions
        # Formula: (non-null fields / 8) * 0.95, capped at 0.5 if has_mismatch or collision
        non_null_count = sum(1 for v in fields.values() if v is not None)
        base_confidence = (non_null_count / 8) * 0.95
        collision = detect_amount_collision(fields)
        if has_mismatch or collision:
            gemini_confidence = min(base_confidence, 0.5)
        else:
            gemini_confidence = base_confidence
        parsed_gemini_items = [
            InvoiceItem(
                designation=it.get("designation"),
                quantite=it.get("quantite"),
                prix_unitaire=it.get("prix_unitaire"),
                tva_rate=it.get("tva_rate"),
                montant=it.get("montant"),
            )
            for it in gemini_items if isinstance(it, dict)
        ]
        logger.info(
            "Extraction via Gemini Vision successful for %s. Issues: %s. Items: %d",
            Path(filename).name, gemini_issues, len(parsed_gemini_items),
        )
        return (
            InvoiceExtractionResponse(
                **fields,
                confidence=gemini_confidence,
                raw_text="Extraction via Gemini Vision",
                engine_used="gemini",
                computed_fields=list(computed_fields),
                amount_mismatch=has_mismatch,
                items=parsed_gemini_items,
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
            raise HTTPException(status_code=503, detail="Service Gemini indisponible")
        logger.warning("Gemini unexpected error, falling back to OCR: %s", exc)
        return (None, str(exc))


def _run_ocr_extraction(
    pages: list, filename: str, ocr_engine: PaddleOcrEngine,
    gemini_hint: Optional[str] = None,
) -> InvoiceExtractionResponse:
    """Run OCR extraction, optionally using a Gemini hint for numero_facture.

    When gemini_hint is provided (a numero_facture value from a previous
    Gemini attempt that failed), it is passed to extract_invoice_fields to
    boost matching candidates in the v2 hybrid extraction.  If the OCR path
    finds no numero_facture, the hint is used as a fallback value.
    """
    all_lines = []
    for page_index, page_image in enumerate(pages):
        result = ocr_engine.recognize(page_image, page_index)
        all_lines.extend(result.lines)

    fields = extract_invoice_fields(all_lines, gemini_hint=gemini_hint)

    # Part 1.5 fallback: if OCR found no numero_facture but Gemini hint exists,
    # inject it as a fallback value.
    if gemini_hint and not fields.get("numero_facture"):
        fields["numero_facture"] = gemini_hint
        logger.info("Injected Gemini hint as fallback numero_facture: %s", gemini_hint)
    confidences = extract_field_confidences(all_lines)
    # Cross-field validation before reconciliation
    issues = cross_validate_fields(fields)
    # Reconcile monetary amounts (compute missing, flag mismatches — never overwrite high-confidence)
    fields, computed_fields, has_mismatch = reconcile_amounts(fields, confidences)
    raw_text = extract_raw_text(all_lines)
    # Confidence with penalties for extraction quality issues
    confidence = compute_confidence(confidences, fields, issues)
    # Cap confidence at 0.5 if mismatch or collision detected
    collision = detect_amount_collision(fields)
    if has_mismatch or collision:
        confidence = min(confidence, 0.5)

    field_names = [k for k, v in fields.items() if v is not None]
    # Item-table extraction (Prompt 7)
    items = extract_item_table(all_lines)
    parsed_items = [
        InvoiceItem(
            designation=it.get("designation"),
            quantite=it.get("quantite"),
            prix_unitaire=it.get("prix_unitaire"),
            tva_rate=it.get("tva_rate"),
            montant=it.get("montant"),
        )
        for it in items if isinstance(it, dict)
    ]

    logger.info(
        "Extraction via OCR successful for %s. Fields: %s. Issues: %s. Items: %d",
        Path(filename).name, field_names, issues, len(parsed_items),
    )

    return InvoiceExtractionResponse(
        **fields,
        confidence=confidence,
        raw_text=raw_text,
        engine_used="ocr",
        computed_fields=list(computed_fields),
        amount_mismatch=has_mismatch,
        items=parsed_items,
    )


async def _do_extract(
    file: UploadFile,
    engine: Literal["auto", "gemini", "ocr"],
) -> InvoiceExtractionResponse:
    """Actual extraction logic, separated for timeout wrapping."""
    filename = file.filename or ""
    raw_suffix = Path(filename).suffix.lower()
    if raw_suffix not in SUPPORTED_SUFFIXES:
        raise HTTPException(status_code=400, detail=f"Type de fichier non supporté : {raw_suffix}")

    ocr_engine: PaddleOcrEngine = app.state.ocr_engine
    file_bytes = await file.read()

    # Reject files exceeding the size limit
    if len(file_bytes) > MAX_FILE_SIZE_BYTES:
        raise HTTPException(
            status_code=413,
            detail="Fichier trop volumineux (max 50 Mo)",
        )

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
    async with _ocr_semaphore:
        result = await asyncio.to_thread(_run_ocr_extraction, pages, filename, ocr_engine)

        # Increment + recycle under a single lock
        async with app.state.ocr_recycle_lock:
            app.state.ocr_request_counter += 1
            if app.state.ocr_request_counter >= OCR_ENGINE_RECYCLE_INTERVAL:
                _recycle_ocr_engine(app.state)

    if gemini_fallback_reason:
        result.gemini_fallback_reason = gemini_fallback_reason
    return result


@app.post("/extract", response_model=InvoiceExtractionResponse)
async def extract(
    request: Request,
    file: UploadFile = File(...),
    engine: Literal["auto", "gemini", "ocr"] = Query(default="auto")
) -> InvoiceExtractionResponse:
    """Extract invoice fields from an uploaded PDF or image file.

    Wrapped in asyncio.wait_for with a 120-second timeout to prevent
    hanging on corrupted files or PaddleOCR deadlocks.

    Rate-limited to 10 requests per minute per IP.
    """
    # Rate limit: 10 req/min per IP
    client_ip = _get_client_ip(request)
    if not _rate_limiter_default.is_allowed(client_ip):
        raise HTTPException(
            status_code=429,
            detail="Trop de requêtes. Réessayez dans une minute.",
        )

    try:
        return await asyncio.wait_for(
            _do_extract(file, engine),
            timeout=120.0,
        )
    except asyncio.TimeoutError:
        logger.error("Extraction timeout for %s", file.filename)
        raise HTTPException(
            status_code=504,
            detail="Délai d'extraction dépassé (120s)",
        )
    except (IngestionError, OcrEngineError) as exc:
        logger.exception("Extraction failed for %s", file.filename or "unknown")
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except HTTPException:
        raise
    except Exception as exc:
        logger.exception("Unexpected error for %s", file.filename or "unknown")
        raise HTTPException(status_code=500, detail="Erreur interne du serveur") from exc
    finally:
        await file.close()
