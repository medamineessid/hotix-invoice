"""Shared utilities for HOTIX invoice extraction."""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal, InvalidOperation
from typing import Iterable, Sequence


@dataclass(frozen=True)
class BoundingBox:
    """Axis-aligned bounding box in image coordinates."""

    x1: float
    y1: float
    x2: float
    y2: float

    @classmethod
    def from_points(cls, points: Sequence[Sequence[float]]) -> "BoundingBox":
        """Build a bounding box from polygon points returned by PaddleOCR."""

        xs = [float(point[0]) for point in points]
        ys = [float(point[1]) for point in points]
        return cls(min(xs), min(ys), max(xs), max(ys))

    @property
    def width(self) -> float:
        """Return the box width."""

        return max(0.0, self.x2 - self.x1)

    @property
    def height(self) -> float:
        """Return the box height."""

        return max(0.0, self.y2 - self.y1)

    @property
    def center_x(self) -> float:
        """Return the horizontal center."""

        return (self.x1 + self.x2) / 2.0

    @property
    def center_y(self) -> float:
        """Return the vertical center."""

        return (self.y1 + self.y2) / 2.0

    def vertical_overlap(self, other: "BoundingBox") -> float:
        """Return the vertical overlap between two boxes."""

        return max(0.0, min(self.y2, other.y2) - max(self.y1, other.y1))

    def horizontal_overlap(self, other: "BoundingBox") -> float:
        """Return the horizontal overlap between two boxes."""

        return max(0.0, min(self.x2, other.x2) - max(self.x1, other.x1))

    def horizontal_gap(self, other: "BoundingBox") -> float:
        """Return the horizontal gap between two boxes."""

        if other.x1 >= self.x2:
            return other.x1 - self.x2
        if self.x1 >= other.x2:
            return self.x1 - other.x2
        return 0.0

    def vertical_gap(self, other: "BoundingBox") -> float:
        """Return the vertical gap between two boxes."""

        if other.y1 >= self.y2:
            return other.y1 - self.y2
        if self.y1 >= other.y2:
            return self.y1 - other.y2
        return 0.0


@dataclass(frozen=True)
class OCRLine:
    """A single OCR text line and its geometry."""

    text: str
    box: BoundingBox
    confidence: float
    page_index: int
    line_index: int


AMOUNT_KEEP_PATTERN = re.compile(r"[^0-9,\.-]")
MULTISPACE_PATTERN = re.compile(r"\s+")
DATE_PATTERN = re.compile(r"\b(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})\b")
DATE_YMD_PATTERN = re.compile(r"\b(\d{4})[/-](\d{1,2})[/-](\d{1,2})\b")
FRENCH_MONTH_PATTERN = re.compile(r"\b(\d{1,2})\s+([a-zA-ZÀ-ÿ]+)\s+(\d{4})\b")
FRENCH_MONTH_PATTERN_ALT = re.compile(r"\b([a-zA-ZÀ-ÿ]+)\s+(\d{1,2}),?\s+(\d{4})\b")

FRENCH_MONTHS = {
    "janvier": 1,
    "fevrier": 2,
    "février": 2,
    "mars": 3,
    "avril": 4,
    "mai": 5,
    "juin": 6,
    "juillet": 7,
    "aout": 8,
    "août": 8,
    "septembre": 9,
    "octobre": 10,
    "novembre": 11,
    "decembre": 12,
    "décembre": 12,
}


def normalize_text(value: str) -> str:
    """Normalize text for keyword matching."""

    normalized = unicodedata.normalize("NFKD", value)
    stripped = "".join(character for character in normalized if not unicodedata.combining(character))
    stripped = stripped.replace("\u00a0", " ").lower()
    return MULTISPACE_PATTERN.sub(" ", stripped).strip()


def collapse_text(value: str) -> str:
    """Collapse whitespace without altering the content otherwise."""

    return MULTISPACE_PATTERN.sub(" ", value.replace("\u00a0", " ")).strip()


def strip_accents(value: str) -> str:
    """Return an accent-free version of the supplied text."""

    normalized = unicodedata.normalize("NFKD", value)
    return "".join(character for character in normalized if not unicodedata.combining(character))


