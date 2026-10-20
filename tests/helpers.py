"""Shared helpers for the behavioral (red) test suite — P0 + P1 state machine.

Assertions follow docs/decisions.md (v0.2.1: D1~D17 + 附录 A), the single
contract source. Payload shapes match app/schemas/payloads.py (v0.2.x:
integer cents, itemized expense/procurement, conditional-required fields).
Helpers assert their own setup steps so a failure names the broken contract
point (e.g. "setup submit returned 501") instead of cascading KeyErrors.

Conventions:
- Actor ids match tests/conftest.py fixtures (u-submitter-1 / u-approver-1).
- All time assertions go through the injected FakeClock (D15, no sleep).
- Error assertions pin the unified envelope {"error": {code, message, details}} (D12).
"""
from __future__ import annotations

import json
from datetime import datetime

# ---------- contract constants (decisions.md D13 / enums.py) ----------

PREFIX_BY_TYPE = {
    "leave": "LV",
    "expense": "EX",
    "procurement": "PR",
    "contract": "CT",
    "permission": "PM",
    "server_provisioning": "SV",
    "account_provisioning": "AC",
}

ALL_TYPES = list(PREFIX_BY_TYPE)

# v1 minimal valid payloads (decisions.md v0.2.1 附录 A / app/schemas/payloads.py)
#
# 日期取值约定（FakeClock 起点 2026-01-01，见 tests/conftest.py）：
# - expense items.incurred_on ≤ 今天 → 取过去日期（服务层按注入时钟校验，D17）；
# - procurement expected_delivery_date ≥ 今天、server expected_ready_date ≥ 今天+1
#   → 取年末远期日期（现有用例最多推进时钟 7 天，不会越过）；
# - account valid_until ≤ 今天+365 → 取 2026-06-30。
VALID_PAYLOADS = {
    "leave": {
        "leave_type": "annual",
        "start_date": "2026-11-02",
        "end_date": "2026-11-04",
        "reason": "annual leave",
    },
    "expense": {
        "expense_type": "travel",
        "items": [
            {"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": 120050},
        ],
        "description": "conference trip",
        "payee_account": "6222021234567890",
        "invoice_numbers": ["INV20250001"],
    },
    "procurement": {
        "item_category": "equipment",
        "items": [
            {"name": "laptop", "quantity": 2, "unit_price_cents": 750000},
        ],
        "expected_delivery_date": "2026-12-31",
        "reason": "team equipment refresh",
    },
    "contract": {
        "contract_type": "nda",
        "contract_name": "NDA with Acme",
        "counterparty": "Acme Ltd",
        "contract_amount_cents": 50000000,
        "currency": "CNY",
        "start_date": "2026-02-01",
        "end_date": "2027-01-31",
        "summary": "mutual NDA for partnership talks",
    },
    "permission": {
        "system_name": "wiki",
        "permission_name": "editor",
        "reason": "docs maintenance",
    },
    "server_provisioning": {
        "environment": "prod",
        "service_name": "api-gateway",
        "cpu_cores": 8,
        "memory_gb": 16,
        "disk_gb": 200,
        "os_type": "ubuntu-22.04",
        "open_ports": [8080],
        "expected_ready_date": "2026-12-31",
        "purpose": "api server rollout",
    },
    "account_provisioning": {
        "system_name": "wiki",
        "account_type": "service",
        "owner_id": "u-owner-1",
        "valid_until": "2026-06-30",
        "purpose": "ci bot account",
    },
}

# One required field per type, dropped by TC-A08 parametrization
REQUIRED_FIELD_PER_TYPE = {
    "leave": "leave_type",
    "expense": "items",
    "procurement": "item_category",
    "contract": "counterparty",
    "permission": "permission_name",
    "server_provisioning": "memory_gb",
    "account_provisioning": "account_type",
}

# One sentinel field per type used to verify payload round-trip after submit
SENTINEL_FIELD_PER_TYPE = {
    "leave": ("leave_type", "annual"),
    "expense": ("expense_type", "travel"),
    "procurement": ("item_category", "equipment"),
    "contract": ("counterparty", "Acme Ltd"),
    "permission": ("permission_name", "editor"),
    "server_provisioning": ("environment", "prod"),
    "account_provisioning": ("account_type", "service"),
}

