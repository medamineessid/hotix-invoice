"""Non-regression tests for the tax_summary kwarg collision in the Gemini path.

Bug: `server.main._run_gemini_extraction()` popped only `items` from the dict
returned by `extract_with_gemini()`, leaving `tax_summary` inside `fields`.
`InvoiceExtractionResponse(**fields, ..., tax_summary=...)` then crashed with
`TypeError: got multiple values for keyword argument 'tax_summary'` — so
engine=gemini always 503'd ("Service Gemini indisponible") and engine=auto
silently fell back to OCR on every request, wasting Gemini quota without ever
returning LLM extraction.

The fix pops `tax_summary` alongside `items` before `fields` is unpacked.
These tests pin that contract for both engine="gemini" (explicit) and
engine="auto" (fallback-allowed), which both go through the same
`_run_gemini_extraction()` function, and assert the response's tax_summary
contains parsed TaxSummaryRow objects rather than the raw dicts.
"""

from __future__ import annotations

import asyncio
import io

import pytest
from PIL import Image

import server.main as main_module
from server.models import InvoiceExtractionResponse, TaxSummaryRow

# Shape of a successful extract_with_gemini() return: 8 flat fields (amounts
# normalized to float by gemini_extractor) + items + tax_summary.
GEMINI_PAYLOAD: dict = {
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


def _make_single_page() -> list:
    """One PIL image page, shaped like load_invoice_images() returns."""
    buf = io.BytesIO()
    Image.new("RGB", (400, 300), (255, 255, 255)).save(buf, format="PNG")
    return [Image.open(buf)]


def _run(engine: str, monkeypatch) -> tuple[InvoiceExtractionResponse | None, str | None]:
    """Drive _run_gemini_extraction with extract_with_gemini mocked."""
    async def fake_extract_with_gemini(image_data: bytes, mime_type: str) -> dict:
        # Return a deep-ish copy so the function under test can pop freely.
        return {
            key: (list(value) if isinstance(value, list) else value)
            for key, value in GEMINI_PAYLOAD.items()
        }

    monkeypatch.setattr(main_module, "extract_with_gemini", fake_extract_with_gemini)

    return asyncio.run(
        main_module._run_gemini_extraction(_make_single_page(), "sample.png", engine)
    )


@pytest.mark.parametrize("engine", ["gemini", "auto"])
def test_run_gemini_extraction_builds_response_with_parsed_tax_summary(engine, monkeypatch) -> None:
    """Both engine modes build the response without the tax_summary collision."""
    res, fallback = _run(engine, monkeypatch)

    assert fallback is None
    assert isinstance(res, InvoiceExtractionResponse)
    assert res.engine_used == "gemini"

    # Amounts normalized to the canonical 3-decimal string at the boundary.
    assert res.montant_ht == "1000.000"
    assert res.montant_tva == "200.000"
    assert res.montant_taxe == "0.000"
    assert res.montant_ttc == "1200.000"

    # Items parsed into InvoiceItem objects.
    assert len(res.items) == 1
    assert res.items[0].designation == "Article A"
    assert res.items[0].prix_unitaire == 400.0

    # tax_summary parsed into TaxSummaryRow objects, NOT the raw dicts.
    assert len(res.tax_summary) == 1
    row = res.tax_summary[0]
    assert isinstance(row, TaxSummaryRow)
    assert row.rate == 0.2
    assert row.base_ht == 1000.0
    assert row.tax_amount == 200.0

    # Reconcile ran cleanly on the consistent payload.
    assert res.amount_mismatch is False
    assert res.confidence == pytest.approx(0.95)


def test_run_gemini_extraction_leaves_no_stray_keys_in_fields(monkeypatch) -> None:
    """Audit: **fields carries exactly the 8 flat fields, nothing else.

    extract_with_gemini returns 10 keys (8 flat + items + tax_summary); both
    composite keys must be popped so InvoiceExtractionResponse (extra="forbid")
    never sees an undeclared or duplicated key.
    """
    res, _ = _run("gemini", monkeypatch)

    assert res is not None
    # If any stray key had leaked into **fields, extra="forbid" would have
    # raised before we ever got here — this is the assertion that matters.
    for field in ("numero_facture", "date", "fournisseur", "client",
                  "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"):
        assert getattr(res, field) is not None
