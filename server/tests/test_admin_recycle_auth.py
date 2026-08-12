"""Regression tests for /admin/recycle-engine authentication hardening.

The endpoint used to force-recycle the OCR engine on a bare POST with no
identity check.  It now requires the X-Admin-Token header to match the
HOTIX_ADMIN_TOKEN environment variable (constant-time comparison) and fails
closed when that variable is unset.
"""

from __future__ import annotations


def _recycle_noop(_app_state) -> float:
    """Stand-in for the real recycle so the 200-path test stays fast/offline."""
    return 0.0


def test_recycle_engine_without_configured_token_is_rejected(client, monkeypatch) -> None:
    """Fail closed: unset HOTIX_ADMIN_TOKEN → 403, even with no header sent."""
    monkeypatch.delenv("HOTIX_ADMIN_TOKEN", raising=False)
    resp = client.post("/admin/recycle-engine")
    assert resp.status_code == 403


def test_recycle_engine_with_wrong_token_is_rejected(client, monkeypatch) -> None:
    """A mismatched token is rejected with 403."""
    monkeypatch.setenv("HOTIX_ADMIN_TOKEN", "correct-secret")
    monkeypatch.setattr("server.main._recycle_ocr_engine", _recycle_noop)
    resp = client.post(
        "/admin/recycle-engine",
        headers={"X-Admin-Token": "wrong-secret"},
    )
    assert resp.status_code == 403


def test_recycle_engine_with_correct_token_is_accepted(client, monkeypatch) -> None:
    """A matching token is accepted with 200 (recycle stubbed out)."""
    monkeypatch.setenv("HOTIX_ADMIN_TOKEN", "correct-secret")
    monkeypatch.setattr("server.main._recycle_ocr_engine", _recycle_noop)
    resp = client.post(
        "/admin/recycle-engine",
        headers={"X-Admin-Token": "correct-secret"},
    )
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert body["status"] == "ok"
    assert body["engine_recycled"] is True
