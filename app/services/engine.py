"""Core approval-flow engine (decisions.md D1~D17).

One HTTP request = one Session; every public entry performs its checks in the
contract order (D11: 404 → 403 → 409 STATE_CONFLICT → 409 VERSION_CONFLICT),
mutates, commits, and returns ORM objects with relationships already loaded.

All time comes from the injected Clock (D15) — no datetime.now() anywhere here.
"""
from __future__ import annotations

from datetime import date, datetime, timedelta

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core.clock import Clock
from app.core.errors import ApiError, ErrorCode
from app.models.enums import (
    TYPE_CN_NAMES,
    TYPE_PREFIX,
    ApprovalType,
    AuditAction,
    InstanceStatus,
    NotificationKind,
    TaskStatus,
)
from app.models.models import (
    ApprovalInstance,
    ApprovalTask,
    AuditLog,
    IdempotencyRecord,
    Notification,
    RouteConfig,
)
from app.schemas.api import ApproveRequest, InstanceCreate, RejectRequest, WithdrawRequest
from app.services.notify import NotificationDispatcher, NotificationEvent, TerminalEvent

# D9: 幂等重放窗口
IDEMPOTENCY_WINDOW = timedelta(hours=24)
# D7: 超时阈值（普通 24h / 加急 4h），每任务最多 3 次提醒
NORMAL_TIMEOUT = timedelta(hours=24)
URGENT_TIMEOUT = timedelta(hours=4)
MAX_REMINDERS = 3
# D17-5: 全局查重只看在途（PENDING）与已通过（APPROVED）单
ACTIVE_STATUSES = (InstanceStatus.PENDING.value, InstanceStatus.APPROVED.value)

AUTO_SKIP_COMMENT = "AUTO_SKIP_SELF_APPROVAL"
VOIDED_SKIP_COMMENT = "VOIDED_INSTANCE_TERMINAL"


# ---------------------------------------------------------------------------
# small writers
# ---------------------------------------------------------------------------

def _audit(
    session: Session,
    instance_id: int,
    actor_id: str | None,
    action: AuditAction,
    level: int | None,
    comment: str | None,
    now: datetime,
) -> None:
    session.add(
        AuditLog(
            instance_id=instance_id,
            actor_id=actor_id,
            action=action.value,
            level=level,
            comment=comment,
            created_at=now,
        )
    )


def _notify(
    session: Session,
    dispatcher: NotificationDispatcher,
    now: datetime,
    kind: NotificationKind,
    recipient_id: str,
    instance: ApprovalInstance,
    message: str,
    task_id: int | None = None,
    actor_id: str | None = None,
) -> None:
    dispatcher.notify(
        session,
        now,
        NotificationEvent(
            kind=kind,
            recipient_id=recipient_id,
            message=message,
            instance_id=instance.id,
            task_id=task_id,
            actor_id=actor_id,
        ),
    )


def _message(instance: ApprovalInstance, headline: str, actor_id: str | None, extra: str = "") -> str:
    """D14/TC-H01: message must carry 单据类型 (raw value), 单据编号 and 动作人."""
    parts = [
        f"{headline}：{instance.number}（类型 {instance.type}）",
        f"动作人：{actor_id or '系统'}",
    ]
    if extra:
        parts.append(extra)
    return "。".join(parts) + "。"


# ---------------------------------------------------------------------------
# submission pipeline (D2/D3/D9/D13/D17)
# ---------------------------------------------------------------------------

def _next_number(session: Session, approval_type: ApprovalType, now: datetime) -> str:
    """D13: {PREFIX}-{YYYYMMDD}-{当日序号:04d}，按类型按创建日（注入时钟）计数。"""
    prefix = TYPE_PREFIX[approval_type]
    day = now.strftime("%Y%m%d")
    like = f"{prefix}-{day}-%"
    count = session.scalar(
        select(func.count())
        .select_from(ApprovalInstance)
        .where(ApprovalInstance.number.like(like))
    )
    return f"{prefix}-{day}-{int(count or 0) + 1:04d}"


def _validation_error(field: str, message: str) -> ApiError:
    """Service-layer 422 whose details.loc points at the offending field (D17-6)."""
    return ApiError(
        422,
        ErrorCode.VALIDATION_ERROR,
        message,
        details=[{"loc": ["body", "payload", field], "msg": message, "type": "value_error"}],
    )


