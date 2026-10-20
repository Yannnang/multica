"""App factory: engine/session/dispatcher wiring + exception handlers + routers."""
from __future__ import annotations

from fastapi import FastAPI
from fastapi.exceptions import RequestValidationError

from app.api import routes_config, routes_instances, routes_system, routes_tasks
from app.core.clock import Clock, SystemClock
from app.core.errors import ApiError, api_error_handler, validation_error_handler
from app.models.db import Base, make_engine, make_session_factory
from app.services.notify import InAppChannel, NotificationDispatcher


def create_app(
    clock: Clock | None = None,
    database_url: str = "sqlite:///./approval.db",
) -> FastAPI:
    app = FastAPI(
        title="Approval System API",
        version="0.2.1",
        description="审批管理系统后端（契约见 docs/decisions.md）",
    )
    app.state.clock = clock or SystemClock()
    app.state.database_url = database_url

    engine = make_engine(database_url)
    Base.metadata.create_all(engine)
    app.state.db_engine = engine
    app.state.session_factory = make_session_factory(engine)

    # 通知渠道（D14 站内）；D18 飞书渠道随实现接入 dispatcher
    app.state.notification_dispatcher = NotificationDispatcher([InAppChannel()])

    app.add_exception_handler(ApiError, api_error_handler)
    app.add_exception_handler(RequestValidationError, validation_error_handler)

    app.include_router(routes_system.router)
    app.include_router(routes_instances.router)
    app.include_router(routes_tasks.router)
    app.include_router(routes_config.router)
    return app


app = create_app()
