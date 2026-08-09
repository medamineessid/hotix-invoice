import asyncio
import json
import logging
import os
import random
from pathlib import Path
from typing import Any, Optional, Dict, List
from google import genai
from google.genai import errors as genai_errors
from google.genai import types

logger = logging.getLogger(__name__)

# ── Prompt constants ──────────────────────────────────────────────────────────

_GEMINI_PROMPT_FR = """Extrais les informations de cette facture sous forme de JSON uniquement.
Les clés doivent être exactement : numero_facture, date, fournisseur, client, montant_ht, montant_tva, montant_taxe, montant_ttc.
Pour numero_facture : cherche dans le coin supérieur droit ou gauche de la facture. Même s'il n'y a pas de label "N° Facture", il y a souvent un identifiant comme "FAC-2025-001", "2025/042", ou "REF-12345" près de la date ou du logo. Si tu trouves un tel identifiant, retourne-le comme numero_facture.
Extrais également les lignes d'articles si présentes dans un tableau nommé "items".
Chaque article a les clés : designation, quantite, unit, prix_unitaire, tva_rate, montant.
Pour unit, extrais le texte brut de la colonne unité (h., pce., stère, kg, m², etc.) comme string.
Pour tva_rate, utilise le format décimal (ex: 0.20 pour 20%, 0.10 pour 10%, 0.055 pour 5.5%).
Extrais aussi le tableau récapitulatif de TVA (souvent en bas de facture, après les lignes d'articles) dans un tableau "tax_summary".
Chaque ligne de tax_summary a les clés : rate, base_ht, tax_amount (tous en float).
Utilise null si une information est absente. Ne devine jamais.
Si la facture n'a pas de tableau d'articles, mets items à []. Si pas de récapitulatif TVA, mets tax_summary à [].
RÈGLES STRICTES :
- numero_facture, date, fournisseur, client doivent être des strings simples (pas d'objet, pas de nombre sans guillemets).
- Les montants (montant_ht, montant_tva, montant_taxe, montant_ttc) doivent être des nombres (float), jamais des strings.
- Chaque item dans "items" doit avoir designation comme string simple et unit comme string simple.
- Ne retourne JAMAIS un objet là où une string est attendue.
Réponds uniquement avec le JSON."""

_GEMINI_PROMPT_EN = """Extract the information from this invoice as JSON only.
The keys must be exactly: numero_facture, date, fournisseur, client, montant_ht, montant_tva, montant_taxe, montant_ttc.
For numero_facture: look in the top-right or top-left corner of the invoice. Even if there's no "Invoice Number" label, there is often an identifier like "FAC-2025-001", "2025/042", or "REF-12345" near the date or logo. If you find such an identifier, return it as numero_facture.
Also extract line items if present in an array named "items".
Each item has keys: designation, quantite, unit, prix_unitaire, tva_rate, montant.
For unit, extract the raw text from the unit column (h., pce., stère, kg, m², etc.) as a string.
For tva_rate use decimal format (e.g. 0.20 for 20%, 0.10 for 10%, 0.055 for 5.5%).
Also extract the tax summary table (usually at the bottom of the invoice, below the line items) in an array named "tax_summary".
Each tax_summary row has keys: rate, base_ht, tax_amount (all as floats).
Use null if information is missing. Never guess.
If the invoice has no item table, set items to []. If no tax summary, set tax_summary to [].
STRICT RULES:
- numero_facture, date, fournisseur, client must be plain strings (no objects, no unquoted numbers).
- Amounts (montant_ht, montant_tva, montant_taxe, montant_ttc) must be numbers (float), never strings.
- Each item in "items" must have designation as a plain string and unit as a plain string.
- NEVER return an object where a string is expected.
Reply with JSON only."""


