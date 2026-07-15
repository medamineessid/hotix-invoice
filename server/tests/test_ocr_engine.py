"""Tests for server/ocr_engine.py — PaddleOCR parsing and padding logic."""

from __future__ import annotations

from unittest.mock import MagicMock, patch

import numpy as np
import pytest
from PIL import Image

from server.ocr_engine import MIN_CONFIDENCE, PaddleOcrEngine, OCRResult


def _make_mock_3x_result(
    texts: list[str],
    scores: list[float],
    polys: list[list[list[float]]] | None = None,
) -> list:
    """Build a fake PaddleOCR 3.x result structure matching the paddlex pipeline
    OCRResult format (dict-like with rec_texts, rec_scores, rec_polys keys)."""
    if polys is None:
        polys = [
            [[0.0, y * 50.0], [60.0, y * 50.0], [60.0, y * 50.0 + 20.0], [0.0, y * 50.0 + 20.0]]
            for y in range(len(texts))
        ]
    inner = {
        "rec_texts": texts,
        "rec_scores": [float(s) for s in scores],
        "rec_polys": [np.array(poly, dtype=np.int16) for poly in polys],
    }
    return [inner]


class TestIterDetections:
    """Verify _iter_detections handles both PaddleOCR 3.x and 2.x formats."""

    def test_3x_dict_format(self):
        """PaddleOCR 3.x dict-like result yields [poly, (text, score)] pairs."""
        engine = PaddleOcrEngine()
        texts = ["FACTURE", "Date: 2024-03-15", "Total: 100 EUR"]
        scores = [0.99, 0.95, 0.98]

        result = _make_mock_3x_result(texts, scores)
        detections = list(engine._iter_detections(result))

        assert len(detections) == 3
        for i, (poly, payload) in enumerate(detections):
            assert payload[0] == texts[i]
            assert abs(payload[1] - scores[i]) < 0.001
            assert poly is not None  # polygon should be preserved

    def test_3x_empty_result(self):
        """Empty result from PaddleOCR 3.x yields no detections."""
        engine = PaddleOcrEngine()
        result = _make_mock_3x_result([], [], [])
        detections = list(engine._iter_detections(result))
        assert len(detections) == 0

    def test_3x_none_result(self):
        """None result from PaddleOCR yields no detections."""
        engine = PaddleOcrEngine()
        detections = list(engine._iter_detections(None))
        assert len(detections) == 0

    def test_3x_empty_list(self):
        """Empty list result yields no detections."""
        engine = PaddleOcrEngine()
        detections = list(engine._iter_detections([]))
        assert len(detections) == 0


class TestParseDetection:
    """Verify _parse_detection converts to OCRLine correctly."""

    def test_parse_valid_detection(self):
        """A valid detection entry produces an OCRLine with correct fields."""
        engine = PaddleOcrEngine()
        poly = np.array([[10, 20], [100, 20], [100, 50], [10, 50]], dtype=np.int16)
        detection = [poly, ("FACTURE", 0.98)]
        line = engine._parse_detection(detection, page_index=0)

        assert line is not None
        assert line.text == "FACTURE"
        assert abs(line.confidence - 0.98) < 0.001
        assert line.page_index == 0

    def test_parse_none_detection(self):
        """None detection or None components return None."""
        engine = PaddleOcrEngine()
        assert engine._parse_detection(None, 0) is None
        assert engine._parse_detection([None, ("text", 0.5)], 0) is None

    def test_parse_empty_box_points(self):
        """Empty list of box points returns None."""
        engine = PaddleOcrEngine()
        assert engine._parse_detection([[], ("text", 0.5)], 0) is None

    def test_parse_short_detection(self):
        """Detection with fewer than 2 elements returns None."""
        engine = PaddleOcrEngine()
        assert engine._parse_detection([np.array([[0, 0]])], 0) is None

    def test_parse_ndarray_poly(self):
        """Numpy ndarray for box_points (PaddleOCR 3.x) is handled correctly."""
        engine = PaddleOcrEngine()
        poly = np.array([[5, 5], [50, 5], [50, 20], [5, 20]], dtype=np.int16)
        detection = [poly, ("Hello", 0.85)]
        line = engine._parse_detection(detection, page_index=1)

        assert line is not None
        assert line.text == "Hello"
        assert line.page_index == 1

    def test_parse_low_confidence_returns_none(self):
        """Detection with confidence below MIN_CONFIDENCE returns None."""
        engine = PaddleOcrEngine()
        poly = np.array([[10, 20], [100, 20], [100, 50], [10, 50]], dtype=np.int16)
        # Confidence just below the threshold
        low_conf = MIN_CONFIDENCE - 0.01
        detection = [poly, ("garbage", low_conf)]
        line = engine._parse_detection(detection, page_index=0)
        assert line is None, f"Expected None for confidence {low_conf} < {MIN_CONFIDENCE}"

    def test_parse_at_threshold_confidence(self):
        """Detection at exactly MIN_CONFIDENCE is accepted."""
        engine = PaddleOcrEngine()
        poly = np.array([[10, 20], [100, 20], [100, 50], [10, 50]], dtype=np.int16)
        detection = [poly, ("valid", MIN_CONFIDENCE)]
        line = engine._parse_detection(detection, page_index=0)
        assert line is not None
        assert line.text == "valid"

    def test_parse_low_confidence_mixed(self):
        """Only low-confidence detections are filtered; high-confidence ones survive."""
        engine = PaddleOcrEngine()
        poly = np.array([[10, 20], [100, 20], [100, 50], [10, 50]], dtype=np.int16)
        result = _make_mock_3x_result(
            ["keep", "discard", "keep"],
            [0.95, MIN_CONFIDENCE - 0.1, 0.80],
        )
        lines = engine._normalize_result(result, page_index=0)
        texts = [line.text for line in lines]
        assert "discard" not in texts
        assert "keep" in texts
        assert len(lines) == 2, f"Expected 2 lines, got {len(lines)}: {texts}"


