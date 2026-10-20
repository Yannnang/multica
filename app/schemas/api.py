"""API request/response models — the wire contract (see docs/decisions.md)."""
from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, ValidationError, model_validator

from app.models.enums import ApprovalType, AuditAction, InstanceStatus, NotificationKind, TaskStatus
from app.schemas.payloads import PAYLOAD_SCHEMAS


class InstanceCreate(BaseModel):
    """POST /api/v1/instances 请求体。

    - payload 按 type 交叉校验（PAYLOAD_SCHEMAS），非法 → 422 VALIDATION_ERROR；
      未知 type → 422（枚举校验）。
    - title 可选；留空时服务端按「{类型中文名}-{提交人}-{YYYYMMDD}」兜底生成（D17-4）。
    - is_urgent=true 时 urgent_reason 必填（5~200 字），缺失/空白 → 422（D17-4）。
    - applicantId 不经客户端传入：操作人一律取 X-User-Id（D10），防替他人提交。
    - 幂等经可选 Idempotency-Key 请求头（D9，等价前端规格的 requestId，24h 窗口）。
    """

    model_config = ConfigDict(extra="forbid")

    type: ApprovalType
    title: str | None = Field(default=None, min_length=1, max_length=100)
    is_urgent: bool = False
    urgent_reason: str | None = Field(default=None, min_length=5, max_length=200)
    payload: dict[str, Any]

    @model_validator(mode="after")
    def _urgent_requires_reason(self) -> "InstanceCreate":
        if self.is_urgent and not (self.urgent_reason and self.urgent_reason.strip()):
            raise ValueError("urgent_reason is required when is_urgent is true")
        return self

    @model_validator(mode="after")
    def _validate_payload_by_type(self) -> "InstanceCreate":
        schema = PAYLOAD_SCHEMAS.get(self.type)
        if schema is None:  # pragma: no cover - enum guards this
            raise ValueError(f"unknown approval type: {self.type!r}")
        try:
            validated = schema.model_validate(self.payload)
        except ValidationError as exc:
            raise ValueError(
                exc.errors(include_url=False, include_context=False)
            ) from None
        self.payload = validated.model_dump(mode="json")
        return self


class TaskOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    level: int
    approver_id: str
    status: TaskStatus
    comment: str | None = None
    activated_at: datetime | None = None
    acted_at: datetime | None = None
    reminders_sent: int = 0
    last_reminded_at: datetime | None = None
    version: int


class InstanceOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    number: str
    type: ApprovalType
    submitter_id: str
    status: InstanceStatus
    is_urgent: bool
    payload: dict[str, Any]
    resubmit_of: int | None = None
    version: int
    created_at: datetime
    updated_at: datetime
    tasks: list[TaskOut] = Field(default_factory=list)


class ApproveRequest(BaseModel):
    """同意。意见可选。version = 任务(TaskOut.version)当前值，乐观锁（D11）。"""

    model_config = ConfigDict(extra="forbid")

    comment: str | None = Field(default=None, max_length=2000)
    version: int = Field(ge=0)


class RejectRequest(BaseModel):
    """驳回。意见必填（D4）：缺失/空白（strip 后为空）→ 422。version = 任务当前值。"""

    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    comment: str = Field(min_length=1, max_length=2000)
    version: int = Field(ge=0)


class WithdrawRequest(BaseModel):
    """撤回（仅提交人）。version = 单据(InstanceOut.version)当前值。"""

    model_config = ConfigDict(extra="forbid")

    reason: str | None = Field(default=None, max_length=2000)
    version: int = Field(ge=0)


class RouteConfigUpsert(BaseModel):
    """路由配置：有序审批级。v1 每级恰好一名审批人（D8），级数 ≥ 1。

    在途单不受配置修改影响（快照于提交时，TC-B02）。
    """

    model_config = ConfigDict(extra="forbid")

    levels: list[list[str]] = Field(min_length=1, max_length=10)

    @model_validator(mode="after")
    def _one_approver_per_level(self) -> "RouteConfigUpsert":
        for i, level in enumerate(self.levels):
            if len(level) != 1 or not level[0].strip():
                raise ValueError(
                    f"levels[{i}] must contain exactly one non-empty approver id in v1"
                )
        return self


class RouteConfigOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    type: ApprovalType
    levels: list[list[str]]
    updated_at: datetime


class NotificationOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    recipient_id: str
    kind: NotificationKind
    instance_id: int | None = None
    task_id: int | None = None
    actor_id: str | None = None
    message: str
    created_at: datetime


class AuditOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    instance_id: int
    actor_id: str | None = None
    action: AuditAction
    level: int | None = None
    comment: str | None = None
    created_at: datetime


class TimeoutScanResult(BaseModel):
    """POST /api/v1/system/timeout-scan 响应：本次扫描发出的提醒数（按注入时钟计算）。"""

    now: datetime
    reminders_sent: int
    reminded_task_ids: list[int] = Field(default_factory=list)


class InstanceListView(BaseModel):
    """GET /api/v1/instances?view=... 的语义说明模型（仅文档用途）。

    - view=submitted：我提交的单，created_at 倒序
    - view=todo：待我审批的单（存在本人 PENDING 任务），排序 = 加急优先，同级按 created_at FIFO（TC-F02）
    """

    view: Literal["submitted", "todo"]
