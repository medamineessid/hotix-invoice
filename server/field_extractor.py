"""Invoice field extraction based on OCR text and bounding boxes."""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from decimal import Decimal
from typing import Mapping, Optional, Sequence


logger = logging.getLogger(__name__)


# ── Extraction debug mode ─────────────────────────────────────────────────────
# Set to True to enable detailed per-field logging for debugging extraction issues.
# Can also be enabled at runtime by setting the HOTIX_DEBUG_EXTRACTION env var.
import os
from datetime import datetime

_DEBUG_EXTRACTION = os.getenv("HOTIX_DEBUG_EXTRACTION", "").lower() in ("1", "true", "yes")


def _debug_log(msg: str) -> None:
    """Emit a debug log line when extraction debug mode is enabled."""
    if _DEBUG_EXTRACTION:
        logger.info("[EXTRACTION_DEBUG] %s", msg)

from .utils import (
    OCRLine,
    clean_amount,
    clean_date,
    cluster_rows,
    collapse_text,
    looks_like_latin_text,
    normalize_text,
    normalize_text_for_output,
)


FIELD_ORDER = (
    "numero_facture",
    "date",
    "fournisseur",
    "client",
    "montant_ht",
    "montant_tva",
    "montant_taxe",
    "montant_ttc",
)

FIELD_ALIASES: dict[str, tuple[str, ...]] = {
    # ── Invoice number ───────────────────────────────────────────────────
    # French: n°, numéro, référence facture
    # English: invoice number, ref
    # Also covers inserted stopwords via _matches_alias_relaxed fallback
    "numero_facture": (
        # French — "n°" variants (full form only; "n° fact" would
        # match "N° Facture" prefix and extract "ure" as garbage)
        "n° facture",
        "n facture",
        "n°facture",
        "n° de facture",
        "n de facture",
        # French — "numéro" variants
        "numéro de facture",
        "numero de facture",
        "numéro facture",
        "numero facture",
        # French — "facture n°" variants
        "facture n°",
        "facture no",
        "facture nº",
        "nº facture",
        "no facture",
        # French — "réf" variants (full form only; "réf fact" would
        # match "Réf Facture" prefix and extract "ure" as garbage)
        "réf facture",
        "ref facture",
        "référence facture",
        "reference facture",
        # English
        "invoice number",
        "invoice n°",
        "invoice no",
        "invoice #",
        "invoice id",
        "invoice ref",
        "inv no",
        # Purchase order / order number (sometimes used as invoice ref)
        "n° commande",
        "numero commande",
        "numéro commande",
        "order number",
        "purchase order",
        "po number",
    ),
    # ── Date ────────────────────────────────────────────────────────────
    # French: date, date d'émission, émise le
    # English: date, invoice date, issued date
    "date": (
        # French — core
        "date",
        "date de facturation",
        "date facture",
        "date de la facture",
        "date d'émission",
        "date d'emission",
        "date d'émission de la facture",
        "date d'emission de la facture",
        "date de création",
        "date de creation",
        "date de création de la facture",
        "date de creation de la facture",
        "date d'échéance",
        "date d'echeance",
        "date d'échéance de la facture",
        "date d'echeance de la facture",
        "échéance",
        "echeance",
        "date limite",
        "date d'expédition",
        "date d'expedition",
        # French — "émise le" / "émis le"
        "émise le",
        "emise le",
        "émis le",
        "emis le",
        "délivrée le",
        "delivree le",
        # French — varying word order
        "facture du",
        # English (no duplicates — "date" is already in French section)
        "invoice date",
        "date of invoice",
        "issued date",
        "issue date",
        "date issued",
        "invoice dated",
        "date d'invoice",
        # Due date
        "due date",
        "payment due",
    ),
    # ── Supplier (fournisseur) ──────────────────────────────────────────
    # French: fournisseur, vendeur, votre entreprise, société, expéditeur
    # English: supplier, seller, vendor, from, bill from
    # Tunisian: same as French
    "fournisseur": (
        # French
        "fournisseur",
        "vendeur",
        "émetteur",
        "emetteur",
        "expéditeur",
        "expediteur",
        "votre entreprise",
        "nos coordonnées",
        "nos informations",
        "informations société",
        "informations entreprise",
        "coordonnées société",
        "coordonnées",
        "société",
        "societe",
        "entreprise",
        "prestataire",
        "émetteur de la facture",
        "emetteur de la facture",
        # English
        "supplier",
        "seller",
        "vendor",
        "from",
        "bill from",
        "billing provider",
        "provider",
    ),
    # ── Client ──────────────────────────────────────────────────────────
    # French: client, acheteur, destinataire, facturé à, livré à
    # English: customer, bill to, ship to
    "client": (
        # French
        "client",
        "acheteur",
        "destinataire",
        "facturé à",
        "facture a",
        "facturée à",
        "facturee a",
        "livré à",
        "livre a",
        "livrée à",
        "livree a",
        "expédié à",
        "expedie a",
        "à l'attention de",
        "a l'attention de",
        "attention de",
        "coordonnées client",
        "informations client",
        # English
        "customer",
        "bill to",
        "billing address",
        "ship to",
        "shipping address",
        "sold to",
    ),
    # ── Montant HT (subtotal / taxable amount) ──────────────────────────
    # French: montant HT, total HT, sous-total HT, base HT
    # English: subtotal, net amount, taxable amount
    # Tunisian: same as French (TND)
    "montant_ht": (
        # French — "HT" variants
        "montant ht",
        "total ht",
        "sous-total ht",
        "sous total ht",
        "ht",
        "base ht",
        "base imposable",
        # French — "hors taxe" variants
        "montant hors taxe",
        "montant hors taxes",
        "total hors taxe",
        "total hors taxes",
        "sous-total hors taxe",
        "sous total hors taxe",
        # French — abbreviated
        "h.t",
        "h.t.",
        "base h.t",
        "total h.t",
        "montant h.t",
        # English
        "subtotal",
        "sub total",
        "sub-total",
        "net amount",
        "net total",
        "total before tax",
        "taxable amount",
        "amount before tax",
        "total before vat",
    ),
    # ── Montant TVA (VAT amount) ───────────────────────────────────────
    # French: TVA, montant TVA, total TVA
    # English: VAT, VAT amount, sales tax, GST
    # Tunisian: same as French (TVA rates differ but label is same)
    "montant_tva": (
        # French
        "tva",
        "montant tva",
        "total tva",
        "t.v.a",
        "t.v.a.",
        "tva due",
        "montant de la tva",
        "tva collectée",
        "tva collectee",
        "tva facturée",
        "tva facturee",
        "tva sur débits",
        "tva sur debits",
        "tva sur encaissements",
        "base tva",
        # English / International
        "vat",
        "vat amount",
        "total vat",
        "amount vat",
        "vat due",
        "vat to pay",
        "sales tax",
        "tax amount",
        "gst",
        "gst amount",
        "hst",
        "pst",
    ),
    # ── Montant Taxe (other taxes / stamp duty) ────────────────────────
    # French: taxe, timbre fiscal, contribution
    # Tunisian: timbre fiscal (stamp duty is very common)
    # English: tax, stamp duty, excise
    "montant_taxe": (
        # French
        "taxe",
        "montant taxe",
        "total taxe",
        # Tunisian — timbre fiscal (stamp duty on invoices)
        "timbre",
        "timbre fiscal",
        "timbre fiscale",
        "droit d'enregistrement",
        "droit d'enregistrement",
        "droit de timbre",
        # French — other specific taxes
        "contribution",
        "taxe spécifique",
        "taxe specifique",
        "taxe d'enregistrement",
        "taxe d'enregistrement",
        "taxe de séjour",
        "taxe de sejour",
        "taxe à l'importation",
        "taxe a l'importation",
        # English
        "tax",
        "other tax",
        "additional tax",
        "stamp duty",
        "excise",
        "duty",
        "customs duty",
        "withholding tax",
    ),
    # ── Montant TTC (total / amount due) ───────────────────────────────
    # French: TTC, net à payer, total général, à payer, montant dû
    # English: total, grand total, amount due, payable
    # Tunisian: same as French
    "montant_ttc": (
        # French — "TTC" variants
        "ttc",
        "montant ttc",
        "total ttc",
        "t.t.c",
        "t.t.c.",
        "total t.t.c",
        # French — "net à payer" variants
        "net à payer",
        "net a payer",
        "net à payer ttc",
        "net a payer ttc",
        "nette à payer",
        "nette a payer",
        # French — "total" variants
        "total général",
        "total general",
        "total facture",
        "total à payer",
        "total a payer",
        "montant total",
        "montant facture",
        # French — "à payer" / due variants
        "à payer",
        "a payer",
        "montant dû",
        "montant du",
        "montant à payer",
        "montant a payer",
        "solde à payer",
        "solde a payer",
        "solde dû",
        "solde du",
        "reste à payer",
        "reste a payer",
        # French — "règlement"
        "règlement",
        "reglement",
        # English
        "total",
        "total amount",
        "grand total",
        "total invoice",
        "invoice total",
        "amount due",
        "total due",
        "net amount due",
        "amount payable",
        "payable",
        "balance due",
        "outstanding",
        "total to pay",
    ),
}

