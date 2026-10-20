"""Task routes — approve/reject a pending task (scaffold stage: 501 stubs)."""
from __future__ import annotations

from fastapi import APIRouter

from app.api.deps import ActorHeader
from app.core.errors import not_implemented
from app.schemas.api import ApproveRequest, InstanceOut, RejectRequest

router = APIRouter(prefix="/api/v1/tasks", tags=["tasks"])

ERROR_RESPONSES = {
    403: {"description": "FORBIDDEN（非该级审批人，TC-C06）/ SELF_APPROVAL_FORBIDDEN"},
    404: {"description": "NOT_FOUND"},
    409: {"description": "STATE_CONFLICT（终态单 TC-C08 / 越级 TC-C04 / 重复审批 TC-C05 / 任务非 PENDING）或 VERSION_CONFLICT（并发 TC-I01）"},
    422: {"description": "VALIDATION_ERROR（驳回意见缺失/空白，D4；version 缺失）"},
}


@router.post("/{task_id}/approve", response_model=InstanceOut, responses=ERROR_RESPONSES, summary="同意（返回更新后的整单）")
def approve_task(task_id: int, body: ApproveRequest, x_user_id: ActorHeader) -> None:
    """检查顺序（D12）：404 → 403（操作人==任务审批人？操作人==单据提交人则 403 SELF_APPROVAL_FORBIDDEN）
    → 409 STATE_CONFLICT（单据终态 / 任务非 PENDING=越级或重复）→ 409 VERSION_CONFLICT。
    通过后：任务 APPROVED（记 comment/acted_at）；有下一级 → 激活（自审批则 SKIP 顺延，D3）+ TASK_ASSIGNED；
    无下一级 → 单据 APPROVED + 通知提交人 APPROVED；审计 APPROVE。version 自增。"""
    not_implemented()


@router.post("/{task_id}/reject", response_model=InstanceOut, responses=ERROR_RESPONSES, summary="驳回（意见必填，返回更新后的整单）")
def reject_task(task_id: int, body: RejectRequest, x_user_id: ActorHeader) -> None:
    """同 approve 的检查顺序。通过后：任务 REJECTED（comment 必填已由 schema 保证）；
    单据立即 REJECTED；其余未终态任务全部 SKIPPED（TC-C02/C03，已 APPROVED 的历史级保留）；
    通知提交人 REJECTED（含意见，TC-D01）；审计 REJECT。"""
    not_implemented()
