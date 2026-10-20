"""App factory. Business logic lands in implementation phase; routes are contract stubs for now."""
from __future__ import annotations

from fastapi import FastAPI
from fastapi.exceptions import RequestValidationError

from app.api import routes_config, routes_instances, routes_system, routes_tasks
from app.core.clock import Clock, SystemClock
from app.core.errors import ApiError, api_error_handler, validation_error_handler


def create_app(
    clock: Clock | None = None,
    database_url: str = "sqlite:///./approval.db",
) -> FastAPI:
    app = FastAPI(
        title="Approval System API",
        version="0.2.0",
        description="审批管理系统后端契约（脚手架阶段：业务端点 501，契约见 docs/decisions.md）",
    )
    app.state.clock = clock or SystemClock()
    app.state.database_url = database_url

    app.add_exception_handler(ApiError, api_error_handler)
    app.add_exception_handler(RequestValidationError, validation_error_handler)

    app.include_router(routes_system.router)
    app.include_router(routes_instances.router)
    app.include_router(routes_tasks.router)
    app.include_router(routes_config.router)
    return app


app = create_app()
