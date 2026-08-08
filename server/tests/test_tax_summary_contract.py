"""Regression tests for Bug B — the tax_summary wire key must be `tax_amount`.

The server emitted `tva_amount` while the WPF client's TaxSummaryRow
deserializes `tax_amount` (JsonPropertyName).  The mismatch silently
blanked the "Tax Amount" column in Excel exports.

These tests pin the contract at the JSON boundary — extraction output dict,
the Pydantic model, and the full /extract response serialization — so the
two sides cannot drift apart again.
"""

from __future__ import annotations

import json
from pathlib import Path

from server.field_extractor import extract_tax_summary
from server.models import InvoiceExtractionResponse, TaxSummaryRow
from server.utils import BoundingBox, OCRLine, cluster_rows


def _tax_line(text: str, x1: float, y1: float, x2: float, y2: float,
              conf: float = 0.95, line_index: int = 0) -> OCRLine:
    return OCRLine(text, BoundingBox(x1, y1, x2, y2), conf, 0, line_index)


def _lines_with_tax_block() -> list[OCRLine]:
    """Item-table row plus a 'TVA 20%' tax-summary row (base HT + VAT amount).

    The tax block label and amounts must be horizontally adjacent (gap below
    cluster_rows' 5x-height sub-row split threshold) so they stay in one row,
    exactly like a real invoice's tax-summary line.
    """
    return [
        # Item table (realistic context above the tax block)
        _tax_line("Désignation", 10, 100, 120, 115, line_index=0),
        _tax_line("Montant HT", 250, 100, 340, 115, line_index=1),
        _tax_line("Widget", 10, 120, 100, 135, line_index=2),
        _tax_line("1000.00", 250, 120, 340, 135, line_index=3),
        # Tax summary block: TVA 20% | base HT 1000.00 | VAT 200.00
        _tax_line("TVA 20%", 10, 200, 180, 215, line_index=4),
        _tax_line("1000.00", 200, 200, 270, 215, line_index=5),
        _tax_line("200.00", 280, 200, 350, 215, line_index=6),
    ]


def test_extract_tax_summary_emits_tax_amount_key() -> None:
    """field_extractor output dict uses the client's key, not tva_amount."""
    lines = _lines_with_tax_block()
    rows = cluster_rows(lines)
    summary = extract_tax_summary(lines, rows)

    assert len(summary) == 1
    row = summary[0]
    assert row["rate"] == 0.2
    assert row["base_ht"] == 1000.0
    assert row["tax_amount"] == 200.0
    assert "tva_amount" not in row


def test_tax_summary_row_serializes_with_client_key() -> None:
    """The Pydantic model serializes the exact key the client deserializes."""
    row = TaxSummaryRow(rate=0.2, base_ht=1000.0, tax_amount=200.0)
    payload = json.loads(row.model_dump_json())
    assert payload == {"rate": 0.2, "base_ht": 1000.0, "tax_amount": 200.0}
    assert "tva_amount" not in payload


def test_full_response_round_trip_uses_tax_amount() -> None:
    """Extraction output -> InvoiceExtractionResponse JSON keeps tax_amount."""
    lines = _lines_with_tax_block()
    rows = cluster_rows(lines)
    summary = extract_tax_summary(lines, rows)

    response = InvoiceExtractionResponse(
        numero_facture="INV-1",
        date="2024-03-15",
        engine_used="ocr",
        tax_summary=[TaxSummaryRow(**row) for row in summary],
    )
    payload = json.loads(response.model_dump_json())
    assert payload["tax_summary"][0]["tax_amount"] == 200.0
    assert "tva_amount" not in payload["tax_summary"][0]


def test_no_old_wire_key_in_server_source() -> None:
    """Drift sentinel: the old server-side wire key must never come back.

    Bug B was exactly this drift (server emitted `tva_amount` while the
    client deserializes `tax_amount`).  Reading the server source and
    failing on the old literal makes the contract self-enforcing, so a
    rename can never silently drift apart again.
    """
    server_dir = Path(__file__).resolve().parents[1]
    offenders = [
        name
        for name in ("models.py", "field_extractor.py", "gemini_extractor.py", "main.py")
        if "tva_amount" in (server_dir / name).read_text(encoding="utf-8")
    ]
    assert not offenders, f"Old 'tva_amount' wire key reintroduced in: {offenders}"
