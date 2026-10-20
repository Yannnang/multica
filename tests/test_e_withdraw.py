"""Section E — 撤回 (TC-E01~E04).

Contract refs: D1 (WITHDRAWN 终态、未激活任务作废 SKIPPED),
D10 (仅提交人可撤回，否则 403 FORBIDDEN),
D11 (乐观锁：withdraw 携带 instance version；VERSION_CONFLICT；
     检查顺序 STATE_CONFLICT 先于 VERSION_CONFLICT),
D14 (WITHDRAWN 通知当时处于 PENDING 的审批人；审计 WITHDRAW 含原因).
"""
from __future__ import annotations

from tests.helpers import (
    APPROVER1,
    APPROVER2,
    APPROVER3,
    OUTSIDER,
    SUBMITTER,
    approve,
    assert_error,
    audit_log,
    get_instance,
    hdr,
    instance_view,
    kinds_of,
    notifications,
    submit,
    submit_configured,
    task_at_level,
    withdraw,
)


# ---------------------------------------------------------------------------
# TC-E01 (P0): 审批中撤回 → WITHDRAWN；当前及后续任务 SKIPPED；通知与审计
# ---------------------------------------------------------------------------

def test_e01_withdraw_in_flight(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, APPROVER3)
    t1 = task_at_level(inst, 1)
    clock.advance(hours=1)
    inst = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert task_at_level(inst, 2)["status"] == "PENDING"

    clock.advance(hours=1)
    fresh = get_instance(client, inst["id"]).json()
    reason = "plan changed, no longer needed"
    updated = withdraw(
        client, fresh["id"], fresh["version"], reason, hdr(SUBMITTER)
    ).json()

    assert updated["status"] == "WITHDRAWN"
    # 历史级 APPROVED 保留；当前及后续级全部 SKIPPED（D1）
    assert task_at_level(updated, 1)["status"] == "APPROVED"
    assert task_at_level(updated, 2)["status"] == "SKIPPED"
    assert task_at_level(updated, 3)["status"] == "SKIPPED"

    # D14: 撤回时处于 PENDING 的审批人（APPROVER2）收到 WITHDRAWN 通知
    a2_notifs = notifications(client, hdr(APPROVER2))
    assert "WITHDRAWN" in kinds_of(a2_notifs)
    wd = [n for n in a2_notifs if n["kind"] == "WITHDRAWN"][0]
    assert wd["actor_id"] == SUBMITTER
    assert wd["instance_id"] == updated["id"]
    # 从未激活的 APPROVER3 不收到 WITHDRAWN（撤回时非 PENDING）
    assert "WITHDRAWN" not in kinds_of(notifications(client, hdr(APPROVER3)))

    # 待办列表消失
    assert instance_view(client, "todo", hdr(APPROVER2)) == []

    # 审计 WITHDRAW 含原因与 actor
    wds = [a for a in audit_log(client, updated["id"]) if a["action"] == "WITHDRAW"]
    assert len(wds) == 1
    assert wds[0]["actor_id"] == SUBMITTER
    assert wds[0]["comment"] == reason


# ---------------------------------------------------------------------------
# TC-E02 (P0): 撤回后允许再次提交同内容 → 新单；原单保持 WITHDRAWN
# ---------------------------------------------------------------------------

def test_e02_submit_again_after_withdraw(client):
    configure_first = submit_configured(client, "leave", APPROVER1)
    withdrawn = withdraw(
        client, configure_first["id"], configure_first["version"]
    ).json()
    assert withdrawn["status"] == "WITHDRAWN"

    again = submit(client, approval_type="leave").json()
    assert again["id"] != withdrawn["id"]
    assert again["status"] == "PENDING"
    assert again["resubmit_of"] is None  # 普通新提交，不走 D5 重提关联

    original = get_instance(client, withdrawn["id"]).json()
    assert original["status"] == "WITHDRAWN"


# ---------------------------------------------------------------------------
# TC-E03 (P1, D10): 非提交人撤回 → 403 FORBIDDEN，状态不变
# ---------------------------------------------------------------------------

def test_e03_withdraw_by_non_submitter_is_forbidden(client):
    inst = submit_configured(client, "leave", APPROVER1)

    for actor in (OUTSIDER, APPROVER1):
        resp = withdraw(client, inst["id"], inst["version"], None, hdr(actor), expect=None)
        assert_error(resp, 403, "FORBIDDEN")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    assert reloaded["version"] == inst["version"]
    assert task_at_level(reloaded, 1)["status"] == "PENDING"


# ---------------------------------------------------------------------------
# TC-E04 (P1, D11): 撤回 vs 审批竞态 — 乐观锁保证恰好一方生效
# ---------------------------------------------------------------------------

def test_e04a_approve_wins_then_stale_withdraw_is_version_conflict(client):
    # 2 级：第 1 级同意成功（instance version 递增，单据仍 PENDING）；
    # 持旧 version 的撤回 → 409 VERSION_CONFLICT，状态不变。
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    stale_version = inst["version"]
    t1 = task_at_level(inst, 1)

    approved = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert approved["status"] == "PENDING"
    assert approved["version"] > stale_version  # 成功写操作 version 自增（D11）

    resp = withdraw(client, inst["id"], stale_version, None, hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "VERSION_CONFLICT")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    assert task_at_level(reloaded, 1)["status"] == "APPROVED"
    assert task_at_level(reloaded, 2)["status"] == "PENDING"
    assert reloaded["version"] == approved["version"]  # 冲突操作不产生写

    # 用最新 version 撤回仍可成功（未被污染）
    withdrawn = withdraw(client, inst["id"], reloaded["version"], "changed mind").json()
    assert withdrawn["status"] == "WITHDRAWN"


def test_e04b_withdraw_wins_then_stale_approve_is_state_conflict(client):
    # 撤回先成功 → 任务 SKIPPED、单据终态；持旧 task version 的审批
    # → 409 STATE_CONFLICT（状态检查先于版本检查，D11）。
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    stale_task_version = t1["version"]

    withdrawn = withdraw(client, inst["id"], inst["version"], "changed mind").json()
    assert withdrawn["status"] == "WITHDRAWN"

    resp = approve(client, t1["id"], stale_task_version, hdr(APPROVER1), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")

    final = get_instance(client, inst["id"]).json()
    assert final["status"] == "WITHDRAWN"
    assert task_at_level(final, 1)["status"] == "SKIPPED"
    assert task_at_level(final, 2)["status"] == "SKIPPED"

    # 恰好一方生效：审计只有 1 条 WITHDRAW、0 条 APPROVE
    actions = [a["action"] for a in audit_log(client, inst["id"])]
    assert actions.count("WITHDRAW") == 1
    assert actions.count("APPROVE") == 0
