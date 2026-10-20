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


def test_business_endpoint_stub_returns_501_envelope(client, submitter_headers):
    resp = client.post("/api/v1/instances", json=VALID_LEAVE_BODY, headers=submitter_headers)
    assert resp.status_code == 501
    body = resp.json()
    assert body["error"]["code"] == "NOT_IMPLEMENTED"


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