NUMERIC_FIELDS = {"montant_ht", "montant_tva", "montant_taxe", "montant_ttc"}
TEXT_FIELDS = {"numero_facture", "fournisseur", "client"}


@dataclass(frozen=True)
class FieldSelection:
    """Track the best candidate selected for a field."""

    value: Optional[str]
    confidence: float
    score: float
    ocr_line: Optional[OCRLine] = None


# Minimum per-character confidence for an extracted field value to be accepted.
# Fields whose associated OCR line falls below this threshold will be returned
# as None (blank) rather than showing a garbled or wrong value.
# Priority: "right or blank" over "always fill something in."
FIELD_CONFIDENCE_THRESHOLD = 0.6

# How far below an anchor (in rows) to search for a value candidate
MAX_LOOKAHEAD_ROWS = 4

# Hard pixel-distance cap for value candidates.
# Even if a candidate is within the row-index window (MAX_LOOKAHEAD_ROWS),
# it is rejected if its real pixel distance from the anchor exceeds this
# threshold.  This prevents header anchors from reaching footer content
# on sparsely-detected pages where few rows span a large physical area.
# Empirically: 250px covers ~6-8 lines of text (enough for any legitimate
# label-value pair in the same page region) while rejecting cross-section
# merges (header↔body, body↔footer).
MAX_CANDIDATE_VERTICAL_GAP = 250.0

# Maximum horizontal gap for a same-row-right candidate.
# Prevents merging unrelated text columns when cluster_rows places them
# in the same sub-row (though the 5x height split usually handles this).
MAX_CANDIDATE_HORIZONTAL_GAP = 500.0

# ── Score thresholds ────────────────────────────────────────────────────────

# Minimum acceptable score for a candidate to be accepted.
# Candidates with scores below this threshold are treated the same as having
# no candidate at all (returned as None).  This follows the project's existing
# "blank is better than wrong" principle already applied to OCR confidence.
# Empirically: a reasonable anchor-value pair on the same page scores at
# least ~400 even with moderate distance penalties, so -300 is a generous
# floor that still catches obviously spurious assignments.
MIN_ACCEPTABLE_SCORE = -300.0

# Constants for same-row-right matching
SAME_ROW_H_OVERLAP_THRESHOLD = 0.3  # min horizontal overlap ratio for "same row"


def extract_invoice_fields(
    ocr_lines: Sequence[OCRLine],
    gemini_hint: Optional[str] = None,
) -> dict[str, Optional[str]]:
    """Extract the eight invoice fields from OCR lines.

    If gemini_hint is provided (a numero_facture value from Gemini extraction),
    it is passed to _extract_numero_facture_v2 to boost matching candidates.
    """

    selections = _extract_field_selections(ocr_lines, gemini_hint=gemini_hint)
    return {
        field: (
            selections[field].value
            if selections[field].value is not None
               and selections[field].confidence >= FIELD_CONFIDENCE_THRESHOLD
            else None
        )
        for field in FIELD_ORDER
    }


def extract_field_selections_raw(
    ocr_lines: Sequence[OCRLine],
    gemini_hint: Optional[str] = None,
) -> dict[str, FieldSelection]:
    """Return the full FieldSelection dict (including OCR line refs) for downstream use."""
    return _extract_field_selections(ocr_lines, gemini_hint=gemini_hint)


def extract_field_confidences(
    ocr_lines: Sequence[OCRLine],
    gemini_hint: Optional[str] = None,
) -> dict[str, float]:
    """Return the per-field confidence values selected by the extractor."""

    selections = _extract_field_selections(ocr_lines, gemini_hint=gemini_hint)
    return {field: selections[field].confidence for field in FIELD_ORDER}


def extract_raw_text(ocr_lines: Sequence[OCRLine]) -> str:
    """Return the full OCR text in row/column layout for debugging.

    Lines are grouped into visual rows using cluster_rows(). Within each
    row, items are joined with a tab character so columns align visually
    in the debug panel, matching the invoice layout.
    """

    rows = cluster_rows(ocr_lines)
    if not rows:
        return ""

    lines_out = []
    for row in rows:
        parts = [line.text.strip() for line in row if line.text.strip()]
        if parts:
            lines_out.append("\t".join(parts))

    return "\n".join(lines_out)


# ── Cross-field validation ────────────────────────────────────────────────────


def cross_validate_fields(fields: dict[str, Optional[str]]) -> list[str]:
    """Validate extracted fields for semantic consistency.

    Returns a list of human-readable validation issues (empty = all good).
    Each issue represents a reason to distrust the extraction.
    """
    issues: list[str] = []

    ht = _safe_parse_decimal(fields.get("montant_ht"))
    tva = _safe_parse_decimal(fields.get("montant_tva"))
    taxe = _safe_parse_decimal(fields.get("montant_taxe"))
    ttc = _safe_parse_decimal(fields.get("montant_ttc"))

    # Count available monetary fields
    amt_count = sum(1 for x in [ht, tva, ttc] if x is not None)

    if amt_count >= 2:
        # HT == TVA is almost certainly an error (unless both are 0)
        if ht is not None and tva is not None and ht == tva and ht > Decimal("0"):
            issues.append("HT equals TVA (duplication error)")

        # HT == TTC is almost certainly an error (unless both are 0)
        if ht is not None and ttc is not None and ht == ttc and ht > Decimal("0"):
            issues.append("HT equals TTC (duplication error)")

        # TVA > TTC is impossible
        if tva is not None and ttc is not None and tva > ttc:
            issues.append("TVA exceeds TTC (impossible)")

        # Negative amounts
        if ht is not None and ht < Decimal("0"):
            issues.append("Negative HT amount")
        if tva is not None and tva < Decimal("0"):
            issues.append("Negative TVA amount")
        if ttc is not None and ttc < Decimal("0"):
            issues.append("Negative TTC amount")

        # TVA should be approximately HT × VAT_RATE for reasonable VAT rates
        if ht is not None and ht > Decimal("0") and tva is not None and tva > Decimal("0"):
            vat_rate = tva / ht
            # Accept VAT rates between 5% and 30% (covers most jurisdictions)
            if vat_rate < Decimal("0.05") or vat_rate > Decimal("0.30"):
                issues.append(f"Unlikely VAT rate: {float(vat_rate)*100:.1f}%")

        # Check arithmetic: HT + TVA + Taxe ≈ TTC (within tolerance)
        if ht is not None and tva is not None and ttc is not None:
            eff_taxe = taxe or Decimal("0")
            expected = ht + tva + eff_taxe
            diff = abs(expected - ttc)
            if diff > Decimal("0.50"):
                issues.append(f"Arithmetic mismatch: HT+IVA+Taxe={expected:.3f} ≠ TTC={ttc:.3f}")

    # Missing mandatory fields
    if not fields.get("numero_facture"):
        issues.append("Invoice number missing")
    if not fields.get("date"):
        issues.append("Date missing")
    if not fields.get("montant_ttc"):
        issues.append("TTC amount missing")

    return issues


def _safe_parse_decimal(value: Optional[str]) -> Optional[Decimal]:
    """Parse a decimal safely, returning None on failure."""
    if not value:
        return None
    try:
        return Decimal(value)
    except Exception:
        return None


def compute_confidence(field_scores: Mapping[str, float], fields: Mapping[str, Optional[str]] | None = None, issues: list[str] | None = None) -> float:
    """Compute a confidence score with penalties for extraction quality issues.

    Base score is the average per-field confidence.
    Penalties are applied for:
    - Missing mandatory fields (invoice number, date, TTC)
    - Duplicated monetary values (HT == TVA, HT == TTC)
    - Arithmetic inconsistency
    - Impossible values (TVA > TTC, negative totals)
    - Unlikely VAT rates
    """

    scores = [score for score in field_scores.values() if score > 0.0]
    if not scores:
        return 0.0
    base = sum(scores) / len(scores)

    penalties = 0.0

    # Penalize based on validation issues
    if issues:
        for issue in issues:
            if "duplication" in issue:
                penalties += 0.25
            elif "impossible" in issue or "Negative" in issue:
                penalties += 0.20
            elif "Arithmetic mismatch" in issue:
                penalties += 0.15
            elif "VAT rate" in issue:
                penalties += 0.10
            elif "missing" in issue:
                penalties += 0.10

    # Penalize missing monetary fields even if not flagged (reduce confidence for
    # incomplete extractions)
    if fields is not None:
        missing_amounts = sum(1 for k in ["montant_ht", "montant_tva", "montant_ttc"] if not fields.get(k))
        if missing_amounts >= 3:
            penalties += 0.20
        elif missing_amounts >= 2:
            penalties += 0.10

    result = base - penalties
    return max(0.0, min(1.0, result))


