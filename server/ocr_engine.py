"""PaddleOCR wrapper used by the HOTIX invoice extractor."""

from __future__ import annotations

import logging
from dataclasses import dataclass, replace
from typing import Any

import numpy as np
from PIL import Image

from .utils import BoundingBox, OCRLine, collapse_text


logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class OCRResult:
    """Normalized OCR result for a single page."""

    lines: list[OCRLine]
    raw_text: str


class OcrEngineError(RuntimeError):
    """Raised when PaddleOCR cannot process an image."""


class PaddleOcrEngine:
    """Lazy PaddleOCR engine configured for French invoice text."""

    def __init__(self, language: str = "fr") -> None:
        """Initialize the engine wrapper without loading the model immediately."""

        self._language = language
        self._ocr = None

    @property
    def ocr(self):
        """Return the initialized PaddleOCR model instance."""

        if self._ocr is None:
            from paddleocr import PaddleOCR

            self._ocr = PaddleOCR(lang=self._language)
        return self._ocr

    def recognize(self, image: Image.Image, page_index: int) -> OCRResult:
        """Run OCR on a PIL image and return normalized text lines."""

        try:
            # Pad the image with white borders before OCR to give the recognition
            # model room to read edge characters. PaddleOCR's recognition model
            # clips characters when the detection crop is too tight (min_x ≈ 0).
            # Empirically, 20px white padding on all sides resolves this.
            padded = self._pad_image(image, pad_px=30)
            result = self.ocr.ocr(np.array(padded.convert("RGB")))
        except Exception as exc:  # pragma: no cover - runtime OCR failures need direct surfacing
            raise OcrEngineError(f"OCR failed on page {page_index + 1}: {exc}") from exc

        lines = self._normalize_result(result, page_index)
        raw_text = "\n".join(line.text for line in lines)
        return OCRResult(lines=lines, raw_text=raw_text)

    @staticmethod
    def _pad_image(image: Image.Image, pad_px: int = 30) -> Image.Image:
        """Add white padding around the image so detection crops have margin."""
        w, h = image.size
        padded = Image.new("RGB", (w + pad_px * 2, h + pad_px * 2), (255, 255, 255))
        padded.paste(image, (pad_px, pad_px))
        return padded

        lines = self._normalize_result(result, page_index)
        raw_text = "\n".join(line.text for line in lines)
        return OCRResult(lines=lines, raw_text=raw_text)

    def _normalize_result(self, result: Any, page_index: int) -> list[OCRLine]:
        """Convert PaddleOCR output to OCRLine objects."""

        if not result:
            return []

        lines = [line for detection in self._iter_detections(result) if (line := self._parse_detection(detection, page_index)) is not None]

        ordered_lines = sorted(lines, key=lambda item: (item.box.y1, item.box.x1, -item.confidence))
        return [replace(line, line_index=index) for index, line in enumerate(ordered_lines)]

    def _iter_detections(self, result: Any):
        """Yield raw PaddleOCR detection entries in a normalized shape.

        Handles both:
        - PaddleOCR 3.x (paddlex pipeline): result[0] is a dict-like OCRResult
          with keys rec_texts, rec_scores, rec_polys (length-N lists)
        - PaddleOCR 2.x (legacy): result[0] is a list of
          [bbox_points, (text, confidence)] pairs
        """

        if not result:
            return

        # Unwrap single-element outer list
        inner = result[0] if (isinstance(result, list) and len(result) == 1) else result

        # PaddleOCR 3.x: dict-like object with rec_texts / rec_scores / rec_polys
        if hasattr(inner, "get"):
            texts = inner.get("rec_texts") or []
            scores = inner.get("rec_scores") or []
            polys = inner.get("rec_polys") or []

            for i in range(len(texts)):
                # Normalize to old format: [polygon_points, (text, confidence)]
                poly = polys[i] if i < len(polys) else None
                text = texts[i] if i < len(texts) else ""
                score = float(scores[i]) if i < len(scores) else 0.0
                yield [poly, (text, score)]
            return

        # PaddleOCR 2.x (legacy): [[bbox, (text, confidence)], ...]
        if isinstance(inner, list):
            yield from inner
            return

    def _parse_detection(self, detection: Any, page_index: int) -> OCRLine | None:
        """Convert one PaddleOCR detection entry into an OCRLine."""

        if not detection or len(detection) < 2:
            return None

        box_points, text_payload = detection[0], detection[1]

        # box_points may be a numpy ndarray (PaddleOCR 3.x) or a list (2.x).
        # Check for None explicitly since ndarray truthiness is ambiguous.
        if box_points is None or text_payload is None:
            return None
        if isinstance(box_points, (list, tuple)) and not box_points:
            return None

        text, confidence = self._extract_text_and_confidence(text_payload)
        text = collapse_text(text)
        if not text:
            return None

        try:
            box = BoundingBox.from_points(box_points)
        except Exception as exc:
            logger.debug("Skipping malformed bounding box on page %s: %s", page_index, exc)
            return None

        return OCRLine(
            text=text,
            box=box,
            confidence=confidence,
            page_index=page_index,
            line_index=0,
        )

    def _extract_text_and_confidence(self, text_payload: Any) -> tuple[str, float]:
        """Extract the recognized text and confidence from a PaddleOCR payload."""

        if isinstance(text_payload, (list, tuple)):
            text = str(text_payload[0]) if text_payload else ""
            confidence = float(text_payload[1]) if len(text_payload) > 1 else 0.0
            return text, confidence

        return str(text_payload), 0.0
