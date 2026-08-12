"""Regression tests for the preview registration token being unpredictable.

The token used to be derived as sha256(resolved_path + timestamp), which a
local attacker who knows the file path and can bound the timestamp window
could recompute without ever calling /preview/register.  It is now generated
with secrets.token_urlsafe(32) (cryptographic random, path-independent).
"""

from __future__ import annotations

import hashlib
import time
from pathlib import Path


def _register(client, file_path: str) -> str:
    resp = client.post("/preview/register", json={"file_path": file_path})
    assert resp.status_code == 200, resp.text
    return resp.json()["token"]


def _make_png(path: Path) -> None:
    # A minimal PNG signature + padding is enough: /preview/register only checks
    # existence and the suffix, it does not decode the image.
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + b"0" * 32)


def test_preview_register_returns_different_tokens_for_same_file(client, tmp_path) -> None:
    """Two registrations of the same file must yield two distinct tokens."""
    png = tmp_path / "invoice.png"
    _make_png(png)

    token_a = _register(client, str(png))
    token_b = _register(client, str(png))

    assert token_a != token_b


def test_preview_token_is_not_reconstructible_from_path_and_timestamp(client, tmp_path) -> None:
    """The token must not equal sha256(path + timestamp) for any nearby timestamp."""
    png = tmp_path / "invoice.png"
    _make_png(png)

    token = _register(client, str(png))

    # token_urlsafe(32) yields 43 base64url chars — a different shape than the
    # old 32-hex-char token, which alone proves the derivation changed.
    assert len(token) == 43

    resolved = str(png.resolve())
    now = time.time()
    # ±10s at 0.1s steps around the call — any plausible timestamp window.
    for offset in range(-100, 101):
        candidate_ts = now + offset / 10.0
        candidate = hashlib.sha256(f"{resolved}:{candidate_ts}".encode()).hexdigest()[:32]
        assert candidate != token