# ── Row utilities ───────────────────────────────────────────────────────────


def _row_index_of(ocr_line: OCRLine, rows: list[list[OCRLine]]) -> int:
    """Return the row index containing the given OCR line, or -1."""
    for i, row in enumerate(rows):
        for line in row:
            if line is ocr_line:
                return i
    return -1


def _lines_below(anchor_row_idx: int, rows: list[list[OCRLine]]) -> list[OCRLine]:
    """Return all OCR lines from rows below the anchor row (flattened)."""
    all_below: list[OCRLine] = []
    for i in range(anchor_row_idx + 1, min(anchor_row_idx + 1 + MAX_LOOKAHEAD_ROWS, len(rows))):
        all_below.extend(rows[i])
    return all_below


def _lines_above(anchor_row_idx: int, rows: list[list[OCRLine]]) -> list[OCRLine]:
    """Return all OCR lines from rows above the anchor row (flattened), used as
    a fallback when the primary below-row search finds nothing.

    Only looks a limited distance above (MAX_LOOKAHEAD_ROWS rows) to avoid
    grabbing content from a completely different section of the page.

    NOTE: rows[i] is already sorted left-to-right (by x1) from cluster_rows.
    We do NOT reverse the items because the nearby-above row should come first
    in iteration order AND its items should be in left-to-right reading order.
    """
    all_above: list[OCRLine] = []
    start = max(0, anchor_row_idx - MAX_LOOKAHEAD_ROWS)
    for i in range(anchor_row_idx - 1, start - 1, -1):
        all_above.extend(rows[i])  # already in left-to-right order
    return all_above


def _is_same_row(anchor: OCRLine, candidate: OCRLine) -> bool:
    """Return True if candidate is in the same visual row as anchor."""
    overlap = anchor.box.vertical_overlap(candidate.box)
    shorter = min(anchor.box.height, candidate.box.height)
    if shorter <= 0:
        return False
    return overlap / shorter > 0.5


# ── Field selection with collision tracking ──────────────────────────────────


def _extract_field_selections(
    ocr_lines: Sequence[OCRLine],
    gemini_hint: Optional[str] = None,
) -> dict[str, FieldSelection]:
    """Resolve the best candidate for each field from the OCR lines, preventing
    two fields from claiming the same OCR line unless the second's score is
    meaningfully higher.

    If gemini_hint is provided, it is passed to _extract_numero_facture_v2
    to boost matching candidates using the Gemini-extracted invoice number.
    """

    rows = cluster_rows(ocr_lines)
    all_lines = sorted(ocr_lines, key=lambda item: (item.page_index, item.line_index, item.box.y1, item.box.x1))

    # Step 1: Each field proposes its best candidate (collect all proposals)
    # Proposal = (field, OCRLine, score, value, confidence)
    proposals: list[tuple[str, OCRLine, float, Optional[str], float]] = []

    for field in FIELD_ORDER:
        if field == "numero_facture":
            # Use v2 hybrid extraction (pattern + position + date proximity + Gemini hint)
            selection = _extract_numero_facture_v2(rows, all_lines, gemini_hint=gemini_hint)
        else:
            selection = _select_best_selection_for_field(field, rows, all_lines, FieldSelection(value=None, confidence=0.0, score=float("-inf"), ocr_line=None))
        if selection.ocr_line is not None and selection.score > float("-inf"):
            proposals.append((field, selection.ocr_line, selection.score, selection.value, selection.confidence))
            _debug_log(
                f"FIELD: {field}\n"
                f"  Candidates: [...]\n"
                f"  Selected: {selection.value}\n"
                f"  from line: {selection.ocr_line.text[:80]!r}\n"
                f"  confidence: {selection.confidence:.3f}  score: {selection.score:.1f}"
            )
        else:
            _debug_log(f"FIELD: {field} — NO CANDIDATE FOUND")

    # Step 2: Resolve conflicts — each OCR line goes to the highest-scoring field
    proposals.sort(key=lambda p: p[2], reverse=True)

    claimed_lines: set[int] = set()
    resolved: dict[str, FieldSelection] = {field: FieldSelection(value=None, confidence=0.0, score=float("-inf")) for field in FIELD_ORDER}

    _debug_log("--- CONFLICT RESOLUTION ---")
    for field, ocr_line, score, value, confidence in proposals:
        line_id = id(ocr_line)
        if line_id in claimed_lines:
            _debug_log(f"  {field}: line already claimed by higher-scoring field — REJECTED")
            continue
        claimed_lines.add(line_id)
        resolved[field] = FieldSelection(value=value, confidence=confidence, score=score, ocr_line=ocr_line)
        _debug_log(f"  {field}: ASSIGNED (value={value}, score={score:.1f})")

    # Step 3: Fields that still don't have a candidate retry with claimed lines excluded
    for field in FIELD_ORDER:
        if resolved[field].score > float("-inf"):
            continue
        _debug_log(f"  {field}: retrying with claimed lines excluded...")
        resolved[field] = _select_best_selection_for_field(
            field, rows, all_lines, resolved[field], excluded_ids=claimed_lines,
        )
        if resolved[field].score > float("-inf"):
            _debug_log(f"  {field}: fallback ASSIGNED (value={resolved[field].value}, score={resolved[field].score:.1f})")
        else:
            _debug_log(f"  {field}: fallback — STILL NO CANDIDATE")

    return resolved


# ── Per-field candidate search ───────────────────────────────────────────────


def _select_best_selection_for_field(field: str, rows: list[list[OCRLine]], all_lines: Sequence[OCRLine], current_selection: FieldSelection, excluded_ids: set[int] | None = None) -> FieldSelection:
    """Evaluate all anchors for a field using geometric search and return the best selection.

    If excluded_ids is provided, lines whose ids are in the set will be skipped
    (used for collision prevention in the fallback pass).
    """

    aliases = FIELD_ALIASES[field]
    anchors = [line for line in all_lines if _contains_any_alias(line.text, aliases)]
    selection = current_selection

    for anchor in anchors:
        if excluded_ids and id(anchor) in excluded_ids:
            continue
        same_line_selection = _selection_from_same_line(field, anchor, selection, excluded_ids)
        if same_line_selection is not None:
            selection = same_line_selection

        geometric_selection = _selection_from_geometric_search(field, anchor, rows, selection, excluded_ids)
        if geometric_selection is not None:
            selection = geometric_selection

    return selection


def _selection_from_same_line(field: str, anchor: OCRLine, current_selection: FieldSelection, excluded_ids: set[int] | None = None) -> FieldSelection | None:
    """Create a selection when the value is embedded in the same OCR line as the label.
    
    If excluded_ids is provided, the anchor itself is skipped if its id is in the set.
    """

    if excluded_ids and id(anchor) in excluded_ids:
        return None

    same_line_value = _extract_inline_value(field, anchor.text)
    if same_line_value is None:
        return None

    score = _score_candidate(anchor, anchor, same_line=True)
    selection = FieldSelection(value=same_line_value, confidence=anchor.confidence, score=score, ocr_line=anchor)
    if selection.score <= current_selection.score:
        return None
    return selection


