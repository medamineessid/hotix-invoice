import os
import json
import logging
from pathlib import Path
from typing import Any, Optional, Dict, List
from google import genai
from google.genai import errors as genai_errors
from google.genai import types

logger = logging.getLogger(__name__)

class GeminiExtractionError(Exception):
    """Raised when Gemini extraction fails."""
    pass


def _safe_float(value: Any) -> Optional[float]:
    """Safely convert a value to float, returning None on failure."""
    if value is None:
        return None
    try:
        return float(value)
    except (ValueError, TypeError):
        return None


def _get_settings_path() -> Path:
    """
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

def extract_with_gemini(image_data: bytes, mime_type: str) -> Dict[str, Any]:
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

    prompt = """Extrais les informations de cette facture sous forme de JSON uniquement.
Les clés doivent être exactement : numero_facture, date, fournisseur, client, montant_ht, montant_tva, montant_taxe, montant_ttc.
Extrais également les lignes d'articles si présentes dans un tableau nommé "items".
Chaque article a les clés : designation, quantite, prix_unitaire, tva_rate, montant.
Utilise null si une information est absente. Ne devine jamais.
Si la facture n'a pas de tableau d'articles, mets items à [].
Réponds uniquement avec le JSON."""

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

        # Strip markdown fences if present
        content = response.text.strip()
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

        # Build result dict: stringify the 8 flat fields
        result: Dict[str, Any] = {
            k: (str(v) if v is not None else None)
            for k, v in data.items() if k in required_keys
        }

        # Parse items array (optional — may be missing or empty)
        raw_items = data.get("items", [])
        if isinstance(raw_items, list):
            parsed_items: list[dict[str, Any]] = []
            for raw_item in raw_items:
                if not isinstance(raw_item, dict):
                    continue
                parsed_items.append({
                    "designation": str(raw_item.get("designation") or "") if raw_item.get("designation") else None,
                    "quantite": _safe_float(raw_item.get("quantite")),
                    "prix_unitaire": _safe_float(raw_item.get("prix_unitaire")),
                    "tva_rate": _safe_float(raw_item.get("tva_rate")),
                    "montant": _safe_float(raw_item.get("montant")),
                })
            result["items"] = parsed_items
        else:
            result["items"] = []

        return result

    except genai_errors.APIError as exc:
        if getattr(exc, "code", None) == 429:
            raise GeminiExtractionError("Quota d'API Gemini dépassé (429)")
        raise GeminiExtractionError(f"Erreur API Gemini: {exc}")
    except json.JSONDecodeError:
        raise GeminiExtractionError("Échec de l'analyse du JSON renvoyé par Gemini")
    except Exception as e:
        if "timeout" in str(e).lower():
            raise GeminiExtractionError("Délai d'attente dépassé pour Gemini")
        raise GeminiExtractionError(f"Erreur inattendue lors de l'extraction Gemini: {e}")
