#!/usr/bin/env python3
"""HOTIX translation-key integrity check (CI).

Verifies:
  1. EN (strings.json) and FR (strings.fr.json) contain the exact same key set.
  2. Every translation key referenced from C# code (TranslationSource.Get/Fmt/Instance)
     and from XAML bindings (Path=[Key]) actually exists in both locale files.

Fails with a non-zero exit code and a list of problems otherwise.
Run from the repository root:  python scripts/check_translations.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CLIENT_DIR = ROOT / "client"
EN_PATH = CLIENT_DIR / "Resources" / "strings.json"
FR_PATH = CLIENT_DIR / "Resources" / "strings.fr.json"

# Patterns for keys referenced in C# code
CS_PATTERNS = [
    re.compile(r'TranslationSource\.Get\("([^"]+)"\)'),
    re.compile(r'TranslationSource\.Fmt\("([^"]+)"'),
    re.compile(r'TranslationSource\.Instance\["([^"]+)"\]'),
    re.compile(r'TranslationSource\.T\["([^"]+)"\]'),
    re.compile(r'\bT\["([^"]+)"\]'),
]

# Pattern for keys referenced in XAML: Path=[Key]
# NOTE: regex-based scanning has known blind spots by design — multi-line
# calls (TranslationSource.Get(\n "Key")) and "Path = [Key]" with spaces are
# not matched. Keep the codebase's single-line style to preserve coverage.
XAML_PATTERN = re.compile(r'Path=\[([^\]]+)\]')


def load_json(path: Path) -> dict[str, str]:
    try:
        with path.open(encoding="utf-8") as f:
            data = json.load(f)
    except json.JSONDecodeError as e:
        print(f"ERROR: {path.name} is not valid JSON: {e}")
        sys.exit(1)
    if not isinstance(data, dict):
        print(f"ERROR: {path.name} is not a JSON object")
        sys.exit(1)
    return data


def collect_cs_keys() -> set[str]:
    keys: set[str] = set()
    for cs_file in CLIENT_DIR.rglob("*.cs"):
        if "obj" in cs_file.parts or "bin" in cs_file.parts:
            continue
        text = cs_file.read_text(encoding="utf-8", errors="replace")
        for pattern in CS_PATTERNS:
            for match in pattern.finditer(text):
                keys.add(match.group(1))
    return keys


def collect_xaml_keys() -> set[str]:
    keys: set[str] = set()
    for xaml_file in CLIENT_DIR.rglob("*.xaml"):
        if "obj" in xaml_file.parts or "bin" in xaml_file.parts:
            continue
        text = xaml_file.read_text(encoding="utf-8", errors="replace")
        for match in XAML_PATTERN.finditer(text):
            keys.add(match.group(1))
    return keys


def main() -> int:
    en = load_json(EN_PATH)
    fr = load_json(FR_PATH)

    errors: list[str] = []

    # 1. Key-set parity
    en_only = set(en) - set(fr)
    fr_only = set(fr) - set(en)
    if en_only:
        errors.append(f"Keys present in EN but missing in FR ({len(en_only)}): {sorted(en_only)}")
    if fr_only:
        errors.append(f"Keys present in FR but missing in EN ({len(fr_only)}): {sorted(fr_only)}")

    # 2. Referenced keys exist in both locales
    referenced = collect_cs_keys() | collect_xaml_keys()
    for key in sorted(referenced):
        if key not in en:
            errors.append(f"Referenced key '{key}' is missing from strings.json")
        if key not in fr:
            errors.append(f"Referenced key '{key}' is missing from strings.fr.json")

    if errors:
        print(f"TRANSLATION CHECK FAILED ({len(errors)} problem(s)):")
        for err in errors:
            print(f"  - {err}")
        return 1

    print(f"TRANSLATION CHECK OK: {len(en)} keys in both locales, "
          f"{len(referenced)} referenced keys all resolve.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