def _selection_from_geometric_search(field: str, anchor: OCRLine, rows: list[list[OCRLine]], current_selection: FieldSelection, excluded_ids: set[int] | None = None) -> FieldSelection | None:
    """Find the best candidate using purely geometric criteria.

    Considers:
    (a) Lines on the same row as the anchor, to the right
    (b) Lines in rows below the anchor, within horizontal range

    If excluded_ids is set, lines whose ids are in the set are skipped.
    """

    anchor_row_idx = _row_index_of(anchor, rows)
    if anchor_row_idx < 0:
        return None

    candidates: list[tuple[OCRLine, float]] = []

    # (a) Same-row-right candidates
    anchor_row = rows[anchor_row_idx]
    for line in anchor_row:
        if line is anchor:
            continue
        if excluded_ids and id(line) in excluded_ids:
            continue
        if line.box.x1 < anchor.box.x2:
            continue
        # Hard cap: reject candidates too far to the right (unrelated column)
        h_gap = line.box.x1 - anchor.box.x2
        if h_gap > MAX_CANDIDATE_HORIZONTAL_GAP:
            continue
        # Use field-specific plausibility check for numero_facture
        if field == "numero_facture":
            if not _candidate_is_plausible_numero_facture(line):
                continue
        else:
            if not _candidate_is_plausible(line):
                continue
        score = _score_same_row_right(anchor, line)
        candidates.append((line, score))

    # (b) Below-row candidates
    for line in _lines_below(anchor_row_idx, rows):
        if excluded_ids and id(line) in excluded_ids:
            continue
        # Hard cap: reject candidates more than MAX_CANDIDATE_VERTICAL_GAP
        # pixels below the anchor.  Prevents header anchors from reaching
        # footer content on pages with sparse text detection.
        v_gap = anchor.box.vertical_gap(line.box)
        if v_gap > MAX_CANDIDATE_VERTICAL_GAP:
            continue
        # Use field-specific plausibility check for numero_facture
        if field == "numero_facture":
            if not _candidate_is_plausible_numero_facture(line):
                continue
        else:
            if not _candidate_is_plausible(line):
                continue
        score = _score_below_row(anchor, line)
        candidates.append((line, score))

    # (c) If nothing found below, try ABOVE the anchor as a fallback.
    # This can happen when cluster_rows sub-row ordering places a right-
    # aligned value sub-row BEFORE its left-aligned label (though the
    # parent-row-based sort should prevent this in most cases).
    if not candidates:
        for line in _lines_above(anchor_row_idx, rows):
            if excluded_ids and id(line) in excluded_ids:
                continue
            v_gap = line.box.vertical_gap(anchor.box)
            if v_gap > MAX_CANDIDATE_VERTICAL_GAP:
                continue
            if field == "numero_facture":
                if not _candidate_is_plausible_numero_facture(line):
                    continue
            else:
                if not _candidate_is_plausible(line):
                    continue
            score = _score_below_row(anchor, line)
            candidates.append((line, score))

    if not candidates:
        return None

    candidates.sort(key=lambda c: c[1], reverse=True)

    # Iterate through candidates in score order and accept the first one
    # whose value cleans successfully.  This handles the case where the
    # highest-scoring candidate happens to be a value that fails
    # _clean_candidate_value (e.g., "20%" fails extract_amount because
    # percentage striping leaves an empty string) while a slightly
    # lower-scoring candidate produces a valid cleaned value.
    for candidate_line, candidate_score in candidates:
        # ── Minimum score floor ────────────────────────────────────────────
        # Reject candidates with absurdly low scores (deeply negative).
        if candidate_score < MIN_ACCEPTABLE_SCORE:
            _debug_log(
                f"  {field}: candidate {candidate_line.text!r} score {candidate_score:.1f}"
                f" < MIN_ACCEPTABLE_SCORE ({MIN_ACCEPTABLE_SCORE}) — REJECTED"
            )
            continue

        candidate_value = _clean_candidate_value(field, candidate_line.text)
        if candidate_value is None:
            _debug_log(
                f"  {field}: candidate {candidate_line.text!r} score {candidate_score:.1f}"
                f" — failed _clean_candidate_value, skipping"
            )
            continue

        selection = FieldSelection(value=candidate_value, confidence=candidate_line.confidence,
                                    score=candidate_score, ocr_line=candidate_line)
        if selection.score <= current_selection.score:
            continue
        return selection

    _debug_log(f"  {field}: no candidate passed cleaning — returning None")
    return None


def _contains_any_alias(text: str, aliases: Sequence[str]) -> bool:
    """Return True when the normalized text contains any alias.

    Uses word-boundary matching (\\b) to prevent false positives from short
    aliases — e.g. "no" matching inside "nom", or "ht" matching inside "echt".

    Non-ASCII aliases (e.g. the Arabic column headers like "البيان") are
    handled separately: normalize_text() ASCII-folds them to an empty string,
    which would otherwise silently skip every such alias.  They are matched
    as whole words against the raw text instead.
    """

    normalized = normalize_text(text)
    for alias in aliases:
        alias_normalized = normalize_text(alias)
        if not alias_normalized:
            # Fully non-ASCII alias — normalize_text() strips it to empty,
            # so match the alias as a whole word against the original text.
            if alias and re.search(r"(?<!\w)" + re.escape(alias) + r"(?!\w)", text):
                return True
            continue
        # Word-boundary anchored pattern prevents substring false matches
        pattern = re.compile(r"\b" + re.escape(alias_normalized) + r"\b")
        if pattern.search(normalized):
            return True
    return False


# ── Relaxed alias matching (subsequence) ────────────────────────────────────

# No stopword list is needed — _matches_alias_relaxed uses pure subsequence
# matching so ANY inserted words (not just stopwords) between required alias
# tokens are tolerated.  This is safe because _NUMERO_FACTURE_ANCHORS all
# require at least 2 core tokens to match in order, making false positives
# very unlikely on real invoice text.


def _matches_alias_relaxed(text: str, aliases: Sequence[str]) -> bool:
    """Return True when the text matches an alias allowing inserted words.

    For each alias, checks whether all alias tokens appear in the text in
    order as a subsequence.  Any extra tokens in the text are simply skipped
    — they are not required to be stopwords.  This handles variants like
    "n° de la facture d'origine" matching the alias "n° de facture".

    Only used for _NUMERO_FACTURE_ANCHORS — regular field aliases use the
    strict word-boundary _contains_any_alias to avoid false positives.
    """

    normalized = normalize_text(text)
    text_tokens = normalized.split()

    for alias in aliases:
        alias_norm = normalize_text(alias)
        alias_tokens = alias_norm.split()

        # Check if alias tokens appear as a subsequence of text tokens
        alias_idx = 0
        for token in text_tokens:
            if alias_idx < len(alias_tokens) and token == alias_tokens[alias_idx]:
                alias_idx += 1
                if alias_idx == len(alias_tokens):
                    return True

    return False


def _extract_inline_value(field: str, text: str) -> Optional[str]:
    """Extract a value that appears on the same line as the label."""

    candidate = collapse_text(text)
    if ":" in candidate:
        remainder = candidate.split(":", 1)[1].strip()
        cleaned = _clean_candidate_value(field, remainder)
        if cleaned is not None:
            return cleaned

    normalized = normalize_text(candidate)
    for alias in sorted(FIELD_ALIASES[field], key=len, reverse=True):
        alias_normalized = normalize_text(alias)
        if alias_normalized not in normalized:
            continue
        suffix = normalized.split(alias_normalized, 1)[1].strip(" :-\\t\\r\\n")
        if not suffix:
            continue
        cleaned = _clean_candidate_value(field, suffix)
        if cleaned is not None:
            return cleaned

    return None


def _candidate_is_plausible(candidate: OCRLine) -> bool:
    """Return True when the candidate could represent an invoice value."""

    normalized = normalize_text(candidate.text)
    if not normalized:
        return False
    if len(normalized) < 2:
        return False
    return not _looks_like_label(normalized)


def _candidate_is_plausible_numero_facture(candidate: OCRLine) -> bool:
    """Field-specific plausibility check for invoice numbers.
    
    Rejects candidates that:
    - Don't contain at least one digit (invoice numbers always have digits)
    - Match an obvious address pattern (number + street type, or multiple commas)
    - Look like a label
    """
    normalized = normalize_text(candidate.text)
    if not normalized:
        return False
    if len(normalized) < 2:
        return False
    
    # Must contain at least one digit
    if not any(c.isdigit() for c in normalized):
        return False
    
    # Reject if it looks like a label
    if _looks_like_label(normalized):
        return False
    
    # Reject obvious address patterns:
    # - Starts with digit followed by street-type word (e.g. "123 rue", "45 avenue")
    # - Contains multiple commas (suggests full address line)
    if re.match(r"^\d+\s+(rue|avenue|boulevard|place|chemin|allée|allee|cour|impasse|quai|square|passage|cours|voie|route|chemin|montée|montee|cote|côte|ruelle|impasse)", normalized):
        return False
    
    if normalized.count(",") >= 2:
        return False
    
    return True


def _clean_candidate_value(field: str, value: str) -> Optional[str]:
    """Normalize a candidate value for the requested field type."""

    cleaned = normalize_text_for_output(value)
    if not cleaned:
        return None

    if field in NUMERIC_FIELDS:
        return clean_amount(cleaned)

    if field == "date":
        return clean_date(cleaned)

    if field in TEXT_FIELDS:
        if not looks_like_latin_text(cleaned):
            return None
        if _looks_like_label(cleaned):
            return None
        return cleaned

    return cleaned


