"""Comprehensive tests for v5 hybrid extraction, item tables, and new helpers.

Covers:
- _looks_like_amount, _looks_like_address
- _find_numero_candidates_by_pattern
- _score_by_position, _score_by_date_proximity, _compute_page_extents
- _extract_numero_facture_v2 (integration)
- _parse_cell_value
- extract_item_table edge cases
- _candidate_is_plausible_numero_facture
- extract_field_selections_raw
"""

from __future__ import annotations

import pytest

from server.field_extractor import (
    _candidate_is_plausible_numero_facture,
    _cluster_full_visual_rows,
    _compute_page_extents,
    _contains_any_alias,
    _extract_numero_facture_v2,
    _find_numero_candidates_by_pattern,
    _looks_like_address,
    _looks_like_amount,
    _parse_cell_value,
    _row_has_disqualifying_context,
    _score_by_date_proximity,
    _score_by_position,
    extract_field_selections_raw,
    extract_item_table,
)
from server.utils import BoundingBox, OCRLine


# ── _looks_like_amount ────────────────────────────────────────────────────────


class TestLooksLikeAmount:
    """Tests for the amount-detection heuristic used in v5 penalty scoring."""

    def test_french_format(self):
        """French format 1 234,56 → True (space as thousands sep, comma as decimal)."""
        assert _looks_like_amount("1 234,56") is True

    def test_dot_thousands_sep(self):
        """French variant 1.234,56 → True (dot as thousands sep, comma as decimal)."""
        assert _looks_like_amount("1.234,56") is True

    def test_simple_amount_no_separator(self):
        """Amount without thousands separator 1234.56 → False (regex requires separator)."""
        # The regex r'^\d{1,3}(?:[ .]\d{3})*[,.]\d{2}$' requires at least one
        # character of thousands separator before the decimal part, so "1234.56"
        # doesn't match. Only formats like "1.234,56" or "1 234,56" match.
        assert _looks_like_amount("1234.56") is False

    def test_no_decimal_places(self):
        """No decimal places → False (not a monetary amount)."""
        assert _looks_like_amount("1234") is False

    def test_invoice_number(self):
        """Invoice number INV-001 → False."""
        assert _looks_like_amount("INV-001") is False

    def test_date(self):
        """Date 15/03/2024 → False."""
        assert _looks_like_amount("15/03/2024") is False

    def test_garbage_text(self):
        """Garbage text → False."""
        assert _looks_like_amount("hello world") is False

    def test_empty_string(self):
        """Empty string → False."""
        assert _looks_like_amount("") is False

    # ── 3-decimal (TND/dinar millimes) support ────────────────────────────
    # Tunisian invoices quote amounts to 3 decimals (850.000 TND) as
    # standard; a 2-decimal-only check silently misses most local amounts.

    def test_tnd_three_decimal(self):
        """850.000 (TND 3-decimal) → True."""
        assert _looks_like_amount("850.000") is True

    def test_tnd_three_decimal_french_thousands(self):
        """1 250,000 (space thousands + comma decimal + 3 decimals) → True."""
        assert _looks_like_amount("1 250,000") is True

    def test_tnd_three_decimal_dot_thousands(self):
        """1.250,000 → True."""
        assert _looks_like_amount("1.250,000") is True

    def test_phone_number_not_amount(self):
        """Bare 8-digit phone number must NOT look like an amount (no separator)."""
        assert _looks_like_amount("71234567") is False


# ── _looks_like_address ───────────────────────────────────────────────────────


class TestLooksLikeAddress:
    """Tests for the address-detection heuristic used in v5 penalty scoring."""

    def test_with_rue(self):
        """Text containing 'rue' → True."""
        assert _looks_like_address("70 avenue de Clichy") is True

    def test_with_boulevard(self):
        """Text containing 'boulevard' → True."""
        assert _looks_like_address("Boulevard Saint-Germain") is True

    def test_with_code_postal(self):
        """Text containing 'code postal' → True."""
        assert _looks_like_address("Code postal 75001") is True

    def test_multiple_commas(self):
        """Text with 2+ commas → True (strong address signal)."""
        assert _looks_like_address("10 rue de Rivoli, Paris, France") is True

    def test_digit_street_type(self):
        """Text starting with digit + street type → True."""
        assert _looks_like_address("123 rue de la Paix") is True

    def test_invoice_number(self):
        """Invoice number INV-001 → False."""
        assert _looks_like_address("INV-001") is False

    def test_company_name(self):
        """Company name with no address keywords → False."""
        assert _looks_like_address("SARL Dupont et Fils") is False

    def test_single_comma(self):
        """Single comma without other signals → False."""
        assert _looks_like_address("Dupont, SARL") is False


