"""File ingestion helpers for invoice image and PDF conversion."""

from __future__ import annotations

import io
from pathlib import Path

from PIL import Image, ImageSequence
from pdf2image import convert_from_bytes


SUPPORTED_EXTENSIONS = {".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"}


class IngestionError(RuntimeError):
    """Raised when uploaded content cannot be converted to images."""


def load_invoice_images(file_bytes: bytes, filename: str, poppler_path: str | None = None) -> list[Image.Image]:
    """Convert an uploaded invoice file into a list of PIL images."""

    suffix = Path(filename).suffix.lower()
    if suffix not in SUPPORTED_EXTENSIONS:
        raise IngestionError(f"Unsupported file type: {suffix or '<none>'}")

    if suffix == ".pdf":
        try:
            kwargs = {"poppler_path": poppler_path} if poppler_path else {}
            return [page.convert("RGB") for page in convert_from_bytes(file_bytes, dpi=300, **kwargs)]
        except Exception as exc:  # pragma: no cover - runtime dependency and OS specific
            raise IngestionError(f"Unable to convert PDF to images: {exc}") from exc

    try:
        image = Image.open(io.BytesIO(file_bytes))
        frames = [frame.convert("RGB") for frame in ImageSequence.Iterator(image)]
        return frames or [image.convert("RGB")]
    except Exception as exc:
        raise IngestionError(f"Unable to open image file: {exc}") from exc