def _validate_relative_dates(body: InstanceCreate, now: datetime) -> None:
    """D17: clock-dependent date rules run in the service layer at submit time.

    payload dates arrive as ISO strings (InstanceCreate dumped with mode="json").
    """
    today = now.date()
    payload = body.payload
    t = body.type

    def _d(field: str) -> date | None:
        raw = payload.get(field)
        return date.fromisoformat(raw) if isinstance(raw, str) else raw

    if t is ApprovalType.PROCUREMENT:
        if _d("expected_delivery_date") < today:  # type: ignore[operator]
            raise _validation_error("expected_delivery_date", "expected_delivery_date must not be earlier than today")
    elif t is ApprovalType.SERVER_PROVISIONING:
        if _d("expected_ready_date") < today + timedelta(days=1):  # type: ignore[operator]
            raise _validation_error("expected_ready_date", "expected_ready_date must be at least tomorrow")
    elif t is ApprovalType.EXPENSE:
        for i, item in enumerate(payload.get("items", [])):
            incurred = item.get("incurred_on")
            incurred = date.fromisoformat(incurred) if isinstance(incurred, str) else incurred
            if incurred is not None and incurred > today:
                raise _validation_error(f"items.{i}.incurred_on", "incurred_on must not be later than today")
    elif t is ApprovalType.ACCOUNT_PROVISIONING:
        valid_until = _d("valid_until")
        if valid_until is not None and valid_until > today + timedelta(days=365):
            raise _validation_error("valid_until", "valid_until must be at most today + 365 days")


def _check_global_duplicates(session: Session, body: InstanceCreate, submitter_id: str) -> None:
    """D17-5: invoice numbers / env+service_name / personal account, 409 on hit.

    Only instances that are in-flight (PENDING) or APPROVED count — a REJECTED
    or WITHDRAWN instance never blocks a fresh submission (D5 resubmit relies
    on this).
    """
    t = body.type
    payload = body.payload
    if t is ApprovalType.EXPENSE:
        invoices = set(payload.get("invoice_numbers", []))
        rows = session.scalars(
            select(ApprovalInstance).where(
                ApprovalInstance.type == t.value,
                ApprovalInstance.status.in_(ACTIVE_STATUSES),
            )
        )
        for row in rows:
            hit = invoices & set((row.payload or {}).get("invoice_numbers", []))
            if hit:
                raise ApiError(
                    409,
                    ErrorCode.INVOICE_DUPLICATE,
                    f"invoice number(s) already submitted in instance {row.number}: {sorted(hit)}",
                    details=[{"loc": ["body", "payload", "invoice_numbers"], "msg": "duplicate invoice number", "type": "duplicate"}],
                )
    elif t is ApprovalType.SERVER_PROVISIONING:
        env, svc = payload.get("environment"), payload.get("service_name")
        rows = session.scalars(
            select(ApprovalInstance).where(
                ApprovalInstance.type == t.value,
                ApprovalInstance.status.in_(ACTIVE_STATUSES),
            )
        )
        for row in rows:
            rp = row.payload or {}
            if rp.get("environment") == env and rp.get("service_name") == svc:
                raise ApiError(
                    409,
                    ErrorCode.DUPLICATE_SERVICE_NAME,
                    f"service {svc!r} in environment {env!r} already requested in instance {row.number}",
                    details=[{"loc": ["body", "payload", "service_name"], "msg": "duplicate service_name for environment", "type": "duplicate"}],
                )
    elif t is ApprovalType.ACCOUNT_PROVISIONING and payload.get("account_type") == "personal":
        rows = session.scalars(
            select(ApprovalInstance).where(
                ApprovalInstance.type == t.value,
                ApprovalInstance.submitter_id == submitter_id,
                ApprovalInstance.status.in_(ACTIVE_STATUSES),
            )
        )
        for row in rows:
            rp = row.payload or {}
            if rp.get("account_type") == "personal" and rp.get("system_name") == payload.get("system_name"):
                raise ApiError(
                    409,
                    ErrorCode.DUPLICATE_ACCOUNT,
                    f"a personal account for system {payload.get('system_name')!r} is already requested in instance {row.number}",
                    details=[{"loc": ["body", "payload", "system_name"], "msg": "duplicate personal account for system", "type": "duplicate"}],
                )


