"""Feishu (飞书) terminal-popup channel — D18.

真实客户端 `FeishuClient`：环境变量凭证（FEISHU_APP_ID / FEISHU_APP_SECRET）→
tenant_access_token（缓存 + 到期自动刷新）→ 交互卡片 `send_card` →
应用内加急 `urgent_app`（飞书客户端弹窗强提醒）。

渠道 `FeishuTerminalChannel`（实现 notify.NotificationChannel 协议）：
- 只响应终态 TerminalEvent（APPROVED / REJECTED；WITHDRAWN 引擎不派发）；
- 提交人 → open_id 经 FEISHU_USER_MAP（env JSON 或 create_app 注入）；
- 失败降级：任何异常不抛出、不重试、不阻塞终态与站内通知，结果入审计
  FEISHU_SENT / FEISHU_FAILED / FEISHU_SKIPPED；
- 终态幂等：已存在 FEISHU_SENT 审计的单据绝不二次发送。

凭证纪律：App Secret 只经环境变量注入运行时，永不写入代码、日志或审计
（审计仅存 message_id / 异常类名与消息）。token 缓存 TTL 用 time.monotonic
（进程内部缓存语义，非业务时间——D15 的注入时钟约束业务时间戳）。
"""
from __future__ import annotations

import json
import os
import time
from datetime import datetime
from typing import Any, Protocol

import httpx
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.models.enums import AuditAction
from app.models.models import AuditLog
from app.services.notify import TerminalEvent

FEISHU_BASE_URL = "https://open.feishu.cn"


class FeishuClientProtocol(Protocol):
    """D18 注入协议（测试用 Fake 实现同签名）。"""

    def send_card(self, open_id: str, card: dict) -> str:
        """发送交互卡片，返回 message_id。"""
        ...

    def urgent_app(self, open_id: str, message_id: str) -> None:
        """对已发消息追加应用内加急（弹窗）。"""
        ...


class FeishuClient:
    """Real Feishu open-platform client (企业自建应用, tenant_access_token)."""

    def __init__(
        self,
        app_id: str,
        app_secret: str,
        base_url: str = FEISHU_BASE_URL,
        timeout: float = 10.0,
    ) -> None:
        self._app_id = app_id
        self._app_secret = app_secret
        self._base_url = base_url.rstrip("/")
        self._timeout = timeout
        self._token: str | None = None
        self._token_expires_at: float = 0.0  # time.monotonic() baseline

    @classmethod
    def from_env(cls, env: os._Environ[str] | dict[str, str] | None = None) -> "FeishuClient | None":
        """凭证仅来自环境变量；缺失任一 → None（渠道停用，不产生 FEISHU_* 审计）。"""
        source = os.environ if env is None else env
        app_id = (source.get("FEISHU_APP_ID") or "").strip()
        app_secret = (source.get("FEISHU_APP_SECRET") or "").strip()
        if not app_id or not app_secret:
            return None
        return cls(app_id, app_secret, base_url=source.get("FEISHU_BASE_URL") or FEISHU_BASE_URL)

    def _tenant_access_token(self) -> str:
        # 到期前 60s 主动刷新；失败异常向上传播，由渠道降级捕获
        if self._token and time.monotonic() < self._token_expires_at - 60:
            return self._token
        data = self._request(
            "POST",
            "/open-apis/auth/v3/tenant_access_token/internal",
            {"app_id": self._app_id, "app_secret": self._app_secret},
            auth=False,
        )
        self._token = data["tenant_access_token"]
        self._token_expires_at = time.monotonic() + float(data.get("expire", 7200))
        return self._token

    def _request(self, method: str, path: str, payload: dict[str, Any], auth: bool = True) -> dict:
        headers = {"Content-Type": "application/json; charset=utf-8"}
        if auth:
            headers["Authorization"] = f"Bearer {self._tenant_access_token()}"
        resp = httpx.request(
            method,
            f"{self._base_url}{path}",
            content=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers=headers,
            timeout=self._timeout,
        )
        resp.raise_for_status()
        data = resp.json()
        if data.get("code") != 0:
            raise RuntimeError(f"feishu api error: path={path} code={data.get('code')} msg={data.get('msg')}")
        return data.get("data") or {}

    def send_card(self, open_id: str, card: dict) -> str:
        data = self._request(
            "POST",
            "/open-apis/im/v1/messages?receive_id_type=open_id",
            {
                "receive_id": open_id,
                "msg_type": "interactive",
                "content": json.dumps(card, ensure_ascii=False),
            },
        )
        return data["message_id"]

    def urgent_app(self, open_id: str, message_id: str) -> None:
        # 应用内加急：PATCH /open-apis/message/v4/urgent_app/{message_id}
        self._request("PATCH", f"/open-apis/message/v4/urgent_app/{message_id}", {"open_ids": [open_id]})


