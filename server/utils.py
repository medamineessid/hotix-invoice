"""Shared helpers for HOTIX invoice extraction."""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal, InvalidOperation
from typing import Optional, Sequence


@dataclass(frozen=True)
class BoundingBox:
    """Axis-aligned bounding box used for geometric proximity scoring."""

    x1: float
    y1: float
    x2: float
    y2: float

    @classmethod
    def from_points(cls, points: Sequence[Sequence[float]]) -> "BoundingBox":
        """Build a bounding box from a polygon returned by PaddleOCR."""

        xs = [float(point[0]) for point in points]
        ys = [float(point[1]) for point in points]
        return cls(min(xs), min(ys), max(xs), max(ys))

    @property
    def width(self) -> float:
        return max(0.0, self.x2 - self.x1)

    @property
    def height(self) -> float:
        return max(0.0, self.y2 - self.y1)

    @property
    def center_x(self) -> float:
        return (self.x1 + self.x2) / 2.0

    @property
    def center_y(self) -> float:
        return (self.y1 + self.y2) / 2.0

    def horizontal_overlap(self, other: "BoundingBox") -> float:
        return max(0.0, min(self.x2, other.x2) - max(self.x1, other.x1))

    def vertical_overlap(self, other: "BoundingBox") -> float:
        return max(0.0, min(self.y2, other.y2) - max(self.y1, other.y1))

    def horizontal_gap(self, other: "BoundingBox") -> float:
        if other.x1 >= self.x2:
            return other.x1 - self.x2
        if self.x1 >= other.x2:
            return self.x1 - other.x2
        return 0.0

    def vertical_gap(self, other: "BoundingBox") -> float:
        if other.y1 >= self.y2:
            return other.y1 - self.y2
        if self.y1 >= other.y2:
            return self.y1 - other.y2
        return 0.0


@dataclass(frozen=True)
class OCRLine:
    """Single OCR line with geometry and confidence metadata."""

    text: str
    box: BoundingBox
    confidence: float
    page_index: int
    line_index: int = 0


STRIP_CHARS = " :-\t\r\n"
AMOUNT_CLEANER = re.compile(r"[^\d,.\-]")
MULTISPACE = re.compile(r"\s+")
DATE_DD_MM_YYYY = re.compile(r"\b(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})\b")
MONTH_NAME_PATTERN = re.compile(
    r"\b(\d{1,2})\s+([a-zÀ-ÿ]+)\s+(\d{4})\b",
    re.IGNORECASE,
)

FRENCH_MONTHS = {
    "janvier": 1, "fevrier": 2, "février": 2, "mars": 3, "avril": 4,
    "mai": 5, "juin": 6, "juillet": 7, "aout": 8, "août": 8,
    "septembre": 9, "octobre": 10, "novembre": 11, "decembre": 12, "décembre": 12,
}


def normalize_text(value: str) -> str:
    """Normalize OCR text for keyword matching."""

    normalized = unicodedata.normalize("NFKD", value)
    normalized = normalized.encode("ascii", "ignore").decode("ascii")
    normalized = normalized.lower().replace("\u00a0", " ")
    return MULTISPACE.sub(" ", normalized).strip()


def collapse_text(value: str) -> str:
    """Collapse whitespace while preserving original characters."""

    return MULTISPACE.sub(" ", value.replace("\u00a0", " ")).strip()


def looks_like_latin_text(text: str) -> bool:
    """Return True when the text contains mostly latin characters."""

    if not text:
        return False
    latin_count = sum(1 for c in text if c.isascii() or unicodedata.category(c).startswith("L"))
    return latin_count / max(len(text), 1) >= 0.5


def normalize_text_for_output(text: str) -> str:
    """Strip leading/trailing noise from a candidate value."""

    cleaned = collapse_text(text)
    return cleaned.strip(STRIP_CHARS)