def _apply_computed_fields(body: InstanceCreate) -> dict:
    """D17-3: server recomputes derived fields into the stored payload."""
    payload = dict(body.payload)
    t = body.type
    if t is ApprovalType.LEAVE:
        start = date.fromisoformat(payload["start_date"])
        end = date.fromisoformat(payload["end_date"])
        payload["duration_days"] = (end - start).days + 1
    elif t is ApprovalType.EXPENSE:
        payload["total_amount_cents"] = sum(int(i["amount_cents"]) for i in payload.get("items", []))
    elif t is ApprovalType.PROCUREMENT:
        payload["total_amount_cents"] = sum(
            int(i["quantity"]) * int(i["unit_price_cents"]) for i in payload.get("items", [])
        )
    return payload


def _activate_from(
    session: Session,
    dispatcher: NotificationDispatcher,
    instance: ApprovalInstance,
    tasks: list[ApprovalTask],
    start_level: int,
    actor_id: str | None,
    now: datetime,
) -> None:
    """Activate the first non-self task at level >= start_level (D1/D3).

    Self-approval levels auto-SKIP (audit SKIP, AUTO_SKIP_SELF_APPROVAL) and the
    chain rolls forward; if every remaining level skips, the instance is
    APPROVED with the triggering actor as final approver.
    """
    for task in tasks:
        if task.level < start_level:
            continue
        if task.approver_id == instance.submitter_id:
            task.status = TaskStatus.SKIPPED.value
            task.acted_at = now
            _audit(session, instance.id, None, AuditAction.SKIP, task.level, AUTO_SKIP_COMMENT, now)
            continue
        task.status = TaskStatus.PENDING.value
        task.activated_at = now
        _notify(
            session,
            dispatcher,
            now,
            NotificationKind.TASK_ASSIGNED,
            task.approver_id,
            instance,
            _message(instance, "您有新的审批待办", actor_id, f"提交人：{instance.submitter_id}"),
            task_id=task.id,
            actor_id=actor_id,
        )
        return
    # 所有剩余级均自审批回避 → 全链自动通过（正常提交已被 ROUTING_ALL_SELF 拦截，
    # 此处覆盖中途路由快照里剩余级恰好全为提交人的边界）
    _finalize_approved(session, dispatcher, instance, tasks, actor_id, None, now)


def _finalize_approved(
    session: Session,
    dispatcher: NotificationDispatcher,
    instance: ApprovalInstance,
    tasks: list[ApprovalTask],
    final_actor_id: str | None,
    now: datetime,
) -> None:
    instance.status = InstanceStatus.APPROVED.value
    instance.version += 1
    instance.updated_at = now
    _notify(
        session,
        dispatcher,
        now,
        NotificationKind.APPROVED,
        instance.submitter_id,
        instance,
        _message(instance, "您的审批单已通过终审", final_actor_id),
        actor_id=final_actor_id,
    )
    dispatcher.terminal(
        session,
        now,
        TerminalEvent(
            instance_id=instance.id,
            number=instance.number,
            approval_type=instance.type,
            type_cn=TYPE_CN_NAMES[ApprovalType(instance.type)],
            status=InstanceStatus.APPROVED.value,
            title=instance.title,
            submitter_id=instance.submitter_id,
            actor_id=final_actor_id,
            comment=None,
        ),
    )


def _void_remaining_tasks(
    session: Session,
    instance: ApprovalInstance,
    tasks: list[ApprovalTask],
    now: datetime,
) -> None:
    """D1: 单据进入终态后作废所有未终态任务（SKIPPED + 审计）。"""
    for task in tasks:
        if task.status in (TaskStatus.WAITING.value, TaskStatus.PENDING.value):
            task.status = TaskStatus.SKIPPED.value
            task.acted_at = now
            _audit(session, instance.id, None, AuditAction.SKIP, task.level, VOIDED_SKIP_COMMENT, now)


