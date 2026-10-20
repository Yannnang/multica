"""Route-config routes (D2/D8): per-type ordered approval levels, overwrite semantics."""
from __future__ import annotations

from fastapi import APIRouter, Depends, Request
from sqlalchemy.orm import Session

from app.api.deps import ActorHeader, get_db
from app.models.enums import ApprovalType
from app.schemas.api import RouteConfigOut, RouteConfigUpsert
from app.services import engine

router = APIRouter(prefix="/api/v1/config/routes", tags=["config"])


@router.put("/{approval_type}", response_model=RouteConfigOut, summary="设置某类型的多级审批人（覆盖式）")
def upsert_route_config(
    approval_type: ApprovalType,
    body: RouteConfigUpsert,
    x_user_id: ActorHeader,
    request: Request,
    db: Session = Depends(get_db),
) -> RouteConfigOut:
    """D2/D8：levels 每级恰好一人；修改仅影响新提交，在途单走提交时快照（TC-B02）。
    未知类型 → 422（枚举校验）。"""
    config = engine.upsert_route_config(db, request.app.state.clock, approval_type, body.levels)
    return RouteConfigOut.model_validate(config)


@router.get("/{approval_type}", response_model=RouteConfigOut, responses={404: {"description": "NOT_FOUND（该类型未配置路由）"}}, summary="查询某类型路由配置")
def get_route_config(approval_type: ApprovalType, x_user_id: ActorHeader, db: Session = Depends(get_db)) -> RouteConfigOut:
    return RouteConfigOut.model_validate(engine.get_route_config_or_404(db, approval_type))


@router.get("", response_model=list[RouteConfigOut], summary="查询全部路由配置")
def list_route_configs(x_user_id: ActorHeader, db: Session = Depends(get_db)) -> list[RouteConfigOut]:
    return [RouteConfigOut.model_validate(row) for row in engine.list_route_configs(db)]