def _looks_like_label(text: str) -> bool:
    """Return True when the text still looks like a label rather than a value.

    Uses regex patterns with word boundaries (\\b) for short tokens to avoid
    false positives where e.g. "ht" matches inside "echt", or "no" matches
    inside "nouvelle".
    Example: "Société Nouvelle SARL" must NOT match "no" (inside "nouvelle").
    "REF-2024-001" must NOT match "ref" (it's an invoice number, not a label).
    """

    normalized = normalize_text(text)
    label_patterns = [
        # Invoice metadata (long words — substring match is safe)
        "facture", "fournisseur", "vendeur", "emetteur",
        "acheteur", "destinataire",
        # Monetary field labels (long — substring match is safe)
        "montant", "netapayer",
        # References (long — substring match is safe)
        "reference", "numero",
        # Product/item descriptions
        "designation", "description", "produit", "article",
        # Address field labels (compound only — street-type words like "rue",
        # "avenue", "boulevard" are NOT included because they appear inside
        # legitimate address values like "70 avenue de Clichy".  Only full
        # label phrases like "adresse", "code postal", and "pays" are kept.)
        # NOTE: "ville" is also excluded — city names like "Mairie de
        # Villefranche" contain "ville" as a substring, causing false-positive
        # label rejection for valid client names.
        "adresse", "code postal", "codepostal", "pays",
        # Additional common invoice labels
        "quantite", "quantité", "prix", "unite", "unité",
        "remise", "escompte", "livraison", "port",
        # Short tokens — use word boundaries to avoid substring false matches
        r"\bdate\b",
        r"\bclient\b",
        r"\bht\b",
        r"\btva\b",
        r"\btaxe\b",
        r"\bttc\b",
        r"\btotal\b",
        r"\bno\b[.\s]*$",
        r"\bref\b[.\s]*$",
    ]
    return any(re.search(pattern, normalized) for pattern in label_patterns)


def _looks_like_date(text: str) -> bool:
    """Return True when the text looks like a date format (e.g. DD/MM/YYYY).

    Checks for common date patterns:
    - DD/MM/YYYY or DD-MM-YYYY
    - YYYY/MM/DD
    - DD Month YYYY

    Strips trailing punctuation (. , ; :) before matching to handle real
    OCR output that often appends stray dots or commas.

    Used to heavily penalize date-shaped values for fields that should not
    contain dates (e.g., numero_facture).
    """
    normalized = normalize_text(text)
    # Strip trailing punctuation that OCR often appends
    normalized = normalized.strip(".,;:")
    # DD/MM/YYYY or DD-MM-YYYY
    if re.match(r"^\d{1,2}[/-]\d{1,2}[/-]\d{2,4}$", normalized):
        return True
    # YYYY/MM/DD or YYYY-MM-DD
    if re.match(r"^\d{4}[/-]\d{1,2}[/-]\d{1,2}$", normalized):
        return True
    # DD Month YYYY (French or English month names)
    month_pattern = r"(janvier|février|fevrier|mars|avril|mai|juin|juillet|aout|août|septembre|octobre|novembre|decembre|décembre|january|february|march|april|june|july|august|september|october|november|december)"
    if re.match(rf"^\d{{1,2}}\s+{month_pattern}\s+\d{{4}}$", normalized, re.IGNORECASE):
        return True
    return False


# ── Specialized extraction: numero_facture ──────────────────────────────────

# Strict anchor patterns that explicitly pair invoice-number identifiers
# with "facture"/"invoice" to avoid false positives.
# Strict anchor patterns for numero_facture — kept in sync with FIELD_ALIASES["numero_facture"].
# These require compound phrases (two+ tokens) to avoid false positives from bare "n°" or "no".
# Relaxed subsequence matching (_matches_alias_relaxed) is used as Phase 2 fallback.
# NOTE: Short truncated forms like "n° fact" and "réf fact" are intentionally excluded
# because _extract_inline_value would extract "ure" (the remainder of "Facture") as garbage.
_NUMERO_FACTURE_ANCHORS = (
    # French — "n°" variants (full forms only)
    "n° facture", "n facture", "n°facture", "n° de facture", "n de facture",
    # French — "numéro" variants
    "numéro de facture", "numero de facture", "numéro facture", "numero facture",
    # French — "facture n°" variants
    "facture n°", "facture no", "facture nº", "nº facture", "no facture",
    # French — "réf" variants (full forms only)
    "réf facture", "ref facture", "référence facture", "reference facture",
    # English
    "invoice number", "invoice n°", "invoice no", "invoice #", "invoice id",
    "invoice ref", "inv no",
    # Order / PO number (often same line as invoice number)
    "n° commande", "numero commande", "numéro commande",
    "order number", "purchase order", "po number",
)


# Labels that, when found on the same visual row as a numero_facture candidate,
# indicate the digits belong to a different field (phone, tax ID, bank account)
# and must never be picked as the invoice number — even though they can match
# the same bare-digit-string regex patterns below (e.g. an 8-digit Tunisian
# phone number matches pattern 3 just as well as a real invoice number).
_NUMERO_DISQUALIFIER_ALIASES = (
    "matricule fiscal", "m.f.", "mf n°", "siret", "siren",
    "rc n°", "registre de commerce", "tva intracommunautaire", "tva intracom",
    "tél", "tel", "téléphone", "telephone", "gsm", "fax", "mobile", "portable",
    "iban", "rib", "swift", "bic",
)


def _row_has_disqualifying_context(line: OCRLine, rows: list[list[OCRLine]]) -> bool:
    """Return True when the candidate's row also carries a non-invoice label.

    A bare digit string can accidentally match the numero_facture regex
    patterns while actually being a phone number, tax ID, or bank reference.
    If another OCR line on the same visual row contains one of the disqualifying
    labels above, the candidate is rejected regardless of its pattern score.
    """
    row_idx = _row_index_of(line, rows)
    if row_idx < 0:
        return False
    row_text = " ".join(l.text for l in rows[row_idx])
    return _contains_any_alias(row_text, _NUMERO_DISQUALIFIER_ALIASES)


# ── v5: Hybrid invoice-number extraction (pattern + position + date proximity) ──

# Regex patterns for detecting invoice numbers without explicit labels.
# Each pattern contributes a different base score depending on confidence.
_NUMERO_PATTERNS: list[tuple[str, int]] = [
    # 0: Letter prefix + separator + digits (FAC-2025-001, INV-001, REF-ABC-123)
    # Handles both simple "INV-001" and year-prefix "FAC-2025-001" / "INV-2024-001"
    (r'^[A-Za-z]{2,5}[-.\s]+(?:\d{4}[-.\s]\d{2,8}|\d{3,8})$', 150),
    # 1: Letter prefix stuck to digits (F2025001, FACT001)
    (r'^[A-Za-z]{1,4}\d{4,10}$', 120),
    # 2: Year + separator + sequence (2025/001, 2025-00042)
    (r'^\d{4}[-/.\\]+\d{2,6}$', 140),
    # 3: Long pure digit (8-12 chars — typical e-invoice)
    (r'^\d{8,12}$', 100),
    # 4: Medium pure digit (5-7 chars)
    (r'^\d{5,7}$', 60),
    # 5: N°/No/Num/Ref prefix — capture group 1 is the number
    (r'(?:^|\s)(?:N[°oº]|No|Num\.?|R\xe9f\.?|Ref\.?)[\s:.]*([A-Za-z0-9/-]{3,20})(?:\s|$)', 90),
    # 6: Facture/Invoice/FAC/INV prefix — value with label inline (e.g. "Facture: 2025-001")
    (r'(?:Facture|Invoice)[\s#:]*([A-Za-z0-9/-]{3,20})', 80),
]


def _looks_like_amount(text: str) -> bool:
    """Return True when the text looks like a monetary amount.

    Accepts both 2-decimal (1.234,56 / 1234.56 — EUR/USD-style) and
    3-decimal (850.000 — TND/dinar millimes) formats. Tunisian invoices
    quote amounts to 3 decimals as standard, so a 2-decimal-only check
    silently fails to recognize the majority of amounts on local invoices.
    """
    if re.match(r'^\d{1,3}(?:[ .]\d{3})*[,.]\d{2,3}$', text):
        return True
    return False


def _looks_like_address(text: str) -> bool:
    """Return True when the text looks like a postal address."""
    normalized = normalize_text(text)
    # Contains street type or address keywords
    address_keywords = ["rue", "avenue", "boulevard", "bp", "code postal",
                        "codepostal", "tunis", "france", "paris", "ville"]
    for kw in address_keywords:
        if kw in normalized.lower():
            return True
    # Contains 2+ commas (strong address signal)
    if normalized.count(",") >= 2:
        return True
    # Starts with digit + street type
    if re.match(r"^\d+\s+(rue|avenue|boulevard|place|chemin|allee|cour|impasse|quai|route)", normalized):
        return True
    return False


def _find_numero_candidates_by_pattern(ocr_lines: Sequence[OCRLine]) -> list[tuple[OCRLine, float, str]]:
    """Strategy A: Find invoice-number candidates via regex pattern matching.

    Tests each OCR line against NUMERO_PATTERNS and returns (line, score, value)
    tuples for every match.  Handles grouped vs un-grouped regexes.
    """
    candidates: list[tuple[OCRLine, float, str]] = []
    for line in ocr_lines:
        text = line.text.strip()
        if not text:
            continue
        for pattern_str, base_score in _NUMERO_PATTERNS:
            m = re.search(pattern_str, text)
            if m:
                # Use group(1) if captured, else full match
                try:
                    raw_val = m.group(1) if m.group(1) else m.group(0)
                except IndexError:
                    raw_val = m.group(0)
                # Normalize
                cleaned = normalize_text_for_output(raw_val)
                if cleaned:
                    candidates.append((line, float(base_score), cleaned))
    return candidates