class TestPadImage:
    """Verify _pad_image adds white borders correctly."""

    def test_padding_adds_border(self):
        """Padding should enlarge the image by 2*pad_px on each dimension."""
        img = Image.new("RGB", (100, 50), (128, 128, 128))
        padded = PaddleOcrEngine._pad_image(img, pad_px=30)

        assert padded.width == 100 + 60  # 160
        assert padded.height == 50 + 60  # 110

    def test_padding_is_white(self):
        """The added border pixels should be white (255, 255, 255)."""
        img = Image.new("RGB", (100, 50), (128, 128, 128))
        padded = PaddleOcrEngine._pad_image(img, pad_px=20)

        # Top-left corner of padding should be white
        assert padded.getpixel((0, 0)) == (255, 255, 255)
        # Original pixel at (0, 0) shifted by pad_px should preserve original
        assert padded.getpixel((20, 20)) == (128, 128, 128)

    def test_default_padding(self):
        """Default padding of 30px should be applied."""
        img = Image.new("RGB", (100, 50), (0, 0, 0))
        padded = PaddleOcrEngine._pad_image(img)
        assert padded.width == 160
        assert padded.height == 110


class TestNormalizeResult:
    """Verify _normalize_result end-to-end using a mocked OCR call."""

    def test_normalize_3x_result(self):
        """A full 3.x result normalizes to sorted OCRLine list with correct text."""
        engine = PaddleOcrEngine()
        texts = ["ZZZ", "AAA", "MMM"]
        scores = [0.9, 0.8, 0.7]
        # Give ZZZ and AAA same y-range so ordering is deterministic
        polys = [
            [[50, 100], [150, 100], [150, 130], [50, 130]],  # ZZZ
            [[10, 100], [40, 100], [40, 130], [10, 130]],    # AAA (same y, smaller x → first)
            [[10, 200], [40, 200], [40, 230], [10, 230]],    # MMM (lower y)
        ]
        result = _make_mock_3x_result(texts, scores, polys)

        lines = engine._normalize_result(result, page_index=0)
        # Should be sorted by (y1, x1, -confidence) → AAA, ZZZ, MMM
        assert len(lines) == 3
        assert lines[0].text == "AAA"
        assert lines[1].text == "ZZZ"
        assert lines[2].text == "MMM"

    def test_normalize_empty(self):
        """Empty/none results produce empty line lists."""
        engine = PaddleOcrEngine()
        assert engine._normalize_result(None, 0) == []
        assert engine._normalize_result([], 0) == []


class TestRecognizeWithMock:
    """Verify recognize() flows correctly with a mocked PaddleOCR call."""

    def test_recognize_calls_pad_and_ocr(self):
        """recognize() should pad the image before calling ocr.ocr()."""
        engine = PaddleOcrEngine()

        # Mock the ocr property to return a known 3.x result
        mock_ocr_instance = MagicMock()
        mock_ocr_instance.ocr.return_value = _make_mock_3x_result(
            ["Hello", "World"], [0.95, 0.90]
        )
        engine._ocr = mock_ocr_instance

        img = Image.new("RGB", (100, 50), (0, 0, 0))
        result = engine.recognize(img, page_index=0)

        assert isinstance(result, OCRResult)
        assert len(result.lines) == 2
        assert "Hello" in result.raw_text
        assert "World" in result.raw_text

        # Verify that ocr.ocr was called with a padded image (wider than 100)
        call_arg = mock_ocr_instance.ocr.call_args[0][0]
        assert call_arg.shape[1] > 100  # width increased due to padding

    def test_recognize_propagation(self):
        """recognize() returns OCRResult with lines matching the input order."""
        engine = PaddleOcrEngine()
        mock_ocr_instance = MagicMock()
        mock_ocr_instance.ocr.return_value = _make_mock_3x_result(
            ["Date:", "2024-03-15", "Total: 100 EUR"],
            [0.99, 0.98, 0.97],
        )
        engine._ocr = mock_ocr_instance

        img = Image.new("RGB", (200, 100), (255, 255, 255))
        result = engine.recognize(img, page_index=0)

        assert len(result.lines) == 3
        texts = [line.text for line in result.lines]
        assert "Date:" in texts
        assert "2024-03-15" in texts