def extract_amount(text: str) -> Optional[str]:
    """Extract a normalized monetary amount with three decimal places."""

    if not text:
        return None

    cleaned = collapse_text(text)
    cleaned = cleaned.replace("TND", "").replace("DT", "").replace("€", "").replace("$", "").replace("£", "")
    cleaned = re.sub(r"[^\d,.\-\s]", "", cleaned).strip()

    if not cleaned:
        return None

    candidate = re.search(r"-?\d[\d\s\.,]*\d|-?\d", cleaned)
    if candidate is None:
        return None

    value = candidate.group(0).replace(" ", "")
    if "," in value and "." in value:
        if value.rfind(",") > value.rfind("."):
            value = value.replace(".", "").replace(",", ".")
        else:
            value = value.replace(",", "")
    elif "," in value:
        if value.count(",") == 1 and len(value.split(",")[-1]) in {1, 2, 3}:
            value = value.replace(",", ".")
        else:
            value = value.replace(",", "")

    try:
        amount = Decimal(value)
        result = amount.quantize(Decimal('0.001'))
    except InvalidOperation:
        return None

    return f"{result:.3f}"


def clean_amount(text: str) -> Optional[str]:
    """Alias for extract_amount used by field_extractor."""
    return extract_amount(text)


def extract_date(text: str) -> Optional[str]:
    """Normalize dates to YYYY-MM-DD."""

    if not text:
        return None

    compact = collapse_text(text)

    match = DATE_DD_MM_YYYY.search(compact)
    if match is not None:
        day, month, year = match.groups()
        year_value = int(year)
        if year_value < 100:
            year_value += 2000
        try:
            return datetime(year_value, int(month), int(day)).date().isoformat()
        except ValueError:
            return None

    match = MONTH_NAME_PATTERN.search(normalize_text(compact))
    if match is not None:
        day, month_name, year = match.groups()
        month = FRENCH_MONTHS.get(normalize_text(month_name))
        if month is None:
            return None
        try:
            return datetime(int(year), month, int(day)).date().isoformat()
        except ValueError:
            return None

    return None


def clean_date(text: str) -> Optional[str]:
    """Alias for extract_date used by field_extractor."""
    return extract_date(text)


def cluster_rows(lines: Sequence[OCRLine]) -> list[list[OCRLine]]:
    """Group OCR lines into visual rows based on vertical overlap.

    Lines whose bounding boxes overlap vertically by more than ~50%
    of the shorter line's height are grouped into the same row.
    Within each row, lines are sorted left-to-right by x1.
    Rows are sorted top-to-bottom by their average y-center.
    """
    if not lines:
        return []

    # Sort by y1 initially (top-to-bottom reading order)
    sorted_lines = sorted(lines, key=lambda l: (l.page_index, l.box.y1))

    rows: list[list[OCRLine]] = []
    used = [False] * len(sorted_lines)

    for i, line in enumerate(sorted_lines):
        if used[i]:
            continue

        current_row = [line]
        used[i] = True

        for j in range(i + 1, len(sorted_lines)):
            if used[j]:
                continue
            candidate = sorted_lines[j]
            if candidate.page_index != line.page_index:
                continue

            # Check vertical overlap: overlap > 50% of the shorter line's height
            overlap = line.box.vertical_overlap(candidate.box)
            shorter_height = min(line.box.height, candidate.box.height)
            if shorter_height > 0 and overlap / shorter_height > 0.5:
                current_row.append(candidate)
                used[j] = True

        # Sort within row left-to-right
        current_row.sort(key=lambda l: l.box.x1)
        rows.append(current_row)

    # Sort rows top-to-bottom by average y-center
    rows.sort(key=lambda r: sum(l.box.center_y for l in r) / len(r))

    return rows


def _parse_decimal(value: Optional[str]) -> Optional[Decimal]:
    """Parse a French-format amount string (e.g. '1 250,00' or '1250.000') to Decimal."""
    if not value:
        return None
    cleaned = value.strip()
    if not cleaned:
        return None
    try:
        return Decimal(cleaned)
    except InvalidOperation:
        pass
    # Try French format: remove spaces, replace comma with dot
    try:
        cleaned = cleaned.replace(" ", "").replace("\u00a0", "")
        if "," in cleaned:
            cleaned = cleaned.replace(".", "").replace(",", ".")
        return Decimal(cleaned)
    except (InvalidOperation, ValueError):
        return None


def _format_amount(value: Decimal) -> str:
    """Format a Decimal amount to the canonical 3-decimal string."""
    return f"{value.quantize(Decimal('0.001')):.3f}"


