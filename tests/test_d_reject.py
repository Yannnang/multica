"""Section D — 驳回 (TC-D01~D03).

Contract refs: D4 (驳回意见必填，缺失/空白 → 422；驳回通知携带意见全文),
D5 (驳回后重提 = 新单 resubmit_of 关联，从第 1 级重走，原单保持 REJECTED),
D10 (resubmit 仅原提交人), D11 (检查顺序), D14 (REJECTED 通知 / REJECT 审计).
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    APPROVER1,
    APPROVER2,
    OUTSIDER,
    SUBMITTER,
    VALID_PAYLOADS,
    approve,
    assert_error,
    assert_status,
    audit_log,
    configure_route,
    get_instance,
    hdr,
    kinds_of,
    notifications,
    reject,
    resubmit,
    submit_configured,
    task_at_level,
)


# ---------------------------------------------------------------------------
# TC-D01 (P0): 驳回附意见 → REJECTED；通知含意见全文；审计记录 actor+comment
# ---------------------------------------------------------------------------

def test_d01_reject_with_comment(client, clock):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    comment = "duplicate invoices found: INV-001"

    clock.advance(hours=3)
    updated = reject(client, t1["id"], t1["version"], comment, hdr(APPROVER1)).json()

    assert updated["status"] == "REJECTED"
    t1_after = task_at_level(updated, 1)
    assert t1_after["status"] == "REJECTED"
    assert t1_after["comment"] == comment
    from tests.helpers import parse_dt

    assert parse_dt(t1_after["acted_at"]) == clock.now()

    # D4/D14: 提交人收到 REJECTED 通知，消息含驳回意见全文
    sub_notifs = notifications(client, hdr(SUBMITTER))
    rejected_notifs = [n for n in sub_notifs if n["kind"] == "REJECTED"]
    assert len(rejected_notifs) == 1
    assert comment in rejected_notifs[0]["message"]
    assert rejected_notifs[0]["actor_id"] == APPROVER1
    assert rejected_notifs[0]["instance_id"] == inst["id"]

    # D14: 审计 REJECT 含 actor 与意见
    rejects = [a for a in audit_log(client, inst["id"]) if a["action"] == "REJECT"]
    assert len(rejects) == 1
    assert rejects[0]["actor_id"] == APPROVER1
    assert rejects[0]["comment"] == comment
    assert rejects[0]["level"] == 1


# ---------------------------------------------------------------------------
# TC-D02 (P1, D4③): 驳回意见必填 — 缺失/空串/纯空白 → 422 VALIDATION_ERROR
# ---------------------------------------------------------------------------

@pytest.mark.parametrize(
    "body_override",
    [
        {},                          # 缺 comment（契约层已生效）
        {"comment": ""},             # 空串（契约层已生效）
        {"comment": "   "},          # D4: 纯空白等同缺失 → 422
        {"comment": "\t\n"},
    ],
    ids=["missing", "empty", "blank-spaces", "blank-whitespace"],
)
def test_d02_reject_requires_non_blank_comment(client, body_override):
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)
    # version 用任务当前真实值，确保失败原因只能是 comment 校验（排除 VERSION_CONFLICT 干扰）
    body = {**body_override, "version": t1["version"]}

    resp = client.post(
        f"/api/v1/tasks/{t1['id']}/reject", json=body, headers=hdr(APPROVER1)
    )
    assert_error(resp, 422, "VALIDATION_ERROR")

    # 驳回未生效
    reloaded = get_instance(client, inst["id"]).json()
    assert reloaded["status"] == "PENDING"
    assert task_at_level(reloaded, 1)["status"] == "PENDING"


def test_d02_approve_comment_optional(client):
    # D4: 同意意见可选 — 不带 comment 的 approve 必须成功
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)
    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "APPROVED"
    assert task_at_level(updated, 1)["comment"] is None


# ---------------------------------------------------------------------------
# TC-D03 (P1, D5④): 驳回后重提 = 新单，resubmit_of 关联，从第 1 级按当前配置重走
# ---------------------------------------------------------------------------

def test_d03_resubmit_after_reject_creates_new_instance(client):
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    reject(client, t1["id"], t1["version"], "fix the dates", hdr(APPROVER1))

    # 重提前修改路由配置 → 新单按新配置快照（D2）
    configure_route(client, "leave", APPROVER2)

    new_payload = {**VALID_PAYLOADS["leave"], "reason": "annual leave (corrected)"}
    resp = resubmit(client, inst["id"], payload=new_payload)
    assert_status(resp, 201)
    new = resp.json()

    assert new["id"] != inst["id"]
    assert new["resubmit_of"] == inst["id"]
    assert new["status"] == "PENDING"
    assert new["type"] == "leave"
    assert new["payload"]["reason"] == "annual leave (corrected)"
    assert len(new["tasks"]) == 1  # 新配置只有 1 级
    assert task_at_level(new, 1)["approver_id"] == APPROVER2

    # 原单保持 REJECTED 终态不变（D1/D5）
    original = get_instance(client, inst["id"]).json()
    assert original["status"] == "REJECTED"


def test_d03_resubmit_permissions_and_state_rules(client):
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)
    reject(client, t1["id"], t1["version"], "no", hdr(APPROVER1))

    # 仅原提交人可重提（D5/D10）
    resp = resubmit(client, inst["id"], headers=hdr(OUTSIDER), expect=None)
    assert_error(resp, 403, "FORBIDDEN")
    resp = resubmit(client, inst["id"], headers=hdr(APPROVER1), expect=None)
    assert_error(resp, 403, "FORBIDDEN")


@pytest.mark.parametrize("state", ["PENDING", "APPROVED", "WITHDRAWN"])
def test_d03_resubmit_requires_rejected_original(client, state):
    # 原单非 REJECTED → 409 STATE_CONFLICT（D5）
    if state == "PENDING":
        inst = submit_configured(client, "leave", APPROVER1)
    elif state == "APPROVED":
        inst = submit_configured(client, "leave", APPROVER1)
        t1 = task_at_level(inst, 1)
        inst = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    else:
        from tests.helpers import withdraw

        inst = submit_configured(client, "leave", APPROVER1)
        inst = withdraw(client, inst["id"], inst["version"], None, hdr(SUBMITTER)).json()

    resp = resubmit(client, inst["id"], expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
