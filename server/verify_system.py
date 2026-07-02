"""Basic environment verification for HOTIX setup."""

from __future__ import annotations

import importlib.util
import shutil
import subprocess
import sys


def fail(message: str) -> int:
    print(f"[FAIL] {message}")
    return 1


def main() -> int:
    try:
        import paddleocr  # noqa: F401
    except Exception as exc:
        return fail(f"paddleocr import failed: {exc}")

    if importlib.util.find_spec("google.genai") is not None:
        try:
            import google.genai  # noqa: F401
        except Exception as exc:
            return fail(f"google.genai import failed: {exc}")
        print("[OK] google.genai available")
    elif importlib.util.find_spec("google.generativeai") is not None:
        try:
            import google.generativeai  # noqa: F401
        except Exception as exc:
            return fail(f"google.generativeai import failed: {exc}")
        print("[OK] google.generativeai available")
    else:
        return fail("Neither google.genai nor google.generativeai is installed")

    pdfinfo_path = shutil.which("pdfinfo")
    if not pdfinfo_path:
        return fail("Poppler/pdfinfo is not on PATH")

    try:
        result = subprocess.run(
            [pdfinfo_path, "-v"],
            capture_output=True,
            text=True,
            check=False,
        )
    except Exception as exc:
        return fail(f"pdfinfo execution failed: {exc}")

    if result.returncode != 0:
        output = (result.stdout or result.stderr or "").strip()
        return fail(f"pdfinfo returned {result.returncode}: {output}")

    print("[OK] Poppler/pdfinfo available")
    print("[OK] System verification completed successfully")
    return 0


if __name__ == "__main__":
    sys.exit(main())