"""Adversarial / fault-injection tests for server/field_extractor.py."""

from __future__ import annotations

import pytest

from server.field_extractor import (
    _candidate_is_plausible,
    _clean_candidate_value,
    _contains_any_alias,
    _extract_field_selections,
    _extract_inline_value,
    _looks_like_label,
    _score_candidate,
    extract_field_confidences,
    extract_invoice_fields,
    extract_raw_text,
)
from server.utils import BoundingBox, OCRLine


# ── extract_invoice_fields ────────────────────────────────────────────────────


class TestExtractInvoiceFields:
    """Adversarial / edge-case inputs for the top-level field extraction."""

    def test_empty_lines(self):
        """Empty OCR line list must return None for all fields."""
        fields = extract_invoice_fields([])
        assert all(v is None for v in fields.values())
        assert set(fields.keys()) == {
            "numero_facture", "date", "fournisseur", "client",
            "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
        }

    def test_only_garbage_lines(self):
        """Lines with no recognizable labels produce all-None fields."""
        lines = [
            OCRLine("sdlkfjsldkfj", BoundingBox(0, 0, 10, 10), 0.5, 0, 0),
            OCRLine("zxcvzxcvzxcv", BoundingBox(0, 15, 10, 25), 0.6, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert all(v is None for v in fields.values())

    def test_label_with_no_value(self):
        """A label with no following candidate line should produce None value."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            # No next line with a value
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is None

    def test_label_with_value_on_next_line(self):
        """A label followed by a number on the next line should extract."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            OCRLine("1234.56", BoundingBox(0, 15, 40, 25), 0.95, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is not None

    def test_multi_page_empty_second_page(self):
        """Lines spanning pages with empty second page."""
        lines = [
            OCRLine("Facture", BoundingBox(0, 0, 30, 10), 0.8, 0, 0),
            OCRLine("No 123", BoundingBox(0, 15, 30, 25), 0.9, 0, 1),
            # page_index=1 has no lines
        ]
        fields = extract_invoice_fields(lines)
        assert fields["numero_facture"] is not None

    def test_inline_value_with_colon(self):
        """Value on same line after colon."""
        lines = [
            OCRLine("Date: 15/03/2024", BoundingBox(0, 0, 60, 10), 0.9, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["date"] is not None

    def test_inline_value_no_colon(self):
        """Value on same line after label without colon."""
        lines = [
            OCRLine("Date 15/03/2024", BoundingBox(0, 0, 60, 10), 0.9, 0, 0),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["date"] == "2024-03-15"

    def test_multiple_candidates_best_wins(self):
        """When multiple lines match a field, the best-scored one wins."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            OCRLine("100.00", BoundingBox(0, 15, 40, 25), 0.95, 0, 1),
            OCRLine("TTC", BoundingBox(100, 0, 150, 10), 0.8, 0, 2),
            OCRLine("200.00", BoundingBox(100, 15, 140, 25), 0.9, 0, 3),
        ]
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is not None

    def test_oversized_amount_from_ocr_garbage(self):
        """A gargantuan number read as OCR garbage in a monetary field
        must not crash (regression guard for the extract_amount fix)."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.9, 0, 0),
            OCRLine(
                "999999999999999999999999999999999999999.99",
                BoundingBox(0, 15, 100, 25), 0.7, 0, 1,
            ),
        ]
        # Must not raise InvalidOperation
        fields = extract_invoice_fields(lines)
        assert fields["montant_ttc"] is None

    def test_zero_length_text(self):
        """Zero-length or whitespace-only lines should be harmless."""
        lines = [
            OCRLine("", BoundingBox(0, 0, 5, 5), 0.0, 0, 0),
            OCRLine(" ", BoundingBox(0, 6, 5, 11), 0.0, 0, 1),
            OCRLine("  \t", BoundingBox(0, 12, 5, 17), 0.0, 0, 2),
        ]
        fields = extract_invoice_fields(lines)
        assert all(v is None for v in fields.values())

    def test_all_confidences_zero(self):
        """Zero-confidence lines should still be processed."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 50, 10), 0.0, 0, 0),
            OCRLine("123.45", BoundingBox(0, 15, 40, 25), 0.0, 0, 1),
        ]
        fields = extract_invoice_fields(lines)
        # Should still extract with 0 confidence
        confidences = extract_field_confidences(lines)
        assert fields["montant_ttc"] is not None
        assert confidences["montant_ttc"] == 0.0


# ── extract_field_confidences ─────────────────────────────────────────────────


class TestExtractFieldConfidences:
    def test_empty(self):
        confs = extract_field_confidences([])
        assert all(c == 0.0 for c in confs.values())

    def test_all_fields_present(self):
        confs = extract_field_confidences([])
        assert set(confs.keys()) == {
            "numero_facture", "date", "fournisseur", "client",
            "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
        }


# ── extract_raw_text ──────────────────────────────────────────────────────────


class TestExtractRawText:
    def test_empty(self):
        assert extract_raw_text([]) == ""

    def test_single_line(self):
        lines = [OCRLine("hello", BoundingBox(0, 0, 10, 5), 0.5, 0, 0)]
        assert extract_raw_text(lines) == "hello"

    def test_empty_lines_skipped(self):
        lines = [
            OCRLine("", BoundingBox(0, 0, 5, 5), 0.0, 0, 0),
            OCRLine("hello", BoundingBox(0, 6, 10, 11), 0.5, 0, 1),
        ]
        assert extract_raw_text(lines) == "hello"


# ── Internal helpers ──────────────────────────────────────────────────────────


class TestContainsAnyAlias:
    def test_exact_match(self):
        assert _contains_any_alias("total ttc", ["ttc"]) is True

    def test_normalized_match(self):
        assert _contains_any_alias("Total TTC", ["ttc"]) is True

    def test_no_match(self):
        assert _contains_any_alias("hello world", ["ttc"]) is False

    def test_empty_text(self):
        assert _contains_any_alias("", ["ttc"]) is False


class TestExtractInlineValue:
    def test_with_colon(self):
        val = _extract_inline_value("montant_ttc", "TTC: 123.45")
        assert val is not None

    def test_without_colon(self):
        val = _extract_inline_value("date", "Date 15/03/2024")
        assert val is not None

    def test_no_match(self):
        val = _extract_inline_value("montant_ttc", "hello world")
        assert val is None


class TestCandidateIsPlausible:
    def test_short_candidate(self):
        c = OCRLine("x", BoundingBox(0, 0, 5, 5), 0.5, 0, 0)
        assert _candidate_is_plausible(c) is False

    def test_looks_like_label(self):
        c = OCRLine("facture", BoundingBox(0, 0, 20, 10), 0.5, 0, 0)
        assert _candidate_is_plausible(c) is False

    def test_valid_candidate(self):
        c = OCRLine("123.45", BoundingBox(0, 0, 20, 10), 0.9, 0, 0)
        assert _candidate_is_plausible(c) is True


class TestLooksLikeLabel:
    def test_label_keywords(self):
        assert _looks_like_label("Total TTC") is True
        assert _looks_like_label("Numero Facture") is True

    def test_not_a_label(self):
        assert _looks_like_label("ABC-123") is False

    def test_value_text(self):
        assert _looks_like_label("123.45") is False


class TestCleanCandidateValue:
    def test_numeric_field(self):
        val = _clean_candidate_value("montant_ttc", "123.45")
        assert val is not None
        assert float(val) == 123.45  # noqa: PLR2004

    def test_date_field(self):
        val = _clean_candidate_value("date", "15/03/2024")
        assert val == "2024-03-15"

    def test_empty(self):
        assert _clean_candidate_value("montant_ttc", "") is None

    def test_oversized_amount(self):
        """Oversized decimal in candidate value must not crash."""
        val = _clean_candidate_value(
            "montant_ttc", "999999999999999999999999999999999999999.99",
        )
        assert val is None


class TestScoreCandidate:
    def test_same_line_score_high(self):
        anchor = OCRLine("TTC:", BoundingBox(0, 0, 30, 10), 0.9, 0, 0)
        candidate = OCRLine("123.45", BoundingBox(35, 0, 65, 10), 0.95, 0, 0)
        score = _score_candidate(anchor, candidate, same_line=True)
        assert score > 0

    def test_next_line_score(self):
        anchor = OCRLine("TTC", BoundingBox(0, 0, 30, 10), 0.9, 0, 0)
        candidate = OCRLine("123.45", BoundingBox(0, 15, 40, 25), 0.95, 0, 1)
        score = _score_candidate(anchor, candidate, same_line=False)
        assert score > 0
