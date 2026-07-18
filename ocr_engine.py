"""PaddleOCR wrapper used by the HOTIX invoice extractor."""

from __future__ import annotations

from dataclasses import dataclass, replace
from typing import Any

import numpy as np
from PIL import Image

from utils import BoundingBox, OCRLine, collapse_text


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

            self._ocr = PaddleOCR(lang=self._language, use_angle_cls=True, show_log=False)
        return self._ocr

    def recognize(self, image: Image.Image, page_index: int) -> OCRResult:
        """Run OCR on a PIL image and return normalized text lines."""

        try:
            result = self.ocr.ocr(np.array(image.convert("RGB")), cls=True)
        except Exception as exc:  # pragma: no cover - runtime OCR failures need direct surfacing
            raise OcrEngineError(f"OCR failed on page {page_index + 1}: {exc}") from exc

        lines = self._normalize_result(result, page_index)
        raw_text = "\n".join(line.text for line in lines)
        return OCRResult(lines=lines, raw_text=raw_text)

    def _normalize_result(self, result: Any, page_index: int) -> list[OCRLine]:
        """Convert PaddleOCR output to OCRLine objects."""

        if not result:
            return []

        detections = result
        if isinstance(result, list) and len(result) == 1 and isinstance(result[0], list):
            detections = result[0]

        lines: list[OCRLine] = []
        for detection in detections or []:
            if not detection or len(detection) < 2:
                continue

            box_points, text_payload = detection[0], detection[1]
            if not box_points or not text_payload:
                continue

            if isinstance(text_payload, (list, tuple)):
                text = str(text_payload[0]) if text_payload else ""
                confidence = float(text_payload[1]) if len(text_payload) > 1 else 0.0
            else:
                text = str(text_payload)
                confidence = 0.0

            text = collapse_text(text)
            if not text:
                continue

            try:
                box = BoundingBox.from_points(box_points)
            except Exception:
                continue

            lines.append(
                OCRLine(
                    text=text,
                    box=box,
                    confidence=confidence,
                    page_index=page_index,
                    line_index=0,
                )
            )

        ordered_lines = sorted(lines, key=lambda item: (item.box.y1, item.box.x1, -item.confidence))
        return [replace(line, line_index=index) for index, line in enumerate(ordered_lines)]