def looks_like_latin_text(value: str) -> bool:
    """Return True when the text contains Latin letters useful for French invoices."""

    stripped = strip_accents(value)
    return any("A" <= character <= "Z" or "a" <= character <= "z" for character in stripped)


def clean_amount(value: str) -> str | None:
    """Normalize a monetary amount to a string with three decimals.

    The parser accepts French-style amounts where the comma is the decimal separator and the period is a thousands separator.
    """

    if not value:
        return None

    candidate = collapse_text(value)
    candidate = candidate.replace("DT", " ").replace("TND", " ").replace("EUR", " ")
    candidate = candidate.replace("€", " ").replace("$", " ").replace("£", " ")
    candidate = AMOUNT_KEEP_PATTERN.sub("", candidate)
    if not candidate:
        return None

    if "," in candidate and "." in candidate:
        decimal_separator = "," if candidate.rfind(",") > candidate.rfind(".") else "."
        normalized = candidate.replace(".", "").replace(",", ".") if decimal_separator == "," else candidate.replace(",", "")
    elif "," in candidate:
        normalized = candidate.replace(".", "").replace(",", ".")
    elif "." in candidate:
        if re.fullmatch(r"\d{1,3}(\.\d{3})+", candidate):
            normalized = candidate.replace(".", "")
        else:
            normalized = candidate
    else:
        normalized = candidate

    try:
        amount = Decimal(normalized)
    except InvalidOperation:
        return None

    return f"{amount.quantize(Decimal('0.001')):.3f}"


def clean_date(value: str) -> str | None:
    """Normalize a date to YYYY-MM-DD when possible."""

    if not value:
        return None

    candidate = normalize_text(value)
    numeric_date = _parse_numeric_date(candidate)
    if numeric_date is not None:
        return numeric_date

    month_date = _parse_month_name_date(candidate)
    if month_date is not None:
        return month_date

    return None


def normalize_text_for_output(value: str) -> str:
    """Return a compact text value suitable for output fields."""

    return collapse_text(value).strip(" :-\t\r\n")


def _parse_numeric_date(candidate: str) -> str | None:
    """Parse numeric date formats such as DD/MM/YYYY or YYYY-MM-DD.

    Assumes DD/MM/YYYY order (French locale). If month part exceeds 12 and
    day part does not, values are swapped to recover a valid date.
    """

    match = DATE_PATTERN.search(candidate)
    if match is not None:
        day, month, year = match.groups()
        year_value = int(year)
        if year_value < 100:
            year_value += 2000
        day_int, month_int = int(day), int(month)
        if month_int > 12 and day_int <= 12:
            day_int, month_int = month_int, day_int
        try:
            return datetime(year_value, month_int, day_int).date().isoformat()
        except ValueError:
            return None

    match = DATE_YMD_PATTERN.search(candidate)
    if match is not None:
        year, month, day = match.groups()
        try:
            return datetime(int(year), int(month), int(day)).date().isoformat()
        except ValueError:
            return None

    return None


def _parse_month_name_date(candidate: str) -> str | None:
    """Parse dates containing French month names."""

    match = FRENCH_MONTH_PATTERN.search(candidate)
    if match is not None:
        day, month_name, year = match.groups()
        month_number = FRENCH_MONTHS.get(strip_accents(month_name).lower())
        if month_number is None:
            return None
        try:
            return datetime(int(year), month_number, int(day)).date().isoformat()
        except ValueError:
            return None

    match = FRENCH_MONTH_PATTERN_ALT.search(candidate)
    if match is not None:
        month_name, day, year = match.groups()
        month_number = FRENCH_MONTHS.get(strip_accents(month_name).lower())
        if month_number is None:
            return None
        try:
            return datetime(int(year), month_number, int(day)).date().isoformat()
        except ValueError:
            return None

    return None


def deduplicate_preserve_order(items: Iterable[str]) -> list[str]:
    """Deduplicate items while preserving the first occurrence order."""

    return list(dict.fromkeys(items))
