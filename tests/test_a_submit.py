"""Section A — 提交与表单校验 (TC-A01~A11).

Contract refs: decisions.md 附录 A (payload 字段集), D9 (幂等), D10 (鉴权头),
D12 (错误封装), D13 (编号规则).

Red-state expectations at scaffold stage:
- 输入校验类断言（422 VALIDATION_ERROR）契约层已生效 → 可能已绿；
- 成功提交（201）与「未创建单据」的列表核验依赖业务实现 → 当前 501 红。
"""
from __future__ import annotations

import pytest

from tests.helpers import (
    ALL_TYPES,
    APPROVER1,
    PREFIX_BY_TYPE,
    REQUIRED_FIELD_PER_TYPE,
    SENTINEL_FIELD_PER_TYPE,
    SUBMITTER,
    VALID_PAYLOADS,
    assert_error,
    assert_field_error,
    assert_status,
    configure_route,
    hdr,
    instance_view,
    parse_dt,
    submit,
    submit_configured,
    task_at_level,
)

# FakeClock 起点（tests/conftest.py）：2026-01-01T00:00:00+00:00 → 编号日期段
CLOCK_DAY = "20260101"


# ---------------------------------------------------------------------------
# TC-A01~A07 (P0): 7 类合法提交 → 201 PENDING，第 1 级激活并路由到配置负责人
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("approval_type", ALL_TYPES)
def test_a01_a07_submit_valid_payload_all_types(client, clock, approval_type):
    configure_route(client, approval_type, APPROVER1)

    resp = submit(client, approval_type=approval_type)
    assert_status(resp, 201)
    # D9: 首次提交非幂等重放
    assert resp.headers.get("Idempotent-Replayed") is None

    inst = resp.json()
    assert inst["type"] == approval_type
    assert inst["status"] == "PENDING"
    assert inst["submitter_id"] == SUBMITTER  # D10: 操作人取自 X-User-Id
    assert inst["is_urgent"] is False
    assert inst["resubmit_of"] is None
    # D13: {PREFIX}-{YYYYMMDD}-{当日序号:04d}，fresh DB 首单序号 0001
    assert inst["number"] == f"{PREFIX_BY_TYPE[approval_type]}-{CLOCK_DAY}-0001"
    assert parse_dt(inst["created_at"]) == clock.now()
    assert isinstance(inst["version"], int)

    # payload 回读：哨兵字段保持提交值
    field, value = SENTINEL_FIELD_PER_TYPE[approval_type]
    assert inst["payload"][field] == value

    # 快照生成任务并激活第 1 级（D1/D2）
    assert len(inst["tasks"]) == 1
    t1 = task_at_level(inst, 1)
    assert t1["approver_id"] == APPROVER1
    assert t1["status"] == "PENDING"
    assert parse_dt(t1["activated_at"]) == clock.now()
    assert t1["acted_at"] is None
    assert t1["reminders_sent"] == 0


# ---------------------------------------------------------------------------
# TC-A08 (P0): 缺必填字段 → 422 VALIDATION_ERROR 且未创建单据
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("approval_type", ALL_TYPES)
def test_a08_missing_required_field_is_422_and_creates_nothing(client, approval_type):
    configure_route(client, approval_type, APPROVER1)
    payload = dict(VALID_PAYLOADS[approval_type])
    missing = REQUIRED_FIELD_PER_TYPE[approval_type]
    del payload[missing]

    resp = submit(client, approval_type=approval_type, payload=payload, expect=None)
    assert_field_error(resp, missing)

    # 未创建审批单
    assert instance_view(client, "submitted", hdr(SUBMITTER)) == []


# ---------------------------------------------------------------------------
# TC-A09 (P1): 非法值 → 422，错误指向字段（契约层已生效，回归钉住）
# ---------------------------------------------------------------------------

INVALID_VALUE_CASES = [
    # (type, payload 覆盖, 错误应引用的字段) — v0.2.0 契约（金额整数分，明细行）
    ("leave", {"end_date": "2026-11-01"}, "end_date"),                      # end < start
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": 0}]}, "amount_cents"),   # 非正数
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": -5}]}, "amount_cents"),
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": 5000001}]}, "amount"),  # 超上限 5,000,000 分
    ("procurement", {"items": [{"name": "laptop", "quantity": 0, "unit_price_cents": 750000}]}, "quantity"),     # ≥1
    ("procurement", {"items": [{"name": "laptop", "quantity": 9999, "unit_price_cents": 100000000}]}, "amount"),  # 超上限 1,000,000,000 分
    ("contract", {"start_date": "2026-05-01", "end_date": "2026-04-01"}, "end_date"),
    ("contract", {"contract_amount_cents": 0}, "contract_amount_cents"),     # =0 需 amount_note
    ("permission", {"duration_days": 0}, "duration_days"),                    # 1~365
    ("permission", {"duration_days": 366}, "duration_days"),
    ("server_provisioning", {"cpu_cores": 3}, "cpu_cores"),                   # 枚举档位 {2,4,8,16,32}
    ("server_provisioning", {"memory_gb": 2049}, "memory_gb"),                # 枚举档位
    ("server_provisioning", {"environment": "uat"}, "environment"),           # 枚举白名单
    ("leave", {"unknown_field": "x"}, "unknown_field"),                       # extra=forbid
]


