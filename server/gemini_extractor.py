import os
import json
import logging
from pathlib import Path
from typing import Optional, Dict, Any
from google import genai
from google.genai import errors as genai_errors
from google.genai import types

logger = logging.getLogger(__name__)

class GeminiExtractionError(Exception):
    """Raised when Gemini extraction fails."""
    pass

def load_gemini_api_key() -> Optional[str]:
    """Load API key from environment or appsettings.json."""
    key = os.getenv("GEMINI_API_KEY")
    if key:
        return key
    
    settings_path = Path(__file__).parent / "appsettings.json"
    if settings_path.exists():
        try:
            with open(settings_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return data.get("gemini_api_key")
        except Exception as e:
            logger.warning(f"Failed to read appsettings.json for Gemini key: {e}")
    return None


def load_gemini_model() -> str:
    """Load the selected Gemini model from appsettings.json, or return the default."""
    settings_path = Path(__file__).parent / "appsettings.json"
    if settings_path.exists():
        try:
            with open(settings_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                model = data.get("gemini_model", "")
                if model:
                    return model
        except Exception as e:
            logger.warning(f"Failed to read gemini_model from appsettings.json: {e}")
    return "gemini-2.5-flash"  # default

def extract_with_gemini(image_data: bytes, mime_type: str) -> Dict[str, Optional[str]]:
    """Extract invoice fields using Gemini Vision."""
    api_key = load_gemini_api_key()
    if not api_key:
        raise GeminiExtractionError("Clé API Gemini non configurée")

    model_name = load_gemini_model()
    client = genai.Client(api_key=api_key)

    # Only 8 fields requested: numero_facture, date, fournisseur, client, montant_ht, montant_tva, montant_taxe, montant_ttc
    prompt = """Extrais les informations de cette facture sous forme de JSON uniquement.
Les clés doivent être exactement : numero_facture, date, fournisseur, client, montant_ht, montant_tva, montant_taxe, montant_ttc.
Utilise null si une information est absente. Ne devine jamais.
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
        
        # Verify required keys
        required_keys = ["numero_facture", "date", "fournisseur", "client", "montant_ht", "montant_tva", "montant_taxe", "montant_ttc"]
        for key in required_keys:
            if key not in data:
                 raise GeminiExtractionError(f"Clé manquante dans la réponse JSON: {key}")

        # Return the fields, non-string values to None
        return {k: (str(v) if v is not None else None) for k, v in data.items() if k in required_keys}

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
