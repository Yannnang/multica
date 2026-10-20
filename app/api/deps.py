"""Shared API dependencies.

v1 鉴权（D10）：X-User-Id 请求头标识操作人，缺失/空白 → 422 VALIDATION_ERROR。
无真实认证；权限 = 角色规则（提交人 / 当前级审批人），在各端点契约中定义。
"""
from __future__ import annotations

from typing import Annotated

from fastapi import Header

ActorHeader = Annotated[
    str,
    Header(
        alias="X-User-Id",
        min_length=1,
        max_length=64,
        description="操作人用户 ID（v1 鉴权，D10）",
    ),
]

IdempotencyKeyHeader = Annotated[
    str | None,
    Header(
        alias="Idempotency-Key",
        max_length=128,
        description="可选幂等键（D9）：同提交人同键 24h 内重放返回原单",
    ),
]
