"""Invoice field extraction based on OCR text and bounding boxes."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Mapping, Optional, Sequence

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
    "numero_facture": (
        "n° facture",
        "n facture",
        "n°facture",
        "numéro",
        "numero",
        "facture n°",
        "facture no",
        "nº facture",
        "no facture",
        "référence",
        "reference",
        "ref",
        "no",
    ),
    "date": (
        "date",
        "date de facturation",
        "date facture",
        "émise le",
        "emise le",
    ),
    "fournisseur": (
        "fournisseur",
        "vendeur",
        "émetteur",
        "emetteur",
    ),
    "client": (
        "client",
        "acheteur",
        "destinataire",
    ),
    "montant_ht": (
        "montant ht",
        "total ht",
        "ht",
    ),
    "montant_tva": (
        "tva",
        "montant tva",
    ),
    "montant_taxe": (
        "taxe",
        "montant taxe",
    ),
    "montant_ttc": (
        "ttc",
        "montant ttc",
        "total ttc",
        "net à payer",
        "net a payer",
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


def compute_confidence(field_scores: Mapping[str, float]) -> float:
    """Compute a normalized confidence score from selected field confidences."""

    scores = [score for score in field_scores.values() if score > 0.0]
    if not scores:
        return 0.0
    average = sum(scores) / len(scores)
    return max(0.0, min(1.0, average))


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
        selection = _select_best_selection_for_field(field, rows, all_lines, FieldSelection(value=None, confidence=0.0, score=float("-inf"), ocr_line=None))
        if selection.ocr_line is not None and selection.score > float("-inf"):
            proposals.append((field, selection.ocr_line, selection.score, selection.value, selection.confidence))

    # Step 2: Resolve conflicts — each OCR line goes to the highest-scoring field
    # Sort proposals by score descending
    proposals.sort(key=lambda p: p[2], reverse=True)

    claimed_lines: set[int] = set()  # id(OCRLine)
    resolved: dict[str, FieldSelection] = {field: FieldSelection(value=None, confidence=0.0, score=float("-inf")) for field in FIELD_ORDER}

    for field, ocr_line, score, value, confidence in proposals:
        line_id = id(ocr_line)
        if line_id in claimed_lines:
            # This line was already claimed by a higher-scoring field — skip
            continue
        claimed_lines.add(line_id)
        resolved[field] = FieldSelection(value=value, confidence=confidence, score=score, ocr_line=ocr_line)

    # Step 3: For fields that didn't get a candidate (no proposal or all claimed),
    # try same-line inline extraction (which doesn't consume a separate line)
    for field in FIELD_ORDER:
        if resolved[field].score > float("-inf"):
            continue  # Already has a candidate
        resolved[field] = _select_best_selection_for_field(field, rows, all_lines, resolved[field])

    return resolved


# ── Per-field candidate search ───────────────────────────────────────────────


def _select_best_selection_for_field(field: str, rows: list[list[OCRLine]], all_lines: Sequence[OCRLine], current_selection: FieldSelection) -> FieldSelection:
    """Evaluate all anchors for a field using geometric search and return the best selection."""

    aliases = FIELD_ALIASES[field]
    anchors = [line for line in all_lines if _contains_any_alias(line.text, aliases)]
    selection = current_selection

    for anchor in anchors:
        same_line_selection = _selection_from_same_line(field, anchor, selection)
        if same_line_selection is not None:
            selection = same_line_selection

        geometric_selection = _selection_from_geometric_search(field, anchor, rows, selection)
        if geometric_selection is not None:
            selection = geometric_selection

    return selection


def _selection_from_same_line(field: str, anchor: OCRLine, current_selection: FieldSelection) -> FieldSelection | None:
    """Create a selection when the value is embedded in the same OCR line as the label."""

    same_line_value = _extract_inline_value(field, anchor.text)
    if same_line_value is None:
        return None

    score = _score_candidate(anchor, anchor, same_line=True)
    selection = FieldSelection(value=same_line_value, confidence=anchor.confidence, score=score, ocr_line=anchor)
    if selection.score <= current_selection.score:
        return None
    return selection


def _selection_from_geometric_search(field: str, anchor: OCRLine, rows: list[list[OCRLine]], current_selection: FieldSelection) -> FieldSelection | None:
    """Find the best candidate using purely geometric criteria.

    Considers:
    (a) Lines on the same row as the anchor, to the right
    (b) Lines in rows below the anchor, within horizontal range
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
        # Must be to the right of the anchor's right edge
        if line.box.x1 < anchor.box.x2:
            continue
        if not _candidate_is_plausible(line):
            continue
        score = _score_same_row_right(anchor, line)
        candidates.append((line, score))

    # (b) Below-row candidates
    for line in _lines_below(anchor_row_idx, rows):
        if not _candidate_is_plausible(line):
            continue
        score = _score_below_row(anchor, line)
        candidates.append((line, score))

    if not candidates:
        return None

    # Sort by score descending
    candidates.sort(key=lambda c: c[1], reverse=True)
    best_line, best_score = candidates[0]

    candidate_value = _clean_candidate_value(field, best_line.text)
    if candidate_value is None:
        return None

    selection = FieldSelection(value=candidate_value, confidence=best_line.confidence, score=best_score, ocr_line=best_line)
    if selection.score <= current_selection.score:
        return None
    return selection


def _contains_any_alias(text: str, aliases: Sequence[str]) -> bool:
    """Return True when the normalized text contains any alias."""

    normalized = normalize_text(text)
    return any(normalize_text(alias) in normalized for alias in aliases)


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

    Uses regex patterns to avoid false positives where short label tokens
    (e.g. "no", "ref") appear as substrings inside legitimate values.
    Example: "Société Nouvelle SARL" must NOT match "no" (inside "nouvelle").
    "REF-2024-001" must NOT match "ref" (it's an invoice number, not a label).
    """

    normalized = normalize_text(text)
    label_patterns = [
        "facture", "date", "fournisseur", "client",
        "vendeur", "emetteur", "acheteur", "destinataire",
        "ht", "tva", "taxe", "ttc",
        "montant", "total", "netapayer",
        "reference", "numero",
        r"\bno\b[.\s]*$",
        r"\bref\b[.\s]*$",
    ]
    return any(re.search(pattern, normalized) for pattern in label_patterns)


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
