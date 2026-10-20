"""Shared helpers for the behavioral (red) test suite — P0 + P1 state machine.

Assertions follow docs/decisions.md (D1~D16), the single contract source.
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

# v1 minimal valid payloads (decisions.md 附录 A / app/schemas/payloads.py)
VALID_PAYLOADS = {
    "leave": {
        "leave_type": "annual",
        "start_date": "2026-11-02",
        "end_date": "2026-11-04",
        "reason": "annual leave",
    },
    "expense": {
        "expense_type": "travel",
        "amount": "1200.50",
        "description": "conference trip",
    },
    "procurement": {
        "item_name": "laptop",
        "quantity": 2,
        "estimated_amount": "15000",
        "reason": "team equipment refresh",
    },
    "contract": {
        "contract_name": "NDA with Acme",
        "counterparty": "Acme Ltd",
        "contract_amount": "500000",
    },
    "permission": {
        "system_name": "wiki",
        "permission_name": "editor",
        "reason": "docs maintenance",
    },
    "server_provisioning": {
        "environment": "prod",
        "cpu_cores": 8,
        "memory_gb": 16,
        "disk_gb": 200,
        "purpose": "api server",
    },
    "account_provisioning": {
        "system_name": "wiki",
        "account_type": "service",
        "purpose": "ci bot account",
    },
}

# One required field per type, dropped by TC-A08 parametrization
REQUIRED_FIELD_PER_TYPE = {
    "leave": "leave_type",
    "expense": "amount",
    "procurement": "quantity",
    "contract": "counterparty",
    "permission": "permission_name",
    "server_provisioning": "memory_gb",
    "account_provisioning": "account_type",
}

# One sentinel field per type used to verify payload round-trip after submit
SENTINEL_FIELD_PER_TYPE = {
    "leave": ("leave_type", "annual"),
    "expense": ("expense_type", "travel"),
    "procurement": ("item_name", "laptop"),
    "contract": ("counterparty", "Acme Ltd"),
    "permission": ("permission_name", "editor"),
    "server_provisioning": ("environment", "prod"),
    "account_provisioning": ("account_type", "service"),
}

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


def submit(client, body: dict | None = None, approval_type: str = "leave",
           payload: dict | None = None, is_urgent: bool = False,
           headers: dict | None = None, idem_key: str | None = None,
           expect: int | None = 201):
    if body is None:
        body = {
            "type": approval_type,
            "is_urgent": is_urgent,
            "payload": VALID_PAYLOADS[approval_type] if payload is None else payload,
        }
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
             headers: dict | None = None, expect: int | None = 201):
    if body is None:
        body = {
            "type": approval_type,
            "is_urgent": is_urgent,
            "payload": VALID_PAYLOADS[approval_type] if payload is None else payload,
        }
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
                      headers: dict | None = None, idem_key: str | None = None) -> dict:
    """configure_route + submit + assert 201; returns the created instance dict."""
    configure_route(client, approval_type, *approvers)
    resp = submit(
        client,
        approval_type=approval_type,
        payload=payload,
        is_urgent=is_urgent,
        headers=headers,
        idem_key=idem_key,
    )
    return resp.json()
