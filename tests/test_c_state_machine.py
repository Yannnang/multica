"""Section C — 多级审批状态机 (TC-C01~C09) + D11 错误检查顺序.

Contract refs: D1 (状态机/终态不可变/级联 SKIP), D3 (自审批回避),
D11 (检查顺序 404 → 403 → 409 STATE_CONFLICT → 409 VERSION_CONFLICT；
越级与重复审批归入 STATE_CONFLICT), D12 (错误封装), D14 (通知/审计).
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    APPROVER1,
    APPROVER2,
    APPROVER3,
    OUTSIDER,
    SUBMITTER,
    approve,
    assert_error,
    audit_log,
    configure_route,
    get_instance,
    hdr,
    kinds_of,
    notifications,
    reject,
    resubmit,
    submit,
    submit_configured,
    task_at_level,
    withdraw,
    escalate,
)


# ---------------------------------------------------------------------------
# TC-C01 (P0): 3 级全部同意 → APPROVED；历史级记录保留；中间级不打扰提交人
# ---------------------------------------------------------------------------

def test_c01_full_approval_chain(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, APPROVER3)

    for level, approver in [(1, APPROVER1), (2, APPROVER2), (3, APPROVER3)]:
        t = task_at_level(inst, level)
        clock.advance(hours=1)
        inst = approve(client, t["id"], t["version"], hdr(approver), comment=f"ok L{level}").json()
        if level < 3:
            assert inst["status"] == "PENDING"

    assert inst["status"] == "APPROVED"
    for level in (1, 2, 3):
        t = task_at_level(inst, level)
        assert t["status"] == "APPROVED"
        assert t["acted_at"] is not None
        assert t["comment"] == f"ok L{level}"

    # D14: 终审通过才通知提交人；中间级通过不通知
    sub_kinds = kinds_of(notifications(client, hdr(SUBMITTER)))
    assert sub_kinds == ["APPROVED", "SUBMITTED"]  # 时间倒序

    # D14: 审计时间升序 SUBMIT + APPROVE×3，actor/level 正确
    audit = audit_log(client, inst["id"])
    assert [a["action"] for a in audit] == ["SUBMIT", "APPROVE", "APPROVE", "APPROVE"]
    assert audit[0]["actor_id"] == SUBMITTER
    assert [a["actor_id"] for a in audit[1:]] == [APPROVER1, APPROVER2, APPROVER3]
    assert [a["level"] for a in audit[1:]] == [1, 2, 3]


# ---------------------------------------------------------------------------
# TC-C02 (P0): 第 1 级驳回 → 单据立即 REJECTED，后续级 SKIPPED
# ---------------------------------------------------------------------------

def test_c02_first_level_reject_skips_rest(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, APPROVER3)
    t1 = task_at_level(inst, 1)

    updated = reject(client, t1["id"], t1["version"], "docs incomplete", hdr(APPROVER1)).json()

    assert updated["status"] == "REJECTED"
    assert task_at_level(updated, 1)["status"] == "REJECTED"
    assert task_at_level(updated, 2)["status"] == "SKIPPED"
    assert task_at_level(updated, 3)["status"] == "SKIPPED"
    # 后续级审批人从未收到待办
    assert notifications(client, hdr(APPROVER2)) == []
    assert notifications(client, hdr(APPROVER3)) == []


# ---------------------------------------------------------------------------
# TC-C03 (P0): 多级中途拒绝 — 第 2/3 级驳回；第 1 级 APPROVED 保留，第 3 级永不激活
# ---------------------------------------------------------------------------

def test_c03_mid_level_reject(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, APPROVER3)
    t1 = task_at_level(inst, 1)
    clock.advance(hours=1)
    inst = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    t2 = task_at_level(inst, 2)

    clock.advance(hours=1)
    updated = reject(client, t2["id"], t2["version"], "quota exceeded", hdr(APPROVER2)).json()

    assert updated["status"] == "REJECTED"
    assert task_at_level(updated, 1)["status"] == "APPROVED"   # 历史级保留
    assert task_at_level(updated, 2)["status"] == "REJECTED"
    t3 = task_at_level(updated, 3)
    assert t3["status"] == "SKIPPED"                            # 终态作废未激活任务 (D1)
    assert t3["activated_at"] is None                           # 永不激活
    assert notifications(client, hdr(APPROVER3)) == []


# ---------------------------------------------------------------------------
# TC-C04 (P0): 越级操作（任务 WAITING）→ 409 STATE_CONFLICT (D11 归类)
# ---------------------------------------------------------------------------

def test_c04_out_of_order_approval_is_state_conflict(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t2 = task_at_level(inst, 2)

    resp = approve(client, t2["id"], t2["version"], hdr(APPROVER2), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    assert task_at_level(reloaded, 1)["status"] == "PENDING"
    assert task_at_level(reloaded, 2)["status"] == "WAITING"


# ---------------------------------------------------------------------------
# TC-C05 (P1): 重复审批 → 409 STATE_CONFLICT（状态检查先于版本检查，D11）
# ---------------------------------------------------------------------------

def test_c05_duplicate_approval_is_state_conflict(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    approve(client, t1["id"], t1["version"], hdr(APPROVER1))

    reloaded = get_instance(client, inst["id"]).json()
    t1_fresh = task_at_level(reloaded, 1)
    assert t1_fresh["status"] == "APPROVED"

    # 用最新 version 重试仍是 STATE_CONFLICT：任务已终态，状态检查先于版本 (D11)
    resp = approve(client, t1_fresh["id"], t1_fresh["version"], hdr(APPROVER1), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")

    after = get_instance(client, inst["id"]).json()
    assert after["status"] == "PENDING"
    assert task_at_level(after, 2)["status"] == "PENDING"  # 第 2 级未被重复激活


# ---------------------------------------------------------------------------
# TC-C06 (P0): 非该级审批人操作 → 403 FORBIDDEN，状态不变
# ---------------------------------------------------------------------------

def test_c06_non_approver_is_forbidden(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)

    for actor in (OUTSIDER, APPROVER2, APPROVER3):
        resp = approve(client, t1["id"], t1["version"], hdr(actor), expect=None)
        assert_error(resp, 403, "FORBIDDEN")
        resp = reject(client, t1["id"], t1["version"], "nope", hdr(actor), expect=None)
        assert_error(resp, 403, "FORBIDDEN")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    assert task_at_level(reloaded, 1)["status"] == "PENDING"


# ---------------------------------------------------------------------------
# TC-C07 (P1, D3②): 自审批 — 自动回避 / 全链自审拒绝 / 防御层 403
# ---------------------------------------------------------------------------

def test_c07a_self_approval_level_auto_skipped(client):
    # 第 1 级审批人 == 提交人 → 提交即自动 SKIPPED 并顺延激活第 2 级
    inst = submit_configured(client, "leave", SUBMITTER, APPROVER2)

    t1 = task_at_level(inst, 1)
    t2 = task_at_level(inst, 2)
    assert t1["status"] == "SKIPPED"
    assert t2["status"] == "PENDING"
    assert inst["status"] == "PENDING"

    # 审计记 SKIP，系统 actor=null，comment=AUTO_SKIP_SELF_APPROVAL (D3)
    skips = [a for a in audit_log(client, inst["id"]) if a["action"] == "SKIP"]
    assert len(skips) == 1
    assert skips[0]["actor_id"] is None
    assert skips[0]["comment"] == "AUTO_SKIP_SELF_APPROVAL"
    assert skips[0]["level"] == 1

    # 被跳过的提交人不会收到 TASK_ASSIGNED；第 2 级审批人收到
    assert "TASK_ASSIGNED" not in kinds_of(notifications(client, hdr(SUBMITTER)))
    assert "TASK_ASSIGNED" in kinds_of(notifications(client, hdr(APPROVER2)))


def test_c07b_all_levels_self_is_422(client):
    configure_route(client, "leave", SUBMITTER, SUBMITTER)
    resp = submit(client, approval_type="leave", expect=None)
    assert_error(resp, 422, "ROUTING_ALL_SELF")

    from tests.helpers import instance_view

    assert instance_view(client, "submitted", hdr(SUBMITTER)) == []


def test_c07c_submitter_approving_own_instance_is_403_self_approval(client):
    # 防御层 (D3): 提交人对自己单据的 approve/reject → 403 SELF_APPROVAL_FORBIDDEN
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    resp = approve(client, t1["id"], t1["version"], hdr(SUBMITTER), expect=None)
    assert_error(resp, 403, "SELF_APPROVAL_FORBIDDEN")
    resp = reject(client, t1["id"], t1["version"], "self reject", hdr(SUBMITTER), expect=None)
    assert_error(resp, 403, "SELF_APPROVAL_FORBIDDEN")

    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    assert task_at_level(reloaded, 1)["status"] == "PENDING"


# ---------------------------------------------------------------------------
# TC-C08 (P0): 终态不可变 — 任何操作 → 409 STATE_CONFLICT
# ---------------------------------------------------------------------------

def _terminal_approved(client) -> dict:
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)
    return approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()


def _terminal_rejected(client) -> dict:
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)
    return reject(client, t1["id"], t1["version"], "no", hdr(APPROVER1)).json()


def _terminal_withdrawn(client) -> dict:
    inst = submit_configured(client, "leave", APPROVER1)
    return withdraw(client, inst["id"], inst["version"], None, hdr(SUBMITTER)).json()


TERMINAL_FIXTURES = {
    "APPROVED": _terminal_approved,
    "REJECTED": _terminal_rejected,
    "WITHDRAWN": _terminal_withdrawn,
}


@pytest.mark.parametrize("terminal", sorted(TERMINAL_FIXTURES))
def test_c08_terminal_states_are_immutable(client, terminal):
    inst = TERMINAL_FIXTURES[terminal](client)
    assert inst["status"] == terminal
    fresh = get_instance(client, inst["id"]).json()
    t1 = task_at_level(fresh, 1)

    resp = approve(client, t1["id"], t1["version"], hdr(APPROVER1), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    resp = reject(client, t1["id"], t1["version"], "late", hdr(APPROVER1), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    resp = withdraw(client, fresh["id"], fresh["version"], None, hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    resp = escalate(client, fresh["id"], hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    if terminal != "REJECTED":
        # D5: 仅 REJECTED 可重提；其余终态重提 → STATE_CONFLICT
        resp = resubmit(client, fresh["id"], expect=None)
        assert_error(resp, 409, "STATE_CONFLICT")

    after = get_instance(client, inst["id"]).json()
    assert after["status"] == terminal
    assert after["version"] == fresh["version"]  # 失败操作不产生写


# ---------------------------------------------------------------------------
# TC-C09 (P1): 单级审批与多级共用状态机
# ---------------------------------------------------------------------------

def test_c09_single_level_chain(client):
    inst = submit_configured(client, "leave", APPROVER1)
    assert len(inst["tasks"]) == 1
    t1 = task_at_level(inst, 1)

    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "APPROVED"
    assert task_at_level(updated, 1)["status"] == "APPROVED"
    assert [a["action"] for a in audit_log(client, updated["id"])] == ["SUBMIT", "APPROVE"]


# ---------------------------------------------------------------------------
# D11: 错误检查顺序 404 → 403 → 409 STATE_CONFLICT → 409 VERSION_CONFLICT
# ---------------------------------------------------------------------------

def test_d11_order_404_before_403(client):
    # 不存在的任务 + 无权操作人 → 404 优先
    resp = approve(client, 424242, 0, hdr(OUTSIDER), expect=None)
    assert_error(resp, 404, "NOT_FOUND")
    resp = reject(client, 424242, 0, "x", hdr(OUTSIDER), expect=None)
    assert_error(resp, 404, "NOT_FOUND")


def test_d11_order_403_before_409(client):
    # 终态单据的任务 + 无权操作人 → 403 优先于 STATE_CONFLICT
    inst = _terminal_approved(client)
    t1 = task_at_level(inst, 1)
    resp = approve(client, t1["id"], t1["version"], hdr(OUTSIDER), expect=None)
    assert_error(resp, 403, "FORBIDDEN")


def test_d11_order_state_before_version(client):
    # 终态单据 + 提交人（过 403）+ 错误 version → STATE_CONFLICT 优先于 VERSION_CONFLICT
    inst = _terminal_approved(client)
    resp = withdraw(client, inst["id"], inst["version"] + 7, None, hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")


@pytest.mark.parametrize(
    "method,url",
    [
        ("get", "/api/v1/instances/424242"),
        ("get", "/api/v1/instances/424242/audit"),
    ],
)
def test_d11_not_found_reads(client, method, url):
    resp = getattr(client, method)(url, headers=hdr(SUBMITTER))
    assert_error(resp, 404, "NOT_FOUND")


def test_d11_not_found_writes(client):
    resp = withdraw(client, 424242, 0, None, hdr(SUBMITTER), expect=None)
    assert_error(resp, 404, "NOT_FOUND")
    resp = escalate(client, 424242, hdr(SUBMITTER), expect=None)
    assert_error(resp, 404, "NOT_FOUND")
    resp = resubmit(client, 424242, expect=None)
    assert_error(resp, 404, "NOT_FOUND")


# ---------------------------------------------------------------------------
# D11: 请求体缺 version → 422 VALIDATION_ERROR（契约层已生效，回归钉住）
# ---------------------------------------------------------------------------

def test_d11_missing_version_is_422(client):
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    resp = client.post(
        f"/api/v1/tasks/{t1['id']}/approve", json={}, headers=hdr(APPROVER1)
    )
    assert_error(resp, 422, "VALIDATION_ERROR")
    resp = client.post(
        f"/api/v1/tasks/{t1['id']}/reject",
        json={"comment": "x"},
        headers=hdr(APPROVER1),
    )
    assert_error(resp, 422, "VALIDATION_ERROR")
    resp = client.post(
        f"/api/v1/instances/{inst['id']}/withdraw", json={}, headers=hdr(SUBMITTER)
    )
    assert_error(resp, 422, "VALIDATION_ERROR")