def validate_amounts(fields: dict[str, Optional[str]]) -> dict[str, Optional[str]]:
    """Validate and correct monetary amounts (HT, TVA, Taxe, TTC) in extracted fields.

    Logic:
    - If two of (montant_ht, montant_tva, montant_ttc) are available, derive the third.
    - If all three are available, verify HT + TVA + Taxe ≈ TTC (within €0.50).
    - If inconsistent, detect common duplication errors (HT == TVA or TTC == HT)
      and correct the duplicated field.
    - If no clear duplication, trust HT and TTC as most reliable and derive TVA.
    """
    result = dict(fields)  # Copy

    epsilon = Decimal("0.50")

    ht = _parse_decimal(result.get("montant_ht"))
    tva = _parse_decimal(result.get("montant_tva"))
    taxe = _parse_decimal(result.get("montant_taxe"))
    ttc = _parse_decimal(result.get("montant_ttc"))

    # How many of the three main amounts are present?
    available = sum(1 for x in [ht, tva, ttc] if x is not None)

    if available < 2:
        return result  # Not enough data to cross-validate

    effective_taxe = taxe or Decimal("0")

    # ── Case 1: All three found ──────────────────────────────────────────────
    if available == 3 and ht is not None and tva is not None and ttc is not None:
        expected_ttc = ht + tva + effective_taxe
        if abs(expected_ttc - ttc) <= epsilon:
            return result  # Already consistent

        # Inconsistent — try to detect common duplication errors
        if ht == tva:
            # TVA is a copy of HT (most common error). Derive from TTC - HT.
            derived_tva = ttc - ht - effective_taxe
            if derived_tva >= Decimal("0"):
                result["montant_tva"] = _format_amount(derived_tva)
        elif ttc == ht:
            # TTC is a copy of HT. Derive TTC from HT + TVA.
            derived_ttc = ht + tva + effective_taxe
            result["montant_ttc"] = _format_amount(derived_ttc)
        elif ttc == tva:
            # TTC is a copy of TVA. Derive TTC from HT + TVA.
            derived_ttc = ht + tva + effective_taxe
            result["montant_ttc"] = _format_amount(derived_ttc)
        else:
            # No obvious duplication. HT and TVA are the base amounts most
            # reliably presented on invoices — prefer keeping both and
            # deriving TTC.
            derived_ttc = ht + tva + effective_taxe
            result["montant_ttc"] = _format_amount(derived_ttc)

        return result

    # ── Case 2: Exactly two found — derive the third ────────────────────────
    if ht is not None and tva is not None and ttc is None:
        derived_ttc = ht + tva + effective_taxe
        result["montant_ttc"] = _format_amount(derived_ttc)
    elif ht is not None and ttc is not None and tva is None:
        derived_tva = ttc - ht - effective_taxe
        if derived_tva >= Decimal("0"):
            result["montant_tva"] = _format_amount(derived_tva)
    elif tva is not None and ttc is not None and ht is None:
        derived_ht = ttc - tva - effective_taxe
        if derived_ht >= Decimal("0"):
            result["montant_ht"] = _format_amount(derived_ht)

    return result


# ── reconcile_amounts ─────────────────────────────────────────────────────────

AMOUNT_MISMATCH_EPSILON = Decimal("0.02")
RECONCILE_MIN_CONFIDENCE = 0.6  # same as FIELD_CONFIDENCE_THRESHOLD in field_extractor


