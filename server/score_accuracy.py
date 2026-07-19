"""Score extraction accuracy against ground truth for synthetic invoices."""

from __future__ import annotations

import csv
import json
import sys
from pathlib import Path

# Ensure project root is on sys.path for package-level imports
_project_root = str(Path(__file__).resolve().parent.parent)
if _project_root not in sys.path:
    sys.path.insert(0, _project_root)

from server.field_extractor import extract_invoice_fields, extract_field_confidences
from server.utils import BoundingBox, OCRLine


def load_ground_truth(csv_path):
    """Load ground truth from CSV file."""
    truth = {}
    with open(csv_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            invoice_id = row.pop("invoice_id")
            truth[invoice_id] = row
    return truth


def load_ocr_data(json_path):
    """Load OCR data from JSON file."""
    with open(json_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    
    lines = []
    for item in data:
        box = BoundingBox(
            item["box"]["x1"],
            item["box"]["y1"],
            item["box"]["x2"],
            item["box"]["y2"],
        )
        line = OCRLine(
            text=item["text"],
            box=box,
            confidence=item["confidence"],
            page_index=item["page_index"],
            line_index=item.get("line_index", 0),
        )
        lines.append(line)
    return lines


def normalize_for_comparison(value):
    """Normalize values for comparison (strip whitespace, lowercase for text fields)."""
    if value is None:
        return None
    value = value.strip()
    if not value:
        return None
    return value


def compare_values(field, extracted, ground_truth):
    """Compare extracted value against ground truth.
    
    For numeric fields, compare as floats (allowing small rounding differences).
    For text fields, do exact string comparison after normalization.
    """
    extracted = normalize_for_comparison(extracted)
    ground_truth = normalize_for_comparison(ground_truth)
    
    if field in ["montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]:
        if extracted is None and ground_truth is None:
            return True
        if extracted is None or ground_truth is None:
            return False
        try:
            ext_val = float(extracted)
            truth_val = float(ground_truth)
            return abs(ext_val - truth_val) < 0.01
        except ValueError:
            return False
    else:
        return extracted == ground_truth


def score_invoice(invoice_id, extracted, truth):
    """Score a single invoice extraction.
    
    Returns dict mapping field names to True (correct) or False (incorrect).
    """
    results = {}
    for field in ["numero_facture", "date", "fournisseur", "client", "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]:
        extracted_val = extracted.get(field)
        truth_val = truth.get(field)
        results[field] = compare_values(field, extracted_val, truth_val)
    return results


def main():
    """Score all synthetic invoices and print accuracy report."""
    invoices_dir = Path(__file__).parent.parent / "invoices"
    ground_truth_csv = invoices_dir / "ground_truth.csv"
    ocr_data_dir = invoices_dir / "ocr_data"
    
    if not ground_truth_csv.exists():
        print("Error: Ground truth file not found: {}".format(ground_truth_csv))
        print("Run generate_test_invoices.py first.")
        sys.exit(1)
    
    if not ocr_data_dir.exists():
        print("Error: OCR data directory not found: {}".format(ocr_data_dir))
        print("Run generate_test_invoices.py first.")
        sys.exit(1)
    
    truth = load_ground_truth(ground_truth_csv)
    print("Loaded {} ground truth invoices\n".format(len(truth)))
    
    all_results = {}
    field_scores = {field: {"correct": 0, "total": 0} for field in [
        "numero_facture", "date", "fournisseur", "client",
        "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"
    ]}
    
    for invoice_id, truth_fields in sorted(truth.items()):
        ocr_json = ocr_data_dir / "{}.json".format(invoice_id)
        if not ocr_json.exists():
            print("Warning: OCR data not found for {}".format(invoice_id))
            continue
        
        ocr_lines = load_ocr_data(ocr_json)
        extracted = extract_invoice_fields(ocr_lines)
        confidences = extract_field_confidences(ocr_lines)
        
        results = score_invoice(invoice_id, extracted, truth_fields)
        all_results[invoice_id] = {
            "results": results,
            "extracted": extracted,
            "confidences": confidences,
        }
        
        for field, is_correct in results.items():
            field_scores[field]["total"] += 1
            if is_correct:
                field_scores[field]["correct"] += 1
    
    print("=" * 100)
    print("DETAILED RESULTS BY INVOICE")
    print("=" * 100)
    for invoice_id in sorted(all_results.keys()):
        data = all_results[invoice_id]
        results = data["results"]
        extracted = data["extracted"]
        truth_fields = truth[invoice_id]
        
        correct_count = sum(1 for v in results.values() if v)
        total_count = len(results)
        
        print("\n{}: {}/{} fields correct".format(invoice_id, correct_count, total_count))
        for field in ["numero_facture", "date", "fournisseur", "client", "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]:
            is_correct = results[field]
            status = "[OK]" if is_correct else "[FAIL]"
            extracted_val = extracted.get(field) or "(none)"
            truth_val = truth_fields.get(field) or "(none)"
            print("  {} {:<20s} extracted={:<20s} truth={:<20s}".format(status, field, extracted_val, truth_val))
    
    print("\n" + "=" * 100)
    print("ACCURACY SUMMARY BY FIELD")
    print("=" * 100)
    print("{:<20s} {:<10s} {:<10s} {:<10s}".format("Field", "Correct", "Total", "Accuracy"))
    print("-" * 50)
    
    overall_correct = 0
    overall_total = 0
    
    for field in ["numero_facture", "date", "fournisseur", "client", "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]:
        correct = field_scores[field]["correct"]
        total = field_scores[field]["total"]
        accuracy = (correct / total * 100) if total > 0 else 0.0
        overall_correct += correct
        overall_total += total
        print("{:<20s} {:<10d} {:<10d} {:>6.1f}%".format(field, correct, total, accuracy))
    
    print("-" * 50)
    overall_accuracy = (overall_correct / overall_total * 100) if overall_total > 0 else 0.0
    print("{:<20s} {:<10d} {:<10d} {:>6.1f}%".format("OVERALL", overall_correct, overall_total, overall_accuracy))
    print("=" * 100)
    
    print("\nBEFORE/AFTER COMPARISON:")
    print("-" * 50)
    print("Field              Before    After     Improvement")
    print("-" * 50)
    
    baseline = {
        "numero_facture": 0.50,
        "fournisseur": 0.60,
        "date": 0.95,
        "client": 0.95,
        "montant_ht": 0.95,
        "montant_tva": 0.95,
        "montant_taxe": 0.95,
        "montant_ttc": 0.95,
    }
    
    for field in ["numero_facture", "date", "fournisseur", "client", "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]:
        correct = field_scores[field]["correct"]
        total = field_scores[field]["total"]
        after = (correct / total * 100) if total > 0 else 0.0
        before = baseline.get(field, 0.0) * 100
        improvement = after - before
        status = "UP" if improvement > 0 else "DOWN" if improvement < 0 else "SAME"
        print("{:<18s} {:>6.1f}%  {:>6.1f}%  {} {:+6.1f}%".format(field, before, after, status, improvement))


if __name__ == "__main__":
    main()
