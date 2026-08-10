"""Regression tests for runtime dependency and CI-install parity."""

from __future__ import annotations

import importlib
import re
from pathlib import Path


ROOT_DIR = Path(__file__).resolve().parents[2]
REQUIREMENTS_PATH = ROOT_DIR / "requirements.txt"
WORKFLOW_PATH = ROOT_DIR / ".github" / "workflows" / "build-check.yml"


# These imports cover the runtime modules used by the FastAPI, ingestion,
# OCR, cloud-extraction, and monitoring paths.  Keep the mapping explicit so
# a dependency can be added to requirements.txt without silently disappearing
# from the test environment.
REQUIRED_IMPORTS = {
    "fastapi": "fastapi",
    "uvicorn": "uvicorn",
    "python-multipart": "multipart",
    "pydantic": "pydantic",
    "Pillow": "PIL",
    "pdf2image": "pdf2image",
    "paddleocr": "paddleocr",
    "paddlepaddle": "paddle",
    "openpyxl": "openpyxl",
    "numpy": "numpy",
    "google-genai": "google.genai",
    "httpx": "httpx",
    "aiofiles": "aiofiles",
    "sentry-sdk": "sentry_sdk",
    "psutil": "psutil",
}


def _requirement_names() -> set[str]:
    """Return normalized project names declared in requirements.txt."""
    names: set[str] = set()
    for raw_line in REQUIREMENTS_PATH.read_text(encoding="utf-8").splitlines():
        line = raw_line.split("#", 1)[0].strip()
        if not line:
            continue
        match = re.match(r"^([A-Za-z0-9_.-]+)", line)
        assert match, f"Cannot parse requirement line: {raw_line!r}"
        names.add(match.group(1).lower().replace("_", "-"))
    return names


def test_ci_installs_the_repository_runtime_requirements_file() -> None:
    """CI must install requirements.txt, not a drifting hand-maintained subset."""
    workflow = WORKFLOW_PATH.read_text(encoding="utf-8")
    assert "pip install -r requirements.txt pytest" in workflow

    declared = _requirement_names()
    expected = {name.lower().replace("_", "-") for name in REQUIRED_IMPORTS}
    assert expected <= declared


def test_runtime_dependencies_required_by_server_tests_are_importable() -> None:
    """The runtime dependency set used by the server test paths must import."""
    declared = _requirement_names()
    for package_name, module_name in REQUIRED_IMPORTS.items():
        assert package_name.lower().replace("_", "-") in declared
        importlib.import_module(module_name)
