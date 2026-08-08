"""Regression tests for Bug A — /extract?engine=ocr crashed with NameError.

The audit found that `cluster_rows` was called in server/main.py but never
imported from .utils, so every OCR extraction request 500'd with
`NameError: name 'cluster_rows' is not defined`.  No test exercised the
OCR extraction path, so the regression shipped silently.

These tests reuse the repo's existing synthetic OCR fixture
(invoices/ocr_data/synthetic_000.json) and drive the real FastAPI /extract
endpoint with a deterministic fake OCR engine, closing that coverage gap.
"""

from __future__ import annotations

import io
import json
from pathlib import Path

import pytest
from PIL import Image

from server.main import app
from server.ocr_engine import OCRResult
from server.utils import BoundingBox, OCRLine

FIXTURE_DIR = Path(__file__).resolve().parents[2] / "invoices" / "ocr_data"


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


def _make_png_bytes() -> bytes:
    """A minimal valid PNG that the ingestion layer can open."""
    buf = io.BytesIO()
    Image.new("RGB", (400, 300), (255, 255, 255)).save(buf, format="PNG")
    return buf.getvalue()


def test_extract_engine_ocr_endpoint_populates_fields(client, fake_ocr_engine) -> None:
    """POST /extract?engine=ocr returns 200 with populated fields.

    This is the exact request that 500'd with
    `NameError: name 'cluster_rows' is not defined` before the import fix.
    """
    resp = client.post(
        "/extract?engine=ocr",
        files={"file": ("synthetic_000.png", _make_png_bytes(), "image/png")},
    )
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert body["engine_used"] == "ocr"
    assert body["numero_facture"] == "INV-2024-001"
    assert body["date"] == "2024-03-15"
    assert body["fournisseur"] == "SARL Dupont et Fils"
    assert body["client"] == "Entreprise Martin EURL"
    assert body["montant_ht"] == "1250.000"
    assert body["montant_tva"] == "250.000"
    assert body["montant_ttc"] == "1500.000"
    assert body["confidence"] > 0
    assert isinstance(body["tax_summary"], list)
    assert body["raw_text"]