def _score_by_position(line: OCRLine, page_height: float, page_width: float) -> float:
    """Strategy B: Score based on page position.

    Invoice numbers are statistically in the top third of the page.
    Returns bonus score based on relative position.
    """
    if page_height <= 0:
        return 0.0
    rel_y = line.box.y1 / page_height
    rel_x = line.box.x1 / page_width if page_width > 0 else 0.5

    if rel_x > 0.6 and rel_y < 0.25:
        return 80.0  # Top-right corner
    if rel_x < 0.4 and rel_y < 0.25:
        return 60.0  # Top-left corner
    if 0.4 <= rel_x <= 0.6 and rel_y < 0.20:
        return 40.0  # Top-center
    if rel_y < 0.35:
        return 20.0  # Upper third, not in a specific corner
    return 0.0


def _score_by_date_proximity(line: OCRLine, rows: list[list[OCRLine]], all_lines: Sequence[OCRLine]) -> float:
    """Strategy C: Score based on proximity to a date field.

    Invoice numbers are often near the date.  Returns bonus score based on
    pixel distance to the nearest date-like line.
    """
    # Find all date-like lines
    date_lines = [
        l for l in all_lines
        if _looks_like_date(l.text) or re.search(r'\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b', l.text)
    ]
    if not date_lines:
        return 0.0

    # Compute minimum Euclidean distance to any date line
    min_dist = float("inf")
    line_center = (line.box.center_x, line.box.center_y)
    for dl in date_lines:
        dx = line_center[0] - dl.box.center_x
        dy = line_center[1] - dl.box.center_y
        dist = (dx * dx + dy * dy) ** 0.5
        if dist < min_dist:
            min_dist = dist

    if min_dist < 100:
        bonus = 50.0
    elif min_dist < 200:
        bonus = 30.0
    elif min_dist < 400:
        bonus = 10.0
    else:
        bonus = 0.0

    # Additional bonus if on the same visual row
    line_row_idx = _row_index_of(line, rows)
    if line_row_idx >= 0:
        for dl in date_lines:
            dl_row_idx = _row_index_of(dl, rows)
            if dl_row_idx >= 0 and dl_row_idx == line_row_idx:
                bonus += 40.0
                break

    return bonus


def _compute_page_extents(ocr_lines: Sequence[OCRLine]) -> tuple[float, float]:
    """Compute page height and width from OCR line bounding boxes."""
    max_x = max_y = 0.0
    for line in ocr_lines:
        if line.box.x2 > max_x:
            max_x = line.box.x2
        if line.box.y2 > max_y:
            max_y = line.box.y2
    return max_y, max_x


def _extract_numero_facture_v2(
    rows: list[list[OCRLine]],
    all_lines: Sequence[OCRLine],
    gemini_hint: Optional[str] = None,
) -> FieldSelection:
    """Hybrid invoice-number extraction with 4-strategy cascade (v5).

    Strategies:
      A — Regex pattern matching
      B — Page position (top-right/left)
      C — Date proximity
      D — Gemini hint similarity

    Each candidate line is scored with all applicable strategies.
    Penalties suppress false positives (dates, amounts, addresses).
    Returns the best candidate with total_score > MIN_NUMERO_SCORE, or None.
    """
    page_height, page_width = _compute_page_extents(all_lines)
    candidate_map: dict[int, tuple[OCRLine, float, str]] = {}  # id -> (line, score, value)

    # Strategy A: Pattern matching
    for line, pattern_score, val in _find_numero_candidates_by_pattern(all_lines):
        lid = id(line)
        # Use normalize_text_for_output for consistency with _clean_candidate_value
        clean_val = normalize_text_for_output(val)
        if clean_val:
            total = pattern_score
            if lid in candidate_map:
                cur_score = candidate_map[lid][1]
                if total > cur_score:
                    candidate_map[lid] = (line, total, clean_val)
            else:
                candidate_map[lid] = (line, total, clean_val)

    # Strategy E: Anchor-based extraction — strict then relaxed subsequence matching
    _default_sel = FieldSelection(value=None, confidence=0.0, score=float("-inf"))
    for anchor in all_lines:
        # Phase 1: strict word-boundary matching
        is_anchor = _contains_any_alias(anchor.text, _NUMERO_FACTURE_ANCHORS)
        # Phase 2: relaxed subsequence matching for inserted stopwords (e.g.
        # "n° de la facture d'origine" matching "n° de facture")
        if not is_anchor:
            is_anchor = _matches_alias_relaxed(anchor.text, _NUMERO_FACTURE_ANCHORS)

        if is_anchor:
            # Try same-line extraction first (e.g. "N° Facture: INV-2024-001")
            val = _extract_inline_value("numero_facture", anchor.text)
            if val:
                clean_val = normalize_text_for_output(val)
                if clean_val:
                    lid = id(anchor)
                    if lid not in candidate_map:
                        candidate_map[lid] = (anchor, 80.0, clean_val)
                    else:
                        old_score = candidate_map[lid][1]
                        if 80.0 > old_score:
                            candidate_map[lid] = (anchor, 80.0, clean_val)
            # Try geometric search for value near anchor
            geom_sel = _selection_from_geometric_search("numero_facture", anchor, rows, _default_sel)
            if geom_sel is not None and geom_sel.value and geom_sel.score > float("-inf"):
                clean_val = normalize_text_for_output(geom_sel.value)
                if clean_val and geom_sel.ocr_line:
                    glid = id(geom_sel.ocr_line)
                    threshold = 70.0
                    if glid in candidate_map:
                        old_val = candidate_map[glid][2]
                        if len(clean_val) > len(old_val):
                            threshold += 20.0
                    if glid not in candidate_map or threshold > candidate_map[glid][1]:
                        candidate_map[glid] = (geom_sel.ocr_line, threshold, clean_val)

    if not candidate_map:
        _debug_log("_extract_numero_facture_v2: no candidates from any strategy")
        return FieldSelection(value=None, confidence=0.0, score=float("-inf"))

    # Score each candidate: position + date proximity + gemini hint
    scored: list[tuple[OCRLine, float, str]] = []
    for lid, (line, pattern_score, val) in candidate_map.items():
        total_score = pattern_score

        # Strategy B: Position scoring
        total_score += _score_by_position(line, page_height, page_width)

        # Strategy C: Date proximity scoring
        total_score += _score_by_date_proximity(line, rows, all_lines)

        # Strategy D: Gemini hint similarity
        if gemini_hint and val:
            # Simple character-overlap similarity
            val_upper = val.upper()
            hint_upper = gemini_hint.upper()
            common = sum(1 for c in val_upper if c in hint_upper)
            max_len = max(len(val_upper), len(hint_upper))
            similarity = common / max_len if max_len > 0 else 0.0
            if similarity > 0.7:
                total_score += 100.0
            elif similarity > 0.5:
                total_score += 50.0

        # ── Penalties ────────────────────────────────────────────────────
        if _looks_like_date(val):
            total_score -= 300.0
        if _looks_like_amount(val):
            total_score -= 250.0
        if _looks_like_address(val):
            total_score -= 200.0
        if _looks_like_label(val):
            total_score -= 400.0  # Label words like "Facture" should NEVER be invoice numbers
        if len(val) < 3:
            total_score -= 150.0
        if len(val) > 25:
            total_score -= 50.0
        if _row_has_disqualifying_context(line, rows):
            total_score -= 350.0

        scored.append((line, total_score, val))

    # Pick the best candidate
    scored.sort(key=lambda x: x[1], reverse=True)
    best_line, best_score, best_val = scored[0]

    MIN_NUMERO_SCORE = 50.0
    if best_score < MIN_NUMERO_SCORE:
        _debug_log(f"_extract_numero_facture_v2: best score {best_score:.1f} < MIN ({MIN_NUMERO_SCORE}), rejecting")
        return FieldSelection(value=None, confidence=0.0, score=float("-inf"))

    _debug_log(f"_extract_numero_facture_v2: selected {best_val!r} score={best_score:.1f} from {len(scored)} candidates")
    # Use the OCR line's actual confidence when available, falling back to
    # score-derived confidence.  The derive-from-score approach produces values
    # well below FIELD_CONFIDENCE_THRESHOLD (0.6) for reasonable scores like 80,
    # causing the field to be silently dropped in extract_invoice_fields.
    line_conf = best_line.confidence if best_line else 0.0
    confidence = max(line_conf, min(1.0, best_score / 500.0))
    return FieldSelection(value=best_val, confidence=confidence, score=best_score, ocr_line=best_line)


# ── Scoring functions ────────────────────────────────────────────────────────


