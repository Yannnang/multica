"""Shared test fixtures — FakeClock per contract D15 (no sleep, injectable time)."""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.main import create_app


class FakeClock:
    """Controllable clock. contract: matches app.core.clock.Clock protocol."""

    def __init__(self, start: datetime | None = None) -> None:
        self._now = start or datetime(2026, 1, 1, tzinfo=timezone.utc)

    def now(self) -> datetime:
        return self._now

    def advance(self, **kwargs: float) -> None:
        self._now += timedelta(**kwargs)

    def set(self, when: datetime) -> None:
        self._now = when


@pytest.fixture()
def clock() -> FakeClock:
    return FakeClock()


@pytest.fixture()
def app(clock: FakeClock) -> FastAPI:
    # Scaffold stage: no tables created yet; implementation phase will add
    # Base.metadata.create_all + session dependency wired to this URL.
    return create_app(clock=clock, database_url="sqlite+pysqlite:///:memory:")


@pytest.fixture()
def client(app: FastAPI) -> TestClient:
    return TestClient(app)


@pytest.fixture()
def submitter_headers() -> dict[str, str]:
    return {"X-User-Id": "u-submitter-1"}


@pytest.fixture()
def approver_headers() -> dict[str, str]:
    return {"X-User-Id": "u-approver-1"}


VALID_LEAVE_BODY: dict = {
    "type": "leave",
    "is_urgent": False,
    "payload": {
        "leave_type": "annual",
        "start_date": "2026-11-02",
        "end_date": "2026-11-04",
        "reason": "annual leave",
    },
}