def reconcile_amounts(fields: dict[str, Optional[str]], field_confidences: dict[str, float]) -> tuple[dict[str, Optional[str]], set[str], bool]:
    """Reconcile monetary amounts (HT, TVA, Taxe, TTC) after extraction.

    Returns (updated_fields, computed_fields, has_mismatch) where:
    - updated_fields: the fields dict with any computed missing amounts filled in
    - computed_fields: set of field names that were computed rather than OCR-read
    - has_mismatch: True if all 3 amounts are present but arithmetic disagrees
      and no field was overwritten (flagged for user review)

    Rules:
    - Never overwrite a field that was already extracted with confidence
      >= RECONCILE_MIN_CONFIDENCE — flag as mismatch instead.
    - If exactly 2 of {HT, TVA, TTC} are present and both source fields have
      confidence >= RECONCILE_MIN_CONFIDENCE, compute the missing one.
    - If all 3 are present but inconsistent (|HT + TVA + Taxe - TTC| > €0.02),
      set has_mismatch=True but do NOT overwrite.
    - Computed fields are tracked in computed_fields set.
    """
    result = dict(fields)
    computed: set[str] = set()
    has_mismatch = False

    ht = _parse_decimal(result.get("montant_ht"))
    tva = _parse_decimal(result.get("montant_tva"))
    taxe = _parse_decimal(result.get("montant_taxe"))
    ttc = _parse_decimal(result.get("montant_ttc"))

    available = sum(1 for x in [ht, tva, ttc] if x is not None)

    if available < 2:
        return result, computed, has_mismatch

    effective_taxe = taxe or Decimal("0")

    if available == 3 and ht is not None and tva is not None and ttc is not None:
        expected_ttc = ht + tva + effective_taxe
        if abs(expected_ttc - ttc) <= AMOUNT_MISMATCH_EPSILON:
            return result, computed, has_mismatch  # Already consistent

        # Mismatch — if taxe is missing and within reasonable range, derive it to reconcile
        if taxe is None:
            derived_taxe = ttc - ht - tva
            max_main = max(ht, tva) if ht is not None and tva is not None else ttc or Decimal("0")
            if (derived_taxe >= Decimal("0")
                and derived_taxe <= max_main * Decimal("2")
                and abs(ht + tva + derived_taxe - ttc) <= AMOUNT_MISMATCH_EPSILON):
                result["montant_taxe"] = _format_amount(derived_taxe)
                computed.add("montant_taxe")
                return result, computed, False  # Reconciled, no mismatch
        has_mismatch = True
        return result, computed, has_mismatch

    # Exactly 2 available — derive the third
    # Only derive if both source fields have sufficient confidence
    def _above_threshold(field_name: str) -> bool:
        return field_confidences.get(field_name, 1.0) >= RECONCILE_MIN_CONFIDENCE

    if ht is not None and tva is not None and ttc is None:
        if _above_threshold("montant_ht") and _above_threshold("montant_tva"):
            derived_ttc = ht + tva + effective_taxe
            result["montant_ttc"] = _format_amount(derived_ttc)
            computed.add("montant_ttc")
    elif ht is not None and ttc is not None and tva is None:
        if _above_threshold("montant_ht") and _above_threshold("montant_ttc"):
            derived_tva = ttc - ht - effective_taxe
            if derived_tva >= Decimal("0"):
                result["montant_tva"] = _format_amount(derived_tva)
                computed.add("montant_tva")
    elif tva is not None and ttc is not None and ht is None:
        if _above_threshold("montant_tva") and _above_threshold("montant_ttc"):
            derived_ht = ttc - tva - effective_taxe
            if derived_ht >= Decimal("0"):
                result["montant_ht"] = _format_amount(derived_ht)
                computed.add("montant_ht")
    elif ht is not None and tva is not None and ttc is not None and taxe is None:
        # All three main amounts present, taxe is the only missing one
        if _above_threshold("montant_ht") and _above_threshold("montant_tva") and _above_threshold("montant_ttc"):
            derived_taxe = ttc - ht - tva
            if derived_taxe >= Decimal("0"):
                result["montant_taxe"] = _format_amount(derived_taxe)
                computed.add("montant_taxe")

    return result, computed, has_mismatch


def detect_amount_collision(fields: dict[str, Optional[str]]) -> bool:
    """Detect if any two of {montant_ht, montant_tva, montant_taxe, montant_ttc}
    have the identical numeric value (collision).
    
    Returns True if a collision is detected, False otherwise.
    Collisions indicate a likely OCR or extraction error and should cap confidence at 0.5.
    """
    amounts = {}
    for key in ["montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]:
        val = fields.get(key)
        if val:
            parsed = _parse_decimal(val)
            if parsed is not None:
                amounts[key] = parsed
    
    # Check if any two amounts are equal
    amount_values = list(amounts.values())
    for i, v1 in enumerate(amount_values):
        for v2 in amount_values[i+1:]:
            if v1 == v2:
                return True
    return False
