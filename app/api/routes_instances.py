"""Instance routes — submission, reads, withdraw, escalate-urgent, resubmit, audit."""
from __future__ import annotations

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse
from sqlalchemy.orm import Session

from app.api.deps import ActorHeader, IdempotencyKeyHeader, get_db
from app.models.enums import ApprovalType
from app.schemas.api import AuditOut, InstanceCreate, InstanceOut, WithdrawRequest
from app.services import engine

router = APIRouter(prefix="/api/v1/instances", tags=["instances"])

ERROR_RESPONSES = {
    403: {"description": "FORBIDDEN / SELF_APPROVAL_FORBIDDEN"},
    404: {"description": "NOT_FOUND"},
    409: {"description": "STATE_CONFLICT / VERSION_CONFLICT / ALREADY_URGENT"},
    422: {"description": "VALIDATION_ERROR / ROUTING_NOT_CONFIGURED / ROUTING_ALL_SELF"},
}


def _instance_json(instance, status_code: int = 200, headers: dict | None = None) -> JSONResponse:
    data = InstanceOut.model_validate(instance).model_dump(mode="json")
    return JSONResponse(status_code=status_code, content=data, headers=headers)


@router.post(
    "",
    response_model=InstanceOut,
    status_code=201,
    responses={**ERROR_RESPONSES, 200: {"description": "幂等重放：返回原单，含响应头 Idempotent-Replayed: true（D9）"}},
    summary="提交审批单",
)
def submit_instance(
    body: InstanceCreate,
    x_user_id: ActorHeader,
    request: Request,
    db: Session = Depends(get_db),
    idempotency_key: IdempotencyKeyHeader = None,
) -> JSONResponse:
    """创建审批单：校验 payload → 校验路由配置（缺配 422 ROUTING_NOT_CONFIGURED；
    全级自审批 422 ROUTING_ALL_SELF）→ 按配置生成全部级别任务快照 → 激活第 1 级
    （审批人==提交人则自动 SKIP 并顺延，D3）→ 通知（SUBMITTED/TASK_ASSIGNED）→ 审计 SUBMIT。
    编号规则 {PREFIX}-{YYYYMMDD}-{当日序号:04d}（D13）。"""
    instance, replayed = engine.submit_instance(
        db,
        request.app.state.clock,
        request.app.state.notification_dispatcher,
        body,
        x_user_id,
        idempotency_key=idempotency_key,
    )
    if replayed:
        return _instance_json(instance, 200, headers={"Idempotent-Replayed": "true"})
    return _instance_json(instance, 201)


@router.get("", response_model=list[InstanceOut], summary="列表：我提交的 / 待我审批的")
def list_instances(
    x_user_id: ActorHeader,
    db: Session = Depends(get_db),
    view: str = Query(pattern="^(submitted|todo)$", description="submitted=我提交的(created_at 倒序)；todo=待我审批的(加急优先，同级 FIFO，TC-F02)"),
) -> list[InstanceOut]:
    rows = engine.list_todo(db, x_user_id) if view == "todo" else engine.list_submitted(db, x_user_id)
    return [InstanceOut.model_validate(row) for row in rows]


@router.get("/{instance_id}", response_model=InstanceOut, responses=ERROR_RESPONSES, summary="单据详情（含任务列表）")
def get_instance(instance_id: int, x_user_id: ActorHeader, db: Session = Depends(get_db)) -> InstanceOut:
    return InstanceOut.model_validate(engine.get_instance_or_404(db, instance_id))


@router.post("/{instance_id}/withdraw", response_model=InstanceOut, responses=ERROR_RESPONSES, summary="撤回（仅提交人，审批中）")
def withdraw_instance(
    instance_id: int,
    body: WithdrawRequest,
    x_user_id: ActorHeader,
    request: Request,
    db: Session = Depends(get_db),
) -> InstanceOut:
    """单据 PENDING 且操作人==提交人且 version 匹配 → WITHDRAWN；当前及后续任务全部 SKIPPED；
    通知当前待办审批人 WITHDRAWN；审计 WITHDRAW。终态单 → 409 STATE_CONFLICT（TC-E01/E03、TC-C08）。"""
    instance = engine.withdraw_instance(
        db, request.app.state.clock, request.app.state.notification_dispatcher, instance_id, body, x_user_id
    )
    return InstanceOut.model_validate(instance)


@router.post("/{instance_id}/escalate-urgent", response_model=InstanceOut, responses=ERROR_RESPONSES, summary="在途加急（仅提交人，一次）")
def escalate_urgent(instance_id: int, x_user_id: ActorHeader, request: Request, db: Session = Depends(get_db)) -> InstanceOut:
    """D6：审批中允许一次升级为加急；已加急 → 409 ALREADY_URGENT；终态 → 409 STATE_CONFLICT；
    不跳级、不改变路由；审计 ESCALATE_URGENT。升级后当前任务超时阈值切换为加急阈值（提醒计数保留）。"""
    instance = engine.escalate_urgent(db, request.app.state.clock, instance_id, x_user_id)
    return InstanceOut.model_validate(instance)


@router.get("/{instance_id}/audit", response_model=list[AuditOut], responses=ERROR_RESPONSES, summary="审计日志（时间升序，只追加）")
def get_audit_log(instance_id: int, x_user_id: ActorHeader, db: Session = Depends(get_db)) -> list[AuditOut]:
    return [AuditOut.model_validate(row) for row in engine.list_audit(db, instance_id)]


@router.post(
    "/resubmit/{instance_id}",
    response_model=InstanceOut,
    status_code=201,
    responses=ERROR_RESPONSES,
    summary="被驳回后重新提交（生成新单，resubmit_of=原单，从第 1 级重走，D5）",
)
def resubmit_instance(
    instance_id: int,
    body: InstanceCreate,
    x_user_id: ActorHeader,
    request: Request,
    db: Session = Depends(get_db),
) -> JSONResponse:
    """原单必须为 REJECTED 且操作人==原提交人，type/payload 允许修改后重提。"""
    instance = engine.resubmit_instance(
        db, request.app.state.clock, request.app.state.notification_dispatcher, instance_id, body, x_user_id
    )
    return _instance_json(instance, 201)


# ApprovalType 在 resubmit 等场景由 body.type 校验；此处 import 保证 OpenAPI 组件齐全
_ = ApprovalType