def load_user_map_from_env(env: os._Environ[str] | dict[str, str] | None = None) -> dict[str, str]:
    """FEISHU_USER_MAP: env JSON object {系统用户id: open_id}；缺失/非法 → {}（全部降级 SKIPPED）。"""
    source = os.environ if env is None else env
    raw = (source.get("FEISHU_USER_MAP") or "").strip()
    if not raw:
        return {}
    try:
        parsed = json.loads(raw)
    except ValueError:
        return {}
    if not isinstance(parsed, dict):
        return {}
    return {str(k): str(v) for k, v in parsed.items() if v}


def build_terminal_card(event: TerminalEvent) -> dict:
    """交互卡片：必含单据类型、编号、审批结果；REJECTED 携带意见全文（J1/J2）。"""
    outcome = "已通过 ✅" if event.status == "APPROVED" else "已驳回 ❌"
    lines = [
        f"**单据编号**：{event.number}",
        f"**单据类型**：{event.type_cn}（{event.approval_type}）",
        f"**审批结果**：{event.status} {outcome}",
    ]
    if event.title:
        lines.insert(0, f"**标题**：{event.title}")
    if event.actor_id:
        lines.append(f"**{'终审通过人' if event.status == 'APPROVED' else '驳回人'}**：{event.actor_id}")
    if event.comment:
        lines.append(f"**审批意见**：{event.comment}")
    return {
        "config": {"wide_screen_mode": True},
        "header": {
            "template": "green" if event.status == "APPROVED" else "red",
            "title": {"tag": "plain_text", "content": f"审批结果通知：{event.number} {event.status}"},
        },
        "elements": [{"tag": "div", "text": {"tag": "lark_md", "content": "\n".join(lines)}}],
    }


class FeishuTerminalChannel:
    """D18 渠道：终态弹窗 = send_card + urgent_app；降级不抛出；每单至多一次。"""

    def __init__(self, client: FeishuClientProtocol, user_map: dict[str, str]) -> None:
        self._client = client
        self._user_map = dict(user_map)

    def on_notify(self, session: Session, now: datetime, event: Any) -> None:
        # 站内通知矩阵（D14）不经飞书；只有终态事件触发弹窗。
        return None

    def on_terminal(self, session: Session, now: datetime, event: TerminalEvent) -> None:
        # 终态幂等（D18）：本单已成功发送过 → 绝不再发
        already_sent = session.scalar(
            select(AuditLog.id).where(
                AuditLog.instance_id == event.instance_id,
                AuditLog.action == AuditAction.FEISHU_SENT.value,
            )
        )
        if already_sent is not None:
            return

        def _record(action: AuditAction, comment: str | None) -> None:
            session.add(
                AuditLog(
                    instance_id=event.instance_id,
                    actor_id=None,
                    action=action.value,
                    level=None,
                    comment=comment,
                    created_at=now,
                )
            )

        open_id = self._user_map.get(event.submitter_id)
        if not open_id:
            _record(AuditAction.FEISHU_SKIPPED, f"NO_OPEN_ID_MAP:{event.submitter_id}")
            return
        try:
            message_id = self._client.send_card(open_id, build_terminal_card(event))
            self._client.urgent_app(open_id, message_id)
        except Exception as exc:  # noqa: BLE001 — 降级语义：任何失败都不阻塞终态（D18）
            _record(AuditAction.FEISHU_FAILED, f"{type(exc).__name__}: {exc}"[:2000])
            return
        _record(AuditAction.FEISHU_SENT, message_id)