# D17-4: is_urgent=true 时信封 urgent_reason 必填（5~200）。
DEFAULT_URGENT_REASON = "escalated: production blocking issue"

# ---------- actors (match conftest fixtures) ----------

SUBMITTER = "u-submitter-1"
APPROVER1 = "u-approver-1"
APPROVER2 = "u-approver-2"
APPROVER3 = "u-approver-3"
OUTSIDER = "u-outsider-9"
SYSTEM_ACTOR = "u-system-cron"


def hdr(user: str) -> dict[str, str]:
    return {"X-User-Id": user}


SUBMITTER_H = hdr(SUBMITTER)
APPROVER1_H = hdr(APPROVER1)
APPROVER2_H = hdr(APPROVER2)
APPROVER3_H = hdr(APPROVER3)
OUTSIDER_H = hdr(OUTSIDER)
SYSTEM_H = hdr(SYSTEM_ACTOR)


# ---------- response assertions (D12 envelope) ----------

def assert_status(resp, expected: int):
    assert resp.status_code == expected, (
        f"expected HTTP {expected}, got {resp.status_code}: {resp.text}"
    )


def assert_error(resp, status: int, code: str) -> dict:
    """Assert unified error envelope and return the error object."""
    assert_status(resp, status)
    body = resp.json()
    assert "error" in body, f"missing error envelope: {body}"
    err = body["error"]
    assert err["code"] == code, f"expected error code {code}, got: {err}"
    assert "message" in err and "details" in err, f"envelope incomplete: {err}"
    return err


def assert_field_error(resp, field: str) -> None:
    """422 VALIDATION_ERROR whose envelope references `field` somewhere."""
    err = assert_error(resp, 422, "VALIDATION_ERROR")
    assert field in json.dumps(err, ensure_ascii=False), (
        f"field {field!r} not referenced in error envelope: {err}"
    )


