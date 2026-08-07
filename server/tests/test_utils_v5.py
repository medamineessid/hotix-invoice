"""Additional tests for server/utils.py — covering untested functions and edge cases."""

from __future__ import annotations

from decimal import Decimal

import pytest

from server.utils import (
    BoundingBox,
    OCRLine,
    _parse_decimal,
    extract_amount,
    looks_like_latin_text,
)
from server.field_extractor import cross_validate_fields


# ── looks_like_latin_text ─────────────────────────────────────────────────────


class TestLooksLikeLatinText:
    """Tests for the latin-text detection heuristic.

    NOTE: looks_like_latin_text counts ALL Unicode letters (category starting
    with 'L') plus ASCII characters. Arabic, Chinese, and other scripts are
    counted because they use the 'L' (Letter) Unicode category.
    The function checks if at least 50% of characters are letters or ASCII.
    """

    def test_ascii_text(self):
        """Plain ASCII text → True."""
        assert looks_like_latin_text("Hello World") is True

    def test_accented_text(self):
        """Accented French text → True."""
        assert looks_like_latin_text("Résumé français") is True

    def test_digits_only(self):
        """Digits only → True (digits are ASCII)."""
        assert looks_like_latin_text("12345") is True

    def test_mixed_digits_and_letters(self):
        """Mixed digits and letters → True."""
        assert looks_like_latin_text("INV-001") is True

    def test_empty_string(self):
        """Empty string → False."""
        assert looks_like_latin_text("") is False

    def test_whitespace_only(self):
        """Whitespace only → True (spaces are ASCII characters)."""
        assert looks_like_latin_text("   ") is True

    def test_arabic_text(self):
        """Arabic script → True (Arabic letters have Unicode category 'Lo')."""
        assert looks_like_latin_text("مرحبا بالعالم") is True

    def test_chinese_text(self):
        """Chinese characters → True (Chinese chars have Unicode category 'Lo')."""
        assert looks_like_latin_text("你好世界") is True

    def test_emoji_mixed(self):
        """Emoji mixed with Latin → True (mostly ASCII + letter)."""
        assert looks_like_latin_text("Hello 😊") is True

    def test_mostly_latin_with_some_non_latin(self):
        """Mostly Latin with some non-letter characters → True (>= 0.5 ratio)."""
        assert looks_like_latin_text("École 你好 World") is True

    def test_mostly_non_latin(self):
        """Mostly non-Latin letters → True (all are 'Lo' category letters)."""
        text = "你好世界 Hello"
        # 4 Chinese letters (Lo) + 1 space + 5 ASCII = 10 chars, 9 letter-or-ASCII → 0.9
        assert looks_like_latin_text(text) is True

    def test_mixed_symbols_low_ratio(self):
        """Mostly symbols and punctuation → may be False (few letters)."""
        text = "!!! ??? *** 123"
        # No letters, only ASCII symbols — isascii() returns True for all
        # 12 chars, all ASCII → ratio = 1.0 → True
        assert looks_like_latin_text(text) is True


# ── OCRLine / BoundingBox edge cases ─────────────────────────────────────────


class TestBoundingBoxEdgeCases:
    """Edge cases for BoundingBox and OCRLine dataclasses."""

    def test_ocr_line_creation(self):
        """OCRLine creation with all fields."""
        line = OCRLine("test", BoundingBox(0, 0, 10, 10), 0.9, 0, 0)
        assert line.text == "test"
        assert line.confidence == 0.9
        assert line.page_index == 0
        assert line.line_index == 0

    def test_ocr_line_zero_confidence(self):
        """OCRLine with zero confidence is valid."""
        line = OCRLine("garbage", BoundingBox(0, 0, 5, 5), 0.0, 0, 0)
        assert line.confidence == 0.0

    def test_bbox_from_points(self):
        """BoundingBox from list of coordinate pairs (x,y)."""
        bb = BoundingBox.from_points([[0, 0], [10, 0], [10, 10], [0, 10]])
        assert bb.x1 == 0
        assert bb.y1 == 0
        assert bb.x2 == 10
        assert bb.y2 == 10

    def test_bbox_center(self):
        """BoundingBox center calculation."""
        bb = BoundingBox(10, 20, 30, 40)
        assert bb.center_x == 20.0  # noqa: PLR2004
        assert bb.center_y == 30.0  # noqa: PLR2004

    def test_bbox_width_height(self):
        """BoundingBox width and height."""
        bb = BoundingBox(10, 20, 50, 60)
        assert bb.width == 40.0  # noqa: PLR2004
        assert bb.height == 40.0  # noqa: PLR2004

    def test_vertical_overlap_full(self):
        """Full vertical overlap."""
        bb1 = BoundingBox(0, 0, 10, 20)
        bb2 = BoundingBox(0, 5, 10, 15)
        assert bb1.vertical_overlap(bb2) == 10.0  # noqa: PLR2004

    def test_vertical_overlap_none(self):
        """No vertical overlap."""
        bb1 = BoundingBox(0, 0, 10, 10)
        bb2 = BoundingBox(0, 20, 10, 30)
        assert bb1.vertical_overlap(bb2) == 0.0

    def test_vertical_gap(self):
        """Vertical gap calculation."""
        bb1 = BoundingBox(0, 0, 10, 10)
        bb2 = BoundingBox(0, 20, 10, 30)
        assert bb1.vertical_gap(bb2) == 10.0  # noqa: PLR2004

    def test_horizontal_gap(self):
        """Horizontal gap calculation."""
        bb1 = BoundingBox(0, 0, 10, 10)
        bb2 = BoundingBox(20, 0, 30, 10)
        assert bb1.horizontal_gap(bb2) == 10.0  # noqa: PLR2004