def submit_instance(
    session: Session,
    clock: Clock,
    dispatcher: NotificationDispatcher,
    body: InstanceCreate,
    submitter_id: str,
    idempotency_key: str | None = None,
    resubmit_of: ApprovalInstance | None = None,
) -> tuple[ApprovalInstance, bool]:
    """Create an instance (or replay an idempotent submit). Returns (instance, replayed)."""
    now = clock.now()

    # D9: idempotent replay inside the 24h window (submit entry only)
    record: IdempotencyRecord | None = None
    if idempotency_key is not None:
        record = session.scalar(
            select(IdempotencyRecord).where(
                IdempotencyRecord.submitter_id == submitter_id,
                IdempotencyRecord.key == idempotency_key,
            )
        )
        if record is not None and now - record.created_at < IDEMPOTENCY_WINDOW:
            original = session.get(ApprovalInstance, record.instance_id)
            if original is not None:
                _ = original.tasks  # eager-load for serialization
                return original, True

    # D2: route config snapshot — no config / empty config → 422, nothing created
    config = session.scalar(select(RouteConfig).where(RouteConfig.type == body.type.value))
    levels = [lv[0] for lv in (config.levels if config else []) if lv]
    if not levels:
        raise ApiError(
            422,
            ErrorCode.ROUTING_NOT_CONFIGURED,
            f"no route configured for approval type {body.type.value!r}",
        )
    # D3: 全链自审批 → 422，不创建单据
    if all(approver == submitter_id for approver in levels):
        raise ApiError(
            422,
            ErrorCode.ROUTING_ALL_SELF,
            "every configured approver is the submitter (self-approval chain forbidden)",
        )

    # D17: clock-relative rules + global dedup, before anything is created
    _validate_relative_dates(body, now)
    _check_global_duplicates(session, body, submitter_id)

    instance = ApprovalInstance(
        number=_next_number(session, body.type, now),
        type=body.type.value,
        title=body.title or f"{TYPE_CN_NAMES[body.type]}-{submitter_id}-{now.strftime('%Y%m%d')}",
        submitter_id=submitter_id,
        status=InstanceStatus.PENDING.value,
        is_urgent=body.is_urgent,
        urgent_reason=body.urgent_reason,
        resubmit_of=resubmit_of.id if resubmit_of is not None else None,
        payload=_apply_computed_fields(body),
        version=0,
        created_at=now,
        updated_at=now,
    )
    session.add(instance)
    session.flush()  # assign instance.id

    tasks = [
        ApprovalTask(
            instance_id=instance.id,
            level=i + 1,
            approver_id=approver,
            status=TaskStatus.WAITING.value,
            reminders_sent=0,
            version=0,
            created_at=now,
        )
        for i, approver in enumerate(levels)
    ]
    session.add_all(tasks)
    session.flush()

    _audit(session, instance.id, submitter_id, AuditAction.SUBMIT, None, None, now)
    _notify(
        session,
        dispatcher,
        now,
        NotificationKind.SUBMITTED,
        submitter_id,
        instance,
        _message(instance, "您的审批单已提交", submitter_id, "等待第 1 级审批"),
        actor_id=submitter_id,
    )
    _activate_from(session, dispatcher, instance, tasks, 1, submitter_id, now)

    # D9: persist / refresh the idempotency record
    if idempotency_key is not None:
        if record is not None:  # expired window → rebind to the new instance
            record.instance_id = instance.id
            record.created_at = now
        else:
            session.add(
                IdempotencyRecord(
                    submitter_id=submitter_id,
                    key=idempotency_key,
                    instance_id=instance.id,
                    created_at=now,
                )
            )

    session.commit()
    return instance, False


def resubmit_instance(
    session: Session,
    clock: Clock,
    dispatcher: NotificationDispatcher,
    instance_id: int,
    body: InstanceCreate,
    actor_id: str,
) -> ApprovalInstance:
    """D5: rejected instance → new instance (resubmit_of), restart from level 1."""
    original = session.get(ApprovalInstance, instance_id)
    if original is None:
        raise ApiError(404, ErrorCode.NOT_FOUND, f"instance {instance_id} not found")
    if actor_id != original.submitter_id:
        raise ApiError(403, ErrorCode.FORBIDDEN, "only the original submitter may resubmit")
    if original.status != InstanceStatus.REJECTED.value:
        raise ApiError(409, ErrorCode.STATE_CONFLICT, f"instance is {original.status}; only REJECTED instances can be resubmitted")
    instance, _ = submit_instance(
        session, clock, dispatcher, body, actor_id, idempotency_key=None, resubmit_of=original
    )
    return instance