def parse_dt(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


# ---------- endpoint wrappers ----------

def put_route(client, approval_type: str, levels: list, headers: dict | None = None,
              expect: int | None = 200):
    resp = client.put(
        f"/api/v1/config/routes/{approval_type}",
        json={"levels": levels},
        headers=headers or SUBMITTER_H,
    )
    if expect is not None:
        assert_status(resp, expect)
    return resp


def configure_route(client, approval_type: str, *approvers: str):
    """One approver per level (D8): configure_route(client, 'leave', A1, A2)."""
    return put_route(client, approval_type, [[a] for a in approvers])


def _make_body(approval_type: str, payload: dict | None, is_urgent: bool,
               urgent_reason: str | None = None) -> dict:
    """Envelope builder — D17-4: is_urgent=true 时必须携带合法 urgent_reason。"""
    body: dict = {
        "type": approval_type,
        "is_urgent": is_urgent,
        "payload": VALID_PAYLOADS[approval_type] if payload is None else payload,
    }
    if is_urgent:
        body["urgent_reason"] = urgent_reason or DEFAULT_URGENT_REASON
    elif urgent_reason is not None:
        body["urgent_reason"] = urgent_reason
    return body


def submit(client, body: dict | None = None, approval_type: str = "leave",
           payload: dict | None = None, is_urgent: bool = False,
           urgent_reason: str | None = None,
           headers: dict | None = None, idem_key: str | None = None,
           expect: int | None = 201):
    if body is None:
        body = _make_body(approval_type, payload, is_urgent, urgent_reason)
    h = dict(headers or SUBMITTER_H)
    if idem_key is not None:
        h["Idempotency-Key"] = idem_key
    resp = client.post("/api/v1/instances", json=body, headers=h)
    if expect is not None:
        assert_status(resp, expect)
    return resp


def get_instance(client, iid: int, headers: dict | None = None, expect: int | None = 200):
    resp = client.get(f"/api/v1/instances/{iid}", headers=headers or SUBMITTER_H)
    if expect is not None:
        assert_status(resp, expect)
    return resp


def instance_view(client, view: str, headers: dict) -> list:
    """GET /api/v1/instances?view=submitted|todo (D14/TC-F02 semantics)."""
    resp = client.get("/api/v1/instances", params={"view": view}, headers=headers)
    assert_status(resp, 200)
    return resp.json()


def approve(client, task_id: int, version: int, headers: dict | None = None,
            comment: str | None = None, expect: int | None = 200):
    body: dict = {"version": version}
    if comment is not None:
        body["comment"] = comment
    resp = client.post(
        f"/api/v1/tasks/{task_id}/approve", json=body, headers=headers or APPROVER1_H
    )
    if expect is not None:
        assert_status(resp, expect)
    return resp


def reject(client, task_id: int, version: int, comment: str = "budget insufficient",
           headers: dict | None = None, expect: int | None = 200):
    resp = client.post(
        f"/api/v1/tasks/{task_id}/reject",
        json={"comment": comment, "version": version},
        headers=headers or APPROVER1_H,
    )
    if expect is not None:
        assert_status(resp, expect)
    return resp


def withdraw(client, iid: int, version: int, reason: str | None = None,
             headers: dict | None = None, expect: int | None = 200):
    body: dict = {"version": version}
    if reason is not None:
        body["reason"] = reason
    resp = client.post(
        f"/api/v1/instances/{iid}/withdraw", json=body, headers=headers or SUBMITTER_H
    )
    if expect is not None:
        assert_status(resp, expect)
    return resp


def escalate(client, iid: int, headers: dict | None = None, expect: int | None = 200):
    resp = client.post(
        f"/api/v1/instances/{iid}/escalate-urgent", headers=headers or SUBMITTER_H
    )
    if expect is not None:
        assert_status(resp, expect)
    return resp


def resubmit(client, iid: int, body: dict | None = None, approval_type: str = "leave",
             payload: dict | None = None, is_urgent: bool = False,
             urgent_reason: str | None = None,
             headers: dict | None = None, expect: int | None = 201):
    # D11 (v0.2.1 修订): resubmit 的 body 即 InstanceCreate，不携带 version。
    if body is None:
        body = _make_body(approval_type, payload, is_urgent, urgent_reason)
    resp = client.post(
        f"/api/v1/instances/resubmit/{iid}", json=body, headers=headers or SUBMITTER_H
    )
    if expect is not None:
        assert_status(resp, expect)
    return resp


def scan(client, headers: dict | None = None, expect: int | None = 200):
    """POST /api/v1/system/timeout-scan (D7 scan-style, injected clock)."""
    resp = client.post("/api/v1/system/timeout-scan", headers=headers or SYSTEM_H)
    if expect is not None:
        assert_status(resp, expect)
    return resp


def notifications(client, headers: dict) -> list:
    resp = client.get("/api/v1/notifications", headers=headers)
    assert_status(resp, 200)
    return resp.json()


def audit_log(client, iid: int, headers: dict | None = None) -> list:
    resp = client.get(
        f"/api/v1/instances/{iid}/audit", headers=headers or SUBMITTER_H
    )
    assert_status(resp, 200)
    return resp.json()


# ---------- domain helpers ----------

def task_at_level(instance: dict, level: int) -> dict:
    matches = [t for t in instance["tasks"] if t["level"] == level]
    assert len(matches) == 1, (
        f"expected exactly one task at level {level}, got {instance['tasks']}"
    )
    return matches[0]


def kinds_of(notifs: list) -> list[str]:
    return [n["kind"] for n in notifs]


def submit_configured(client, approval_type: str = "leave", *approvers: str,
                      is_urgent: bool = False, payload: dict | None = None,
                      urgent_reason: str | None = None,
                      headers: dict | None = None, idem_key: str | None = None) -> dict:
    """configure_route + submit + assert 201; returns the created instance dict."""
    configure_route(client, approval_type, *approvers)
    resp = submit(
        client,
        approval_type=approval_type,
        payload=payload,
        is_urgent=is_urgent,
        urgent_reason=urgent_reason,
        headers=headers,
        idem_key=idem_key,
    )
    return resp.json()
