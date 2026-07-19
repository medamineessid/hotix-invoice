"""Generate synthetic invoice OCR data for testing field extraction accuracy."""

from __future__ import annotations

import csv
import json
from pathlib import Path

from utils import BoundingBox, OCRLine


INVOICE_TEMPLATES = [
    {
        "numero_facture": "INV-2024-001",
        "date": "2024-03-15",
        "fournisseur": "SARL Dupont et Fils",
        "client": "Entreprise Martin EURL",
        "montant_ht": "1250.000",
        "montant_tva": "250.000",
        "montant_taxe": "0.000",
        "montant_ttc": "1500.000",
        "layout": [
            ("N° Facture", 10, 10, 80, 25),
            ("INV-2024-001", 100, 10, 200, 25),
            ("Date", 10, 35, 50, 50),
            ("15/03/2024", 100, 35, 180, 50),
            ("Fournisseur", 10, 60, 100, 75),
            ("SARL Dupont et Fils", 100, 60, 280, 75),
            ("Client", 10, 85, 60, 100),
            ("Entreprise Martin EURL", 100, 85, 300, 100),
            ("Montant HT", 10, 150, 100, 165),
            ("1250.00", 100, 150, 180, 165),
            ("TVA", 10, 175, 50, 190),
            ("250.00", 100, 175, 180, 190),
            ("TTC", 10, 200, 50, 215),
            ("1500.00", 100, 200, 180, 215),
        ]
    },
    {
        "numero_facture": "FAC-2024-0042",
        "date": "2024-03-16",
        "fournisseur": "Societe Nouvelle SARL",
        "client": "Cabinet Conseil Associes",
        "montant_ht": "2500.000",
        "montant_tva": "500.000",
        "montant_taxe": "0.000",
        "montant_ttc": "3000.000",
        "layout": [
            ("Facture N°", 10, 10, 80, 25),
            ("FAC-2024-0042", 100, 10, 220, 25),
            ("Date Facture", 10, 35, 100, 50),
            ("16/03/2024", 100, 35, 200, 50),
            ("Fournisseur", 10, 60, 100, 75),
            ("Societe Nouvelle SARL", 100, 60, 300, 75),
            ("Designation", 10, 85, 100, 100),
            ("Prestation de conseil", 100, 85, 280, 100),
            ("Client", 10, 125, 60, 140),
            ("Cabinet Conseil Associes", 100, 125, 320, 140),
            ("Montant HT", 10, 175, 100, 190),
            ("2500.00", 100, 175, 200, 190),
            ("TVA", 10, 200, 50, 215),
            ("500.00", 100, 200, 200, 215),
            ("TTC", 10, 225, 50, 240),
            ("3000.00", 100, 225, 200, 240),
        ]
    },
    {
        "numero_facture": "2024-00123",
        "date": "2024-03-17",
        "fournisseur": "Construction Moderne SARL",
        "client": "Mairie de Villefranche",
        "montant_ht": "5000.000",
        "montant_tva": "1000.000",
        "montant_taxe": "0.000",
        "montant_ttc": "6000.000",
        "layout": [
            ("Numero de Facture", 10, 10, 120, 25),
            ("2024-00123", 130, 10, 230, 25),
            ("Date", 10, 35, 50, 50),
            ("17/03/2024", 130, 35, 230, 50),
            ("Adresse Fournisseur", 10, 60, 150, 75),
            ("123 rue de la Paix", 130, 60, 300, 75),
            ("Fournisseur", 10, 85, 100, 100),
            ("Construction Moderne SARL", 130, 85, 350, 100),
            ("Client", 10, 125, 60, 140),
            ("Mairie de Villefranche", 130, 125, 320, 140),
            ("Montant HT", 10, 175, 100, 190),
            ("5000.00", 130, 175, 230, 190),
            ("TVA", 10, 200, 50, 215),
            ("1000.00", 130, 200, 230, 215),
            ("TTC", 10, 225, 50, 240),
            ("6000.00", 130, 225, 230, 240),
        ]
    },
    {
        "numero_facture": "INV-2024-789",
        "date": "2024-03-18",
        "fournisseur": "Etablissements Leclerc",
        "client": "Pharmacie du Centre",
        "montant_ht": "750.000",
        "montant_tva": "150.000",
        "montant_taxe": "0.000",
        "montant_ttc": "900.000",
        "layout": [
            ("N° Facture", 10, 10, 80, 25),
            ("INV-2024-789", 100, 10, 220, 25),
            ("Date", 10, 35, 50, 50),
            ("18/03/2024", 100, 35, 200, 50),
            ("Adresse", 10, 60, 80, 75),
            ("45 avenue Foch, 75008 Paris, France", 100, 60, 400, 75),
            ("Fournisseur", 10, 85, 100, 100),
            ("Etablissements Leclerc", 100, 85, 300, 100),
            ("Client", 10, 125, 60, 140),
            ("Pharmacie du Centre", 100, 125, 280, 140),
            ("Montant HT", 10, 175, 100, 190),
            ("750.00", 100, 175, 180, 190),
            ("TVA", 10, 200, 50, 215),
            ("150.00", 100, 200, 180, 215),
            ("TTC", 10, 225, 50, 240),
            ("900.00", 100, 225, 180, 240),
        ]
    },
    {
        "numero_facture": "REF-2024-555",
        "date": "2024-03-19",
        "fournisseur": "Atelier Creation Design",
        "client": "Agence Publicite Plus",
        "montant_ht": "3200.000",
        "montant_tva": "640.000",
        "montant_taxe": "0.000",
        "montant_ttc": "3840.000",
        "layout": [
            ("Facture", 10, 10, 70, 25),
            ("REF-2024-555", 100, 10, 220, 25),
            ("Date", 10, 35, 50, 50),
            ("19/03/2024", 100, 35, 200, 50),
            ("Description", 10, 60, 100, 75),
            ("Creation graphique", 100, 60, 280, 75),
            ("Fournisseur", 10, 85, 100, 100),
            ("Atelier Creation Design", 100, 85, 300, 100),
            ("Client", 10, 125, 60, 140),
            ("Agence Publicite Plus", 100, 125, 300, 140),
            ("Montant HT", 10, 175, 100, 190),
            ("3200.00", 100, 175, 200, 190),
            ("TVA", 10, 200, 50, 215),
            ("640.00", 100, 200, 200, 215),
            ("TTC", 10, 225, 50, 240),
            ("3840.00", 100, 225, 200, 240),
        ]
    },
]


