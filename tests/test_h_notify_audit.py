"""Section H — 通知与审计 (TC-H01, TC-H02).

Contract refs: D14 (通知矩阵：SUBMITTED/TASK_ASSIGNED/APPROVED/REJECTED/
WITHDRAWN/TIMEOUT_REMINDER；内容必含单据类型、编号、动作人；本人通知时间倒序；
审计只追加：SUBMIT/APPROVE/REJECT/WITHDRAW/SKIP/ESCALATE_URGENT/TIMEOUT_REMIND，
时间升序), D15 (注入时钟).

解释性约定（如实现另有定义请在 issue 线程提出）：
- SUBMITTED/WITHDRAWN 通知 actor = 提交人；REJECTED 通知 actor = 驳回人；
  APPROVED 通知 actor = 终审通过人；TASK_ASSIGNED actor = 触发激活的动作人。
"""
from __future__ import annotations

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


def _kinds_for(client, user: str) -> list[str]:
    return kinds_of(notifications(client, hdr(user)))


# ---------------------------------------------------------------------------
# TC-H01 (P0): 通知矩阵 —  happy path（2 级全通过）
# ---------------------------------------------------------------------------

def test_h01_notification_matrix_full_approval(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    iid, number = inst["id"], inst["number"]

    clock.advance(hours=1)
    t1 = task_at_level(inst, 1)
    approve(client, t1["id"], t1["version"], hdr(APPROVER1))
    clock.advance(hours=1)
    inst2 = get_instance(client, iid).json()
    t2 = task_at_level(inst2, 2)
    approve(client, t2["id"], t2["version"], hdr(APPROVER2))

    # 提交人：SUBMITTED + 终审 APPROVED，中间级通过不打扰（D14）
    sub = notifications(client, hdr(SUBMITTER))
    assert _kinds_for(client, SUBMITTER) == ["APPROVED", "SUBMITTED"]  # 时间倒序
    approved_n = sub[0]
    assert approved_n["actor_id"] == APPROVER2
    assert approved_n["instance_id"] == iid
    # 内容必含：单据类型、单据编号（D14）
    assert number in approved_n["message"]
    assert "leave" in approved_n["message"]
    submitted_n = sub[1]
    assert submitted_n["actor_id"] == SUBMITTER
    assert number in submitted_n["message"]

    # 审批人：各自只收到 TASK_ASSIGNED（第 2 级由第 1 级通过触发）
    a1 = notifications(client, hdr(APPROVER1))
    assert _kinds_for(client, APPROVER1) == ["TASK_ASSIGNED"]
    assert a1[0]["task_id"] == t1["id"]
    a2 = notifications(client, hdr(APPROVER2))
    assert _kinds_for(client, APPROVER2) == ["TASK_ASSIGNED"]
    assert a2[0]["task_id"] == t2["id"]
    assert number in a2[0]["message"]


def test_h01_notification_matrix_reject(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    clock.advance(hours=1)
    reject(client, t1["id"], t1["version"], "insufficient quota", hdr(APPROVER1))

    sub = notifications(client, hdr(SUBMITTER))
    assert _kinds_for(client, SUBMITTER) == ["REJECTED", "SUBMITTED"]
    assert sub[0]["actor_id"] == APPROVER1
    assert "insufficient quota" in sub[0]["message"]  # 含意见全文（D4）
    # APPROVER2 的级从未激活 → 无任何通知
    assert notifications(client, hdr(APPROVER2)) == []


def test_h01_notification_matrix_withdraw(client, clock):
    inst = submit_configured(client, "leave", APPROVER1)
    clock.advance(hours=1)
    withdraw(client, inst["id"], inst["version"], "no longer needed", hdr(SUBMITTER))

    a1 = notifications(client, hdr(APPROVER1))
    assert _kinds_for(client, APPROVER1) == ["WITHDRAWN", "TASK_ASSIGNED"]
    assert a1[0]["actor_id"] == SUBMITTER


def test_h01_notification_matrix_timeout(client, clock):
    inst = submit_configured(client, "leave", APPROVER1)
    clock.advance(hours=24)
    scan(client)
    assert _kinds_for(client, APPROVER1) == ["TIMEOUT_REMINDER", "TASK_ASSIGNED"]


def test_h01_notifications_only_own_and_desc(client, clock):
    # 只返回本人通知；时间倒序（D14）
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    clock.advance(hours=1)
    t1 = task_at_level(inst, 1)
    approve(client, t1["id"], t1["version"], hdr(APPROVER1))

    for user in (SUBMITTER, APPROVER1, APPROVER2):
        notifs = notifications(client, hdr(user))
        assert all(n["recipient_id"] == user for n in notifs), f"leaked notif for {user}"
        times = [parse_dt(n["created_at"]) for n in notifs]
        assert times == sorted(times, reverse=True), f"not desc for {user}"

    # APPROVER2 的任务由 APPROVER1 的动作激活
    a2 = notifications(client, hdr(APPROVER2))
    assert _kinds_for(client, APPROVER2) == ["TASK_ASSIGNED"]
    # 时间戳来自注入时钟
    assert parse_dt(a2[0]["created_at"]) == clock.now()


# ---------------------------------------------------------------------------
# TC-H02 (P1): 审计日志 — 升序、完整、actor/level/comment/时间戳、只追加
# ---------------------------------------------------------------------------

def test_h02_audit_trail_lifecycle(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, "u-approver-3")
    iid = inst["id"]
    t_submit = clock.now()

    clock.advance(hours=1)
    t1 = task_at_level(inst, 1)
    approve(client, t1["id"], t1["version"], hdr(APPROVER1), comment="l1 ok")
    t_approve = clock.now()

    clock.advance(hours=1)
    inst2 = get_instance(client, iid).json()
    t2 = task_at_level(inst2, 2)
    reject(client, t2["id"], t2["version"], "l2 not ok", hdr(APPROVER2))
    t_reject = clock.now()

    audit = audit_log(client, iid)
    actions = [a["action"] for a in audit]
    # SUBMIT → APPROVE(L1) → REJECT(L2) → SKIP(L3 终态作废, 系统)
    assert actions == ["SUBMIT", "APPROVE", "REJECT", "SKIP"]

    by_action = {a["action"]: a for a in audit}
    assert by_action["SUBMIT"]["actor_id"] == SUBMITTER
    assert parse_dt(by_action["SUBMIT"]["created_at"]) == t_submit

    assert by_action["APPROVE"]["actor_id"] == APPROVER1
    assert by_action["APPROVE"]["level"] == 1
    assert by_action["APPROVE"]["comment"] == "l1 ok"
    assert parse_dt(by_action["APPROVE"]["created_at"]) == t_approve

    assert by_action["REJECT"]["actor_id"] == APPROVER2
    assert by_action["REJECT"]["level"] == 2
    assert by_action["REJECT"]["comment"] == "l2 not ok"
    assert parse_dt(by_action["REJECT"]["created_at"]) == t_reject

    assert by_action["SKIP"]["actor_id"] is None  # 系统作废（D14）
    assert by_action["SKIP"]["level"] == 3

    assert all(a["instance_id"] == iid for a in audit)

    # 时间升序（D14）
    times = [parse_dt(a["created_at"]) for a in audit]
    assert times == sorted(times)

    # 只追加：重复读取内容一致，无更新/删除接口（OpenAPI 中 audit 仅 GET）
    assert audit_log(client, iid) == audit
    paths = client.get("/openapi.json").json()["paths"]
    audit_path = paths["/api/v1/instances/{instance_id}/audit"]
    assert set(audit_path.keys()) == {"get"}


def test_h02_audit_records_auto_skip_and_timeout(client, clock):
    # 自审批回避 SKIP + 超时 TIMEOUT_REMIND 均入审计（D3/D7/D14）
    inst = submit_configured(client, "leave", SUBMITTER, APPROVER2)
    clock.advance(hours=24)
    scan(client)

    audit = audit_log(client, inst["id"])
    actions = [a["action"] for a in audit]
    assert actions == ["SUBMIT", "SKIP", "TIMEOUT_REMIND"]
    skip = audit[1]
    assert skip["actor_id"] is None
    assert skip["comment"] == "AUTO_SKIP_SELF_APPROVAL"
    assert skip["level"] == 1
    remind = audit[2]
    assert remind["actor_id"] is None
    assert remind["level"] == 2


def test_h02_audit_of_missing_instance_is_404(client):
    from tests.helpers import assert_error

    resp = client.get("/api/v1/instances/424242/audit", headers=hdr(SUBMITTER))
    assert_error(resp, 404, "NOT_FOUND")