# ── French-format amount parsing ─────────────────────────────────────────────


class TestFrenchAmountParsing:
    """Amounts on French/Tunisian invoices use ',' as the decimal separator and
    space / NBSP (U+00A0) / thin-space (U+202F) as the thousands separator.

    Regression tests for the normalization used by _parse_decimal (arithmetic
    validation / VAT-rate checks) and extract_amount (field extraction).  The
    two parsers must agree — before the U+202F fix, _parse_decimal returned
    None for thin-space amounts while extract_amount succeeded, silently
    dropping the amount from cross-validation.
    """

    def test_thin_space_thousands_french_decimal(self):
        """'1\u202f234,56' — thin space thousands, comma decimal. Was None before the fix."""
        assert _parse_decimal("1\u202f234,56") == Decimal("1234.56")

    def test_nbsp_thousands_french_decimal(self):
        """NBSP thousands separator (\u00a0), comma decimal."""
        assert _parse_decimal("1\u00a0234,56") == Decimal("1234.56")

    def test_ascii_space_thousands_french_decimal(self):
        """Plain ASCII space thousands separator."""
        assert _parse_decimal("1 234,56") == Decimal("1234.56")

    def test_period_thousands_comma_decimal(self):
        """European style: '1.234,56'."""
        assert _parse_decimal("1.234,56") == Decimal("1234.56")

    def test_us_thousands_dot_decimal(self):
        """US style: '1,234.56'."""
        assert _parse_decimal("1,234.56") == Decimal("1234.56")

    def test_tnd_three_decimal_millimes(self):
        """Tunisian invoices quote to 3 decimals: '850,000' = 850.000 TND."""
        assert _parse_decimal("850,000") == Decimal("850.000")

    def test_thin_space_three_decimal_millimes(self):
        """Thin-space thousands with 3-decimal TND amount."""
        assert _parse_decimal("1\u202f000,000") == Decimal("1000.000")

    def test_extract_amount_thin_space(self):
        """extract_amount must normalize thin-space amounts too."""
        assert extract_amount("1\u202f234,56") == "1234.560"

    def test_extract_amount_nbsp(self):
        """extract_amount must normalize NBSP amounts too."""
        assert extract_amount("1\u00a0234,56") == "1234.560"

    def test_cross_validate_french_consistent_no_spurious_issues(self):
        """A consistent French-formatted invoice must NOT raise
        'Unlikely VAT rate' or 'Arithmetic mismatch' issues.

        Before the U+202F fix, thin-space amounts parsed to None / wrong
        magnitudes and triggered exactly these spurious validation issues
        across real invoices (reported as VAT rates of 1.0%, 526.3%, 1428.6%).
        """
        fields = {
            "numero_facture": "FAC-2025-001",
            "date": "2025-03-15",
            "fournisseur": "Fournisseur SARL",
            "client": "Client SA",
            "montant_ht": "1\u202f000,000",
            "montant_tva": "190,000",
            "montant_taxe": "0,000",
            "montant_ttc": "1\u202f190,000",
        }
        issues = cross_validate_fields(fields)
        assert not any("Unlikely VAT rate" in i for i in issues), f"Unexpected: {issues}"
        assert not any("Arithmetic mismatch" in i for i in issues), f"Unexpected: {issues}"

    def test_unlikely_vat_rate_1428_repro(self):
        """Documents the exact symptom from the production log: HT=7,000 /
        TVA=100,000 yields VAT rate 1428.6%.

        Validation must still flag it (genuinely inconsistent extraction);
        the fix guarantees valid French strings can no longer be misparsed
        into such magnitudes by the amount parser itself.
        """
        fields = {
            "numero_facture": "INV-001",
            "date": "2025-01-10",
            "montant_ht": "7,000",
            "montant_tva": "100,000",
            "montant_ttc": "107,000",
        }
        issues = cross_validate_fields(fields)
        assert any("Unlikely VAT rate: 1428.6%" in i for i in issues), f"Expected repro, got: {issues}"
