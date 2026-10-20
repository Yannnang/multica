"""Scaffold baseline: what is already GREEN at contract level before implementation.

These are not the P0/P1 behavioral tests (test agent will land those as failing
tests). They pin the scaffold invariants:
1. app boots, health works;
2. OpenAPI contains all contracted paths;
3. business endpoints uniformly return the 501 NOT_IMPLEMENTED envelope;
4. request validation + unified error envelope already work (422 VALIDATION_ERROR).
"""
from __future__ import annotations

from tests.conftest import VALID_LEAVE_BODY

CONTRACTED_PATHS = [
    "/api/v1/health",
    "/api/v1/instances",
    "/api/v1/instances/{instance_id}",
    "/api/v1/instances/{instance_id}/withdraw",
    "/api/v1/instances/{instance_id}/escalate-urgent",
    "/api/v1/instances/{instance_id}/audit",
    "/api/v1/instances/resubmit/{instance_id}",
    "/api/v1/tasks/{task_id}/approve",
    "/api/v1/tasks/{task_id}/reject",
    "/api/v1/config/routes",
    "/api/v1/config/routes/{approval_type}",
    "/api/v1/system/timeout-scan",
    "/api/v1/notifications",
]


def test_health(client):
    resp = client.get("/api/v1/health")
    assert resp.status_code == 200
    assert resp.json()["status"] == "ok"


def test_openapi_contains_all_contracted_paths(client):
    paths = client.get("/openapi.json").json()["paths"]
    for path in CONTRACTED_PATHS:
        assert path in paths, f"contract path missing from OpenAPI: {path}"


def test_business_endpoints_are_implemented_no_501_stubs(client, submitter_headers):
    # D12: NOT_IMPLEMENTED 是脚手架期占位，实现完成后消失——合法提交不再返回 501。
    # 无路由配置 → 422 ROUTING_NOT_CONFIGURED（业务已实现且契约错误封装不变）。
    resp = client.post("/api/v1/instances", json=VALID_LEAVE_BODY, headers=submitter_headers)
    assert resp.status_code == 422
    body = resp.json()
    assert body["error"]["code"] == "ROUTING_NOT_CONFIGURED"


def test_unknown_approval_type_is_422_validation_error(client, submitter_headers):
    resp = client.post(
        "/api/v1/instances",
        json={"type": "holiday", "payload": {}},
        headers=submitter_headers,
    )
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "VALIDATION_ERROR"


def test_payload_cross_validation_end_before_start_is_422(client, submitter_headers):
    body = {
        "type": "leave",
        "payload": {
            "leave_type": "annual",
            "start_date": "2026-11-05",
            "end_date": "2026-11-01",
            "reason": "x",
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "VALIDATION_ERROR"


def test_missing_required_payload_field_is_422(client, submitter_headers):
    resp = client.post(
        "/api/v1/instances",
        json={"type": "leave", "payload": {"leave_type": "annual", "start_date": "2026-11-02"}},
        headers=submitter_headers,
    )
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "VALIDATION_ERROR"


def test_missing_actor_header_is_422(client):
    resp = client.post("/api/v1/instances", json=VALID_LEAVE_BODY)
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "VALIDATION_ERROR"


def test_reject_requires_comment_at_contract_level(client, approver_headers):
    # RejectRequest.comment min_length=1 — empty comment fails schema validation (D4)
    resp = client.post(
        "/api/v1/tasks/1/reject",
        json={"comment": "", "version": 0},
        headers=approver_headers,
    )
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "VALIDATION_ERROR"


# ---- v0.2.0 contract additions (D17: frontend form spec reconciliation) ----


def test_urgent_without_urgent_reason_is_422(client, submitter_headers):
    body = dict(VALID_LEAVE_BODY, is_urgent=True)
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422
    assert resp.json()["error"]["code"] == "VALIDATION_ERROR"


def test_leave_over_3_days_requires_handover_person(client, submitter_headers):
    body = {
        "type": "leave",
        "payload": {
            "leave_type": "annual",
            "start_date": "2026-11-02",
            "end_date": "2026-11-06",  # 5 calendar days > 3
            "reason": "long trip",
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422


def test_expense_item_amount_must_be_positive_cents(client, submitter_headers):
    body = {
        "type": "expense",
        "payload": {
            "expense_type": "travel",
            "items": [{"name": "taxi", "incurred_on": "2026-10-01", "amount_cents": 0}],
            "description": "october travel expenses",
            "payee_account": "6222021234567890",
            "invoice_numbers": ["INV20260001"],
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422


def test_expense_rejects_server_computed_total_field(client, submitter_headers):
    # D17-3: computed fields are not accepted as input (extra=forbid)
    body = {
        "type": "expense",
        "payload": {
            "expense_type": "travel",
            "items": [{"name": "taxi", "incurred_on": "2026-10-01", "amount_cents": 1000}],
            "description": "october travel expenses",
            "payee_account": "6222021234567890",
            "invoice_numbers": ["INV20260001"],
            "total_amount_cents": 1000,
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422


def test_server_cpu_must_be_enum_tier(client, submitter_headers):
    body = {
        "type": "server_provisioning",
        "payload": {
            "environment": "prod",
            "service_name": "api-gateway",
            "cpu_cores": 3,  # not in {2,4,8,16,32}
            "memory_gb": 8,
            "disk_gb": 100,
            "os_type": "ubuntu-22.04",
            "expected_ready_date": "2026-11-01",
            "purpose": "gateway rollout",
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422


def test_server_port_22_requires_network_note(client, submitter_headers):
    body = {
        "type": "server_provisioning",
        "payload": {
            "environment": "staging",
            "service_name": "bastion-host",
            "cpu_cores": 2,
            "memory_gb": 4,
            "disk_gb": 75,  # not a multiple of 50 → also invalid; fixed below via note test
            "os_type": "ubuntu-22.04",
            "open_ports": [22],
            "expected_ready_date": "2026-11-01",
            "purpose": "ssh bastion",
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422  # disk step + missing network_note

    body["payload"]["disk_gb"] = 100
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422  # still missing network_note for port 22


def test_account_shared_requires_owner_and_valid_until(client, submitter_headers):
    body = {
        "type": "account_provisioning",
        "payload": {
            "system_name": "crm",
            "account_type": "shared",
            "purpose": "team shared inbox account",
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422


def test_contract_zero_amount_requires_amount_note(client, submitter_headers):
    body = {
        "type": "contract",
        "payload": {
            "contract_type": "nda",
            "contract_name": "framework nda",
            "counterparty": "Acme Ltd",
            "contract_amount_cents": 0,
            "start_date": "2026-11-01",
            "end_date": "2027-10-31",
            "summary": "framework NDA without fixed amount",
        },
    }
    resp = client.post("/api/v1/instances", json=body, headers=submitter_headers)
    assert resp.status_code == 422