# ---------------------------------------------------------------------------
# task actions (approve / reject) — D1/D3/D4/D11/D14
# ---------------------------------------------------------------------------

def _load_task_for_action(session: Session, task_id: int, body_version: int, actor_id: str) -> tuple[ApprovalTask, ApprovalInstance, list[ApprovalTask]]:
    """Shared 404 → 403 → STATE_CONFLICT → VERSION_CONFLICT gate for task actions."""
    task = session.get(ApprovalTask, task_id)
    if task is None:
        raise ApiError(404, ErrorCode.NOT_FOUND, f"task {task_id} not found")
    instance = session.get(ApprovalInstance, task.instance_id)
    if instance is None:  # pragma: no cover - FK guarantees existence
        raise ApiError(404, ErrorCode.NOT_FOUND, f"instance {task.instance_id} not found")
    # 403: D3 兜底防御先于普通权限（提交人对自己的单 → SELF_APPROVAL_FORBIDDEN）
    if actor_id == instance.submitter_id:
        raise ApiError(403, ErrorCode.SELF_APPROVAL_FORBIDDEN, "submitter may not approve/reject their own instance")
    if actor_id != task.approver_id:
        raise ApiError(403, ErrorCode.FORBIDDEN, "only the assigned approver of this task may act")
    # 409 state: 终态单 / 越级（WAITING）/ 重复审批（任务已终态）
    if instance.status != InstanceStatus.PENDING.value or task.status != TaskStatus.PENDING.value:
        raise ApiError(
            409,
            ErrorCode.STATE_CONFLICT,
            f"task is {task.status} and instance is {instance.status}; action requires both PENDING",
        )
    # 409 version: 乐观锁
    if body_version != task.version:
        raise ApiError(409, ErrorCode.VERSION_CONFLICT, f"task version {body_version} does not match current {task.version}")
    return task, instance, list(instance.tasks)


def approve_task(
    session: Session,
    clock: Clock,
    dispatcher: NotificationDispatcher,
    task_id: int,
    body: ApproveRequest,
    actor_id: str,
) -> ApprovalInstance:
    now = clock.now()
    task, instance, tasks = _load_task_for_action(session, task_id, body.version, actor_id)

    task.status = TaskStatus.APPROVED.value
    task.comment = body.comment
    task.acted_at = now
    task.version += 1
    _audit(session, instance.id, actor_id, AuditAction.APPROVE, task.level, body.comment, now)

    next_level = task.level + 1
    if any(t.level >= next_level for t in tasks):
        _activate_from(session, dispatcher, instance, tasks, next_level, actor_id, now)
    else:
        _finalize_approved(session, dispatcher, instance, tasks, actor_id, now)

    instance.version += 1
    instance.updated_at = now
    session.commit()
    return instance


def reject_task(
    session: Session,
    clock: Clock,
    dispatcher: NotificationDispatcher,
    task_id: int,
    body: RejectRequest,
    actor_id: str,
) -> ApprovalInstance:
    now = clock.now()
    task, instance, tasks = _load_task_for_action(session, task_id, body.version, actor_id)

    task.status = TaskStatus.REJECTED.value
    task.comment = body.comment
    task.acted_at = now
    task.version += 1
    _audit(session, instance.id, actor_id, AuditAction.REJECT, task.level, body.comment, now)

    instance.status = InstanceStatus.REJECTED.value
    instance.version += 1
    instance.updated_at = now
    # D1: 未终态任务全部作废（已 APPROVED 的历史级保留，未激活级永不激活）
    _void_remaining_tasks(session, instance, tasks, now)

    # D4/D14: 通知提交人，携带驳回意见全文
    _notify(
        session,
        dispatcher,
        now,
        NotificationKind.REJECTED,
        instance.submitter_id,
        instance,
        _message(instance, "您的审批单已被驳回", actor_id, f"驳回意见：{body.comment}"),
        task_id=task.id,
        actor_id=actor_id,
    )
    dispatcher.terminal(
        session,
        now,
        TerminalEvent(
            instance_id=instance.id,
            number=instance.number,
            approval_type=instance.type,
            type_cn=TYPE_CN_NAMES[ApprovalType(instance.type)],
            status=InstanceStatus.REJECTED.value,
            title=instance.title,
            submitter_id=instance.submitter_id,
            actor_id=actor_id,
            comment=body.comment,
        ),
    )
    session.commit()
    return instance


