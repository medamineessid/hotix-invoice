"""Smoke test for the PaddleOCR installation used by HOTIX."""

from __future__ import annotations

__test__ = False

import argparse
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from paddleocr import PaddleOCR


def parse_args() -> argparse.Namespace:
    """Parse command line arguments for the smoke test."""

    parser = argparse.ArgumentParser(description="Smoke test PaddleOCR on a file or generated sample image.")
    parser.add_argument("file", nargs="?", help="Optional PDF or image file to test.")
    return parser.parse_args()


def create_sample_image() -> Path:
    """Create a temporary invoice-like sample image for OCR validation."""

    image = Image.new("RGB", (1400, 1000), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.load_default()
    draw.text((60, 60), "FACTURE N° FAC-2026-001", fill="black", font=font)
    draw.text((60, 130), "Date: 01/05/2026", fill="black", font=font)
    draw.text((60, 200), "Fournisseur: ABC SARL", fill="black", font=font)
    draw.text((60, 270), "Client: HOTIX", fill="black", font=font)
    draw.text((60, 360), "Total HT: 1000.000", fill="black", font=font)
    draw.text((60, 430), "TVA: 190.000", fill="black", font=font)
    draw.text((60, 500), "TTC: 1190.000", fill="black", font=font)

    temp_file = tempfile.NamedTemporaryFile(delete=False, suffix=".png")
    image.save(temp_file.name)
    temp_file.close()
    return Path(temp_file.name)


def resolve_target_path(file_arg: str | None) -> tuple[Path, bool]:
    """Resolve the requested file path or create a temporary sample image."""

    if file_arg:
        return Path(file_arg), False
    return create_sample_image(), True


def load_image(path: Path) -> Image.Image:
    """Load the target file as an RGB image."""

    return Image.open(path).convert("RGB")


def initialize_ocr() -> PaddleOCR:
    """Initialize the PaddleOCR engine for French text."""

    return PaddleOCR(lang="fr", use_angle_cls=True, show_log=False)


def count_detected_lines(result: object) -> int:
    """Count detected OCR lines in a PaddleOCR result payload."""

    total = 0
    for page in result or []:
        for line in page or []:
            if len(line) >= 2:
                total += 1
    return total


def run_smoke_test(file_arg: str | None) -> int:
    """Run the OCR smoke test and print the detected text count."""

    target_path, generated = resolve_target_path(file_arg)
    if file_arg and not target_path.exists():
        print(f"Input file not found: {target_path}", file=sys.stderr)
        return 2

    try:
        ocr = initialize_ocr()
    except Exception as exc:
        print(f"FAIL: unable to initialize PaddleOCR: {exc}")
        return 1

    try:
        image = load_image(target_path)
        result = ocr.ocr(np.array(image), cls=True)
    except Exception as exc:
        print(f"FAIL: OCR inference failed: {exc}")
        return 1
    finally:
        if generated:
            target_path.unlink(missing_ok=True)

    detected_lines = count_detected_lines(result)
    if detected_lines == 0:
        print("OCR completed, but no text was detected.")
        return 1

    print(f"PASS: PaddleOCR initialized and processed {target_path}")
    print(f"Detected lines: {detected_lines}")
    return 0


def main() -> int:
    """Run the smoke test entry point."""

    args = parse_args()
    return run_smoke_test(args.file)


if __name__ == "__main__":
    raise SystemExit(main())
