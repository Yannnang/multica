"""Notification channels (D14 in-app + D18 external).

The engine emits two kinds of events:

- `NotificationEvent` — one row of the D14 notification matrix (SUBMITTED,
  TASK_ASSIGNED, APPROVED, REJECTED, WITHDRAWN, TIMEOUT_REMINDER). The in-app
  channel persists it; external channels may react to it in the future.
- `TerminalEvent` — the instance reached a final APPROVED/REJECTED state (D18).
  WITHDRAWN is never dispatched here (撤回是本人操作，不发弹窗). External
  channels (Feishu popup) subscribe to this event only.

Channels must never raise out of `on_*`: a failing external channel degrades
(its own audit records the outcome) without blocking the state transition (D18).
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from typing import Protocol

from sqlalchemy.orm import Session

from app.models.enums import NotificationKind
from app.models.models import Notification


@dataclass(frozen=True)
class NotificationEvent:
    """One D14 matrix notification."""

    kind: NotificationKind
    recipient_id: str
    message: str
    instance_id: int
    task_id: int | None = None
    actor_id: str | None = None


@dataclass(frozen=True)
class TerminalEvent:
    """Instance reached terminal APPROVED/REJECTED (D18 popup trigger)."""

    instance_id: int
    number: str
    approval_type: str          # raw enum value, e.g. "leave"
    type_cn: str                # 中文名, e.g. "请假"
    status: str                 # "APPROVED" | "REJECTED"
    title: str | None
    submitter_id: str
    actor_id: str | None        # 终审通过人 / 驳回人
    comment: str | None         # 驳回意见全文（REJECTED 时）


class NotificationChannel(Protocol):
    """A delivery channel. `now` is the injected-clock timestamp (D15)."""

    def on_notify(self, session: Session, now: datetime, event: NotificationEvent) -> None: ...

    def on_terminal(self, session: Session, now: datetime, event: TerminalEvent) -> None: ...


class InAppChannel:
    """站内通知（v1 的基础渠道，D14）：persist every NotificationEvent as a row."""

    def on_notify(self, session: Session, now: datetime, event: NotificationEvent) -> None:
        session.add(
            Notification(
                recipient_id=event.recipient_id,
                kind=event.kind.value,
                instance_id=event.instance_id,
                task_id=event.task_id,
                actor_id=event.actor_id,
                message=event.message,
                created_at=now,
            )
        )

    def on_terminal(self, session: Session, now: datetime, event: TerminalEvent) -> None:
        # 站内终态通知（APPROVED/REJECTED）由引擎作为普通 NotificationEvent 发出，此处无需重复。
        return None


class NotificationDispatcher:
    """Fan-out to all configured channels; channel failures must not escape."""

    def __init__(self, channels: list[NotificationChannel]) -> None:
        self._channels = list(channels)

    @property
    def channels(self) -> list[NotificationChannel]:
        return list(self._channels)

    def notify(self, session: Session, now: datetime, event: NotificationEvent) -> None:
        for channel in self._channels:
            channel.on_notify(session, now, event)

    def terminal(self, session: Session, now: datetime, event: TerminalEvent) -> None:
        for channel in self._channels:
            channel.on_terminal(session, now, event)