# ---------------------------------------------------------------------------
# instance actions (withdraw / escalate-urgent) — D6/D10/D11
# ---------------------------------------------------------------------------

def withdraw_instance(
    session: Session,
    clock: Clock,
    dispatcher: NotificationDispatcher,
    instance_id: int,
    body: WithdrawRequest,
    actor_id: str,
) -> ApprovalInstance:
    now = clock.now()
    instance = session.get(ApprovalInstance, instance_id)
    if instance is None:
        raise ApiError(404, ErrorCode.NOT_FOUND, f"instance {instance_id} not found")
    if actor_id != instance.submitter_id:
        raise ApiError(403, ErrorCode.FORBIDDEN, "only the submitter may withdraw")
    if instance.status != InstanceStatus.PENDING.value:
        raise ApiError(409, ErrorCode.STATE_CONFLICT, f"instance is {instance.status}; only PENDING instances can be withdrawn")
    if body.version != instance.version:
        raise ApiError(409, ErrorCode.VERSION_CONFLICT, f"instance version {body.version} does not match current {instance.version}")

    tasks = list(instance.tasks)
    pending_approvers = [t.approver_id for t in tasks if t.status == TaskStatus.PENDING.value]

    instance.status = InstanceStatus.WITHDRAWN.value
    instance.version += 1
    instance.updated_at = now
    _audit(session, instance.id, actor_id, AuditAction.WITHDRAW, None, body.reason, now)
    _void_remaining_tasks(session, instance, tasks, now)

    # D14: 撤回时处于 PENDING 的审批人收到 WITHDRAWN（actor=提交人）；D18: WITHDRAWN 不发弹窗
    for approver in pending_approvers:
        _notify(
            session,
            dispatcher,
            now,
            NotificationKind.WITHDRAWN,
            approver,
            instance,
            _message(instance, "审批单已被提交人撤回", actor_id, f"撤回原因：{body.reason}" if body.reason else ""),
            actor_id=actor_id,
        )
    session.commit()
    return instance


def escalate_urgent(
    session: Session,
    clock: Clock,
    instance_id: int,
    actor_id: str,
) -> ApprovalInstance:
    """D6: submitter-only, in-flight only, once. No body → no version (D11 v0.2.1)."""
    now = clock.now()
    instance = session.get(ApprovalInstance, instance_id)
    if instance is None:
        raise ApiError(404, ErrorCode.NOT_FOUND, f"instance {instance_id} not found")
    if actor_id != instance.submitter_id:
        raise ApiError(403, ErrorCode.FORBIDDEN, "only the submitter may escalate to urgent")
    if instance.status != InstanceStatus.PENDING.value:
        raise ApiError(409, ErrorCode.STATE_CONFLICT, f"instance is {instance.status}; only PENDING instances can be escalated")
    if instance.is_urgent:
        raise ApiError(409, ErrorCode.ALREADY_URGENT, "instance is already urgent")

    instance.is_urgent = True
    instance.version += 1
    instance.updated_at = now
    _audit(session, instance.id, actor_id, AuditAction.ESCALATE_URGENT, None, None, now)
    # 超时阈值切换由 timeout-scan 读取 instance.is_urgent 实现；提醒计数保留在任务行（D6/D7）
    session.commit()
    return instance


# ---------------------------------------------------------------------------
# timeout scan (D7)
# ---------------------------------------------------------------------------