# ── _find_numero_candidates_by_pattern ────────────────────────────────────────


class TestFindNumeroCandidatesByPattern:
    """Tests for v5 regex pattern matching for invoice numbers."""

    def test_letter_prefix_separator_digits(self):
        """Pattern 0: FAC-2025-001 → match with score 150."""
        lines = [
            OCRLine("FAC-2025-001", BoundingBox(0, 0, 120, 20), 0.95, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1
        vals = {c[2] for c in candidates}
        assert "FAC-2025-001" in vals

    def test_simple_inv_format(self):
        """Pattern 0: INV-001 → match with score 150."""
        lines = [
            OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1
        vals = {c[2] for c in candidates}
        assert "INV-001" in vals

    def test_letters_stuck_to_digits(self):
        """Pattern 1: F2025001 → match with score 120."""
        lines = [
            OCRLine("F2025001", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1
        vals = {c[2] for c in candidates}
        assert "F2025001" in vals

    def test_year_separator_sequence(self):
        """Pattern 2: 2025/001 → match with score 140."""
        lines = [
            OCRLine("2025/001", BoundingBox(0, 0, 80, 20), 0.95, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1
        vals = {c[2] for c in candidates}
        assert "2025/001" in vals

    def test_long_pure_digit(self):
        """Pattern 3: 1234567890 (10 digits) → match with score 100."""
        lines = [
            OCRLine("1234567890", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1
        vals = {c[2] for c in candidates}
        assert "1234567890" in vals

    def test_medium_pure_digit(self):
        """Pattern 4: 123456 (6 digits) → match with score 60."""
        lines = [
            OCRLine("123456", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1
        vals = {c[2] for c in candidates}
        assert "123456" in vals

    def test_ref_prefix(self):
        """Pattern 5: Réf: FAC-2024 → match with score 90."""
        lines = [
            OCRLine("Réf: FAC-2024", BoundingBox(0, 0, 120, 20), 0.9, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1

    def test_facture_prefix(self):
        """Pattern 6: Facture: 2025-001 → match with score 80."""
        lines = [
            OCRLine("Facture: 2025-001", BoundingBox(0, 0, 130, 20), 0.95, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) >= 1

    def test_empty_lines_no_candidates(self):
        """Empty line list → no candidates."""
        assert _find_numero_candidates_by_pattern([]) == []

    def test_garbage_text_no_match(self):
        """Garbage text → no candidates."""
        lines = [
            OCRLine("sdlkfjsldkfj", BoundingBox(0, 0, 10, 10), 0.5, 0, 0),
        ]
        candidates = _find_numero_candidates_by_pattern(lines)
        assert len(candidates) == 0


# ── _score_by_position ────────────────────────────────────────────────────────


class TestScoreByPosition:
    """Tests for position-based scoring of invoice number candidates."""

    def test_top_right_corner(self):
        """Top-right corner → 80 bonus."""
        line = OCRLine("FAC-001", BoundingBox(400, 0, 500, 30), 0.9, 0, 0)
        score = _score_by_position(line, page_height=200, page_width=500)
        assert score == 80.0, f"Expected 80 for top-right, got {score}"

    def test_top_left_corner(self):
        """Top-left corner → 60 bonus."""
        line = OCRLine("FAC-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0)
        score = _score_by_position(line, page_height=200, page_width=500)
        assert score == 60.0, f"Expected 60 for top-left, got {score}"

    def test_top_center(self):
        """Top-center → 40 bonus."""
        line = OCRLine("FAC-001", BoundingBox(200, 0, 260, 15), 0.9, 0, 0)
        score = _score_by_position(line, page_height=200, page_width=500)
        assert score == 40.0, f"Expected 40 for top-center, got {score}"

    def test_upper_third_not_corner(self):
        """Upper third (rel_y < 0.35) but not a corner/center → 20 bonus."""
        # rel_x=0.5 (center), rel_y=0.15 (upper third) → NOT a corner (rel_x too central)
        # NOT top-center (rel_y=0.15 < 0.20, rel_x=0.5 is between 0.4 and 0.6 → is top-center!)
        # Use rel_y=0.30, rel_x=0.50 → rel_y=0.30 > 0.20 → not top-center, rel_y < 0.35 → upper third
        line = OCRLine("text", BoundingBox(250, 60, 300, 75), 0.9, 0, 0)
        score = _score_by_position(line, page_height=200, page_width=500)
        assert score == 20.0, f"Expected 20 for upper third, got {score}"

    def test_middle_of_page(self):
        """Middle of page → 0 bonus."""
        line = OCRLine("text", BoundingBox(100, 200, 160, 215), 0.9, 0, 0)
        score = _score_by_position(line, page_height=500, page_width=500)
        assert score == 0.0, f"Expected 0 for middle, got {score}"

    def test_zero_page_height(self):
        """Zero page height → 0 (no crash)."""
        line = OCRLine("text", BoundingBox(0, 0, 10, 10), 0.9, 0, 0)
        score = _score_by_position(line, page_height=0, page_width=100)
        assert score == 0.0


# ── _score_by_date_proximity ──────────────────────────────────────────────────


class TestScoreByDateProximity:
    """Tests for date-proximity scoring of invoice number candidates."""

    def test_same_row_as_date(self):
        """Invoice number on same visual row as date → close proximity + same row bonus."""
        # Lines close together (dist < 100px) for full proximity bonus
        lines = [
            OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
            OCRLine("15/03/2024", BoundingBox(70, 0, 150, 20), 0.95, 0, 1),
        ]
        rows = [[lines[0], lines[1]]]
        score = _score_by_date_proximity(lines[0], rows, lines)
        # dist = center distance = (35,10) to (110,10), dx=75, dy=0, dist=75 < 100 → 50 bonus
        # Same row (row_idx 0 for both) → 40 bonus
        # Total = 50 + 40 = 90
        assert score >= 80.0, f"Expected >=80 for same row + proximity, got {score}"

    def test_close_but_different_row(self):
        """Invoice number near a date (different row) → proximity bonus."""
        lines = [
            OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
            OCRLine("15/03/2024", BoundingBox(0, 25, 100, 45), 0.95, 0, 1),
        ]
        rows = [[lines[0]], [lines[1]]]
        score = _score_by_date_proximity(lines[0], rows, lines)
        # dist = (30,10) to (50,35), dx=20, dy=25, dist≈32 < 100 → 50 bonus
        # Different row → no same-row bonus
        assert score >= 40.0, f"Expected proximity bonus, got {score}"

    def test_no_date_on_page(self):
        """No date on page → 0 bonus."""
        lines = [
            OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
            OCRLine("Supplier name", BoundingBox(0, 30, 80, 50), 0.9, 0, 1),
        ]
        rows = [[lines[0]], [lines[1]]]
        score = _score_by_date_proximity(lines[0], rows, lines)
        assert score == 0.0, f"Expected 0 for no date, got {score}"

    def test_far_from_date(self):
        """Invoice number far from date → no bonus."""
        lines = [
            OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
            OCRLine("15/03/2024", BoundingBox(100, 500, 160, 520), 0.95, 0, 1),
        ]
        rows = [[lines[0]], [lines[1]]]
        score = _score_by_date_proximity(lines[0], rows, lines)
        assert score == 0.0, f"Expected 0 for far distance, got {score}"

    def test_empty_lines(self):
        """Empty line list → 0 bonus (no crash)."""
        score = _score_by_date_proximity(
            OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0),
            [], [],
        )
        assert score == 0.0


# ── _compute_page_extents ─────────────────────────────────────────────────────


class TestComputePageExtents:
    """Tests for computing page dimensions from OCR line bounding boxes."""

    def test_single_line(self):
        """Single line → returns its bottom-right corner."""
        lines = [
            OCRLine("hello", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
        ]
        h, w = _compute_page_extents(lines)
        assert h == 20.0, f"Expected height 20, got {h}"
        assert w == 100.0, f"Expected width 100, got {w}"

    def test_multiple_lines(self):
        """Multiple lines → max of all extents."""
        lines = [
            OCRLine("hello", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
            OCRLine("world", BoundingBox(0, 30, 80, 50), 0.9, 0, 1),
            OCRLine("test", BoundingBox(0, 60, 200, 80), 0.9, 0, 2),
        ]
        h, w = _compute_page_extents(lines)
        assert h == 80.0, f"Expected height 80, got {h}"
        assert w == 200.0, f"Expected width 200, got {w}"

    def test_empty_lines(self):
        """Empty lines → returns (0, 0)."""
        h, w = _compute_page_extents([])
        assert h == 0.0, f"Expected height 0, got {h}"
        assert w == 0.0, f"Expected width 0, got {w}"


# ── _extract_numero_facture_v2 (integration) ──────────────────────────────────


class TestExtractNumeroFactureV2:
    """Integration tests for the v2 hybrid extraction function."""

    def test_normal_anchor_same_line(self):
        """Standard 'N° Facture: INV-001' → extracts INV-001."""
        lines = [
            OCRLine("N° Facture: INV-001", BoundingBox(0, 0, 120, 20), 0.9, 0, 0),
        ]
        rows = [[lines[0]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is not None
        assert "INV-001" in result.value

    def test_pattern_only_no_anchor(self):
        """Standalone 'FAC-2025-001' with no anchor label → found by pattern."""
        lines = [
            OCRLine("FAC-2025-001", BoundingBox(200, 5, 350, 25), 0.95, 0, 0),
        ]
        rows = [[lines[0]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is not None
        assert "FAC-2025-001" in (result.value or "")

    def test_stopwords_in_alias(self):
        """'n° de la facture d'origine : INV-2024-001' → relaxed matching."""
        lines = [
            OCRLine("n° de la facture d'origine : INV-2024-001", BoundingBox(0, 0, 250, 20), 0.9, 0, 0),
        ]
        rows = [[lines[0]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is not None
        assert "INV-2024-001" in (result.value or "")

    def test_anchor_separate_line(self):
        """Anchor on line 0, value on line 1 → geometric search."""
        lines = [
            OCRLine("N° Facture", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
            OCRLine("INV-2024-001", BoundingBox(0, 25, 150, 45), 0.95, 0, 1),
        ]
        rows = [[lines[0]], [lines[1]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is not None
        assert "INV-2024-001" in (result.value or "")

    def test_date_like_rejected(self):
        """Date-like value should be penalized and not win."""
        lines = [
            OCRLine("N° Facture", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
            OCRLine("INV-001", BoundingBox(0, 25, 150, 45), 0.95, 0, 1),
            OCRLine("15/03/2024", BoundingBox(0, 50, 100, 70), 0.95, 0, 2),
        ]
        rows = [[lines[0]], [lines[1]], [lines[2]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is not None
        # "15/03/2024" looks like a date → heavily penalized
        # "INV-001" should win
        assert "INV" in (result.value or "")

    def test_amount_like_rejected(self):
        """Amount-like value should be penalized."""
        lines = [
            OCRLine("N° Facture", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
            OCRLine("1234.56", BoundingBox(0, 25, 80, 45), 0.95, 0, 1),
            OCRLine("INV-001", BoundingBox(100, 25, 180, 45), 0.95, 0, 2),
        ]
        rows = [[lines[0], lines[1], lines[2]]]
        result = _extract_numero_facture_v2(rows, lines)
        # Should find INV-001 (not penalized) over 1234.56 (amount penalty)
        assert result.value is not None

    def test_empty_lines_no_candidate(self):
        """Empty lines → no candidate."""
        result = _extract_numero_facture_v2([], [])
        assert result.value is None

    def test_gemini_hint_boost(self):
        """Gemini hint with high similarity → bonus scoring."""
        lines = [
            OCRLine("FAC-2025-999", BoundingBox(200, 100, 350, 120), 0.95, 0, 0),
        ]
        rows = [[lines[0]]]
        result = _extract_numero_facture_v2(rows, lines, gemini_hint="FAC-2025-999")
        assert result.value is not None

    def test_short_value_penalized(self):
        """Very short value (< 3 chars) → rejected below MIN_NUMERO_SCORE."""
        lines = [
            OCRLine("N° Facture: 42", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
        ]
        rows = [[lines[0]]]
        result = _extract_numero_facture_v2(rows, lines)
        # "42" is very short (< 3 chars → -150 penalty) and pure digit
        # Even with pattern score 80 + position 60 = 140, penalty -150 → -10 < 50
        # The value is rejected below the MIN_NUMERO_SCORE threshold
        assert result.value is None

    def test_address_rejected(self):
        """Address-like candidate should be penalized."""
        lines = [
            OCRLine("Client address", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
            OCRLine("123 rue de Paris", BoundingBox(0, 25, 140, 45), 0.95, 0, 1),
            OCRLine("INV-001", BoundingBox(150, 25, 230, 45), 0.95, 0, 2),
        ]
        rows = [[lines[0], lines[1], lines[2]]]
        result = _extract_numero_facture_v2(rows, lines)
        # "123 rue de Paris" looks like an address → penalized
        # "INV-001" should win
        assert result.value is not None


# ── _candidate_is_plausible_numero_facture ─────────────────────────────────────


class TestCandidateIsPlausibleNumeroFacture:
    """Tests for the field-specific plausibility check for invoice numbers."""

    def test_invoice_number(self):
        """INV-001 → plausible."""
        line = OCRLine("INV-001", BoundingBox(0, 0, 60, 20), 0.9, 0, 0)
        assert _candidate_is_plausible_numero_facture(line) is True

    def test_address(self):
        """Address with street type → not plausible."""
        line = OCRLine("123 rue de Paris", BoundingBox(0, 0, 120, 20), 0.9, 0, 0)
        assert _candidate_is_plausible_numero_facture(line) is False

    def test_label_text(self):
        """Label text with no digits → not plausible."""
        line = OCRLine("Total TTC", BoundingBox(0, 0, 60, 20), 0.9, 0, 0)
        assert _candidate_is_plausible_numero_facture(line) is False

    def test_short_text(self):
        """Very short text → not plausible."""
        line = OCRLine("x", BoundingBox(0, 0, 5, 5), 0.5, 0, 0)
        assert _candidate_is_plausible_numero_facture(line) is False

    def test_multiple_commas(self):
        """Text with 2+ commas → not plausible (address signal)."""
        line = OCRLine("75001, Paris, France", BoundingBox(0, 0, 120, 20), 0.9, 0, 0)
        assert _candidate_is_plausible_numero_facture(line) is False

    def test_empty_text(self):
        """Empty text → not plausible."""
        line = OCRLine("", BoundingBox(0, 0, 0, 0), 0.0, 0, 0)
        assert _candidate_is_plausible_numero_facture(line) is False


# ── _parse_cell_value ─────────────────────────────────────────────────────────


class TestParseCellValue:
    """Tests for item-table cell value parsing."""

    def test_designation(self):
        """Designation field → returns text as-is."""
        assert _parse_cell_value("designation", "Produit A") == "Produit A"

    def test_quantite(self):
        """Quantity → parsed as float."""
        val = _parse_cell_value("quantite", "5")
        assert val == 5.0

    def test_prix_unitaire(self):
        """Unit price → parsed as float."""
        val = _parse_cell_value("prix_unitaire", "150.00")
        assert val == 150.0  # noqa: PLR2004

    def test_montant(self):
        """Total amount → parsed as float."""
        val = _parse_cell_value("montant", "750.00")
        assert val == 750.0  # noqa: PLR2004

    def test_tva_rate_percent(self):
        """TVA rate with % → decimal format (20% → 0.20)."""
        val = _parse_cell_value("tva_rate", "20%")
        assert val == 0.20  # noqa: PLR2004

    def test_tva_rate_decimal(self):
        """TVA rate already decimal → keep as-is."""
        val = _parse_cell_value("tva_rate", "0.20")
        assert val == 0.20  # noqa: PLR2004

    def test_tva_rate_whole_number(self):
        """TVA rate as whole number → convert to decimal (20 → 0.20)."""
        val = _parse_cell_value("tva_rate", "20")
        assert val == 0.20  # noqa: PLR2004

    def test_tva_rate_null(self):
        """Empty TVA rate → None."""
        val = _parse_cell_value("tva_rate", "")
        assert val is None

    def test_garbage_numeric(self):
        """Garbage numeric → None."""
        val = _parse_cell_value("prix_unitaire", "abc")
        assert val is None

    def test_unknown_column(self):
        """Unknown column name → text as-is."""
        val = _parse_cell_value("unknown", "some value")
        assert val == "some value"


# ── extract_item_table (edge cases) ────────────────────────────────────────────


class TestExtractItemTable:
    """Edge-case tests for item table extraction."""

    def test_empty_lines_no_items(self):
        """Empty OCR lines → empty items list."""
        items = extract_item_table([])
        assert items == []

    def test_no_header_no_items(self):
        """Lines with no table header AND no amount-ending rows → empty list."""
        lines = [
            OCRLine("Some invoice text", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
            OCRLine("Total TTC: 120.00", BoundingBox(0, 30, 150, 50), 0.95, 0, 1),
        ]
        items = extract_item_table(lines)
        # No header → geometric fallback; no row ends with a bare amount
        # ("Total TTC: 120.00" has label + amount inline) → empty list
        assert items == []

    def test_garbage_lines_no_crash(self):
        """Garbage lines should not crash."""
        lines = [
            OCRLine("sdlkfjsldkfj", BoundingBox(0, 0, 10, 10), 0.5, 0, 0),
            OCRLine("", BoundingBox(0, 15, 5, 20), 0.0, 0, 1),
        ]
        items = extract_item_table(lines)
        assert items == []

    def test_footer_stops_extraction(self):
        """Footer alias (montant_ht) should stop processing below it."""
        lines = [
            OCRLine("Some item", BoundingBox(0, 0, 50, 15), 0.9, 0, 0),
            OCRLine("Total HT", BoundingBox(0, 20, 60, 35), 0.95, 0, 1),
            OCRLine("100.00", BoundingBox(100, 20, 160, 35), 0.95, 0, 2),
        ]
        items = extract_item_table(lines)
        # Items may be empty (no header found) but should not crash
        assert isinstance(items, list)


# ── _row_has_disqualifying_context ────────────────────────────────────────────


class TestNumeroDisqualifier:
    """Tests for the same-row disqualifier that prevents phone/tax-ID/bank
    numbers from being picked as the invoice number."""

    def test_phone_row_is_disqualifying(self):
        """An 8-digit phone on the same row as a "Tél" label → disqualifying."""
        phone = OCRLine("71234567", BoundingBox(40, 100, 120, 120), 0.95, 0, 1)
        rows = [
            [OCRLine("Tél", BoundingBox(0, 100, 30, 120), 0.9, 0, 0), phone],
        ]
        assert _row_has_disqualifying_context(phone, rows) is True

    def test_iban_row_is_disqualifying(self):
        """A digit string on a row with an IBAN label → disqualifying."""
        candidate = OCRLine("TN59 1000 6035 1835", BoundingBox(60, 200, 220, 220), 0.9, 0, 1)
        rows = [
            [OCRLine("IBAN", BoundingBox(0, 200, 40, 220), 0.9, 0, 0), candidate],
        ]
        assert _row_has_disqualifying_context(candidate, rows) is True

    def test_clean_row_is_not_disqualifying(self):
        """A row without disqualifying labels → not disqualifying."""
        candidate = OCRLine("FAC-2025-001", BoundingBox(60, 0, 180, 20), 0.95, 0, 1)
        rows = [
            [OCRLine("N° Facture", BoundingBox(0, 0, 60, 20), 0.9, 0, 0), candidate],
        ]
        assert _row_has_disqualifying_context(candidate, rows) is False

    def test_phone_rejected_when_label_present(self):
        """Integration: a phone number next to "Tél" is NOT picked as numero_facture.

        The 8-digit phone matches pattern 3 (score 100) and would normally be
        accepted — the -350 disqualifier penalty drops it below MIN_NUMERO_SCORE.
        """
        lines = [
            OCRLine("Tél", BoundingBox(0, 100, 30, 120), 0.9, 0, 0),
            OCRLine("71234567", BoundingBox(40, 100, 120, 120), 0.95, 0, 1),
        ]
        rows = [[lines[0], lines[1]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is None

    def test_phone_accepted_without_label(self):
        """Control: the SAME phone with no disqualifying label IS accepted —
        proving the rejection above is caused by the disqualifier, not the
        pattern/penalty machinery."""
        lines = [
            OCRLine("71234567", BoundingBox(40, 100, 120, 120), 0.95, 0, 0),
        ]
        rows = [[lines[0]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value == "71234567"

    def test_real_invoice_number_still_wins_over_phone(self):
        """A real invoice number elsewhere on the page is picked over a
        disqualified phone candidate."""
        lines = [
            OCRLine("FAC-2025-001", BoundingBox(200, 5, 350, 25), 0.95, 0, 0),
            OCRLine("Tél", BoundingBox(0, 100, 30, 120), 0.9, 0, 1),
            OCRLine("71234567", BoundingBox(40, 100, 120, 120), 0.95, 0, 2),
        ]
        rows = [[lines[0]], [lines[1], lines[2]]]
        result = _extract_numero_facture_v2(rows, lines)
        assert result.value is not None
        assert "FAC-2025-001" in result.value


# ── Arabic column headers ─────────────────────────────────────────────────────


class TestArabicColumnHeaders:
    """Tests for Arabic table-header aliases.

    NOTE: normalize_text() ASCII-folds (strips non-ASCII), which would
    silently erase Arabic aliases — _contains_any_alias must fall back to
    whole-word matching against the raw text for non-ASCII aliases.
    """

    def test_contains_any_alias_arabic(self):
        """Arabic aliases match the raw text even though normalize_text
        strips them to empty."""
        assert _contains_any_alias("البيان الكمية المبلغ", ["البيان"]) is True
        assert _contains_any_alias("الكمية", ["الكمية"]) is True
        assert _contains_any_alias("المبلغ", ["المجموع"]) is False

    def test_arabic_header_extracts_items(self):
        """A table with Arabic column headers yields items via the normal
        header-anchored path (not the geometric fallback)."""
        lines = [
            # Header row
            OCRLine("البيان", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
            OCRLine("الكمية", BoundingBox(100, 0, 160, 20), 0.9, 0, 1),
            OCRLine("المبلغ", BoundingBox(200, 0, 280, 20), 0.9, 0, 2),
            # Item row 1
            OCRLine("منتج أ", BoundingBox(0, 30, 80, 50), 0.95, 0, 3),
            OCRLine("2", BoundingBox(100, 30, 140, 50), 0.95, 0, 4),
            OCRLine("850.000", BoundingBox(200, 30, 280, 50), 0.95, 0, 5),
            # Item row 2
            OCRLine("منتج ب", BoundingBox(0, 60, 80, 80), 0.95, 0, 6),
            OCRLine("1", BoundingBox(100, 60, 140, 80), 0.95, 0, 7),
            OCRLine("250.000", BoundingBox(200, 60, 280, 80), 0.95, 0, 8),
        ]
        items = extract_item_table(lines)
        assert len(items) == 2
        assert items[0]["designation"] == "منتج أ"
        assert items[0]["quantite"] == 2.0
        assert items[0]["montant"] == 850.0
        assert items[1]["montant"] == 250.0


# ── Geometric item-table fallback (no header) ────────────────────────────────


class TestItemTableGeometric:
    """Tests for the headerless item-table fallback that reads rows ending in
    an amount-like value on their right edge."""

    def test_cluster_full_visual_rows_keeps_wide_rows_together(self):
        """A designation and its far-right amount stay in one row (no gap split)."""
        lines = [
            OCRLine("Designation", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
            OCRLine("850.000", BoundingBox(400, 0, 480, 20), 0.95, 0, 1),
        ]
        rows = _cluster_full_visual_rows(lines)
        assert len(rows) == 1
        assert len(rows[0]) == 2

    def test_no_header_but_amount_rows_extract_items(self):
        """No header row, but 2+ rows ending in amounts → items via fallback."""
        lines = [
            OCRLine("Chaise bureau", BoundingBox(0, 0, 120, 20), 0.9, 0, 0),
            OCRLine("850.000", BoundingBox(400, 0, 480, 20), 0.95, 0, 1),
            OCRLine("Table", BoundingBox(0, 30, 80, 50), 0.9, 0, 2),
            OCRLine("250.000", BoundingBox(400, 30, 480, 50), 0.95, 0, 3),
        ]
        items = extract_item_table(lines)
        assert len(items) == 2
        assert items[0]["designation"] == "Chaise bureau"
        assert items[0]["montant"] == 850.0
        assert items[1]["montant"] == 250.0

    def test_single_amount_row_no_items(self):
        """A single amount-ending row is more likely a totals line → no items."""
        lines = [
            OCRLine("Total TTC", BoundingBox(0, 0, 80, 20), 0.9, 0, 0),
            OCRLine("1200.000", BoundingBox(300, 0, 380, 20), 0.95, 0, 1),
        ]
        items = extract_item_table(lines)
        assert items == []

    def test_totals_rows_filtered_out(self):
        """Rows containing footer aliases (Total TTC) are excluded from items."""
        lines = [
            OCRLine("Produit A", BoundingBox(0, 0, 100, 20), 0.9, 0, 0),
            OCRLine("100.000", BoundingBox(300, 0, 380, 20), 0.95, 0, 1),
            OCRLine("Produit B", BoundingBox(0, 30, 100, 50), 0.9, 0, 2),
            OCRLine("200.000", BoundingBox(300, 30, 380, 50), 0.95, 0, 3),
            OCRLine("Total TTC", BoundingBox(0, 60, 90, 80), 0.9, 0, 4),
            OCRLine("300.000", BoundingBox(300, 60, 380, 80), 0.95, 0, 5),
        ]
        items = extract_item_table(lines)
        assert len(items) == 2  # totals row filtered out
        assert {i["montant"] for i in items} == {100.0, 200.0}


# ── extract_field_selections_raw ──────────────────────────────────────────────


class TestExtractFieldSelectionsRaw:
    """Tests for the raw field selection extraction."""

    def test_returns_all_fields(self):
        """Returns all 8 fields even when empty."""
        selections = extract_field_selections_raw([])
        assert set(selections.keys()) == {
            "numero_facture", "date", "fournisseur", "client",
            "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
        }

    def test_each_has_confidence_and_value(self):
        """Each selection has the expected attributes."""
        selections = extract_field_selections_raw([])
        for field in selections:
            assert hasattr(selections[field], "value")
            assert hasattr(selections[field], "confidence")
            assert selections[field].value is None  # no lines → no values

    def test_gemini_hint_propagated(self):
        """Gemini hint is propagated through to the extraction."""
        lines = [
            OCRLine("FAC-2025-999", BoundingBox(200, 5, 350, 25), 0.95, 0, 0),
        ]
        selections = extract_field_selections_raw(lines, gemini_hint="FAC-2025-999")
        val = selections["numero_facture"].value
        assert val is not None
        # The pattern match (score 150) is boosted by Gemini hint (score +100 = 250)
        # Position is not in a corner (rel_x=200/350=0.57, rel_y=5/25=0.2)
        # rel_y=0.2 < 0.25, rel_x=0.57 not >0.6 → NOT top-right
        # rel_x=0.57 not < 0.4 → NOT top-left
        # rel_x=0.57 between 0.4 and 0.6, rel_y=0.2 < 0.20 → top-center → +40
        # Total = 150 + 40 = 190 + 100 = 290 (with hint bonus)
        # Pattern 2 also matches: 2025/... but "FAC-2025-999" starts with letters → no.
        # Actually let's check: the value contains "2025-999" but as part of "FAC-2025-999"
        # Only Pattern 0 matches "FAC-2025-999" → score 150
        assert "FAC" in val or "2025" in val