def _score_candidate(anchor: OCRLine, candidate: OCRLine, same_line: bool) -> float:
    """Score a candidate using bounding-box geometry as a tiebreaker."""

    if same_line:
        horizontal_gap = max(0.0, candidate.box.x1 - anchor.box.x2)
        vertical_offset = abs(candidate.box.center_y - anchor.box.center_y)
        return 1000.0 - horizontal_gap * 10.0 - vertical_offset * 5.0 + candidate.confidence * 100.0

    vertical_gap = anchor.box.vertical_gap(candidate.box)
    horizontal_gap = abs(candidate.box.center_x - anchor.box.center_x)
    return 800.0 - vertical_gap * 10.0 - horizontal_gap * 2.0 + candidate.confidence * 100.0


def _score_same_row_right(anchor: OCRLine, candidate: OCRLine) -> float:
    """Score a candidate that is on the same visual row and to the right."""
    horizontal_gap = max(0.0, candidate.box.x1 - anchor.box.x2)
    vertical_offset = abs(candidate.box.center_y - anchor.box.center_y)
    return 1000.0 - horizontal_gap * 5.0 - vertical_offset * 5.0 + candidate.confidence * 100.0


def _score_below_row(anchor: OCRLine, candidate: OCRLine) -> float:
    """Score a candidate that is in a row below the anchor.

    Prefers candidates that are horizontally aligned with the anchor
    (small horizontal_gap between their x-ranges).
    """
    # Use horizontal gap between the two boxes (how far apart they are on x-axis)
    h_gap = anchor.box.horizontal_gap(candidate.box)
    v_gap = anchor.box.vertical_gap(candidate.box)
    # Penalize large horizontal gaps, but less severely than vertical gaps
    return 800.0 - v_gap * 8.0 - h_gap * 3.0 + candidate.confidence * 100.0


# ── Item-table extraction ────────────────────────────────────────────────────

# Minimum number of header words that must match on one row to treat as a table header.
TABLE_HEADER_MIN_MATCHES = 2

# Bounded directional search radius (pixels) for repair fallback
_TABLE_REPAIR_RADIUS = 30.0

TABLE_COLUMN_HEADERS: dict[str, tuple[str, ...]] = {
    "designation": (
        "désignation", "designation", "description", "libellé", "libelle",
        "article", "produit",
        "البيان", "الوصف", "الصنف",  # Arabic: statement/description/item
    ),
    "quantite": (
        "quantité", "quantite", "qté", "qte", "qty", "quant",
        "الكمية",  # Arabic: quantity
    ),
    "prix_unitaire": (
        "prix unitaire", "prixunitaire", "p.u.", "pu", "prix ht", "prix",
        "سعر الوحدة", "السعر",  # Arabic: unit price / price
    ),
    "tva_rate": (
        "tva", "taux tva", "taux", "tva %", "taux de tva", "tva%",
        "ض.ق.م", "نسبة",  # Arabic: VAT abbreviation / rate
    ),
    "montant": (
        "montant", "total ht", "total", "montant ht", "montant h.t.",
        "montant ttc",
        "المبلغ", "المجموع",  # Arabic: amount / total
    ),
}


_FOOTER_ALIASES: set[str] = set()
for _f in ("montant_ht", "montant_tva", "montant_taxe", "montant_ttc"):
    _FOOTER_ALIASES.update(FIELD_ALIASES[_f])
_FOOTER_ALIASES.add("sous-total")
_FOOTER_ALIASES.add("sous total")
_FOOTER_ALIASES.add("net à payer")
_FOOTER_ALIASES.add("net a payer")
_FOOTER_ALIASES.add("total général")
_FOOTER_ALIASES.add("total general")
_FOOTER_ALIASES.add("total ttc")
_FOOTER_ALIASES.add("reste à payer")
_FOOTER_ALIASES.add("reste a payer")
_FOOTER_ALIASES.add("arrêté")
_FOOTER_ALIASES.add("arrete")
_FOOTER_ALIASES.add("total due")
_FOOTER_ALIASES.add("grand total")


def _cluster_full_visual_rows(ocr_lines: Sequence[OCRLine]) -> list[list[OCRLine]]:
    """Group OCR lines by vertical overlap only, WITHOUT splitting on horizontal gaps.

    cluster_rows() (in utils.py) intentionally splits a visual row into
    sub-rows when two lines are more than 5x-height apart horizontally —
    correct for separating unrelated two-column blocks, but wrong here: a
    real item-table row's designation and its amount are typically *exactly*
    that far apart (the empty quantity/price columns sit between them). Using
    cluster_rows() directly would silently split every table row into two
    "rows" — designation alone, amount alone — and the amount-ending check
    below would never see a designation next to its amount.
    """
    if not ocr_lines:
        return []
    sorted_lines = sorted(ocr_lines, key=lambda l: (l.page_index, l.box.y1))
    rows: list[list[OCRLine]] = []
    used = [False] * len(sorted_lines)
    for i, ln in enumerate(sorted_lines):
        if used[i]:
            continue
        current_row = [ln]
        used[i] = True
        for j in range(i + 1, len(sorted_lines)):
            if used[j]:
                continue
            candidate = sorted_lines[j]
            if candidate.page_index != ln.page_index:
                continue
            overlap = ln.box.vertical_overlap(candidate.box)
            shorter_height = min(ln.box.height, candidate.box.height)
            if shorter_height > 0 and overlap / shorter_height > 0.5:
                current_row.append(candidate)
                used[j] = True
        current_row.sort(key=lambda l: l.box.x1)
        rows.append(current_row)
    return rows


def _extract_item_table_geometric(
    ocr_lines: Sequence[OCRLine], rows: list[list[OCRLine]]
) -> list[dict[str, object]]:
    """Fallback item-table extraction when no header row can be found.

    Some invoices lay out their item table without OCR-readable column
    headers (skewed scan, stylized/colored header row, unusual wording not
    covered by TABLE_COLUMN_HEADERS). This strategy skips header detection
    entirely and looks for a run of rows that each end with an amount-like
    value on their right edge — the visual signature of an item table
    (designation ... price/total flush right on every row). The rightmost
    token on each row is read as ``montant``; everything to its left is
    joined as ``designation``; a bare small integer or a "%"-suffixed token
    among the remaining tokens is opportunistically read as ``quantite`` /
    ``tva_rate``. ``prix_unitaire`` is left unset — position alone can't
    reliably distinguish it from designation without a header.

    Because there is no explicit column identity here (only position),
    results are inherently less certain than header-anchored extraction —
    this is a best-effort fallback, not a replacement for it.

    NOTE: ``rows`` (the caller's cluster_rows() output, sub-row split) is
    accepted but intentionally unused for row grouping — see
    _cluster_full_visual_rows for why. It's kept as a parameter so the call
    site in extract_item_table doesn't need to special-case this function.
    """
    full_rows = _cluster_full_visual_rows(ocr_lines)
    if not full_rows:
        return []

    candidate_rows: list[list[OCRLine]] = []
    for row in full_rows:
        if len(row) < 2:
            continue
        rightmost = max(row, key=lambda l: l.box.x2)
        if _looks_like_amount(rightmost.text.strip()):
            candidate_rows.append(row)

    # Require 2+ amount-ending rows — a single match is more likely a totals
    # line than a genuine item table.
    if len(candidate_rows) < 2:
        _debug_log("extract_item_table_geometric: fewer than 2 amount-ending rows — skipping")
        return []

    # Drop rows that are actually part of the totals block (HT/TVA/TTC etc.)
    filtered_rows = [
        row for row in candidate_rows
        if not _contains_any_alias(
            " ".join(l.text.strip() for l in row if l.text.strip()).lower(),
            list(_FOOTER_ALIASES),
        )
    ]
    if len(filtered_rows) < 2:
        _debug_log("extract_item_table_geometric: fewer than 2 rows after footer filtering — skipping")
        return []

    items: list[dict[str, object]] = []
    for row in filtered_rows:
        sorted_row = sorted(row, key=lambda l: l.box.x1)
        rightmost = sorted_row[-1]
        montant = _parse_cell_value("montant", rightmost.text.strip())

        remaining = sorted_row[:-1]
        designation_parts = [l.text.strip() for l in remaining if l.text.strip()]
        designation = " ".join(designation_parts) if designation_parts else None

        quantite = None
        tva_rate = None
        for token in remaining:
            text = token.text.strip()
            if quantite is None and re.match(r'^\d{1,4}$', text):
                quantite = _parse_cell_value("quantite", text)
            elif tva_rate is None and "%" in text:
                tva_rate = _parse_cell_value("tva_rate", text)

        if designation is None and montant is None:
            continue

        items.append({
            "designation": designation,
            "quantite": quantite,
            "prix_unitaire": None,
            "tva_rate": tva_rate,
            "montant": montant,
        })

    _debug_log(f"extract_item_table_geometric: extracted {len(items)} items via geometric fallback")
    return items


