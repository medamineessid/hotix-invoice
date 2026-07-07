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


INVOICE_FIELD_NAMES: tuple[str, ...] = (
    "numero_facture",
    "date",
    "fournisseur",
    "client",
    "montant_ht",
    "montant_tva",
    "montant_taxe",
    "montant_ttc",
)

STRIP_CHARS = " :-\t\r\n"
AMOUNT_CLEANER = re.compile(r"[^\d,\.\-]")
MULTISPACE = re.compile(r"\s+")
DATE_DD_MM_YYYY = re.compile(r"\b(\d{1,2})[\/\-](\d{1,2})[\/\-](\d{2,4})\b")
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
    cleaned = re.sub(r"[^\d,\.\-\s]", "", cleaned).strip()

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
    except InvalidOperation:
        return None

    return f"{amount.quantize(Decimal('0.001')):.3f}"


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
