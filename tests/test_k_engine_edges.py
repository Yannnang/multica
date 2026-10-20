"""Section K — engine edge regression tests found during implementation.

Pins behavior the A~J baseline does not directly cover:
- D3 trailing self-approval cascade (last level(s) == submitter → auto-SKIP,
  instance APPROVED with the triggering approver as final actor)
- D17 service-layer relative-date validation against the injected clock
- D17-5 dedup excludes REJECTED/WITHDRAWN instances (so D5 resubmit works)
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    APPROVER1,
    SUBMITTER,
    VALID_PAYLOADS,
    approve,
    assert_field_error,
    audit_log,
    configure_route,
    hdr,
    reject,
    submit,
    submit_configured,
    task_at_level,
)


# ---------------------------------------------------------------------------
# D3: trailing self-approval levels auto-skip; instance finalizes APPROVED
# ---------------------------------------------------------------------------

def test_k_trailing_self_level_auto_skips_to_approved(client):
    inst = submit_configured(client, "leave", APPROVER1, SUBMITTER)
    assert task_at_level(inst, 1)["status"] == "PENDING"
    assert task_at_level(inst, 2)["status"] == "WAITING"

    t1 = task_at_level(inst, 1)
    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()

    assert updated["status"] == "APPROVED"
    t2 = task_at_level(updated, 2)
    assert t2["status"] == "SKIPPED"
    assert t2["activated_at"] is None  # 永不激活

    skips = [a for a in audit_log(client, updated["id"]) if a["action"] == "SKIP"]
    assert len(skips) == 1
    assert skips[0]["actor_id"] is None
    assert skips[0]["comment"] == "AUTO_SKIP_SELF_APPROVAL"
    assert skips[0]["level"] == 2


# ---------------------------------------------------------------------------
# D17: service-layer relative-date validation (injected clock)
# ---------------------------------------------------------------------------

def test_k_procurement_delivery_date_before_today_is_422(client):
    configure_route(client, "procurement", APPROVER1)
    payload = {**VALID_PAYLOADS["procurement"], "expected_delivery_date": "2025-12-31"}
    resp = submit(client, approval_type="procurement", payload=payload, expect=None)
    assert_field_error(resp, "expected_delivery_date")


def test_k_server_ready_date_must_be_tomorrow_or_later(client):
    configure_route(client, "server_provisioning", APPROVER1)
    # FakeClock 今天 = 2026-01-01：ready date = 今天 → 422；今天+1 → 201
    payload = {**VALID_PAYLOADS["server_provisioning"], "expected_ready_date": "2026-01-01"}
    resp = submit(client, approval_type="server_provisioning", payload=payload, expect=None)
    assert_field_error(resp, "expected_ready_date")

    ok = {**VALID_PAYLOADS["server_provisioning"], "expected_ready_date": "2026-01-02",
          "service_name": "other-service"}
    resp = submit(client, approval_type="server_provisioning", payload=ok)
    assert resp.status_code == 201


def test_k_expense_incurred_on_future_is_422(client):
    configure_route(client, "expense", APPROVER1)
    payload = {
        **VALID_PAYLOADS["expense"],
        "items": [{"name": "taxi", "incurred_on": "2026-01-02", "amount_cents": 1000}],
        "invoice_numbers": ["INV20260002"],
    }
    resp = submit(client, approval_type="expense", payload=payload, expect=None)
    assert_field_error(resp, "incurred_on")


def test_k_relative_dates_follow_clock_advance(client, clock):
    # 时钟推进后，原「明天」的 ready date 变成「今天」→ 422（校验以提交时刻的时钟为准）
    configure_route(client, "server_provisioning", APPROVER1)
    payload = {**VALID_PAYLOADS["server_provisioning"], "expected_ready_date": "2026-01-02"}
    assert submit(client, approval_type="server_provisioning", payload=payload).status_code == 201

    clock.advance(days=2)  # 今天 = 2026-01-03
    payload2 = {**payload, "service_name": "later-service"}
    resp = submit(client, approval_type="server_provisioning", payload=payload2, expect=None)
    assert_field_error(resp, "expected_ready_date")


# ---------------------------------------------------------------------------
# D17-5: dedup only counts PENDING/APPROVED — rejected instance frees the key
# ---------------------------------------------------------------------------

def test_k_invoice_dedup_blocks_active_then_frees_after_reject(client):
    configure_route(client, "expense", APPROVER1)
    first = submit(client, approval_type="expense").json()

    # 同发票号在途单 → 409 INVOICE_DUPLICATE
    resp = submit(client, approval_type="expense", expect=None)
    assert resp.status_code == 409
    assert resp.json()["error"]["code"] == "INVOICE_DUPLICATE"

    # 驳回后同发票号可重新提交（D5 重提依赖此语义）
    t1 = task_at_level(first, 1)
    reject(client, t1["id"], t1["version"], "wrong invoice", hdr(APPROVER1))
    again = submit(client, approval_type="expense")
    assert again.status_code == 201
    assert again.json()["id"] != first["id"]


def test_k_duplicate_service_name_blocks_active(client):
    configure_route(client, "server_provisioning", APPROVER1)
    submit(client, approval_type="server_provisioning")
    resp = submit(client, approval_type="server_provisioning", expect=None)
    assert resp.status_code == 409
    assert resp.json()["error"]["code"] == "DUPLICATE_SERVICE_NAME"


def test_k_duplicate_personal_account_blocks_same_submitter(client):
    configure_route(client, "account_provisioning", APPROVER1)
    payload = {
        "system_name": "wiki",
        "account_type": "personal",
        "purpose": "personal editor account",
    }
    submit(client, approval_type="account_provisioning", payload=payload)
    resp = submit(client, approval_type="account_provisioning", payload=payload, expect=None)
    assert resp.status_code == 409
    assert resp.json()["error"]["code"] == "DUPLICATE_ACCOUNT"

    # 其他提交人不受影响
    resp2 = submit(client, approval_type="account_provisioning", payload=payload,
                   headers=hdr("u-submitter-2"))
    assert resp2.status_code == 201


# ---------------------------------------------------------------------------
# D17-3: computed fields land in the stored payload
# ---------------------------------------------------------------------------

@pytest.mark.parametrize(
    "approval_type,field,expected",
    [
        ("leave", "duration_days", 3),                      # 11-02..11-04 含首尾
        ("expense", "total_amount_cents", 120050),
        ("procurement", "total_amount_cents", 1500000),     # 2 × 750000
    ],
)
def test_k_server_computed_fields_in_payload(client, approval_type, field, expected):
    inst = submit_configured(client, approval_type, APPROVER1)
    assert inst["payload"][field] == expected