def extract_item_table(ocr_lines: Sequence[OCRLine]) -> list[dict[str, object]]:
    """Extract line items from an invoice table using header-row anchoring.

    Steps:
    1. Cluster OCR lines into visual rows.
    2. Search for a row where 2+ column-header words co-occur (header row).
    3. Record the x-boundary of each found header column.
    4. Read items from subsequent rows until a footer anchor is hit.
    5. Apply bounded directional repair for cells missed by strict x-range.
    6. If no header is confidently found, return empty list.

    Each returned item contains whichever of {designation, quantite,
    prix_unitaire, tva_rate, montant} were actually found.
    """
    rows = cluster_rows(ocr_lines)
    if not rows:
        return []

    # Step 1: find the header row
    header_row_idx, column_bounds = _find_table_header(rows)
    if header_row_idx is None or not column_bounds:
        _debug_log("extract_item_table: no table header found — trying geometric fallback")
        return _extract_item_table_geometric(ocr_lines, rows)

    _debug_log(f"extract_item_table: header at row {header_row_idx}, columns: {list(column_bounds.keys())}")

    # Step 2: extract items from rows below the header, stopping at footer rows
    items: list[dict[str, object]] = []
    for i in range(header_row_idx + 1, len(rows)):
        row = rows[i]
        row_text = " ".join(line.text.strip() for line in row if line.text.strip()).lower()

        # Check for footer anchor: if any footer alias matches, stop
        if _contains_any_alias(row_text, list(_FOOTER_ALIASES)):
            _debug_log(f"extract_item_table: footer hit at row {i} — stopping")
            break

        item = _extract_row_as_item(row, column_bounds, rows, i)
        if item is not None:
            items.append(item)

    _debug_log(f"extract_item_table: extracted {len(items)} items")
    return items


def _find_table_header(rows: list[list[OCRLine]]) -> tuple[int | None, dict[str, tuple[float, float]]]:
    """Find the table header row using a sliding window of 2 rows.

    A row (or pair of adjacent rows) is considered a table header when at least
    TABLE_HEADER_MIN_MATCHES (2) column-header aliases from TABLE_COLUMN_HEADERS
    match in the combined text of the two rows.  This handles headers that span
    two lines (e.g. "Prix\nUnitaire" → "prix unitaire").

    For each matched column, the column center is computed as the average
    center_x of all matching OCR lines.  This enables grid snapping (distance-
    based cell assignment) downstream.

    Returns (row_index, {column_name: (x_center, x_center)}).
    If no header found, returns (None, {}).
    """
    for row_idx in range(len(rows)):
        # Single-row check
        row = rows[row_idx]
        row_text = " ".join(line.text.strip() for line in row)
        matched_columns = _match_columns_in_text(row, row_text)
        if len(matched_columns) >= TABLE_HEADER_MIN_MATCHES:
            return row_idx, matched_columns

        # Two-row sliding window: combine this row and the next
        if row_idx + 1 < len(rows):
            next_row = rows[row_idx + 1]
            combined_lines = list(row) + list(next_row)
            combined_text = " ".join(line.text.strip() for line in combined_lines)
            matched_columns = _match_columns_in_text(combined_lines, combined_text)
            if len(matched_columns) >= TABLE_HEADER_MIN_MATCHES:
                return row_idx, matched_columns

    return None, {}


def _match_columns_in_text(lines: list[OCRLine], text: str) -> dict[str, tuple[float, float]]:
    """Match column headers in the given text and return column-center positions.

    Returns {column_name: (center_x, center_x)} where both values are the same
    (the average center_x of all matching OCR lines for that column).  Using
    identical min/max simplifies downstream grid snapping.
    """
    matched: dict[str, tuple[float, float]] = {}
    for col_name, col_aliases in TABLE_COLUMN_HEADERS.items():
        if _contains_any_alias(text, col_aliases):
            # Compute average center_x of matching lines
            x_centers: list[float] = []
            for line in lines:
                if _contains_any_alias(line.text, col_aliases):
                    x_centers.append(line.box.center_x)
            if x_centers:
                center = sum(x_centers) / len(x_centers)
                # Store as (center, center) so downstream code that uses
                # (x_min, x_max) tuple unpacking still works
                matched[col_name] = (center, center)
    return matched


# Maximum distance (pixels) for grid snapping: an OCR line must be within this
# distance of a column center to be assigned to that column.
_COLUMN_SNAP_MAX_DISTANCE = 150.0


def _extract_row_as_item(row: list[OCRLine], column_bounds: dict[str, tuple[float, float]], all_rows: list[list[OCRLine]], row_idx: int) -> dict[str, object] | None:
    """Extract an item from a single row using grid snapping.

    For each known column, finds the OCR line whose center_x is closest to the
    column's center_x, within _COLUMN_SNAP_MAX_DISTANCE.  If the column bounds
    store an x-range (x_min != x_max), also accepts lines strictly within the
    range as a fallback.  Applies bounded directional search as repair fallback.
    """
    item: dict[str, object] = {}
    found_any = False
    used_line_ids: set[int] = set()

    for col_name, (x1, x2) in column_bounds.items():
        col_center = (x1 + x2) / 2.0
        value, best_line = _find_cell_value_by_snap_or_range(
            row, col_name, col_center, x1, x2, used_line_ids,
        )

        # Repair fallback: bounded directional search around column center
        if value is None:
            value = _bounded_directional_search(row, all_rows, row_idx, col_name, col_center)

        if value is not None:
            item[col_name] = value
            found_any = True
        else:
            item[col_name] = None

    return item if found_any else None


def _find_cell_value_by_snap_or_range(
    row: list[OCRLine], col_name: str, col_center: float,
    x_min: float, x_max: float, used_line_ids: set[int],
) -> tuple[object | None, OCRLine | None]:
    """Find a cell value using grid snapping, falling back to strict x-range.

    First pass: snap to the closest OCR line within _COLUMN_SNAP_MAX_DISTANCE
    of col_center that hasn't been used by another column.
    Second pass (if no snap found): use strict x-range (x_min, x_max).
    """
    best_line = None
    best_dist = float("inf")

    for line in row:
        if id(line) in used_line_ids:
            continue
        dist = abs(line.box.center_x - col_center)
        if dist <= _COLUMN_SNAP_MAX_DISTANCE and dist < best_dist:
            best_dist = dist
            best_line = line

    if best_line is not None:
        text = best_line.text.strip()
        if text:
            val = _parse_cell_value(col_name, text)
            if val is not None:
                used_line_ids.add(id(best_line))
                return val, best_line

    # Fallback: strict x-range (unchanged behaviour for existing callers)
    for line in row:
        cx = line.box.center_x
        if x_min <= cx <= x_max:
            text = line.text.strip()
            if text:
                val = _parse_cell_value(col_name, text)
                if val is not None:
                    return val, line

    return None, None


def _bounded_directional_search(row: list[OCRLine], all_rows: list[list[OCRLine]], row_idx: int, col_name: str, col_center: float) -> object | None:
    """Search a small distance from the column center for a value.

    Looks within _TABLE_REPAIR_RADIUS pixels of col_center in the same row,
    then in adjacent rows. Returns None if nothing found.
    """
    x_min = col_center - _TABLE_REPAIR_RADIUS
    x_max = col_center + _TABLE_REPAIR_RADIUS

    for line in row:
        cx = line.box.center_x
        if x_min <= cx <= x_max:
            text = line.text.strip()
            if text:
                val = _parse_cell_value(col_name, text)
                if val is not None:
                    return val

    # Check adjacent rows (up/down one) within radius
    for adj_idx in [row_idx - 1, row_idx + 1]:
        if adj_idx < 0 or adj_idx >= len(all_rows):
            continue
        for line in all_rows[adj_idx]:
            cx = line.box.center_x
            if x_min <= cx <= x_max:
                text = line.text.strip()
                if text:
                    val = _parse_cell_value(col_name, text)
                    if val is not None:
                        return val

    return None


def _parse_cell_value(col_name: str, text: str) -> object:
    """Parse a cell text value into the appropriate type for the column."""
    if col_name == "designation":
        return text

    # TVA rate: normalize to decimal (0.20 for 20%)
    if col_name == "tva_rate":
        cleaned = text.strip().replace(",", ".").replace(" ", "")
        if "%" in cleaned:
            try:
                return float(cleaned.replace("%", "").strip()) / 100.0
            except (ValueError, TypeError):
                return None
        try:
            val = float(cleaned)
            if val > 1:
                return val / 100.0
            return val
        except (ValueError, TypeError):
            return None

    # Numeric columns (quantite, prix_unitaire, montant)
    cleaned = clean_amount(text)
    if cleaned:
        try:
            return float(cleaned)
        except (ValueError, TypeError):
            pass

    # If cleaning failed but col expects a number, try direct parse
    if col_name in ("quantite", "prix_unitaire", "montant"):
        try:
            return float(text.replace(",", ".").replace(" ", ""))
        except (ValueError, TypeError):
            return None

    return text
