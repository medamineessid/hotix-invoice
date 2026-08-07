#!/usr/bin/env python3
"""HOTIX Invoice Extraction Evaluation Harness.

Run:
    python eval_extraction.py [--ground-truth-dir PATH] [--output PATH]

Scoring modes:
    - Per-field exact match for text fields (numero_facture, date, fournisseur, client, direction)
    - Per-field numeric tolerance (0.01 TND) for amounts
    - Per-item precision/recall with best-alignment matching (by description similarity + line_total)

Output:
    - JSON report file
    - Human-readable table on stdout
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import sys
from datetime import datetime
from decimal import Decimal
from difflib import SequenceMatcher
from pathlib import Path
from typing import Optional

# Ensure server package is importable
_project_root = str(Path(__file__).resolve().parent.parent)
if _project_root not in sys.path:
    sys.path.insert(0, _project_root)

from server.field_extractor import extract_invoice_fields, extract_field_confidences, extract_item_table
from server.ocr_engine import PaddleOcrEngine
from server.ingestion import load_invoice_images
from server.utils import _parse_decimal, OCRLine

# ── Configure logging ─────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.WARNING,
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger("eval_extraction")
logger.setLevel(logging.INFO)

# ── Constants ─────────────────────────────────────────────────────────────────
FIELD_ORDER = (
    "numero_facture", "date", "fournisseur", "client",
    "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
)
NUMERIC_FIELDS = {"montant_ht", "montant_tva", "montant_taxe", "montant_ttc"}
AMOUNT_TOLERANCE = Decimal("0.01")
DESCRIPTION_SIMILARITY_THRESHOLD = 0.6

# ── Ground truth loading ──────────────────────────────────────────────────────


def load_ground_truth(gt_dir: Path) -> dict[str, dict]:
    """Load all ground truth JSON files from a directory.

    Expected format per file (e.g. INV001.json):
    {
        "invoice_id": "INV001",
        "image_path": "path/to/invoice.pdf",
        "fields": {
            "numero_facture": "FAC-2025-001",
            "date": "2025-03-15",
            "fournisseur": "Supplier SARL",
            "client": "Client SA",
            "montant_ht": "1000.000",
            "montant_tva": "190.000",
            "montant_taxe": "0.000",
            "montant_ttc": "1190.000",
            "direction": "achat"
        },
        "items": [
            {
                "description": "Produit A",
                "quantity": 2.0,
                "unit_price": 100.0,
                "vat_rate": 0.19,
                "vat_amount": 38.0,
                "discount": 0.0,
                "line_total": 200.0
            }
        ]
    }
    """
    truths: dict[str, dict] = {}
    for gt_file in sorted(gt_dir.glob("*.json")):
        with open(gt_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        invoice_id = data.get("invoice_id", gt_file.stem)
        truths[invoice_id] = data
    return truths


# ── Field comparison ──────────────────────────────────────────────────────────


def _normalize_text(value: Optional[str]) -> Optional[str]:
    """Normalize for comparison: strip whitespace, lowercase."""
    if value is None:
        return None
    v = value.strip()
    return v if v else None


def compare_field_exact(field: str, extracted: Optional[str], ground_truth: Optional[str]) -> bool:
    """Exact string match after normalization."""
    ext = _normalize_text(extracted)
    gt = _normalize_text(ground_truth)
    return ext == gt


def compare_field_tolerance(field: str, extracted: Optional[str], ground_truth: Optional[str]) -> bool:
    """Numeric tolerance match (within 0.01 TND)."""
    ext_dec = _parse_decimal(extracted)
    gt_dec = _parse_decimal(ground_truth)
    if ext_dec is None and gt_dec is None:
        return True
    if ext_dec is None or gt_dec is None:
        return False
    return abs(ext_dec - gt_dec) < AMOUNT_TOLERANCE


# ── Item matching ─────────────────────────────────────────────────────────────


def _description_similarity(a: str, b: str) -> float:
    """Compute similarity ratio between two item descriptions."""
    if not a or not b:
        return 0.0
    return SequenceMatcher(None, a.lower().strip(), b.lower().strip()).ratio()


def _match_items_by_alignment(
    extracted_items: list[dict],
    ground_truth_items: list[dict],
) -> tuple[list[tuple[int, int]], set[int], set[int]]:
    """Best-alignment matching: pair extracted and ground-truth items by
    description similarity + line_total proximity.

    Returns (matched_pairs, unmatched_extracted_indices, unmatched_gt_indices).
    """
    n_ext = len(extracted_items)
    n_gt = len(ground_truth_items)

    if n_ext == 0 or n_gt == 0:
        return [], set(range(n_ext)), set(range(n_gt))

    # Build score matrix: each cell scores how well extracted[i] matches gt[j]
    scores: list[tuple[float, int, int]] = []
    for i in range(n_ext):
        ext = extracted_items[i]
        ext_desc = str(ext.get("designation", "") or "")
        ext_total = _parse_decimal(str(ext.get("montant", "")) if ext.get("montant") is not None else None)
        for j in range(n_gt):
            gt = ground_truth_items[j]
            gt_desc = str(gt.get("description", "") or "")
            gt_total = _parse_decimal(str(gt.get("line_total", "")) if gt.get("line_total") is not None else None)

            desc_sim = _description_similarity(ext_desc, gt_desc)
            total_sim = 0.0
            if ext_total is not None and gt_total is not None:
                diff = abs(ext_total - gt_total)
                if diff < AMOUNT_TOLERANCE:
                    total_sim = 1.0
                elif gt_total > 0:
                    total_sim = max(0.0, 1.0 - float(diff / gt_total))

            combined = desc_sim * 0.6 + total_sim * 0.4
            if combined >= DESCRIPTION_SIMILARITY_THRESHOLD:
                scores.append((combined, i, j))

    scores.sort(key=lambda x: x[0], reverse=True)

    matched_ext: set[int] = set()
    matched_gt: set[int] = set()
    pairs: list[tuple[int, int]] = []

    for _, i, j in scores:
        if i not in matched_ext and j not in matched_gt:
            pairs.append((i, j))
            matched_ext.add(i)
            matched_gt.add(j)

    unmatched_ext = set(range(n_ext)) - matched_ext
    unmatched_gt = set(range(n_gt)) - matched_gt
    return pairs, unmatched_ext, unmatched_gt


def _compare_item_fields(extracted_item: dict, gt_item: dict) -> dict[str, bool]:
    """Compare all fields of a matched item pair."""
    field_map = [
        ("designation", "description", "exact"),
        ("quantite", "quantity", "tolerance"),
        ("prix_unitaire", "unit_price", "tolerance"),
        ("tva_rate", "vat_rate", "tolerance"),
        ("montant", "line_total", "tolerance"),
    ]
    results: dict[str, bool] = {}
    for ext_key, gt_key, mode in field_map:
        ext_val = str(extracted_item.get(ext_key, "") or "")
        gt_val = str(gt_item.get(gt_key, "") or "")
        if mode == "exact":
            results[ext_key] = compare_field_exact(ext_key, ext_val, gt_val)
        else:
            results[ext_key] = compare_field_tolerance(ext_key, ext_val, gt_val)
    return results


# ── Main scoring ──────────────────────────────────────────────────────────────


def score_single_invoice(
    invoice_id: str,
    gt_data: dict,
    fields_ocr: dict[str, Optional[str]],
    items_ocr: list[dict],
    fields_gemini: dict[str, Optional[str]] | None = None,
    items_gemini: list[dict] | None = None,
    fields_reconciled: dict[str, Optional[str]] | None = None,
    items_reconciled: list[dict] | None = None,
) -> dict:
    """Score one invoice against ground truth.

    Returns a dict with per-field results for each engine, plus item metrics.
    """
    gt_fields = gt_data.get("fields", {})
    gt_items = gt_data.get("items", [])

    result: dict = {
        "invoice_id": invoice_id,
        "image_path": gt_data.get("image_path", ""),
        "field_results": {},
        "item_results": {},
    }

    # ── Field-level scoring per engine ──
    for engine_name, fields in [("ocr", fields_ocr), ("gemini", fields_gemini), ("reconciled", fields_reconciled)]:
        if fields is None:
            continue
        field_scores: dict[str, dict] = {}
        for field in FIELD_ORDER:
            ext_val = fields.get(field)
            gt_val = gt_fields.get(field)
            exact = compare_field_exact(field, ext_val, gt_val)
            tolerance = compare_field_tolerance(field, ext_val, gt_val) if field in NUMERIC_FIELDS else exact
            field_scores[field] = {
                "extracted": ext_val,
                "ground_truth": gt_val,
                "exact_match": exact,
                "tolerance_match": tolerance,
            }
        result["field_results"][engine_name] = field_scores

    # ── Item-level scoring per engine ──
    for engine_name, items in [("ocr", items_ocr), ("gemini", items_gemini), ("reconciled", items_reconciled)]:
        if items is None:
            continue
        pairs, unmatched_ext, unmatched_gt = _match_items_by_alignment(items, gt_items)

        item_scores: dict = {
            "gt_count": len(gt_items),
            "extracted_count": len(items),
            "matched_count": len(pairs),
            "precision": len(pairs) / max(len(items), 1),
            "recall": len(pairs) / max(len(gt_items), 1),
            "f1": 0.0,
            "matched_items": [],
            "unmatched_extracted": list(unmatched_ext),
            "unmatched_ground_truth": list(unmatched_gt),
        }
        if item_scores["precision"] + item_scores["recall"] > 0:
            item_scores["f1"] = 2 * item_scores["precision"] * item_scores["recall"] / (
                item_scores["precision"] + item_scores["recall"]
            )

        for ext_idx, gt_idx in pairs:
            field_results = _compare_item_fields(items[ext_idx], gt_items[gt_idx])
            item_scores["matched_items"].append({
                "extracted_idx": ext_idx,
                "gt_idx": gt_idx,
                "extracted": items[ext_idx],
                "ground_truth": gt_items[gt_idx],
                "field_results": field_results,
            })

        result["item_results"][engine_name] = item_scores

    return result


def aggregate_results(all_results: list[dict]) -> dict:
    """Aggregate per-invoice results into summary statistics."""
    field_order = list(FIELD_ORDER)
    engines = ["ocr", "gemini", "reconciled"]

    summary: dict = {
        "total_invoices": len(all_results),
        "field_accuracy": {},
        "item_metrics": {},
    }

    for engine in engines:
        field_counts: dict[str, dict[str, int]] = {
            field: {"exact_correct": 0, "exact_total": 0, "tolerance_correct": 0, "tolerance_total": 0}
            for field in field_order
        }
        item_precisions: list[float] = []
        item_recalls: list[float] = []
        item_f1s: list[float] = []
        total_matched = 0
        total_extracted = 0
        total_gt = 0

        for r in all_results:
            # Field metrics
            field_results = r.get("field_results", {}).get(engine)
            if field_results:
                for field, scores in field_results.items():
                    if scores["exact_match"]:
                        field_counts[field]["exact_correct"] += 1
                    field_counts[field]["exact_total"] += 1
                    if field in NUMERIC_FIELDS:
                        if scores.get("tolerance_match"):
                            field_counts[field]["tolerance_correct"] += 1
                        field_counts[field]["tolerance_total"] += 1

            # Item metrics
            item_results = r.get("item_results", {}).get(engine)
            if item_results:
                item_precisions.append(item_results["precision"])
                item_recalls.append(item_results["recall"])
                item_f1s.append(item_results["f1"])
                total_matched += item_results["matched_count"]
                total_extracted += item_results["extracted_count"]
                total_gt += item_results["gt_count"]

        # Compute percentages
        field_pcts: dict[str, dict] = {}
        for field in field_order:
            fc = field_counts.get(field, {"exact_correct": 0, "exact_total": 0})
            field_pcts[field] = {
                "exact_accuracy": fc["exact_correct"] / max(fc["exact_total"], 1) * 100,
                "exact_correct": fc["exact_correct"],
                "exact_total": fc["exact_total"],
            }
            if field in NUMERIC_FIELDS:
                field_pcts[field]["tolerance_accuracy"] = fc["tolerance_correct"] / max(fc["tolerance_total"], 1) * 100
                field_pcts[field]["tolerance_correct"] = fc["tolerance_correct"]
                field_pcts[field]["tolerance_total"] = fc["tolerance_total"]

        summary["field_accuracy"][engine] = field_pcts
        summary["item_metrics"][engine] = {
            "avg_precision": sum(item_precisions) / max(len(item_precisions), 1),
            "avg_recall": sum(item_recalls) / max(len(item_recalls), 1),
            "avg_f1": sum(item_f1s) / max(len(item_f1s), 1),
            "total_matched": total_matched,
            "total_extracted": total_extracted,
            "total_gt": total_gt,
        }

    return summary


# ── Main entry point ──────────────────────────────────────────────────────────


def main():
    parser = argparse.ArgumentParser(description="HOTIX Invoice Extraction Evaluation Harness")
    parser.add_argument(
        "--ground-truth-dir",
        type=Path,
        default=Path(__file__).parent.parent / "evaluation" / "ground_truth",
        help="Directory containing ground truth JSON files",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="Output path for JSON report (default: evaluation/report_TIMESTAMP.json)",
    )
    parser.add_argument(
        "--engine",
        choices=["ocr", "gemini", "all"],
        default="ocr",
        help="Which engine(s) to evaluate (default: ocr)",
    )
    parser.add_argument(
        "--verbose", "-v",
        action="store_true",
        help="Print per-invoice detailed results",
    )
    args = parser.parse_args()

    gt_dir = args.ground_truth_dir
    if not gt_dir.exists():
        print(f"ERROR: Ground truth directory not found: {gt_dir}")
        print("Create evaluation/ground_truth/ with one JSON file per invoice.")
        print("See eval_extraction.py docstring for format.")
        sys.exit(1)

    truths = load_ground_truth(gt_dir)
    if not truths:
        print(f"ERROR: No ground truth JSON files found in {gt_dir}")
        sys.exit(1)

    print(f"Loaded {len(truths)} ground truth invoices from {gt_dir}")

    # ── Run extraction ──
    ocr_engine = PaddleOcrEngine()
    poppler_path = os.getenv("POPPLER_PATH")
    all_results: list[dict] = []

    for invoice_id, gt_data in sorted(truths.items()):
        image_path = gt_data.get("image_path", "")
        print(f"\nProcessing {invoice_id} ({image_path})...")

        # Resolve image path relative to the ground truth file or absolute
        if image_path and not Path(image_path).is_absolute():
            # Try relative to ground truth dir, then relative to project root
            candidates = [
                gt_dir / image_path,
                Path(_project_root) / image_path,
            ]
            resolved = None
            for c in candidates:
                if c.exists():
                    resolved = c
                    break
            if resolved is None:
                print(f"  WARNING: Image not found: {image_path} (tried {candidates})")
                continue
            image_path = str(resolved)

        if not image_path or not Path(image_path).exists():
            print(f"  WARNING: Image path not set or file missing: {image_path}")
            continue

        # Load image pages via same pipeline as server
        try:
            file_bytes = Path(image_path).read_bytes()
            pages = load_invoice_images(file_bytes, Path(image_path).name, poppler_path=poppler_path)
        except Exception as exc:
            print(f"  ERROR loading image: {exc}")
            continue

        # OCR extraction
        all_lines: list[OCRLine] = []
        for page_idx, page_image in enumerate(pages):
            ocr_result = ocr_engine.recognize(page_image, page_idx)
            all_lines.extend(ocr_result.lines)

        fields_ocr = extract_invoice_fields(all_lines)
        confidences_ocr = extract_field_confidences(all_lines)
        items_ocr_raw = extract_item_table(all_lines)
        items_ocr = [
            {k: v for k, v in it.items()} for it in items_ocr_raw if isinstance(it, dict)
        ]

        # Score
        result = score_single_invoice(
            invoice_id=invoice_id,
            gt_data=gt_data,
            fields_ocr=fields_ocr,
            items_ocr=items_ocr,
            # Gemini and reconciled not run in offline eval mode
            fields_gemini=None,
            items_gemini=None,
            fields_reconciled=None,
            items_reconciled=None,
        )
        all_results.append(result)

        if args.verbose:
            ocr_field = result["field_results"]["ocr"]
            correct = sum(1 for f in FIELD_ORDER if ocr_field[f]["exact_match"])
            print(f"  OCR: {correct}/{len(FIELD_ORDER)} fields correct")
            item_r = result.get("item_results", {}).get("ocr", {})
            if item_r:
                print(f"  Items: precision={item_r['precision']:.1%}, recall={item_r['recall']:.1%}, f1={item_r['f1']:.1%}")

    if not all_results:
        print("\nERROR: No invoices were successfully processed.")
        sys.exit(1)

    # ── Aggregate ──
    summary = aggregate_results(all_results)

    # ── Output ──
    output_dir = args.output.parent if args.output else Path(_project_root) / "evaluation"
    output_dir.mkdir(parents=True, exist_ok=True)
    output_path = args.output or (output_dir / f"report_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json")

    report = {
        "timestamp": datetime.now().isoformat(),
        "engine": args.engine,
        "total_invoices": len(all_results),
        "summary": summary,
        "per_invoice": all_results,
    }

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False, default=str)
    print(f"\nJSON report written to: {output_path}")

    # ── Human-readable table ──
    _print_summary_table(summary)

    return 0


def _print_summary_table(summary: dict) -> None:
    """Print a human-readable accuracy summary table."""
    field_order = list(FIELD_ORDER)

    for engine in ["ocr", "gemini", "reconciled"]:
        fa = summary["field_accuracy"].get(engine)
        if fa is None:
            continue

        print(f"\n{'='*80}")
        print(f"  ENGINE: {engine.upper()}")
        print(f"{'='*80}")
        print(f"{'Field':<20s} {'Exact':>8s} {'Total':>8s} {'Acc %':>7s}", end="")
        has_tolerance = any("tolerance_accuracy" in fa.get(f, {}) for f in field_order)
        if has_tolerance:
            print(f"  {'Tol Corr':>9s} {'Tol Acc%':>8s}", end="")
        print()
        print("-" * (50 if not has_tolerance else 70))

        overall_exact_correct = 0
        overall_exact_total = 0
        overall_tol_correct = 0
        overall_tol_total = 0

        for field in field_order:
            f = fa.get(field, {})
            ec = f.get("exact_correct", 0)
            et = f.get("exact_total", 0)
            ea = f.get("exact_accuracy", 0)
            overall_exact_correct += ec
            overall_exact_total += et
            line = f"{field:<20s} {ec:>8d} {et:>8d} {ea:>6.1f}%"
            if has_tolerance and "tolerance_accuracy" in f:
                tc = f.get("tolerance_correct", 0)
                tt = f.get("tolerance_total", 0)
                ta = f.get("tolerance_accuracy", 0)
                overall_tol_correct += tc
                overall_tol_total += tt
                line += f"  {tc:>9d} {ta:>7.1f}%"
            print(line)

        print("-" * (50 if not has_tolerance else 70))
        overall_ea = overall_exact_correct / max(overall_exact_total, 1) * 100
        line = f"{'OVERALL':<20s} {overall_exact_correct:>8d} {overall_exact_total:>8d} {overall_ea:>6.1f}%"
        if has_tolerance:
            overall_ta = overall_tol_correct / max(overall_tol_total, 1) * 100
            line += f"  {overall_tol_correct:>9d} {overall_ta:>7.1f}%"
        print(line)

        # Item metrics
        im = summary["item_metrics"].get(engine, {})
        if im:
            print(f"\n  Items: P={im['avg_precision']:.1%}  R={im['avg_recall']:.1%}  F1={im['avg_f1']:.1%}")
            print(f"         matched={im['total_matched']}  extracted={im['total_extracted']}  gt={im['total_gt']}")


if __name__ == "__main__":
    sys.exit(main())
