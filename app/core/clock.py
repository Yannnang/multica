"""Injectable clock — all time-dependent logic MUST go through app.state.clock.

Tests inject FakeClock (see tests/conftest.py); production uses SystemClock.
No time logic may call datetime.now() directly.
"""
from __future__ import annotations

from datetime import datetime, timezone
from typing import Protocol


class Clock(Protocol):
    def now(self) -> datetime:
        """Return the current time as an aware UTC datetime."""
        ...


class SystemClock:
    """Production clock."""

    def now(self) -> datetime:
        return datetime.now(timezone.utc)