@pytest.mark.parametrize("approval_type,override,bad_field", INVALID_VALUE_CASES)
def test_a09_invalid_values_are_422(client, approval_type, override, bad_field):
    payload = {**VALID_PAYLOADS[approval_type], **override}
    resp = submit(client, approval_type=approval_type, payload=payload, expect=None)
    assert_field_error(resp, bad_field)


@pytest.mark.parametrize(
    "approval_type,override",
    [
        # 上限含等号 → 合法（v0.2.0：金额一律整数分）
        ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": 5000000}]}),
        ("permission", {"duration_days": 365}),       # 上限含等号 → 合法
        ("procurement", {"items": [{"name": "laptop", "quantity": 1, "unit_price_cents": 750000}]}),  # 下限含等号 → 合法
    ],
)
def test_a09_inclusive_boundaries_are_valid(client, approval_type, override):
    configure_route(client, approval_type, APPROVER1)
    payload = {**VALID_PAYLOADS[approval_type], **override}
    resp = submit(client, approval_type=approval_type, payload=payload)
    assert_status(resp, 201)


# ---------------------------------------------------------------------------
# TC-A10 (P1): 未知审批类型 → 422（提交与路由配置两个入口）
# ---------------------------------------------------------------------------

def test_a10_unknown_type_on_submit_is_422(client):
    resp = submit(
        client,
        body={"type": "holiday", "is_urgent": False, "payload": {}},
        expect=None,
    )
    assert_error(resp, 422, "VALIDATION_ERROR")


def test_a10_unknown_type_on_route_config_is_422(client):
    from tests.helpers import put_route

    resp = put_route(client, "holiday", [["u-x"]], expect=None)
    assert_error(resp, 422, "VALIDATION_ERROR")


# ---------------------------------------------------------------------------
# TC-A11 (D9 拍板): Idempotency-Key 幂等语义
#   同 (submitter, key) 24h 内重放 → 200 + Idempotent-Replayed: true + 原单；
#   首次 → 201；无 key 允许重复。
# ---------------------------------------------------------------------------

def test_a11_idempotency_key_replay_returns_original(client, clock):
    inst1 = submit_configured(client, "leave", APPROVER1, idem_key="idem-k1")

    # 同提交人同 key 重放 → 200 + 重放头 + 原单，且不新增单据
    resp2 = submit(client, approval_type="leave", idem_key="idem-k1", expect=None)
    assert_status(resp2, 200)
    assert resp2.headers.get("Idempotent-Replayed") == "true"
    assert resp2.json()["id"] == inst1["id"]
    assert len(instance_view(client, "submitted", hdr(SUBMITTER))) == 1

    # 同提交人不同 key → 新单
    resp3 = submit(client, approval_type="leave", idem_key="idem-k2")
    assert_status(resp3, 201)
    assert resp3.json()["id"] != inst1["id"]

    # 不同提交人同 key → 新单（key 按提交人隔离）
    resp4 = submit(client, approval_type="leave", idem_key="idem-k1",
                   headers=hdr("u-submitter-2"))
    assert_status(resp4, 201)
    assert resp4.json()["id"] != inst1["id"]

    # 24h 窗口过期 → 新单（D9: 24h 内重放）
    clock.advance(hours=24, minutes=5)
    resp5 = submit(client, approval_type="leave", idem_key="idem-k1")
    assert_status(resp5, 201)
    assert resp5.json()["id"] != inst1["id"]


def test_a11_without_key_duplicates_allowed(client):
    submit_configured(client, "leave", APPROVER1)
    resp_a = submit(client, approval_type="leave")
    resp_b = submit(client, approval_type="leave")
    assert_status(resp_a, 201)
    assert_status(resp_b, 201)
    assert resp_a.json()["id"] != resp_b.json()["id"]
    assert len(instance_view(client, "submitted", hdr(SUBMITTER))) == 3


# ---------------------------------------------------------------------------
# D10: 鉴权头缺失/空白 → 422 VALIDATION_ERROR
# ---------------------------------------------------------------------------

def test_d10_missing_actor_header_is_422(client):
    resp = client.post(
        "/api/v1/instances",
        json={"type": "leave", "is_urgent": False, "payload": VALID_PAYLOADS["leave"]},
    )
    assert_error(resp, 422, "VALIDATION_ERROR")


def test_d10_blank_actor_header_is_422(client):
    # D10: 缺失/空白 → 422。空白头（" "）不得视为有效操作人。
    resp = submit(client, approval_type="leave", headers=hdr(" "), expect=None)
    assert_error(resp, 422, "VALIDATION_ERROR")
