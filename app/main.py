"""App factory: engine/session/dispatcher wiring + exception handlers + routers."""
from __future__ import annotations

from fastapi import FastAPI
from fastapi.exceptions import RequestValidationError

from app.api import routes_config, routes_instances, routes_system, routes_tasks
from app.core.clock import Clock, SystemClock
from app.core.errors import ApiError, api_error_handler, validation_error_handler
from app.models.db import Base, make_engine, make_session_factory
from app.services.feishu import FeishuClient, FeishuTerminalChannel, load_user_map_from_env
from app.services.notify import InAppChannel, NotificationDispatcher

APP_VERSION = "0.3.0"


def create_app(
    clock: Clock | None = None,
    database_url: str = "sqlite:///./approval.db",
    feishu_client: object | None = None,
    feishu_user_map: dict[str, str] | None = None,
) -> FastAPI:
    """Create an application instance.

    D18 注入点：
    - `feishu_client`：满足 FeishuClientProtocol（send_card/urgent_app）的实现；
      测试注入 Fake。缺省时尝试从环境变量凭证构造真实 `FeishuClient`；凭证缺失
      → 渠道停用（不产生任何 FEISHU_* 审计，站内通知不受影响）。
    - `feishu_user_map`：系统用户 id → open_id；缺省解析 `FEISHU_USER_MAP` env JSON。
    """
    app = FastAPI(
        title="Approval System API",
        version=APP_VERSION,
        description="审批管理系统后端（契约见 docs/decisions.md）",
    )
    app.state.clock = clock or SystemClock()
    app.state.database_url = database_url

    engine = make_engine(database_url)
    Base.metadata.create_all(engine)
    app.state.db_engine = engine
    app.state.session_factory = make_session_factory(engine)

    # 通知渠道：站内（D14，必开）+ 飞书终态弹窗（D18，凭证/注入才启用）
    client = feishu_client if feishu_client is not None else FeishuClient.from_env()
    user_map = feishu_user_map if feishu_user_map is not None else load_user_map_from_env()
    channels: list = [InAppChannel()]
    if client is not None:
        channels.append(FeishuTerminalChannel(client, user_map))
    app.state.feishu_client = client
    app.state.feishu_user_map = user_map
    app.state.notification_dispatcher = NotificationDispatcher(channels)

    app.add_exception_handler(ApiError, api_error_handler)
    app.add_exception_handler(RequestValidationError, validation_error_handler)

    app.include_router(routes_system.router)
    app.include_router(routes_instances.router)
    app.include_router(routes_tasks.router)
    app.include_router(routes_config.router)
    return app


app = create_app()