def generate_ocr_lines(template):
    """Convert a template layout into OCRLine objects."""
    lines = []
    for i, (text, x1, y1, x2, y2) in enumerate(template["layout"]):
        box = BoundingBox(x1, y1, x2, y2)
        confidence = 0.95 if any(label in text.lower() for label in [
            "facture", "date", "fournisseur", "client", "montant", "tva", "ttc",
            "designation", "description", "adresse", "numero", "n°", "n"
        ]) else 0.90
        lines.append(OCRLine(text, box, confidence, page_index=0, line_index=i))
    return lines


def create_ground_truth_csv(output_path):
    """Create a CSV file with ground truth for all synthetic invoices."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "invoice_id",
            "numero_facture",
            "date",
            "fournisseur",
            "client",
            "montant_ht",
            "montant_tva",
            "montant_taxe",
            "montant_ttc",
        ])
        writer.writeheader()
        
        for i, template in enumerate(INVOICE_TEMPLATES):
            writer.writerow({
                "invoice_id": f"synthetic_{i:03d}",
                "numero_facture": template["numero_facture"],
                "date": template["date"],
                "fournisseur": template["fournisseur"],
                "client": template["client"],
                "montant_ht": template["montant_ht"],
                "montant_tva": template["montant_tva"],
                "montant_taxe": template["montant_taxe"],
                "montant_ttc": template["montant_ttc"],
            })


def create_ocr_json(output_dir):
    """Create JSON files with OCR data for each synthetic invoice."""
    output_dir.mkdir(parents=True, exist_ok=True)
    
    for i, template in enumerate(INVOICE_TEMPLATES):
        ocr_lines = generate_ocr_lines(template)
        ocr_data = [
            {
                "text": line.text,
                "box": {
                    "x1": line.box.x1,
                    "y1": line.box.y1,
                    "x2": line.box.x2,
                    "y2": line.box.y2,
                },
                "confidence": line.confidence,
                "page_index": line.page_index,
                "line_index": line.line_index,
            }
            for line in ocr_lines
        ]
        
        output_file = output_dir / f"synthetic_{i:03d}.json"
        with open(output_file, "w", encoding="utf-8") as f:
            json.dump(ocr_data, f, indent=2)


if __name__ == "__main__":
    test_data_dir = Path(__file__).parent.parent / "invoices"
    test_data_dir.mkdir(exist_ok=True)
    
    create_ground_truth_csv(test_data_dir / "ground_truth.csv")
    print("[OK] Created ground truth: {}".format(test_data_dir / "ground_truth.csv"))
    
    ocr_dir = test_data_dir / "ocr_data"
    create_ocr_json(ocr_dir)
    print("[OK] Created {} OCR JSON files in {}".format(len(INVOICE_TEMPLATES), ocr_dir))
    
    print("\nTest data ready. Run score_accuracy.py to evaluate extraction.")
