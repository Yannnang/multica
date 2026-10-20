"""ORM tables. String columns hold enum values (SQLite-friendly); JSON holds payload/levels.

Concurrency: instances and tasks carry an integer `version` for optimistic locking (D11).
Timeout scan state: tasks carry `activated_at`, `reminders_sent`, `last_reminded_at` (D7).
Timestamps use TZDateTime so SQLite round-trips stay aware UTC (D15).
"""
from __future__ import annotations

from datetime import datetime

from sqlalchemy import JSON, Boolean, ForeignKey, Integer, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .db import Base, TZDateTime


class ApprovalInstance(Base):
    __tablename__ = "approval_instances"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    number: Mapped[str] = mapped_column(String(32), unique=True, index=True)   # {PREFIX}-{YYYYMMDD}-{seq:04d}
    type: Mapped[str] = mapped_column(String(32), index=True)                  # ApprovalType value
    title: Mapped[str | None] = mapped_column(String(120), nullable=True)      # D17-4: 可选，留空服务端兜底生成
    submitter_id: Mapped[str] = mapped_column(String(64), index=True)
    status: Mapped[str] = mapped_column(String(16), default="PENDING", index=True)
    is_urgent: Mapped[bool] = mapped_column(Boolean, default=False)
    urgent_reason: Mapped[str | None] = mapped_column(String(200), nullable=True)  # is_urgent 时必填（D17-4）
    resubmit_of: Mapped[int | None] = mapped_column(
        ForeignKey("approval_instances.id"), nullable=True
    )  # D5: 驳回后重提 = 新单，关联原单
    payload: Mapped[dict] = mapped_column(JSON)
    version: Mapped[int] = mapped_column(Integer, default=0)                   # D11 乐观锁
    created_at: Mapped[datetime] = mapped_column(TZDateTime)
    updated_at: Mapped[datetime] = mapped_column(TZDateTime)

    tasks: Mapped[list["ApprovalTask"]] = relationship(
        back_populates="instance", order_by="ApprovalTask.level"
    )


class ApprovalTask(Base):
    __tablename__ = "approval_tasks"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    instance_id: Mapped[int] = mapped_column(
        ForeignKey("approval_instances.id"), index=True
    )
    level: Mapped[int] = mapped_column(Integer)                # 1-based
    approver_id: Mapped[str] = mapped_column(String(64), index=True)
    status: Mapped[str] = mapped_column(String(16), default="WAITING")
    comment: Mapped[str | None] = mapped_column(Text, nullable=True)
    activated_at: Mapped[datetime | None] = mapped_column(
        TZDateTime, nullable=True
    )  # 进入 PENDING 的时刻；超时按级独立从此计时（D7）
    acted_at: Mapped[datetime | None] = mapped_column(TZDateTime, nullable=True)
    reminders_sent: Mapped[int] = mapped_column(Integer, default=0)
    last_reminded_at: Mapped[datetime | None] = mapped_column(
        TZDateTime, nullable=True
    )
    version: Mapped[int] = mapped_column(Integer, default=0)
    created_at: Mapped[datetime] = mapped_column(TZDateTime)

    instance: Mapped[ApprovalInstance] = relationship(back_populates="tasks")

    __table_args__ = (UniqueConstraint("instance_id", "level", name="uq_task_instance_level"),)


class RouteConfig(Base):
    """Per-type ordered approval levels. v1: exactly one approver per level (D8)."""

    __tablename__ = "route_configs"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    type: Mapped[str] = mapped_column(String(32), unique=True, index=True)
    levels: Mapped[list] = mapped_column(JSON)   # [[approver_id], [approver_id], ...]
    updated_at: Mapped[datetime] = mapped_column(TZDateTime)


class Notification(Base):
    __tablename__ = "notifications"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    recipient_id: Mapped[str] = mapped_column(String(64), index=True)
    kind: Mapped[str] = mapped_column(String(32))                # NotificationKind value
    instance_id: Mapped[int | None] = mapped_column(ForeignKey("approval_instances.id"), nullable=True)
    task_id: Mapped[int | None] = mapped_column(ForeignKey("approval_tasks.id"), nullable=True)
    actor_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    message: Mapped[str] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(TZDateTime, index=True)


class AuditLog(Base):
    """Append-only audit trail (D14): actor / timestamp / action / comment, time-ordered."""

    __tablename__ = "audit_logs"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    instance_id: Mapped[int] = mapped_column(ForeignKey("approval_instances.id"), index=True)
    actor_id: Mapped[str | None] = mapped_column(String(64), nullable=True)   # None = system (auto-skip/timeout)
    action: Mapped[str] = mapped_column(String(32))                            # AuditAction value
    level: Mapped[int | None] = mapped_column(Integer, nullable=True)
    comment: Mapped[str | None] = mapped_column(Text, nullable=True)
    created_at: Mapped[datetime] = mapped_column(TZDateTime)


class IdempotencyRecord(Base):
    """(submitter_id, key) unique; 24h replay window (D9)."""

    __tablename__ = "idempotency_records"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    submitter_id: Mapped[str] = mapped_column(String(64))
    key: Mapped[str] = mapped_column(String(128))
    instance_id: Mapped[int] = mapped_column(ForeignKey("approval_instances.id"))
    created_at: Mapped[datetime] = mapped_column(TZDateTime)

    __table_args__ = (UniqueConstraint("submitter_id", "key", name="uq_idem_submitter_key"),)
