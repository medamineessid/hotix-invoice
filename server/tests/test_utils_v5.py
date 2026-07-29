"""Additional tests for server/utils.py — covering untested functions and edge cases."""

from __future__ import annotations

import pytest

from server.utils import (
    BoundingBox,
    OCRLine,
    looks_like_latin_text,
)


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
