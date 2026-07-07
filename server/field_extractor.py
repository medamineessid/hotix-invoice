"""Invoice field extraction based on OCR text and bounding boxes."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Mapping, Optional, Sequence

from .utils import (
    INVOICE_FIELD_NAMES,
    OCRLine,
    clean_amount,
    clean_date,
    collapse_text,
    looks_like_latin_text,
    normalize_text,
    normalize_text_for_output,
)


FIELD_ORDER = INVOICE_FIELD_NAMES

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


def extract_invoice_fields(ocr_lines: Sequence[OCRLine]) -> dict[str, Optional[str]]:
    """Extract the eight invoice fields from OCR lines."""

    selections = _extract_field_selections(ocr_lines)
    return {field: selections[field].value for field in FIELD_ORDER}


def extract_field_confidences(ocr_lines: Sequence[OCRLine]) -> dict[str, float]:
    """Return the per-field confidence values selected by the extractor."""

    selections = _extract_field_selections(ocr_lines)
    return {field: selections[field].confidence for field in FIELD_ORDER}


def extract_raw_text(ocr_lines: Sequence[OCRLine]) -> str:
    """Return the full OCR text joined in reading order for debugging."""

    ordered = sorted(ocr_lines, key=lambda item: (item.page_index, item.box.y1, item.box.x1, item.line_index))
    return "\n".join(line.text for line in ordered if line.text.strip())


def compute_confidence(field_scores: Mapping[str, float]) -> float:
    """Compute a normalized confidence score from selected field confidences."""

    scores = [score for score in field_scores.values() if score > 0.0]
    if not scores:
        return 0.0
    average = sum(scores) / len(scores)
    return max(0.0, min(1.0, average))


def _extract_field_selections(ocr_lines: Sequence[OCRLine]) -> dict[str, FieldSelection]:
    """Resolve the best candidate for each field from the OCR lines."""

    ordered_lines = sorted(ocr_lines, key=lambda item: (item.page_index, item.line_index, item.box.y1, item.box.x1))
    selections: dict[str, FieldSelection] = {field: FieldSelection(value=None, confidence=0.0, score=float("-inf")) for field in FIELD_ORDER}

    for field in FIELD_ORDER:
        selections[field] = _select_best_selection_for_field(field, ordered_lines, selections[field])

    return selections


def _select_best_selection_for_field(field: str, ordered_lines: Sequence[OCRLine], current_selection: FieldSelection) -> FieldSelection:
    """Evaluate all anchors for a field and return the best selection."""

    aliases = FIELD_ALIASES[field]
    anchors = [line for line in ordered_lines if _contains_any_alias(line.text, aliases)]
    selection = current_selection

    for anchor in anchors:
        same_line_selection = _selection_from_same_line(field, anchor, selection)
        if same_line_selection is not None:
            selection = same_line_selection

        next_line_selection = _selection_from_next_line(field, anchor, ordered_lines, selection)
        if next_line_selection is not None:
            selection = next_line_selection

    return selection


def _selection_from_same_line(field: str, anchor: OCRLine, current_selection: FieldSelection) -> FieldSelection | None:
    """Create a selection when the value is embedded in the same OCR line as the label."""

    same_line_value = _extract_inline_value(field, anchor.text)
    if same_line_value is None:
        return None

    score = _score_candidate(anchor, anchor, same_line=True)
    selection = FieldSelection(value=same_line_value, confidence=anchor.confidence, score=score)
    if selection.score <= current_selection.score:
        return None
    return selection


def _selection_from_next_line(field: str, anchor: OCRLine, ordered_lines: Sequence[OCRLine], current_selection: FieldSelection) -> FieldSelection | None:
    """Create a selection when the value is on the next plausible OCR line."""

    candidate = _find_next_candidate(anchor, ordered_lines)
    if candidate is None:
        return None

    candidate_value = _clean_candidate_value(field, candidate.text)
    if candidate_value is None:
        return None

    score = _score_candidate(anchor, candidate, same_line=False)
    selection = FieldSelection(value=candidate_value, confidence=candidate.confidence, score=score)
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
        suffix = normalized.split(alias_normalized, 1)[1].strip(" :-\t\r\n")
        if not suffix:
            continue
        cleaned = _clean_candidate_value(field, suffix)
        if cleaned is not None:
            return cleaned

    return None


def _find_next_candidate(anchor: OCRLine, ordered_lines: Sequence[OCRLine]) -> OCRLine | None:
    """Find the nearest plausible candidate on the same line or the next line."""

    same_page = [line for line in ordered_lines if line.page_index == anchor.page_index and line.line_index > anchor.line_index]
    if not same_page:
        return None

    window = [line for line in same_page if 0 < line.line_index - anchor.line_index <= 2]
    if not window:
        return None

    scored = [(candidate, _score_candidate(anchor, candidate, same_line=False)) for candidate in window if _candidate_is_plausible(candidate)]
    if not scored:
        return None

    scored.sort(key=lambda item: item[1], reverse=True)
    return scored[0][0]


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
    """Return True when the text still looks like a label rather than a value."""

    normalized = normalize_text(text).replace(" ", "")
    label_tokens = {
        "facture",
        "date",
        "fournisseur",
        "client",
        "vendeur",
        "emetteur",
        "acheteur",
        "destinataire",
        "ht",
        "tva",
        "taxe",
        "ttc",
        "montant",
        "total",
        "netapayer",
        "reference",
        "numero",
        "no",
        "ref",
    }
    return any(token in normalized for token in label_tokens)


def _score_candidate(anchor: OCRLine, candidate: OCRLine, same_line: bool) -> float:
    """Score a candidate using bounding-box geometry as a tiebreaker."""

    if same_line:
        horizontal_gap = max(0.0, candidate.box.x1 - anchor.box.x2)
        vertical_offset = abs(candidate.box.center_y - anchor.box.center_y)
        return 1000.0 - horizontal_gap * 10.0 - vertical_offset * 5.0 + candidate.confidence * 100.0

    vertical_gap = anchor.box.vertical_gap(candidate.box)
    horizontal_gap = abs(candidate.box.center_x - anchor.box.center_x)
    return 800.0 - vertical_gap * 10.0 - horizontal_gap * 2.0 + candidate.confidence * 100.0
