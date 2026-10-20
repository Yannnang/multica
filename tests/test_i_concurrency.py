"""Section I — 并发与一致性 (TC-I01；TC-I02 = v1 范围外，钉住 D8 配置强制).

Contract refs: D11 (instance/task 各带整数 version 乐观锁，成功写操作自增；
版本不匹配 → 409 VERSION_CONFLICT；并发双写恰好一方生效；
检查顺序 404 → 403 → 409 STATE_CONFLICT → 409 VERSION_CONFLICT),
D8 (会签/或签 v1 不支持，每级恰好一人由配置校验强制).

说明：TestClient 为同步串行，真实线程级竞态不可复现；按 D11 乐观锁语义，
以「同 version 两次串行写」等价钉住「恰好一方生效」的可观测结果。
"""
from __future__ import annotations

from tests.helpers import (
    APPROVER1,
    APPROVER2,
    SUBMITTER,
    approve,
    assert_error,
    audit_log,
    get_instance,
    hdr,
    kinds_of,
    notifications,
    put_route,
    submit_configured,
    task_at_level,
    withdraw,
)


# ---------------------------------------------------------------------------
# TC-I01 (P1): 同一任务两次同 version 同意 → 仅一次生效，另一次 409
# ---------------------------------------------------------------------------

def test_i01_double_approve_same_task_exactly_one_wins(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    v = t1["version"]

    first = approve(client, t1["id"], v, hdr(APPROVER1), expect=None)
    assert first.status_code == 200, first.text
    second = approve(client, t1["id"], v, hdr(APPROVER1), expect=None)
    # 任务已终态 → 状态检查先于版本检查（D11 顺序）→ STATE_CONFLICT
    assert_error(second, 409, "STATE_CONFLICT")

    # 状态机不重复迁移：第 2 级恰好激活一次
    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    l2_tasks = [t for t in reloaded["tasks"] if t["level"] == 2]
    assert len(l2_tasks) == 1
    assert l2_tasks[0]["status"] == "PENDING"

    assigned = [n for n in notifications(client, hdr(APPROVER2)) if n["kind"] == "TASK_ASSIGNED"]
    assert len(assigned) == 1
    approves = [a for a in audit_log(client, inst["id"]) if a["action"] == "APPROVE"]
    assert len(approves) == 1


def test_i01b_double_withdraw_exactly_one_wins(client):
    inst = submit_configured(client, "leave", APPROVER1)
    v = inst["version"]

    first = withdraw(client, inst["id"], v, "first", hdr(SUBMITTER), expect=None)
    assert first.status_code == 200, first.text
    second = withdraw(client, inst["id"], v, "second", hdr(SUBMITTER), expect=None)
    # 单据已终态 → STATE_CONFLICT（先于 VERSION_CONFLICT，D11）
    assert_error(second, 409, "STATE_CONFLICT")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "WITHDRAWN"
    withdraws = [a for a in audit_log(client, inst["id"]) if a["action"] == "WITHDRAW"]
    assert len(withdraws) == 1
    assert withdraws[0]["comment"] == "first"


# ---------------------------------------------------------------------------
# D11: version 语义 — 成功写操作自增；stale version → VERSION_CONFLICT
# ---------------------------------------------------------------------------

def test_d11_version_increments_on_successful_writes(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    v_inst, v_task = inst["version"], task_at_level(inst, 1)["version"]

    approved = approve(client, task_at_level(inst, 1)["id"], v_task, hdr(APPROVER1)).json()
    assert approved["version"] > v_inst                     # instance 行被写 → 自增
    assert task_at_level(approved, 1)["version"] > v_task   # task 行被写 → 自增

    withdrawn = withdraw(client, inst["id"], approved["version"], "stop").json()
    assert withdrawn["version"] > approved["version"]


def test_d11_stale_instance_version_is_version_conflict(client):
    # 在途单据（非终态）+ stale instance version → 409 VERSION_CONFLICT
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    stale = inst["version"]
    t1 = task_at_level(inst, 1)
    approve(client, t1["id"], t1["version"], hdr(APPROVER1))  # instance version 递增

    resp = withdraw(client, inst["id"], stale, "too late", hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "VERSION_CONFLICT")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"                  # 冲突操作不产生写
    assert task_at_level(reloaded, 2)["status"] == "PENDING"


# ---------------------------------------------------------------------------
# TC-I02 (P2 → D8⑦): 会签/或签 v1 范围外 — 每级多人配置在契约层被拒绝
# ---------------------------------------------------------------------------

def test_i02_multi_approver_level_rejected_out_of_scope(client):
    resp = put_route(client, "leave", [[APPROVER1, APPROVER2]], expect=None)
    assert_error(resp, 422, "VALIDATION_ERROR")
