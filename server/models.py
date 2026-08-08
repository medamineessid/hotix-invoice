"""Pydantic models for the HOTIX invoice extraction API."""

from __future__ import annotations

from typing import Any, Literal, Optional

from pydantic import BaseModel, ConfigDict, Field


class InvoiceItem(BaseModel):
    """A single line item from an invoice."""
    model_config = ConfigDict(extra="forbid")
    designation: Optional[str] = Field(default=None, description="Item description / product name")
    quantite: Optional[float] = Field(default=None, description="Quantity")
    unit: Optional[str] = Field(default=None, description="Unit of measure (h., pce., stère, kg, etc.)")
    prix_unitaire: Optional[float] = Field(default=None, description="Unit price")
    tva_rate: Optional[float] = Field(default=None, description="VAT rate (e.g. 0.20 for 20%)")
    montant: Optional[float] = Field(default=None, description="Line total (usually qty × unit price)")


class ApiKeyValidationRequest(BaseModel):
    """Request payload for API key validation endpoints."""
    model_config = ConfigDict(extra="forbid")
    api_key: str = Field(..., min_length=10, max_length=512, description="API key to validate")


class TvaSummaryRow(BaseModel):
    """A single row from the tax summary table (per-rate VAT breakdown)."""
    model_config = ConfigDict(extra="forbid")
    rate: Optional[float] = Field(default=None, description="VAT rate as decimal (e.g. 0.20 for 20%)")
    base_ht: Optional[float] = Field(default=None, description="Taxable base (HT) for this rate")
    tva_amount: Optional[float] = Field(default=None, description="VAT amount for this rate")


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
    items: list[InvoiceItem] = Field(
        default_factory=list,
        description="Line items extracted from the invoice table (may be empty if no table found).",
    )
    field_confidences: dict[str, float] = Field(
        default_factory=dict,
        description="Per-field confidence scores from the OCR engine (0.0-1.0). Keys match the 8 standard field names.",
    )
    tax_summary: list[TvaSummaryRow] = Field(
        default_factory=list,
        description="Per-rate VAT breakdown (tax summary table below line items).",
    )


class HealthResponse(BaseModel):
    """Detailed health check response returned by GET /health."""

    model_config = ConfigDict(extra="forbid")

    status: str = Field(default="ok", description="Overall status: ok or degraded")
    ocr_ready: bool = Field(default=False, description="OCR engine is instantiated")
    ocr_model_loaded: bool = Field(default=False, description="OCR model weights are in memory")
    poppler_available: bool = Field(default=False, description="Poppler (pdftoppm/pdfinfo) is accessible")
    gemini_configured: bool = Field(default=False, description="Gemini API key is configured")
    grok_configured: bool = Field(default=False, description="Grok/xAI API key is configured")
    version: str = Field(default="1.0.0", description="API version")



class ErrorResponse(BaseModel):
    """Structured error response for predictable API failures."""

    model_config = ConfigDict(extra="forbid")

    detail: str