# ── Canonical wire schema ───────────────────────────────────────────────────
# These are the exact keys the WPF client deserializes (InvoiceResult.cs /
# InvoiceItem / TaxSummaryRow). The extraction prompts and the normalizers
# below MUST agree with them — server/tests/test_llm_schema_parity.py pins
# this contract across all three LLM paths so a rename cannot drift silently
# (the Bug B drift class that once blanked the tax column).

GEMINI_ITEM_KEYS = ("designation", "quantite", "unit", "prix_unitaire", "tva_rate", "montant")
TAX_SUMMARY_KEYS = ("rate", "base_ht", "tax_amount")


def _get_gemini_prompt() -> str:
    """Return the extraction prompt in the configured language.

    Language is determined by the HOTIX_LANG environment variable.
    Fallback is French (fr).
    """
    lang = os.getenv("HOTIX_LANG", "fr").lower().strip()
    if lang == "en":
        return _GEMINI_PROMPT_EN
    return _GEMINI_PROMPT_FR


class GeminiExtractionError(Exception):
    """Raised when Gemini extraction fails."""
    pass


def _normalize_string_field(value: Any) -> Optional[str]:
    """Force any JSON value into a clean string or None."""
    if value is None:
        return None
    if isinstance(value, str):
        return value.strip() or None
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, dict):
        # Gemini sometimes returns {"text": "..."} or {"value": "..."}
        # Try common keys, fallback to first string value
        for key in ("text", "value", "content", "nom", "name", "designation"):
            if key in value and isinstance(value[key], str):
                return value[key].strip() or None
        # If no known key, convert first string value found
        for v in value.values():
            if isinstance(v, str):
                return v.strip() or None
        return None
    if isinstance(value, list):
        # Flatten array to comma-separated string (e.g. ["Ordinateur", "portable"])
        flat = [str(x) for x in value if x is not None]
        return ", ".join(flat).strip() or None
    return str(value).strip() or None


def _normalize_amount_field(value: Any) -> Optional[float]:
    """Extract a float amount from various Gemini output formats."""
    if value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        # Handle "1 234,56" → 1234.56, "1,234.56" → 1234.56, "1234.56" → 1234.56
        # Strip ALL Unicode whitespace (ASCII, NBSP, thin-space U+202F, ...)
        # so French thousands separators of any kind collapse before
        # separator disambiguation.  Consistent with utils._parse_decimal.
        cleaned = "".join(value.strip().split())
        # French format: "1 234,56" or "1234,56"
        if "," in cleaned and "." not in cleaned:
            cleaned = cleaned.replace(",", ".")
        # US format with comma thousands: "1,234.56" → remove comma
        elif "," in cleaned and "." in cleaned:
            # Determine which is the decimal separator: the LAST occurrence
            last_comma = cleaned.rfind(",")
            last_dot = cleaned.rfind(".")
            if last_comma > last_dot:
                # European: "1.234,56" → decimal is comma
                cleaned = cleaned.replace(".", "").replace(",", ".")
            else:
                # US: "1,234.56" → decimal is dot
                cleaned = cleaned.replace(",", "")
        try:
            return float(cleaned)
        except (ValueError, TypeError):
            return None
    if isinstance(value, dict):
        # Try common amount keys
        for key in ("value", "amount", "montant", "total"):
            if key in value:
                return _normalize_amount_field(value[key])
    return None



def _get_settings_path() -> Path:
    r"""
    Returns the canonical path to the user-writable appsettings.json.

    Prefers %LOCALAPPDATA%\Hotix\appsettings.json (always writable by the current user).
    Falls back to the install-directory location (server/appsettings.json) for backwards
    compatibility with existing installations that haven't been migrated yet.
    """
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        user_path = Path(local_app_data) / "Hotix" / "appsettings.json"
        if user_path.exists():
            return user_path

    # Fallback: check install-directory location (pre-migration installs)
    fallback = Path(__file__).parent / "appsettings.json"
    if fallback.exists():
        return fallback

    # If neither exists, prefer the user-writable path (will be created there)
    if local_app_data:
        user_path = Path(local_app_data) / "Hotix" / "appsettings.json"
        user_path.parent.mkdir(parents=True, exist_ok=True)
        return user_path

    # Last resort: install directory
    return Path(__file__).parent / "appsettings.json"


