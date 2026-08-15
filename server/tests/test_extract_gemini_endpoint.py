"""End-to-end regression tests for the Gemini extraction path.

These drive the REAL FastAPI POST /extract route (TestClient) with the Gemini
API call stubbed, closing the gap left by the unit-level
test_gemini_tax_summary_kwarg.py tests: they prove the route-level wiring
works end to end for engine=gemini (explicit), engine=auto (prefers Gemini),
the auto→OCR fallback, and the explicit-gemini 503.

Before the tax_summary kwarg fix, engine=gemini always returned 503
("Service Gemini indisponible") and engine=auto silently fell back to OCR —
these tests pin the corrected behaviour.
"""

from __future__ import annotations

import io
import json
from pathlib import Path

import pytest
from PIL import Image

import server.main as main_module
from server.gemini_extractor import GeminiExtractionError
from server.main import app
from server.ocr_engine import OCRResult
from server.utils import BoundingBox, OCRLine

FIXTURE_DIR = Path(__file__).resolve().parents[2] / "invoices" / "ocr_data"


def _make_png_bytes() -> bytes:
    """A minimal valid PNG that the ingestion layer can open."""
    buf = io.BytesIO()
    Image.new("RGB", (400, 300), (255, 255, 255)).save(buf, format="PNG")
    return buf.getvalue()


def _gemini_payload() -> dict:
    """Valid extract_with_gemini() return: 8 flat fields + items + tax_summary."""
    return {
        "numero_facture": "FAC-2026-001",
        "date": "2026-08-14",
        "fournisseur": "ACME SARL",
        "client": "Client Test",
        "montant_ht": 1000.0,
        "montant_tva": 200.0,
        "montant_taxe": 0.0,
        "montant_ttc": 1200.0,
        "items": [
            {
                "designation": "Article A",
                "quantite": 2.0,
                "unit": "pce",
                "prix_unitaire": 400.0,
                "tva_rate": 0.2,
                "montant": 800.0,
            },
        ],
        "tax_summary": [
            {"rate": 0.2, "base_ht": 1000.0, "tax_amount": 200.0},
        ],
    }


@pytest.fixture()
def stub_gemini_success(monkeypatch):
    """Stub extract_with_gemini with a full valid payload."""
    async def fake_extract_with_gemini(image_data: bytes, mime_type: str) -> dict:
        return {
            key: (list(value) if isinstance(value, list) else value)
            for key, value in _gemini_payload().items()
        }

    monkeypatch.setattr(main_module, "extract_with_gemini", fake_extract_with_gemini)


@pytest.fixture()
def stub_gemini_failure(monkeypatch):
    """Stub extract_with_gemini to raise a Gemini error."""
    async def fake_extract_with_gemini(image_data: bytes, mime_type: str) -> dict:
        raise GeminiExtractionError("Clé API Gemini non configurée")

    monkeypatch.setattr(main_module, "extract_with_gemini", fake_extract_with_gemini)


def _load_synthetic_lines(fixture_name: str = "synthetic_000.json") -> list[OCRLine]:
    """Load OCR lines from the repo's synthetic invoice fixture."""
    payload = json.loads((FIXTURE_DIR / fixture_name).read_text(encoding="utf-8"))
    return [
        OCRLine(
            text=entry["text"],
            box=BoundingBox(
                entry["box"]["x1"], entry["box"]["y1"],
                entry["box"]["x2"], entry["box"]["y2"],
            ),
            confidence=entry["confidence"],
            page_index=entry.get("page_index", 0),
            line_index=entry.get("line_index", i),
        )
        for i, entry in enumerate(payload)
    ]


class _FakeOcrEngine:
    """Deterministic stand-in for PaddleOcrEngine fed from the fixture."""

    def __init__(self, lines: list[OCRLine]) -> None:
        self._lines = lines

    def recognize(self, page_image, page_index: int) -> OCRResult:
        return OCRResult(lines=self._lines, raw_text="\n".join(l.text for l in self._lines))


@pytest.fixture()
def fake_ocr_engine(monkeypatch):
    """Swap app.state.ocr_engine for the fixture-backed fake."""
    engine = _FakeOcrEngine(_load_synthetic_lines())
    monkeypatch.setattr(app.state, "ocr_engine", engine)
    return engine


def _post_extract(client, engine: str):
    return client.post(
        f"/extract?engine={engine}",
        files={"file": ("sample.png", _make_png_bytes(), "image/png")},
    )


def test_extract_engine_gemini_endpoint_returns_full_response(client, stub_gemini_success) -> None:
    """engine=gemini (explicit) now returns 200 with a fully populated response.

    Before the tax_summary kwarg fix this exact request 503'd with
    `TypeError: got multiple values for keyword argument 'tax_summary'`.
    """
    resp = _post_extract(client, "gemini")
    assert resp.status_code == 200, resp.text
    body = resp.json()

    assert body["engine_used"] == "gemini"
    assert body["numero_facture"] == "FAC-2026-001"
    assert body["date"] == "2026-08-14"
    assert body["fournisseur"] == "ACME SARL"
    assert body["client"] == "Client Test"
    # Amounts normalized to the canonical 3-decimal string at the boundary
    assert body["montant_ht"] == "1000.000"
    assert body["montant_tva"] == "200.000"
    assert body["montant_taxe"] == "0.000"
    assert body["montant_ttc"] == "1200.000"
    assert body["confidence"] > 0

    # Items parsed into the wire contract
    assert len(body["items"]) == 1
    assert body["items"][0]["designation"] == "Article A"
    assert body["items"][0]["prix_unitaire"] == 400.0

    # tax_summary parsed into canonical rows (rate/base_ht/tax_amount)
    assert len(body["tax_summary"]) == 1
    assert body["tax_summary"][0] == {"rate": 0.2, "base_ht": 1000.0, "tax_amount": 200.0}


def test_extract_engine_auto_prefers_gemini_when_it_succeeds(client, stub_gemini_success) -> None:
    """engine=auto now actually uses Gemini instead of silently falling back to OCR."""
    resp = _post_extract(client, "auto")
    assert resp.status_code == 200, resp.text
    body = resp.json()

    assert body["engine_used"] == "gemini"
    assert body["gemini_fallback_reason"] is None
    assert body["montant_ttc"] == "1200.000"


def test_extract_engine_auto_falls_back_to_ocr_when_gemini_fails(
    client, stub_gemini_failure, fake_ocr_engine,
) -> None:
    """engine=auto still falls back to OCR when Gemini fails, with the reason surfaced."""
    resp = _post_extract(client, "auto")
    assert resp.status_code == 200, resp.text
    body = resp.json()

    assert body["engine_used"] == "ocr"
    assert body["gemini_fallback_reason"] is not None
    assert "Clé API Gemini non configurée" in body["gemini_fallback_reason"]
    assert body["numero_facture"] == "INV-2024-001"


def test_extract_engine_gemini_raises_503_when_api_fails(client, stub_gemini_failure) -> None:
    """engine=gemini (explicit) surfaces failures as 503, no silent OCR fallback."""
    resp = _post_extract(client, "gemini")
    assert resp.status_code == 503
    assert "Clé API Gemini non configurée" in resp.json()["detail"]
