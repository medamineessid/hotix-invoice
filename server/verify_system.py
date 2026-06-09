"""Verify the local HOTIX invoice extraction environment."""

from __future__ import annotations

import importlib
import shutil
import sys
from pathlib import Path


REQUIRED_MODULES = (
    "fastapi",
    "uvicorn",
    "pydantic",
    "paddleocr",
    "paddle",
    "PIL",
    "pdf2image",
    "openpyxl",
)


def check_module(name: str) -> tuple[bool, str]:
    """Check whether a module can be imported."""

    try:
        importlib.import_module(name)
        return True, "ok"
    except Exception as exc:
        return False, str(exc)


def check_poppler() -> tuple[bool, str]:
    """Check whether Poppler utilities are discoverable on PATH."""

    for binary in ("pdftoppm", "pdfinfo"):
        if shutil.which(binary):
            continue
        return False, f"{binary} not found on PATH"
    return True, "ok"


def main() -> int:
    """Print environment diagnostics and return a process exit code."""

    print(f"Python: {sys.version.split()[0]}")
    print(f"Executable: {sys.executable}")
    print(f"Workspace: {Path.cwd()}")

    exit_code = 0
    for module_name in REQUIRED_MODULES:
        ok, message = check_module(module_name)
        status = "OK" if ok else "FAIL"
        print(f"{status} module {module_name}: {message}")
        if not ok:
            exit_code = 1

    poppler_ok, poppler_message = check_poppler()
    print(f"{'OK' if poppler_ok else 'FAIL'} poppler: {poppler_message}")
    if not poppler_ok:
        exit_code = 1

    try:
        from paddleocr import PaddleOCR

        _ = PaddleOCR(lang="fr", use_angle_cls=True, show_log=False)
        print("OK paddleocr init: success")
    except Exception as exc:
        print(f"FAIL paddleocr init: {exc}")
        exit_code = 1

    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
"""Validate the local environment for HOTIX invoice extraction."""

from __future__ import annotations

import importlib
import shutil
import sys
from dataclasses import dataclass
from typing import Iterable


REQUIRED_MODULES = (
    "fastapi",
    "uvicorn",
    "paddleocr",
    "paddlepaddle",
    "pdf2image",
    "PIL",
    "openpyxl",
)


@dataclass(frozen=True)
class CheckResult:
    """Capture the result of a single verification step."""

    name: str
    ok: bool
    message: str


def check_python_version() -> CheckResult:
    """Verify that Python 3.12 or newer is available."""

    version = sys.version_info
    ok = version.major == 3 and version.minor >= 12
    return CheckResult(
        name="Python version",
        ok=ok,
        message=f"{version.major}.{version.minor}.{version.micro}",
    )


def check_module(module_name: str) -> CheckResult:
    """Verify that a Python module can be imported."""

    try:
        importlib.import_module(module_name)
        return CheckResult(name=f"Import {module_name}", ok=True, message="ok")
    except Exception as exc:
        return CheckResult(name=f"Import {module_name}", ok=False, message=str(exc))


def check_poppler() -> CheckResult:
    """Verify that the Poppler command-line tools are available."""

    pdfinfo = shutil.which("pdfinfo")
    pdftoppm = shutil.which("pdftoppm")
    ok = pdfinfo is not None and pdftoppm is not None
    message = f"pdfinfo={pdfinfo or 'missing'}, pdftoppm={pdftoppm or 'missing'}"
    return CheckResult(name="Poppler tools", ok=ok, message=message)


def check_ocr_runtime() -> CheckResult:
    """Verify that PaddleOCR can be instantiated."""

    try:
        from paddleocr import PaddleOCR

        PaddleOCR(lang="fr", use_angle_cls=True, show_log=False)
        return CheckResult(name="PaddleOCR runtime", ok=True, message="initialized")
    except Exception as exc:
        return CheckResult(name="PaddleOCR runtime", ok=False, message=str(exc))


def print_results(results: Iterable[CheckResult]) -> int:
    """Print verification results and return the process exit code."""

    exit_code = 0
    for result in results:
        status = "PASS" if result.ok else "FAIL"
        print(f"[{status}] {result.name}: {result.message}")
        if not result.ok:
            exit_code = 1
    return exit_code


def main() -> int:
    """Run all verification checks."""

    checks = [check_python_version(), check_poppler()]
    checks.extend(check_module(module_name) for module_name in REQUIRED_MODULES)
    checks.append(check_ocr_runtime())
    return print_results(checks)


if __name__ == "__main__":
    raise SystemExit(main())
