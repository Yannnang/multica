"""Route-config routes (scaffold stage: 501 stubs)."""
from __future__ import annotations

from fastapi import APIRouter

from app.api.deps import ActorHeader
from app.core.errors import not_implemented
from app.models.enums import ApprovalType
from app.schemas.api import RouteConfigOut, RouteConfigUpsert

router = APIRouter(prefix="/api/v1/config/routes", tags=["config"])


@router.put("/{approval_type}", response_model=RouteConfigOut, summary="设置某类型的多级审批人（覆盖式）")
def upsert_route_config(approval_type: ApprovalType, body: RouteConfigUpsert, x_user_id: ActorHeader) -> None:
    """D2/D8：levels 每级恰好一人；修改仅影响新提交，在途单走提交时快照（TC-B02）。
    未知类型 → 422（枚举校验）。"""
    not_implemented()


@router.get("/{approval_type}", response_model=RouteConfigOut, responses={404: {"description": "NOT_FOUND（该类型未配置路由）"}}, summary="查询某类型路由配置")
def get_route_config(approval_type: ApprovalType, x_user_id: ActorHeader) -> None:
    not_implemented()


@router.get("", response_model=list[RouteConfigOut], summary="查询全部路由配置")
def list_route_configs(x_user_id: ActorHeader) -> None:
    not_implemented()
