"""Section B — 自动路由 (TC-B01~B05) + 路由配置契约 (D2/D8).

Contract refs: D2 (快照路由、缺配 422 ROUTING_NOT_CONFIGURED、无静默兜底),
D8 (v1 每级恰好一人，配置校验强制), D14 (TASK_ASSIGNED 通知).
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    ALL_TYPES,
    APPROVER1,
    APPROVER2,
    OUTSIDER_H,
    SUBMITTER,
    assert_error,
    assert_status,
    configure_route,
    get_instance,
    hdr,
    kinds_of,
    notifications,
    put_route,
    submit,
    submit_configured,
    task_at_level,
)


# ---------------------------------------------------------------------------
# TC-B01 (P0): 7 类各自路由到自己配置的负责人（矩阵）
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("approval_type", ALL_TYPES)
def test_b01_routing_matrix_routes_to_configured_owner(client, approval_type):
    owner = f"u-owner-of-{approval_type}"
    inst = submit_configured(client, approval_type, owner)

    t1 = task_at_level(inst, 1)
    assert t1["approver_id"] == owner
    assert t1["status"] == "PENDING"

    # D14: 第 1 级激活 → 该级审批人收到 TASK_ASSIGNED
    notifs = notifications(client, hdr(owner))
    assert kinds_of(notifs) == ["TASK_ASSIGNED"]
    assert notifs[0]["recipient_id"] == owner
    assert notifs[0]["instance_id"] == inst["id"]


# ---------------------------------------------------------------------------
# TC-B02 (P1): 提交时快照路由配置；修改配置只影响新单，在途单不变
# ---------------------------------------------------------------------------

def test_b02_route_config_change_does_not_affect_in_flight(client):
    configure_route(client, "leave", APPROVER1)
    in_flight = submit(client, approval_type="leave").json()
    assert task_at_level(in_flight, 1)["approver_id"] == APPROVER1

    # 修改配置 → 新提交走新负责人
    configure_route(client, "leave", APPROVER2)
    fresh = submit(client, approval_type="leave").json()
    assert task_at_level(fresh, 1)["approver_id"] == APPROVER2

    # 在途单任务快照不变（重新读取详情核验）
    reloaded = get_instance(client, in_flight["id"]).json()
    assert task_at_level(reloaded, 1)["approver_id"] == APPROVER1


# ---------------------------------------------------------------------------
# TC-B03 (P1, D2①): 无路由配置 → 422 ROUTING_NOT_CONFIGURED，不创建单据，无兜底
# ---------------------------------------------------------------------------

def test_b03_missing_route_config_is_422_and_creates_nothing(client):
    resp = submit(client, approval_type="leave", expect=None)
    assert_error(resp, 422, "ROUTING_NOT_CONFIGURED")

    assert instance_view_empty(client)


def instance_view_empty(client) -> bool:
    from tests.helpers import instance_view

    return instance_view(client, "submitted", hdr(SUBMITTER)) == []


def test_b03_other_types_unaffected_by_one_config(client):
    # 仅配置 leave；expense 提交仍缺配报错
    configure_route(client, "leave", APPROVER1)
    resp = submit(client, approval_type="expense", expect=None)
    assert_error(resp, 422, "ROUTING_NOT_CONFIGURED")


# ---------------------------------------------------------------------------
# TC-B04 (P0): 第 N 级通过 → 自动激活并路由第 N+1 级，无需提交人操作
# ---------------------------------------------------------------------------

def test_b04_next_level_auto_activates(client, clock):
    from tests.helpers import approve, parse_dt

    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, "u-approver-3")
    t1 = task_at_level(inst, 1)
    t2_before = task_at_level(inst, 2)
    assert t2_before["status"] == "WAITING"
    assert t2_before["activated_at"] is None

    clock.advance(hours=2)
    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()

    assert updated["status"] == "PENDING"  # 单据未终结
    t2 = task_at_level(updated, 2)
    assert t2["status"] == "PENDING"
    assert t2["approver_id"] == APPROVER2
    assert parse_dt(t2["activated_at"]) == clock.now()
    # 第 3 级仍未轮到
    assert task_at_level(updated, 3)["status"] == "WAITING"

    # D14: 第 2 级激活 → APPROVER2 收到 TASK_ASSIGNED
    assert "TASK_ASSIGNED" in kinds_of(notifications(client, hdr(APPROVER2)))


# ---------------------------------------------------------------------------
# TC-B05 (P1): 加急不改变路由结果，仅附加标记
# ---------------------------------------------------------------------------

def test_b05_urgent_does_not_change_routing(client):
    configure_route(client, "leave", APPROVER1, APPROVER2)
    normal = submit(client, approval_type="leave").json()
    urgent = submit(client, approval_type="leave", is_urgent=True).json()

    assert task_at_level(normal, 1)["approver_id"] == task_at_level(urgent, 1)["approver_id"]
    assert task_at_level(normal, 2)["approver_id"] == task_at_level(urgent, 2)["approver_id"]
    assert urgent["is_urgent"] is True
    assert normal["is_urgent"] is False


# ---------------------------------------------------------------------------
# 路由配置契约 (D8): 每级恰好一人、级数 1~10、非空 id；查询语义
# ---------------------------------------------------------------------------

INVALID_LEVELS_CASES = [
    ([["a", "b"]], "每级两人（会签 v1 不支持，D8）"),
    ([[]], "空级"),
    ([[""]], "空白审批人 id"),
    ([["  "]], "全空白审批人 id"),
    ([], "levels 为空"),
    ([[f"u{i}"] for i in range(11)], "超过 10 级"),
]


@pytest.mark.parametrize("levels,_desc", INVALID_LEVELS_CASES)
def test_b08_invalid_route_config_is_422(client, levels, _desc):
    resp = put_route(client, "leave", levels, expect=None)
    assert_error(resp, 422, "VALIDATION_ERROR")


def test_b08_route_config_roundtrip(client, clock):
    from tests.helpers import parse_dt

    resp = configure_route(client, "leave", APPROVER1, APPROVER2)
    cfg = resp.json()
    assert cfg["type"] == "leave"
    assert cfg["levels"] == [[APPROVER1], [APPROVER2]]
    assert parse_dt(cfg["updated_at"]) == clock.now()

    got = client.get("/api/v1/config/routes/leave", headers=OUTSIDER_H)
    assert_status(got, 200)
    assert got.json()["levels"] == [[APPROVER1], [APPROVER2]]

    listed = client.get("/api/v1/config/routes", headers=OUTSIDER_H)
    assert_status(listed, 200)
    assert any(c["type"] == "leave" for c in listed.json())


def test_b08_get_unconfigured_route_is_404(client):
    resp = client.get("/api/v1/config/routes/leave", headers=OUTSIDER_H)
    assert_error(resp, 404, "NOT_FOUND")
