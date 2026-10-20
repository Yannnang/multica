"""System routes: health + timeout-scan (D7) + notifications (D14)."""
from __future__ import annotations

from fastapi import APIRouter, Depends, Request
from sqlalchemy.orm import Session

from app.api.deps import ActorHeader, get_db
from app.schemas.api import NotificationOut, TimeoutScanResult
from app.services import engine

router = APIRouter(prefix="/api/v1", tags=["system"])


@router.get("/health", summary="健康检查")
def health() -> dict[str, str]:
    return {"status": "ok", "service": "approval-system", "version": "0.3.0"}


@router.post(
    "/system/timeout-scan",
    response_model=TimeoutScanResult,
    summary="按注入时钟执行一次超时扫描（D7：不改变任何单据状态）",
)
def timeout_scan(x_user_id: ActorHeader, request: Request, db: Session = Depends(get_db)) -> TimeoutScanResult:
    """扫描所有 PENDING 任务：now >= activated_at + 阈值（含等号，普通 24h / 加急 4h，按单据当前
    is_urgent 取值）且距上次提醒满一个阈值周期、reminders_sent < 3 → 发 TIMEOUT_REMINDER 通知
    当前审批人 + 审计 TIMEOUT_REMIND。任务非 PENDING（已同意/驳回/撤回/跳过）自然不再提醒（TC-G03）。
    无后台线程/真实定时器：由调用方（测试用 FakeClock，生产用 cron）驱动，时间取 app.state.clock.now()。"""
    result = engine.run_timeout_scan(db, request.app.state.clock, request.app.state.notification_dispatcher)
    return TimeoutScanResult(**result)


@router.get("/notifications", response_model=list[NotificationOut], summary="当前操作人的通知列表（时间倒序）")
def list_notifications(x_user_id: ActorHeader, db: Session = Depends(get_db)) -> list[NotificationOut]:
    return [NotificationOut.model_validate(row) for row in engine.list_notifications(db, x_user_id)]
