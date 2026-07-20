"""
Diagnostic script for debugging HOTIX invoice extraction.

Usage:
    python -m server.diagnose_invoice path/to/invoice.png [--output path/to/output.json]

Dumps:
    1. Every raw OCR line with bounding box (text, x1, y1, x2, y2, confidence, page)
    2. Row-grouped output after cluster_rows()
    3. Per-field extraction proposals and conflict resolution
    4. Final extracted fields with confidence scores

Run with HOTIX_DEBUG_EXTRACTION=1 for extra per-field debug logging.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

# Add project root to path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from PIL import Image

from server.ocr_engine import PaddleOcrEngine, MIN_CONFIDENCE
from server.utils import cluster_rows, BoundingBox, OCRLine
from server.field_extractor import (
    extract_invoice_fields,
    extract_field_confidences,
    extract_field_selections_raw,
    extract_raw_text,
    _extract_field_selections,
    FIELD_ORDER,
    FIELD_ALIASES,
    FieldSelection,
)


def make_serializable(obj):
    """Convert objects to JSON-serializable types."""
    if isinstance(obj, FieldSelection):
        return {
            "value": obj.value,
            "confidence": obj.confidence,
            "score": obj.score,
            "ocr_line_text": obj.ocr_line.text[:80] if obj.ocr_line else None,
            "ocr_line_box": str(obj.ocr_line.box) if obj.ocr_line else None,
        }
    if isinstance(obj, BoundingBox):
        return {"x1": obj.x1, "y1": obj.y1, "x2": obj.x2, "y2": obj.y2, "w": obj.width, "h": obj.height}
    if isinstance(obj, OCRLine):
        return {
            "text": obj.text,
            "page": obj.page_index,
            "line_index": obj.line_index,
            "confidence": obj.confidence,
            "box": make_serializable(obj.box),
        }
    if isinstance(obj, dict):
        return {k: make_serializable(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [make_serializable(v) for v in obj]
    return obj


def diagnose(image_path: str) -> dict:
    print(f"\n{'='*60}")
    print(f"DIAGNOSTIC: {image_path}")
    print(f"{'='*60}\n")

    result = {
        "image": image_path,
        "settings": {
            "MIN_CONFIDENCE": MIN_CONFIDENCE,
            "FIELD_CONFIDENCE_THRESHOLD": 0.6,  # from field_extractor.py
            "MAX_LOOKAHEAD_ROWS": 4,  # from field_extractor.py
        },
        "timing": {},
    }

    # ── Step 1: Load image ──────────────────────────────────────────────
    print("1. Loading image...")
    t0 = time.time()
    try:
        image = Image.open(image_path)
    except Exception as e:
        print(f"   FAILED: {e}")
        result["error"] = str(e)
        return result
    result["image_size"] = {"width": image.width, "height": image.height}
    print(f"   Size: {image.width} x {image.height}")
    print(f"   Time: {time.time() - t0:.2f}s")

    # ── Step 2: Run OCR ─────────────────────────────────────────────────
    print("\n2. Running PaddleOCR...")
    print(f"   (This may take a while on first run while models load)")
    t0 = time.time()
    try:
        engine = PaddleOcrEngine()
        ocr_result = engine.recognize(image, page_index=0)
    except Exception as e:
        print(f"   FAILED: {e}")
        result["error"] = f"OCR failed: {e}"
        return result
    ocr_time = time.time() - t0
    result["timing"]["ocr"] = round(ocr_time, 2)
    print(f"   Detected {len(ocr_result.lines)} lines in {ocr_time:.2f}s")

    # ── Step 3: Dump raw lines ──────────────────────────────────────────
    print(f"\n3. Raw OCR lines (before clustering):")
    raw_lines = []
    for i, line in enumerate(ocr_result.lines):
        entry = make_serializable(line)
        raw_lines.append(entry)
        print(f"   [{i:3d}] conf={line.confidence:.3f} "
              f"box=({line.box.x1:.0f},{line.box.y1:.0f},{line.box.x2:.0f},{line.box.y2:.0f}) "
              f"h={line.box.height:.0f} w={line.box.width:.0f} "
              f"page={line.page_index} "
              f"text={line.text!r}")
    result["raw_lines"] = raw_lines

    # ── Step 4: Cluster rows ────────────────────────────────────────────
    print(f"\n4. After cluster_rows():")
    rows = cluster_rows(ocr_result.lines)
    clustered = []
    for ri, row in enumerate(rows):
        y_centers = [l.box.center_y for l in row]
        avg_y = sum(y_centers) / len(y_centers) if y_centers else 0
        items = []
        for line in row:
            items.append(make_serializable(line))
        entry = {"row_index": ri, "avg_y_center": round(avg_y, 1), "items": items}
        clustered.append(entry)
        texts = [l.text[:60] for l in row]
        print(f"   Row {ri:3d} (y≈{avg_y:.0f}): {' | '.join(texts)}")
    result["clustered_rows"] = clustered

    # ── Step 5: Run full extraction ─────────────────────────────────────
    print(f"\n5. Full field extraction:")
    result["extraction"] = {}

    selections = _extract_field_selections(ocr_result.lines)
    print(f"\n   Raw selections (before confidence filtering):")
    for field in FIELD_ORDER:
        sel = selections[field]
        if sel.value is not None:
            print(f"     {field:20s} = {sel.value!r:30s}  "
                  f"conf={sel.confidence:.3f}  score={sel.score:.1f}")
            if sel.ocr_line:
                print(f"     {'':20s}   from: {sel.ocr_line.text[:80]!r}")
        else:
            print(f"     {field:20s} = None")
    result["extraction"]["selections"] = make_serializable(selections)

    fields = extract_invoice_fields(ocr_result.lines)
    confidences = extract_field_confidences(ocr_result.lines)
    print(f"\n   Final fields (after confidence threshold):")
    for field in FIELD_ORDER:
        val = fields[field]
        conf = confidences[field]
        print(f"     {field:20s} = {val!r:30s}  conf={conf:.3f}")
    result["extraction"]["final_fields"] = fields
    result["extraction"]["confidences"] = confidences

    # ── Step 6: Raw text (as shown in the app) ──────────────────────────
    raw_text = extract_raw_text(ocr_result.lines)
    result["raw_text"] = raw_text
    print(f"\n6. Raw OCR text (from extract_raw_text):")
    for line in raw_text.split("\n"):
        print(f"     {line}")

    print(f"\n{'='*60}")
    print(f"DIAGNOSTIC COMPLETE")
    print(f"{'='*60}\n")

    return result


def main():
    parser = argparse.ArgumentParser(description="Diagnose HOTIX invoice extraction")
    parser.add_argument("image", help="Path to invoice image (PNG, JPG, PDF, etc.)")
    parser.add_argument("--output", "-o", default=None, help="Output JSON file path")
    args = parser.parse_args()

    if not os.path.isfile(args.image):
        print(f"Error: file not found: {args.image}")
        sys.exit(1)

    result = diagnose(args.image)

    output_path = args.output or (Path(args.image).stem + "_diagnostic.json")
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)
    print(f"\nDiagnostic saved to: {output_path}")


if __name__ == "__main__":
    main()
