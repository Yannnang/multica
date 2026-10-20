"""SQLAlchemy base, engine helpers & tz-aware DateTime type (SQLite).

SQLite has no native timezone storage: `TZDateTime` normalizes aware datetimes to
UTC on bind and re-attaches UTC on read, so every round-trip stays aware (D15:
all timestamps are aware UTC) and compares equal to the injected clock's values.

In-memory SQLite needs `StaticPool` so every connection in the process shares
the one in-memory database (per-test isolation via a fresh engine per app).
"""
from __future__ import annotations

from datetime import datetime, timezone

from sqlalchemy import DateTime, TypeDecorator, create_engine
from sqlalchemy.orm import DeclarativeBase, Session, sessionmaker
from sqlalchemy.pool import StaticPool


class Base(DeclarativeBase):
    pass


class TZDateTime(TypeDecorator):
    """Aware-UTC DateTime that survives SQLite's tz-less storage (D15)."""

    impl = DateTime
    cache_ok = True

    def process_bind_param(self, value: datetime | None, dialect) -> datetime | None:
        if value is None:
            return None
        if value.tzinfo is None:
            raise TypeError(f"naive datetime not allowed (D15 requires aware UTC): {value!r}")
        return value.astimezone(timezone.utc)

    def process_result_value(self, value: datetime | None, dialect) -> datetime | None:
        if value is not None and value.tzinfo is None:
            return value.replace(tzinfo=timezone.utc)
        return value


def make_engine(database_url: str = "sqlite:///./approval.db"):
    kwargs: dict = {}
    if database_url.startswith("sqlite"):
        kwargs["connect_args"] = {"check_same_thread": False}
        if ":memory:" in database_url:
            kwargs["poolclass"] = StaticPool
    return create_engine(database_url, **kwargs)


def make_session_factory(engine) -> sessionmaker[Session]:
    return sessionmaker(bind=engine, expire_on_commit=False)
