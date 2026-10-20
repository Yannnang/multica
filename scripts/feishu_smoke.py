#!/usr/bin/env python3
"""Real-send smoke script for the D18 Feishu terminal-popup channel.

留给凭证到位后由用户执行（本仓库的自动化测试全部使用 FakeFeishu，不打真实飞书）。

前置：
1. 飞书企业自建应用已开启机器人能力，并申请权限：
   - im:message（发送交互卡片）
   - im:message.urgent_app（应用内加急 = 客户端弹窗强提醒）
2. 环境变量注入凭证（切勿写入代码/仓库/issue）：
   - FEISHU_APP_ID / FEISHU_APP_SECRET
   - FEISHU_USER_MAP：JSON 对象，{"<系统用户id>": "<feishu open_id>"}
     （获取 open_id：飞书管理后台 / API 调试台，或用手机号经
       POST /open-apis/contact/v3/users/batch_get_id 换取）

用法（仓库根目录）：
    python scripts/feishu_smoke.py <open_id 或 FEISHU_USER_MAP 中的用户id>

脚本会：send_card（模拟一张 APPROVED 终态卡片）→ urgent_app（应用内加急）。
成功输出 message_id；失败打印异常类型与信息并以非零码退出。
凭证与 token 全程不打印。
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.services.feishu import FeishuClient, build_terminal_card  # noqa: E402
from app.services.notify import TerminalEvent  # noqa: E402


def main() -> int:
    client = FeishuClient.from_env()
    if client is None:
        print("[FAIL] FEISHU_APP_ID / FEISHU_APP_SECRET not set — refusing to run.")
        return 2
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    target = sys.argv[1]

    # 允许传用户 id（经 FEISHU_USER_MAP 换 open_id）或直接传 open_id
    user_map_raw = os.environ.get("FEISHU_USER_MAP") or ""
    open_id = target
    if user_map_raw:
        from app.services.feishu import load_user_map_from_env

        open_id = load_user_map_from_env().get(target, target)

    event = TerminalEvent(
        instance_id=0,
        number="LV-00000000-0000",
        approval_type="leave",
        type_cn="请假",
        status="APPROVED",
        title="飞书渠道冒烟测试（可忽略）",
        submitter_id=target,
        actor_id="smoke-script",
        comment=None,
    )
    card = build_terminal_card(event)

    try:
        message_id = client.send_card(open_id, card)
        print(f"[OK] send_card succeeded, message_id={message_id}")
        client.urgent_app(open_id, message_id)
        print("[OK] urgent_app succeeded — check the Feishu client for the popup.")
    except Exception as exc:  # noqa: BLE001 — smoke script prints and exits non-zero
        print(f"[FAIL] {type(exc).__name__}: {exc}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