def run_timeout_scan(
    session: Session,
    clock: Clock,
    dispatcher: NotificationDispatcher,
) -> dict:
    """Scan PENDING tasks against the injected clock; never changes any status.

    Reminder rule: due at (last_reminded_at or activated_at) + threshold, where
    threshold = 4h (urgent instance) / 24h (normal), inclusive of equality;
    capped at MAX_REMINDERS per task. Only reminders_sent / last_reminded_at are
    written — task.version and instance rows are untouched (D7 v0.2.1).
    """
    now = clock.now()
    reminded_ids: list[int] = []
    tasks = session.scalars(
        select(ApprovalTask).where(ApprovalTask.status == TaskStatus.PENDING.value).order_by(ApprovalTask.id)
    ).all()
    for task in tasks:
        if task.reminders_sent >= MAX_REMINDERS or task.activated_at is None:
            continue
        instance = task.instance
        threshold = URGENT_TIMEOUT if instance.is_urgent else NORMAL_TIMEOUT
        due_at = (task.last_reminded_at or task.activated_at) + threshold
        if now < due_at:
            continue
        task.reminders_sent += 1
        task.last_reminded_at = now
        _notify(
            session,
            dispatcher,
            now,
            NotificationKind.TIMEOUT_REMINDER,
            task.approver_id,
            instance,
            _message(
                instance,
                "审批待办超时提醒",
                None,
                f"提交人：{instance.submitter_id}，已超出处理阈值（{'加急 4h' if instance.is_urgent else '普通 24h'}），请尽快处理",
            ),
            task_id=task.id,
            actor_id=None,
        )
        _audit(session, instance.id, None, AuditAction.TIMEOUT_REMIND, task.level, None, now)
        reminded_ids.append(task.id)
    session.commit()
    return {"now": now, "reminders_sent": len(reminded_ids), "reminded_task_ids": reminded_ids}


# ---------------------------------------------------------------------------
# reads (D10: v1 读接口不做数据范围隔离；通知仅返回本人)
# ---------------------------------------------------------------------------

def get_instance_or_404(session: Session, instance_id: int) -> ApprovalInstance:
    instance = session.get(ApprovalInstance, instance_id)
    if instance is None:
        raise ApiError(404, ErrorCode.NOT_FOUND, f"instance {instance_id} not found")
    _ = instance.tasks  # load while the session is open
    return instance


def list_submitted(session: Session, user_id: str) -> list[ApprovalInstance]:
    rows = session.scalars(
        select(ApprovalInstance)
        .where(ApprovalInstance.submitter_id == user_id)
        .order_by(ApprovalInstance.created_at.desc(), ApprovalInstance.id.desc())
    ).all()
    for row in rows:
        _ = row.tasks
    return list(rows)


def list_todo(session: Session, user_id: str) -> list[ApprovalInstance]:
    """待我审批：存在本人 PENDING 任务；加急优先，同级 created_at FIFO（TC-F02）。"""
    rows = session.scalars(
        select(ApprovalInstance)
        .join(ApprovalTask, ApprovalTask.instance_id == ApprovalInstance.id)
        .where(
            ApprovalTask.approver_id == user_id,
            ApprovalTask.status == TaskStatus.PENDING.value,
        )
        .order_by(
            ApprovalInstance.is_urgent.desc(),
            ApprovalInstance.created_at.asc(),
            ApprovalInstance.id.asc(),
        )
    ).all()
    for row in rows:
        _ = row.tasks
    return list(rows)


def list_notifications(session: Session, user_id: str) -> list[Notification]:
    return list(
        session.scalars(
            select(Notification)
            .where(Notification.recipient_id == user_id)
            .order_by(Notification.created_at.desc(), Notification.id.desc())
        ).all()
    )


def list_audit(session: Session, instance_id: int) -> list[AuditLog]:
    get_instance_or_404(session, instance_id)  # 404 for a missing instance
    return list(
        session.scalars(
            select(AuditLog)
            .where(AuditLog.instance_id == instance_id)
            .order_by(AuditLog.created_at.asc(), AuditLog.id.asc())
        ).all()
    )


# ---------------------------------------------------------------------------
# route config (D2/D8)
# ---------------------------------------------------------------------------

def upsert_route_config(session: Session, clock: Clock, approval_type: ApprovalType, levels: list[list[str]]) -> RouteConfig:
    now = clock.now()
    config = session.scalar(select(RouteConfig).where(RouteConfig.type == approval_type.value))
    if config is None:
        config = RouteConfig(type=approval_type.value, levels=levels, updated_at=now)
        session.add(config)
    else:
        config.levels = levels
        config.updated_at = now
    session.commit()
    return config


def get_route_config_or_404(session: Session, approval_type: ApprovalType) -> RouteConfig:
    config = session.scalar(select(RouteConfig).where(RouteConfig.type == approval_type.value))
    if config is None:
        raise ApiError(404, ErrorCode.NOT_FOUND, f"no route configured for approval type {approval_type.value!r}")
    return config


def list_route_configs(session: Session) -> list[RouteConfig]:
    return list(session.scalars(select(RouteConfig).order_by(RouteConfig.id)).all())
