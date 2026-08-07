"""Shared pytest fixtures for the HOTIX server test suite."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from server.main import app


@pytest.fixture(scope="session")
def client() -> TestClient:
    """A session-scoped TestClient with the FastAPI lifespan running.

    The lifespan instantiates the OCR engine and pre-warms PaddleOCR; in
    environments without paddleocr installed (CI, dev boxes) the pre-warm
    fails gracefully (logged warning) and the app still serves /health.
    """
    with TestClient(app) as test_client:
        yield test_client
