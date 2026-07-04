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


class HealthResponse(BaseModel):
    """Response payload returned by GET /health."""

    model_config = ConfigDict(extra="forbid")

    status: str = Field(default="ok")


class ErrorResponse(BaseModel):
    """Structured error response for predictable API failures."""

    model_config = ConfigDict(extra="forbid")

    detail: str
