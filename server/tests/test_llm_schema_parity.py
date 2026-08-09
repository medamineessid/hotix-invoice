"""Regression tests for the LLM schema-parity contract.

The three LLM extraction paths — client Gemini direct, client Grok direct,
and server Gemini (via /extract?engine=gemini) — must all declare the SAME
wire-format field names, because the WPF client deserializes by exact name.
A rename on one side drifts silently and blanks a column in the UI/export
(the tva_amount/tax_amount bug class; a real unite/unit drift existed in the
server Gemini prompt before this test was written — the prompt told the model
to return "unite" while the client schema, both client prompts, and the
server normalizer all use "unit").

The client is C#, so this cross-boundary test reads the client's prompt
strings from client/Resources/strings.json and the server's prompt constants
(_GEMINI_PROMPT_FR/_GEMINI_PROMPT_EN) and asserts they agree on every key the
client deserializes. Importing the server module is safe here: conftest.py
already imports server.main (which imports gemini_extractor) and the CI
installs google-genai, so the whole suite shares that dependency.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from server.gemini_extractor import (
    GEMINI_ITEM_KEYS,
    TAX_SUMMARY_KEYS,
    _GEMINI_PROMPT_EN,
    _GEMINI_PROMPT_FR,
    _normalize_items,
    _normalize_tax_summary,
)

ROOT = Path(__file__).resolve().parents[2]

# The exact top-level keys the client deserializes (InvoiceResult.cs).
TOP_LEVEL_KEYS = (
    "numero_facture", "date", "fournisseur", "client",
    "montant_ht", "montant_tva", "montant_taxe", "montant_ttc",
)


def _word_token(text: str, key: str) -> bool:
    """True if `key` appears as a standalone word token (so "unit" matches
    but "unite"/"montant_ht" do not)."""
    return re.search(rf"\b{re.escape(key)}\b", text) is not None


def _client_prompts() -> dict[str, str]:
    """Load the two client-direct prompts from the WPF resources file."""
    strings = json.loads((ROOT / "client" / "Resources" / "strings.json").read_text(encoding="utf-8"))
    return {
        "client-Gemini": strings["GeminiExtractionText"],
        "client-Grok": strings["GrokExtractionText"],
    }


def _server_prompts() -> dict[str, str]:
    """The two server-side Gemini prompt variants (FR + EN)."""
    return {"_GEMINI_PROMPT_FR": _GEMINI_PROMPT_FR, "_GEMINI_PROMPT_EN": _GEMINI_PROMPT_EN}


def _all_prompts() -> dict[str, str]:
    return {**_client_prompts(), **_server_prompts()}


def test_all_three_paths_declare_top_level_keys():
    """The 8 top-level invoice keys must be declared by every path."""
    for name, prompt in _all_prompts().items():
        for key in TOP_LEVEL_KEYS:
            assert _word_token(prompt, key), \
                f"[{name}] prompt does not declare top-level key '{key}'"


def test_all_three_paths_declare_canonical_item_keys():
    """Item keys must be {designation, quantite, unit, prix_unitaire,
    tva_rate, montant} everywhere — this is the test that caught the real
    server-prompt drift (it said 'unite' instead of 'unit')."""
    for name, prompt in _all_prompts().items():
        for key in GEMINI_ITEM_KEYS:
            assert _word_token(prompt, key), \
                f"[{name}] prompt does not declare item key '{key}'"


def test_all_three_paths_declare_canonical_tax_summary_keys():
    """Tax-summary keys must be {rate, base_ht, tax_amount} everywhere
    (the tva_amount/tax_amount bug was this exact drift class)."""
    for name, prompt in _all_prompts().items():
        for key in TAX_SUMMARY_KEYS:
            assert _word_token(prompt, key), \
                f"[{name}] prompt does not declare tax_summary key '{key}'"


def test_server_prompt_no_longer_says_unite():
    """The server prompt must not drift back to the French key 'unite'
    (the normalizer tolerates it as INPUT, but the prompt must declare the
    wire key 'unit' so the model returns canonical output)."""
    for name, prompt in _server_prompts().items():
        assert not _word_token(prompt, "unite"), \
            f"[{name}] prompt still declares legacy key 'unite' (must be 'unit')"


def test_normalizer_always_emits_canonical_item_keys():
    """Whatever shape the model returns (list, dict, legacy 'unite' key),
    the server normalizer must emit exactly the canonical wire keys."""
    raw = [
        {"designation": "Widget", "quantite": 2, "unite": "pce.", "prix_unitaire": 50.0,
         "tva_rate": 0.20, "montant": 100.0},
        {"designation": "Stère de bois", "quantite": 1, "unit": "stère", "prix_unitaire": 30.0,
         "tva_rate": 0.10, "montant": 30.0},
    ]
    items = _normalize_items(raw)
    assert len(items) == 2
    for item in items:
        assert tuple(item.keys()) == GEMINI_ITEM_KEYS, \
            f"Normalizer drifted: {tuple(item.keys())} != {GEMINI_ITEM_KEYS}"
    # Legacy input key "unite" must be canonicalized to "unit".
    assert items[0]["unit"] == "pce."
    assert items[1]["unit"] == "stère"


def test_normalizer_handles_dict_shaped_items():
    """Gemini sometimes returns items as a dict with numeric keys."""
    items = _normalize_items({"0": {"designation": "A", "quantite": 1, "montant": 5.0},
                              "1": {"designation": "B", "quantite": 3, "montant": 15.0}})
    assert [i["designation"] for i in items] == ["A", "B"]
    assert all(tuple(i.keys()) == GEMINI_ITEM_KEYS for i in items)


def test_normalizer_tax_summary_emits_canonical_keys():
    rows = _normalize_tax_summary([
        {"rate": 0.20, "base_ht": 1000.0, "tax_amount": 200.0},
        {"rate": 0.10, "base_ht": 500.0, "tax_amount": 50.0},
    ])
    assert len(rows) == 2
    assert all(tuple(r.keys()) == TAX_SUMMARY_KEYS for r in rows)