def load_gemini_api_key() -> Optional[str]:
    """Load API key from environment or appsettings.json."""
    key = os.getenv("GEMINI_API_KEY")
    if key:
        return key
    
    settings_path = _get_settings_path()
    if settings_path.exists():
        try:
            with open(settings_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return data.get("gemini_api_key")
        except Exception as e:
            logger.warning(f"Failed to read {settings_path} for Gemini key: {e}")
    return None


def load_gemini_model() -> str:
    """Load the selected Gemini model from appsettings.json, or return the default."""
    settings_path = _get_settings_path()
    if settings_path.exists():
        try:
            with open(settings_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                model = data.get("gemini_model", "")
                if model:
                    return model
        except Exception as e:
            logger.warning(f"Failed to read gemini_model from {settings_path}: {e}")
    return "gemini-2.5-flash"  # default


async def _call_gemini_with_retry(client, model_name: str, prompt: str, image_data: bytes, mime_type: str, attempt: int = 1, max_attempts: int = 3) -> str:
    """Call Gemini with exponential backoff retry for transient errors (429, 503, timeout).

    Last resort: raises GeminiExtractionError after max_attempts failures.
    """
    try:
        response = client.models.generate_content(
            model=model_name,
            contents=[
                prompt,
                types.Part.from_bytes(data=image_data, mime_type=mime_type),
            ],
            config=types.GenerateContentConfig(
                response_mime_type="application/json",
            ),
        )
        if not response.text:
            raise GeminiExtractionError("Réponse vide de Gemini")
        return response.text
    except GeminiExtractionError:
        raise  # Non-retryable
    except genai_errors.APIError as exc:
        code = getattr(exc, "code", None)
        if code in (429, 503) and attempt < max_attempts:
            delay = min(2 ** attempt, 10) + random.uniform(0, 1)
            logger.warning(
                "Gemini API error %s (attempt %d/%d), retrying in %.1fs...",
                code, attempt, max_attempts, delay,
            )
            await asyncio.sleep(delay)
            return await _call_gemini_with_retry(client, model_name, prompt, image_data, mime_type, attempt + 1, max_attempts)
        if code == 429:
            raise GeminiExtractionError("Quota d'API Gemini dépassé (429)")
        raise GeminiExtractionError(f"Erreur API Gemini: {exc}")
    except Exception as e:
        if "timeout" in str(e).lower() and attempt < max_attempts:
            delay = min(2 ** attempt, 10) + random.uniform(0, 1)
            logger.warning(
                "Gemini timeout (attempt %d/%d), retrying in %.1fs...",
                attempt, max_attempts, delay,
            )
            await asyncio.sleep(delay)
            return await _call_gemini_with_retry(client, model_name, prompt, image_data, mime_type, attempt + 1, max_attempts)
        raise GeminiExtractionError(f"Erreur inattendue lors de l'extraction Gemini: {e}")


def _normalize_item(raw_item: dict[str, Any]) -> dict[str, Any]:
    """Normalize ONE raw Gemini item into the canonical wire format.

    Tolerates the legacy French key "unite" (older prompts / model habits) —
    the OUTPUT always uses "unit", the key the WPF client deserializes.
    """
    return {
        "designation": _normalize_string_field(raw_item.get("designation")),
        "quantite": _normalize_amount_field(raw_item.get("quantite")),
        "unit": _normalize_string_field(raw_item.get("unite") or raw_item.get("unit")),
        "prix_unitaire": _normalize_amount_field(raw_item.get("prix_unitaire")),
        "tva_rate": _normalize_amount_field(raw_item.get("tva_rate")),
        "montant": _normalize_amount_field(raw_item.get("montant")),
    }


def _normalize_items(raw_items: Any) -> list[dict[str, Any]]:
    """Normalize Gemini's raw items payload (list, or dict with numeric keys)
    into the canonical wire format. Handles the single-string-object shape
    Gemini sometimes returns and always emits GEMINI_ITEM_KEYS."""
    parsed_items: list[dict[str, Any]] = []
    if isinstance(raw_items, list):
        for raw_item in raw_items:
            if not isinstance(raw_item, dict):
                continue
            # Handle Gemini returning a single string instead of an object
            if len(raw_item) == 1 and isinstance(list(raw_item.values())[0], str):
                key = list(raw_item.keys())[0]
                raw_item = {"designation": raw_item[key]}
            parsed_items.append(_normalize_item(raw_item))
    elif isinstance(raw_items, dict):
        # Gemini sometimes returns items as a dict with numeric keys
        for key in sorted(raw_items.keys()):
            item = raw_items[key]
            if isinstance(item, dict):
                parsed_items.append(_normalize_item(item))
    return parsed_items


def _normalize_tax_summary(raw_tax: Any) -> list[dict[str, Any]]:
    """Normalize Gemini's raw tax_summary payload into the canonical wire
    format (TAX_SUMMARY_KEYS)."""
    parsed_tax: list[dict[str, Any]] = []
    if isinstance(raw_tax, list):
        for row in raw_tax:
            if isinstance(row, dict):
                parsed_tax.append({
                    "rate": _normalize_amount_field(row.get("rate")),
                    "base_ht": _normalize_amount_field(row.get("base_ht")),
                    "tax_amount": _normalize_amount_field(row.get("tax_amount")),
                })
    return parsed_tax


async def extract_with_gemini(image_data: bytes, mime_type: str) -> Dict[str, Any]:
    """Extract invoice fields (and optionally line items) using Gemini Vision.

    Returns a dict with the 8 standard fields plus an optional "items" key
    containing a list of item dicts: {designation, quantite, prix_unitaire,
    tva_rate, montant}. Items may be an empty list if none found.
    """
    api_key = load_gemini_api_key()
    if not api_key:
        raise GeminiExtractionError("Clé API Gemini non configurée")

    model_name = load_gemini_model()
    client = genai.Client(api_key=api_key)
    prompt = _get_gemini_prompt()

    try:
        response_text = await _call_gemini_with_retry(client, model_name, prompt, image_data, mime_type)

        # Strip markdown fences if present
        content = response_text.strip()
        if content.startswith("```json"):
            content = content[7:]
        if content.endswith("```"):
            content = content[:-3]
        content = content.strip()

        data = json.loads(content)
        
        # Verify required keys (items is optional)
        required_keys = ["numero_facture", "date", "fournisseur", "client", "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]
        for key in required_keys:
            if key not in data:
                 raise GeminiExtractionError(f"Clé manquante dans la réponse JSON: {key}")

        # Build result dict with robust normalization for each field type
        result: Dict[str, Any] = {}
        for key in required_keys:
            value = data.get(key)
            if key in ("montant_ht", "montant_tva", "montant_taxe", "montant_ttc"):
                result[key] = _normalize_amount_field(value)
            else:
                result[key] = _normalize_string_field(value)

        # Parse items array with robust normalization (optional — may be missing or empty)
        result["items"] = _normalize_items(data.get("items", []))

        # Parse tax_summary array (optional per-rate VAT breakdown)
        result["tax_summary"] = _normalize_tax_summary(data.get("tax_summary", []))

        return result

    except json.JSONDecodeError:
        logger.error("Gemini returned non-JSON response: %s", response_text[:500])
        raise GeminiExtractionError("Échec de l'analyse du JSON renvoyé par Gemini")
    except GeminiExtractionError:
        raise
    except Exception as e:
        logger.error("Gemini extraction failed. Raw response: %s", response_text[:1000])
        raise GeminiExtractionError(f"Erreur inattendue lors de l'extraction Gemini: {e}")
