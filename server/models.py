"""Pydantic models for the HOTIX invoice extraction API."""

from __future__ import annotations

from typing import Literal, Optional

from pydantic import BaseModel, ConfigDict, Field


class InvoiceExtractionResponse(BaseModel):
    """Response payload returned by POST /extract."""

    model_config = ConfigDict(extra="forbid")

    numero_facture: Optional[str] = Field(default=None)
    date: Optional[str] = Field(default=None)
    fournisseur: Optional[str] = Field(default=None)
    client: Optional[str] = Field(default=None)
    montant_ht: Optional[str] = Field(default=None)
    montant_tva: Optional[str] = Field(default=None)
    montant_taxe: Optional[str] = Field(default=None)
    montant_ttc: Optional[str] = Field(default=None)
    confidence: float = Field(default=0.0, ge=0.0, le=1.0)
    raw_text: str = Field(default="")
    engine_used: Literal["gemini", "ocr"] = Field(default="ocr")
    gemini_fallback_reason: Optional[str] = Field(
        default=None,
        description="If engine_used is 'ocr' but Gemini was tried first, contains the Gemini error reason.",
    )
    computed_fields: list[str] = Field(
        default_factory=list,
        description="Field names whose values were computed arithmetically rather than OCR-read.",
    )
    amount_mismatch: bool = Field(
        default=False,
        description="True when all 3 amounts (HT, TVA, TTC) are present but arithmetic is inconsistent.",
    )


class HealthResponse(BaseModel):
    """Response payload returned by GET /health."""

    model_config = ConfigDict(extra="forbid")

    status: str = Field(default="ok")


class ErrorResponse(BaseModel):
    """Structured error response for predictable API failures."""

    model_config = ConfigDict(extra="forbid")

    detail: str
