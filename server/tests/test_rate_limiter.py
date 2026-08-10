"""Regression tests for the local extraction rate-limit policy."""

from __future__ import annotations

from server.main import (
    EXTRACT_RATE_LIMIT,
    SimpleRateLimiter,
    _rate_limiter_default,
    _rate_limiter_validate,
)


def test_extract_rate_limit_allows_normal_burst_from_one_local_ip() -> None:
    """A burst larger than the old limit must remain allowed for local batches."""
    _rate_limiter_default._requests.clear()
    burst_size = min(11, EXTRACT_RATE_LIMIT)

    assert EXTRACT_RATE_LIMIT >= burst_size
    assert all(_rate_limiter_default.is_allowed("127.0.0.1") for _ in range(burst_size))


def test_extract_rate_limit_still_rejects_requests_beyond_the_ceiling() -> None:
    """The limiter remains active after the sane local-batch ceiling."""
    _rate_limiter_default._requests.clear()

    for _ in range(EXTRACT_RATE_LIMIT):
        assert _rate_limiter_default.is_allowed("127.0.0.1")
    assert not _rate_limiter_default.is_allowed("127.0.0.1")


def test_validation_rate_limit_remains_strict_and_unchanged() -> None:
    """The key-validation limiter stays at its separate 5/60 policy."""
    assert _rate_limiter_validate.max_requests == 5
    assert _rate_limiter_validate.window == 60
