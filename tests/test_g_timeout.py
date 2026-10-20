"""Section G — 超时提醒 (TC-G01~G06).

Contract refs: D7 (普通 24h / 加急 4h；按任务级从 activated_at 独立计时；
now >= activated_at + 阈值 含等号；每满一个阈值周期再提醒，每任务最多 3 次；
扫描式 POST /api/v1/system/timeout-scan；任务离开 PENDING 自然不再提醒；
超时不改变任何状态), D14 (TIMEOUT_REMINDER 通知 + TIMEOUT_REMIND 审计，系统 actor=null),
D15 (注入时钟，禁止 sleep).
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    APPROVER1,
    APPROVER2,
    SUBMITTER,
    approve,
    assert_status,
    audit_log,
    get_instance,
    hdr,
    kinds_of,
    notifications,
    parse_dt,
    reject,
    scan,
    submit_configured,
    task_at_level,
    withdraw,
)


# ---------------------------------------------------------------------------
# TC-G01 (P0): 超阈值扫描 → 提醒当前审批人；状态不变
# ---------------------------------------------------------------------------

def test_g01_reminder_fires_after_24h(client, clock):
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    clock.advance(hours=24)
    result = scan(client).json()

    assert parse_dt(result["now"]) == clock.now()
    assert result["reminders_sent"] == 1
    assert result["reminded_task_ids"] == [t1["id"]]

    # 通知：当前 PENDING 任务审批人收到 TIMEOUT_REMINDER（D7/D14）
    notifs = notifications(client, hdr(APPROVER1))
    assert "TIMEOUT_REMINDER" in kinds_of(notifs)
    tr = [n for n in notifs if n["kind"] == "TIMEOUT_REMINDER"][0]
    assert tr["recipient_id"] == APPROVER1
    assert tr["instance_id"] == inst["id"]
    assert tr["task_id"] == t1["id"]
    assert inst["number"] in tr["message"]  # 通知内容含单据编号（D14/TC-H01）

    # 任务侧计数与时间戳更新；单据与任务状态不变（D1/D7）
    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    t1_now = task_at_level(reloaded, 1)
    assert t1_now["status"] == "PENDING"
    assert t1_now["reminders_sent"] == 1
    assert parse_dt(t1_now["last_reminded_at"]) == clock.now()

    # 审计 TIMEOUT_REMIND，系统 actor=null（D14）
    reminds = [a for a in audit_log(client, inst["id"]) if a["action"] == "TIMEOUT_REMIND"]
    assert len(reminds) == 1
    assert reminds[0]["actor_id"] is None


def test_g01_scan_without_pending_tasks_is_noop(client):
    result = scan(client).json()
    assert result["reminders_sent"] == 0
    assert result["reminded_task_ids"] == []


# ---------------------------------------------------------------------------
# TC-G02 (P1, D7): 边界含等号 — now >= activated_at + 阈值
# ---------------------------------------------------------------------------

def test_g02_threshold_boundary_inclusive(client, clock):
    inst = submit_configured(client, "leave", APPROVER1)

    clock.advance(hours=23, minutes=59, seconds=59)
    assert scan(client).json()["reminders_sent"] == 0  # 差 1 秒不触发

    clock.advance(seconds=1)  # 恰好 24h，含等号触发
    assert scan(client).json()["reminders_sent"] == 1


# ---------------------------------------------------------------------------
# TC-G03 (P0, D7): 任务离开 PENDING（同意/驳回/撤回）→ 不再提醒
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("action", ["approve", "reject", "withdraw"])
def test_g03_no_reminder_after_task_leaves_pending(client, clock, action):
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    if action == "approve":
        approve(client, t1["id"], t1["version"], hdr(APPROVER1))
    elif action == "reject":
        reject(client, t1["id"], t1["version"], "no", hdr(APPROVER1))
    else:
        withdraw(client, inst["id"], inst["version"], "cancel", hdr(SUBMITTER))

    clock.advance(hours=72)
    result = scan(client).json()
    assert result["reminders_sent"] == 0
    assert result["reminded_task_ids"] == []
    assert "TIMEOUT_REMINDER" not in kinds_of(notifications(client, hdr(APPROVER1)))


# ---------------------------------------------------------------------------
# TC-G04 (P1, D7): 按任务级独立计时 — 从前级 activated_at 起算，与前级耗时无关
# ---------------------------------------------------------------------------

def test_g04_per_level_independent_timing(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)

    clock.advance(hours=20)  # 第 1 级耗时 20h（未满 24h，不提醒）
    assert scan(client).json()["reminders_sent"] == 0
    inst = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    t2 = task_at_level(inst, 2)

    clock.advance(hours=4)  # 距提交 24h，但距第 2 级激活仅 4h
    assert scan(client).json()["reminders_sent"] == 0

    clock.advance(hours=20)  # 距第 2 级激活满 24h
    result = scan(client).json()
    assert result["reminders_sent"] == 1
    assert result["reminded_task_ids"] == [t2["id"]]
    assert "TIMEOUT_REMINDER" in kinds_of(notifications(client, hdr(APPROVER2)))
    assert "TIMEOUT_REMINDER" not in kinds_of(notifications(client, hdr(APPROVER1)))


# ---------------------------------------------------------------------------
# TC-G05 (P1, D1/D7): 超时不改变状态，提醒后仍可正常审批
# ---------------------------------------------------------------------------

def test_g05_state_unchanged_and_still_actionable(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    before = get_instance(client, inst["id"]).json()

    clock.advance(hours=24)
    scan(client)

    after_scan = get_instance(client, inst["id"]).json()
    assert after_scan["status"] == before["status"] == "PENDING"
    assert after_scan["version"] == before["version"]  # 扫描不改单据状态/版本
    assert task_at_level(after_scan, 1)["status"] == "PENDING"

    # 提醒后审批人仍可正常同意，链路继续
    t1 = task_at_level(after_scan, 1)
    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "PENDING"
    assert task_at_level(updated, 2)["status"] == "PENDING"


# ---------------------------------------------------------------------------
# TC-G06 (D7⑥ 拍板): 周期重复提醒，每任务最多 3 次；同周期内扫描不重复发
# ---------------------------------------------------------------------------

def test_g06_repeat_every_period_capped_at_three(client, clock):
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    clock.advance(hours=24)
    assert scan(client).json()["reminders_sent"] == 1
    # 同一周期内再次扫描 → 不重复提醒
    assert scan(client).json()["reminders_sent"] == 0

    clock.advance(hours=24)
    assert scan(client).json()["reminders_sent"] == 1  # 第 2 次
    clock.advance(hours=24)
    assert scan(client).json()["reminders_sent"] == 1  # 第 3 次

    # 上限 3 次：之后不再提醒
    clock.advance(hours=96)
    assert scan(client).json()["reminders_sent"] == 0
    assert scan(client).json()["reminders_sent"] == 0

    reloaded = get_instance(client, inst["id"]).json()
    assert task_at_level(reloaded, 1)["reminders_sent"] == 3
    tr = [n for n in notifications(client, hdr(APPROVER1)) if n["kind"] == "TIMEOUT_REMINDER"]
    assert len(tr) == 3
    reminds = [a for a in audit_log(client, inst["id"]) if a["action"] == "TIMEOUT_REMIND"]
    assert len(reminds) == 3
