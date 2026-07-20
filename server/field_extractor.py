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


def extract_invoice_fields(ocr_lines: Sequence[OCRLine]) -> dict[str, Optional[str]]:
    """Extract the eight invoice fields from OCR lines."""

    selections = _extract_field_selections(ocr_lines)
    return {
        field: (
            selections[field].value
            if selections[field].value is not None
               and selections[field].confidence >= FIELD_CONFIDENCE_THRESHOLD
            else None
        )
        for field in FIELD_ORDER
    }


def extract_field_selections_raw(ocr_lines: Sequence[OCRLine]) -> dict[str, FieldSelection]:
    """Return the full FieldSelection dict (including OCR line refs) for downstream use."""
    return _extract_field_selections(ocr_lines)


def extract_field_confidences(ocr_lines: Sequence[OCRLine]) -> dict[str, float]:
    """Return the per-field confidence values selected by the extractor."""

    selections = _extract_field_selections(ocr_lines)
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
        if missing_amounts >= 2:
            penalties += 0.10
        elif missing_amounts >= 3:
            penalties += 0.20

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


def _extract_field_selections(ocr_lines: Sequence[OCRLine]) -> dict[str, FieldSelection]:
    """Resolve the best candidate for each field from the OCR lines, preventing
    two fields from claiming the same OCR line unless the second's score is
    meaningfully higher."""

    rows = cluster_rows(ocr_lines)
    all_lines = sorted(ocr_lines, key=lambda item: (item.page_index, item.line_index, item.box.y1, item.box.x1))

    # Step 1: Each field proposes its best candidate (collect all proposals)
    # Proposal = (field, OCRLine, score, value, confidence)
    proposals: list[tuple[str, OCRLine, float, Optional[str], float]] = []

    for field in FIELD_ORDER:
        if field == "numero_facture":
            # Use specialized extraction to avoid false positives from broad anchors
            selection = _extract_numero_facture(rows, all_lines)
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
    """

    normalized = normalize_text(text)
    for alias in aliases:
        alias_normalized = normalize_text(alias)
        if not alias_normalized:
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


def _extract_numero_facture(rows: list[list[OCRLine]], all_lines: Sequence[OCRLine]) -> FieldSelection:
    """Extract invoice number with strict + relaxed anchor matching.

    Phase 1: strict compound anchors (word-boundary matched).
    Phase 2: if Phase 1 yields no usable value, try relaxed subsequence
    matching which tolerates inserted stopwords — e.g. "n° de la facture
    d'origine" matching "n° de facture".
    """
    selection = FieldSelection(value=None, confidence=0.0, score=float("-inf"))

    # Phase 1: strict word-boundary matching (prevents false positives)
    strict_anchors = [line for line in all_lines if _contains_any_alias(line.text, _NUMERO_FACTURE_ANCHORS)]
    _debug_log(f"_extract_numero_facture: strict anchors found = {len(strict_anchors)}")

    for anchor in strict_anchors:
        selection = _process_numero_anchor(anchor, rows, selection)

    # Phase 2: if strict matching found nothing, try relaxed subsequence matching
    # to handle inserted stopwords (e.g. "n° de la facture d'origine").
    if selection.value is None:
        relaxed_anchors = [
            line for line in all_lines
            if _matches_alias_relaxed(line.text, _NUMERO_FACTURE_ANCHORS)
            and not _contains_any_alias(line.text, _NUMERO_FACTURE_ANCHORS)  # avoid re-processing
        ]
        _debug_log(f"_extract_numero_facture: relaxed anchors found = {len(relaxed_anchors)}")
        for anchor in relaxed_anchors:
            selection = _process_numero_anchor(anchor, rows, selection)

    _debug_log(
        f"_extract_numero_facture: final={selection.value!r} "
        f"score={selection.score:.1f} "
        f"strict_anchors={len(strict_anchors)}"
    )
    return selection


def _process_numero_anchor(anchor: OCRLine, rows: list[list[OCRLine]], current_selection: FieldSelection) -> FieldSelection:
    """Try to extract a numero_facture value from a single anchor line.

    Tries same-line extraction first, then geometric search.
    Returns the best selection found (or the original if nothing better).
    """
    selection = current_selection

    # Try same-line extraction first (e.g. "N° Facture: INV-2024-001")
    same_line = _selection_from_same_line("numero_facture", anchor, selection)
    if same_line is not None:
        value = same_line.value or ""
        # Apply heavy date penalty to same-line results too
        if _looks_like_date(value):
            _debug_log(
                f"  numero_facture: same-line value {value!r} looks like a date — "
                f"applying -500 penalty"
            )
            same_line = FieldSelection(
                value=same_line.value,
                confidence=same_line.confidence,
                score=same_line.score - 500.0,
                ocr_line=same_line.ocr_line,
            )
        quality_boost = _invoice_number_quality_score(same_line.value or "")
        adjusted = FieldSelection(
            value=same_line.value,
            confidence=same_line.confidence,
            score=same_line.score + quality_boost * 100.0,
            ocr_line=same_line.ocr_line,
        )
        if adjusted.score > selection.score:
            selection = adjusted

    # Try geometric search (value on same row to right, or row below)
    geometric = _selection_from_geometric_search("numero_facture", anchor, rows, selection)
    if geometric is not None:
        # ── Date penalty for invoice numbers ────────────────────────────────
        # A value that parses cleanly as a date (e.g. "24/11/2020") is very
        # unlikely to also be a valid invoice number, even if it scores better
        # geometrically because it happens to be closer to the anchor.  Apply
        # a HEAVY penalty (500 points, not the small ±30 in quality_score) to
        # make date-like values lose against any non-date candidate.
        value = geometric.value or ""
        if _looks_like_date(value):
            _debug_log(
                f"  numero_facture: value {value!r} looks like a date — "
                f"applying -500 penalty"
            )
            geometric = FieldSelection(
                value=geometric.value,
                confidence=geometric.confidence,
                score=geometric.score - 500.0,
                ocr_line=geometric.ocr_line,
            )
        quality_boost = _invoice_number_quality_score(geometric.value or "")
        adjusted = FieldSelection(
            value=geometric.value,
            confidence=geometric.confidence,
            score=geometric.score + quality_boost * 100.0,
            ocr_line=geometric.ocr_line,
        )
        if adjusted.score > selection.score:
            selection = adjusted

    return selection


def _invoice_number_quality_score(value: str) -> float:
    """Score how likely a text value is a real invoice number vs a false positive.

    Returns a quality boost between -0.3 and +0.3.
    Positive for invoice-like patterns (INV, FAC, year prefixes).
    Negative for very short values, pure-digit short strings, or date-like patterns.
    """
    if not value:
        return -0.3

    score: float = 0.0

    # Boost if it looks like an invoice number pattern
    if any(pattern in value.upper() for pattern in ["INV", "FAC"]):
        score += 0.25

    # Boost if it contains a recent year (common in invoice IDs)
    if any(year in value for year in ["2026", "2025", "2024", "2023"]):
        score += 0.2

    # Penalize very short values (likely false positives like "42" or "001")
    if len(value) < 3:
        score -= 0.3
    elif len(value) < 5:
        score -= 0.1

    # Penalize if only digits and short (could be quantity, PO reference)
    if value.isdigit() and len(value) < 6:
        score -= 0.2

    # Penalize if it looks like a date
    if re.match(r"\d{1,2}[/-]\d{1,2}[/-]\d{2,4}", value):
        score -= 0.3

    return max(-0.3, min(0.3, score))


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
