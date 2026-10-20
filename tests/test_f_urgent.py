"""Section F — 加急 (TC-F01~F05).

Contract refs: D6 (中途加急：仅提交人、仅在途、仅一次；409 ALREADY_URGENT；
不跳级；阈值切换为加急值、提醒计数保留；审计 ESCALATE_URGENT),
D7 (加急超时阈值 4h，含等号触发), TC-F02 (todo 排序 = 加急优先，同级 FIFO).
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    APPROVER1,
    APPROVER2,
    APPROVER3,
    OUTSIDER,
    SUBMITTER,
    VALID_PAYLOADS,
    approve,
    assert_error,
    assert_field_error,
    assert_status,
    audit_log,
    escalate,
    get_instance,
    hdr,
    instance_view,
    scan,
    submit,
    submit_configured,
    task_at_level,
)


# ---------------------------------------------------------------------------
# TC-F01 (P0): 提交时加急 → 标记持久化，待办可见
# ---------------------------------------------------------------------------

def test_f01_urgent_flag_persisted(client):
    inst = submit_configured(client, "leave", APPROVER1, is_urgent=True)
    assert inst["is_urgent"] is True

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["is_urgent"] is True

    todo = instance_view(client, "todo", hdr(APPROVER1))
    assert [i["id"] for i in todo] == [inst["id"]]
    assert todo[0]["is_urgent"] is True


# ---------------------------------------------------------------------------
# TC-F02 (P0): 待办排序 — 加急优先；同为加急按提交时间 FIFO
# ---------------------------------------------------------------------------

def test_f02_todo_ordering_urgent_first_then_fifo(client, clock):
    from tests.helpers import configure_route

    configure_route(client, "leave", APPROVER1)

    n1 = submit(client, approval_type="leave").json()          # t0 普通
    clock.advance(minutes=1)
    u1 = submit(client, approval_type="leave", is_urgent=True).json()  # t1 加急
    clock.advance(minutes=1)
    n2 = submit(client, approval_type="leave").json()          # t2 普通
    clock.advance(minutes=1)
    u2 = submit(client, approval_type="leave", is_urgent=True).json()  # t3 加急

    todo = instance_view(client, "todo", hdr(APPROVER1))
    # 加急（FIFO）在前，普通（FIFO）在后
    assert [i["id"] for i in todo] == [u1["id"], u2["id"], n1["id"], n2["id"]]


# ---------------------------------------------------------------------------
# TC-F03 (P1, D6): 加急 ≠ 跳级 — 仍走完全部审批级
# ---------------------------------------------------------------------------

def test_f03_urgent_does_not_skip_levels(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, APPROVER3,
                             is_urgent=True)

    t1 = task_at_level(inst, 1)
    inst = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert inst["status"] == "PENDING"
    t2 = task_at_level(inst, 2)
    inst = approve(client, t2["id"], t2["version"], hdr(APPROVER2)).json()
    assert inst["status"] == "PENDING"  # 两级通过仍未终结
    t3 = task_at_level(inst, 3)
    inst = approve(client, t3["id"], t3["version"], hdr(APPROVER3)).json()
    assert inst["status"] == "APPROVED"


# ---------------------------------------------------------------------------
# TC-F04 (P1, D7): 加急阈值 4h，含等号触发
# ---------------------------------------------------------------------------

def test_f04_urgent_threshold_4h_inclusive(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, is_urgent=True)
    t1 = task_at_level(inst, 1)

    clock.advance(hours=3, minutes=59, seconds=59)
    result = scan(client).json()
    assert result["reminders_sent"] == 0
    assert result["reminded_task_ids"] == []

    clock.advance(seconds=1)  # 恰好 4h：now >= activated_at + 阈值（含等号）
    result = scan(client).json()
    assert result["reminders_sent"] == 1
    assert result["reminded_task_ids"] == [t1["id"]]


# ---------------------------------------------------------------------------
# TC-F05 (D6⑤ 拍板): 中途加急 — 仅提交人、仅在途、仅一次；审计；阈值切换
# ---------------------------------------------------------------------------

def test_f05a_escalate_basic(client):
    inst = submit_configured(client, "leave", APPROVER1)
    assert inst["is_urgent"] is False

    resp = escalate(client, inst["id"], hdr(SUBMITTER))
    assert_status(resp, 200)
    escalated = resp.json()
    assert escalated["is_urgent"] is True
    assert escalated["status"] == "PENDING"  # 不跳级、不改路由
    assert task_at_level(escalated, 1)["approver_id"] == APPROVER1
    assert task_at_level(escalated, 1)["status"] == "PENDING"

    esc = [a for a in audit_log(client, inst["id"]) if a["action"] == "ESCALATE_URGENT"]
    assert len(esc) == 1
    assert esc[0]["actor_id"] == SUBMITTER

    # 已加急再升级 → 409 ALREADY_URGENT
    resp = escalate(client, inst["id"], hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "ALREADY_URGENT")


def test_f05b_escalate_permissions(client):
    inst = submit_configured(client, "leave", APPROVER1)

    # 仅提交人（D6/D10）
    resp = escalate(client, inst["id"], hdr(OUTSIDER), expect=None)
    assert_error(resp, 403, "FORBIDDEN")
    resp = escalate(client, inst["id"], hdr(APPROVER1), expect=None)
    assert_error(resp, 403, "FORBIDDEN")

    # 状态不变
    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["is_urgent"] is False


def test_f05c_escalate_switches_timeout_threshold(client, clock):
    # 升级后当前任务超时阈值切换为加急值（4h），无需等满普通 24h（D6）
    inst = submit_configured(client, "leave", APPROVER1)
    escalate(client, inst["id"], hdr(SUBMITTER))

    clock.advance(hours=4)
    result = scan(client).json()
    assert result["reminders_sent"] == 1
    assert result["reminded_task_ids"] == [task_at_level(inst, 1)["id"]]


def test_f05d_escalate_preserves_reminder_count(client, clock):
    # 普通单已提醒 1 次 → 升级加急 → 提醒计数保留，按加急周期继续（D6）
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    clock.advance(hours=24)
    scan(client)
    after_first = get_instance(client, inst["id"]).json()
    assert task_at_level(after_first, 1)["reminders_sent"] == 1

    escalate(client, inst["id"], hdr(SUBMITTER))
    clock.advance(hours=4)  # 加急周期满
    scan(client)

    after_second = get_instance(client, inst["id"]).json()
    t1_now = task_at_level(after_second, 1)
    assert t1_now["reminders_sent"] == 2  # 计数保留（不重置），累计第 2 次
    assert t1_now["status"] == "PENDING"
    assert t1_now["id"] == t1["id"]


# ---------------------------------------------------------------------------
# TC-F06 (D17-4): 信封加急理由 — is_urgent=true 时 urgent_reason 必填（5~200）
#   缺失/纯空白/超短/超长 → 422（契约层已生效，绿钉）；
#   200 字上边界 → 合法（当前红：501 级联）。
# ---------------------------------------------------------------------------

URGENT_REASON_INVALID_CASES = [
    ({}, "missing"),                       # is_urgent=true 未带 urgent_reason
    ({"urgent_reason": "     "}, "blank"),  # 纯空白（≥5 字符）等同缺失
    ({"urgent_reason": "prod"}, "too-short"),   # 4 字 < 5
    ({"urgent_reason": "x" * 201}, "too-long"),  # 201 字 > 200
]


@pytest.mark.parametrize("extra,_desc", URGENT_REASON_INVALID_CASES)
def test_f06_urgent_reason_required_and_bounded(client, extra, _desc):
    body = {
        "type": "leave",
        "is_urgent": True,
        "payload": VALID_PAYLOADS["leave"],
        **extra,
    }
    resp = submit(client, body=body, expect=None)
    assert_field_error(resp, "urgent_reason")


def test_f06_urgent_reason_upper_boundary_valid(client):
    body = {
        "type": "leave",
        "is_urgent": True,
        "urgent_reason": "x" * 200,  # 上边界含等号 → 合法
        "payload": VALID_PAYLOADS["leave"],
    }
    resp = submit(client, body=body)
    assert_status(resp, 201)
    assert resp.json()["is_urgent"] is True
