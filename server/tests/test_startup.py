"""Startup / syntax regression tests.

These would have caught both historical crashes this sweep fixed:
- the IndentationError in server/main.py that prevented uvicorn from binding
  the port at all (the client saw a bare socket-connect failure because the
  module never imported), and
- the invalid escape-sequence SyntaxWarning in gemini_extractor.py.

They run on every pytest invocation and are wired into CI (build-check.yml).
"""

from __future__ import annotations

import ast
import warnings
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent.parent


def test_main_py_has_no_syntax_errors() -> None:
    """server/main.py must parse cleanly (raises SyntaxError otherwise).

    This exact assertion would have caught the historical IndentationError at
    the `items = extract_item_table(all_lines)` statement: ast.parse() raises
    on broken indentation/syntax before the app ever boots.
    """
    source = (SERVER_DIR / "main.py").read_text(encoding="utf-8")
    ast.parse(source)


def test_gemini_extractor_has_no_invalid_escape_warnings() -> None:
    """No invalid escape sequences (e.g. '\\H') in gemini_extractor.py.

    A non-raw docstring containing a Windows path produces a SyntaxWarning at
    compile time.  compile() with SyntaxWarning promoted to an error catches
    it exactly the way `python -W error::SyntaxWarning -c "import ..."` would.
    """
    path = SERVER_DIR / "gemini_extractor.py"
    source = path.read_text(encoding="utf-8")
    with warnings.catch_warnings():
        warnings.simplefilter("error", SyntaxWarning)
        compile(source, str(path), "exec")


def test_app_imports_and_health_check_works(client) -> None:
    """The FastAPI app object builds and /health answers 200.

    Proves the whole module graph imports without runtime errors (not just
    that main.py parses) and the lifespan started the OCR engine state.
    """
    resp = client.get("/health")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] in ("ok", "degraded")
    assert body["version"] == "1.0.0"
