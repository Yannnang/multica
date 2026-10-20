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
# TC-A09 (P1): 非法值 → 422，错误指向字段（v0.2.x 契约层已生效，回归钉住）
#   金额为整数分（D17-2）；「needle」= 错误封装中必须出现的字段名/语义关键词，
#   模型级(跨字段)校验的 msg 由 app/schemas/payloads.py 文案钉住。
# ---------------------------------------------------------------------------

INVALID_VALUE_CASES = [
    # (type, payload 覆盖, 错误应引用的字段/语义)
    # leave：end ≥ start、reason ≥5、>3 日历日需 handover_person（附录 A）
    ("leave", {"end_date": "2026-11-01"}, "end_date"),
    ("leave", {"reason": "trip"}, "reason"),
    ("leave", {"start_date": "2026-11-02", "end_date": "2026-11-07"}, "handover_person"),
    # expense：items[]{name/incurred_on/amount_cents>0} + description(≥5)
    #          + payee_account(10~32 位数字) + invoice_numbers(8~20 位字母数字)
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": 0}]}, "amount_cents"),
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": -5}]}, "amount_cents"),
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2025-12-20", "amount_cents": 5_000_001}]}, "amount_cents"),  # 单行即超 5,000,000 分上限
    ("expense", {"items": []}, "items"),
    ("expense", {"description": "trip"}, "description"),
    ("expense", {"payee_account": "12345"}, "payee_account"),               # <10 位
    ("expense", {"payee_account": "62220212345abc890"}, "payee_account"),   # 非纯数字
    ("expense", {"invoice_numbers": ["INV001"]}, "invoice_numbers"),         # <8 位
    ("expense", {"invoice_numbers": ["INV20250001", "INV20250001"]}, "duplicate invoice number"),  # 请求内去重
    # procurement：item_category 枚举 + items[]{quantity 1~9999, unit_price_cents≥0}
    #              + 0 < 总额 ≤ 1,000,000,000 分
    ("procurement", {"item_category": "vehicle"}, "item_category"),
    ("procurement", {"items": [{"name": "laptop", "quantity": 0, "unit_price_cents": 750000}]}, "quantity"),
    ("procurement", {"items": [{"name": "laptop", "quantity": 10000, "unit_price_cents": 1}]}, "quantity"),
    ("procurement", {"items": [{"name": "laptop", "quantity": 1, "unit_price_cents": -1}]}, "unit_price_cents"),
    ("procurement", {"items": [{"name": "laptop", "quantity": 1, "unit_price_cents": 0}]}, "greater than 0"),
    ("procurement", {"items": [{"name": "gpu", "quantity": 9999, "unit_price_cents": 100_000_000}]}, "exceeds cap"),  # 总额 999,900,000,000 > 上限
    ("procurement", {"reason": "buy"}, "reason"),
    # contract：contract_type/currency 枚举 + end > start + 0 金额需 amount_note + summary ≥5
    ("contract", {"start_date": "2026-05-01", "end_date": "2026-04-01"}, "end_date"),
    ("contract", {"start_date": "2026-05-01", "end_date": "2026-05-01"}, "end_date"),  # 相等也不行（end > start）
    ("contract", {"contract_type": "lease"}, "contract_type"),
    ("contract", {"contract_amount_cents": -1}, "contract_amount_cents"),
    ("contract", {"contract_amount_cents": 10_000_000_001}, "contract_amount_cents"),
    ("contract", {"contract_amount_cents": 0, "amount_note": None}, "amount_note"),  # 框架合同需说明
    ("contract", {"currency": "BTC"}, "currency"),
    ("contract", {"summary": "ok"}, "summary"),
    # permission：reason ≥5、duration_days 1~365（D17 拍板⑤）
    ("permission", {"reason": "ops"}, "reason"),
    ("permission", {"duration_days": 0}, "duration_days"),
    ("permission", {"duration_days": 366}, "duration_days"),
    # server_provisioning：枚举档位 / disk 50~10000 步进 50 / service_name regex /
    #                      os_type 白名单 / 端口 22、3389 → network_note 必填 / 端口去重
    ("server_provisioning", {"cpu_cores": 3}, "cpu_cores"),
    ("server_provisioning", {"memory_gb": 24}, "memory_gb"),
    ("server_provisioning", {"disk_gb": 75}, "disk_gb"),                    # 非 50 步进
    ("server_provisioning", {"disk_gb": 10050}, "disk_gb"),                 # 超上限
    ("server_provisioning", {"os_type": "macos"}, "os_type"),
    ("server_provisioning", {"environment": "uat"}, "environment"),
    ("server_provisioning", {"service_name": "Bad_Name"}, "service_name"),  # regex ^[a-z0-9][a-z0-9-]{1,63}$
    ("server_provisioning", {"service_name": "a"}, "service_name"),         # 最短 2 字符
    ("server_provisioning", {"open_ports": [22]}, "network_note"),
    ("server_provisioning", {"open_ports": [3389]}, "network_note"),
    ("server_provisioning", {"open_ports": [80, 80]}, "open_ports"),        # 去重
    ("server_provisioning", {"open_ports": [0]}, "open_ports"),             # 1~65535
    # account_provisioning：account_type 枚举；shared/service → owner_id + valid_until 必填
    ("account_provisioning", {"account_type": "enterprise"}, "account_type"),
    ("account_provisioning", {"account_type": "shared", "owner_id": None}, "owner_id"),
    ("account_provisioning", {"account_type": "shared", "valid_until": None}, "valid_until"),
    ("account_provisioning", {"account_type": "service", "owner_id": None}, "owner_id"),
    # 信封/payload extra=forbid（D17-3：服务端计算字段不接受入参）
    ("leave", {"unknown_field": "x"}, "unknown_field"),
    ("leave", {"duration_days": 3}, "duration_days"),
    ("expense", {"total_amount_cents": 100}, "total_amount_cents"),
]


@pytest.mark.parametrize("approval_type,override,bad_field", INVALID_VALUE_CASES)
def test_a09_invalid_values_are_422(client, approval_type, override, bad_field):
    payload = {**VALID_PAYLOADS[approval_type], **override}
    resp = submit(client, approval_type=approval_type, payload=payload, expect=None)
    assert_field_error(resp, bad_field)


# 边界含等号 → 合法（当前红：submit 501 级联；实现后应 201，金额断言一律用分 D17-2）
@pytest.mark.parametrize(
    "approval_type,override",
    [
        # 报销总额 = 5,000,000 分（含等号）
        ("expense", {"items": [{"name": "hotel", "incurred_on": "2025-12-20", "amount_cents": 5_000_000}]}),
        # 采购总额 = 10 × 100,000,000 = 1,000,000,000 分（含等号）
        ("procurement", {"items": [{"name": "gpu", "quantity": 10, "unit_price_cents": 100_000_000}]}),
        # 合同金额 = 10,000,000,000 分（含等号）
        ("contract", {"contract_amount_cents": 10_000_000_000}),
        ("permission", {"duration_days": 365}),
        ("permission", {"duration_days": 1}),
        ("server_provisioning", {"disk_gb": 50}),
        ("server_provisioning", {"disk_gb": 10000}),
        # 恰好 3 个日历日（含首尾）无需 handover_person
        ("leave", {"start_date": "2026-11-02", "end_date": "2026-11-04"}),
        # 4 个日历日 + handover_person → 合法
        ("leave", {"start_date": "2026-11-02", "end_date": "2026-11-05", "handover_person": "u-backup-1"}),
    ],
)
def test_a09_inclusive_boundaries_are_valid(client, approval_type, override):
    configure_route(client, approval_type, APPROVER1)
    payload = {**VALID_PAYLOADS[approval_type], **override}
    resp = submit(client, approval_type=approval_type, payload=payload)
    assert_status(resp, 201)


# ---------------------------------------------------------------------------
# TC-A09b (D17-4): 信封 title 可选（1~100）
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("bad_title", ["", "x" * 101])
def test_a09_title_boundaries_are_422(client, bad_title):
    body = {
        "type": "leave",
        "is_urgent": False,
        "title": bad_title,
        "payload": VALID_PAYLOADS["leave"],
    }
    resp = submit(client, body=body, expect=None)
    assert_field_error(resp, "title")


def test_a09_optional_title_and_omitted_title_accepted(client):
    configure_route(client, "leave", APPROVER1)
    body = {
        "type": "leave",
        "is_urgent": False,
        "title": "annual leave request",
        "payload": VALID_PAYLOADS["leave"],
    }
    resp = submit(client, body=body)
    assert_status(resp, 201)
    # 省略 title 同样合法（服务端兜底生成，D17-4；InstanceOut 不回显 title，不断言文案）
    resp2 = submit(client, approval_type="leave")
    assert_status(resp2, 201)


# ---------------------------------------------------------------------------
# TC-A09c (D17 附录/拍板③⑤): 相对日期校验 — 服务层按注入时钟执行
#   FakeClock 起点 2026-01-01（tests/conftest.py）。
#   当前红：submit 501 级联；实现后反例 → 422 且 details 指明字段，正例 → 201。
# ---------------------------------------------------------------------------

RELATIVE_DATE_INVALID_CASES = [
    # (type, payload 覆盖, 422 应指向的字段, 语义)
    ("procurement", {"expected_delivery_date": "2025-12-31"}, "expected_delivery_date"),  # < 今天
    ("server_provisioning", {"expected_ready_date": "2026-01-01"}, "expected_ready_date"),  # < 今天+1
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2026-01-02", "amount_cents": 1000}]}, "incurred_on"),  # > 今天
    ("account_provisioning", {"valid_until": "2027-01-02"}, "valid_until"),  # > 今天+365
]


@pytest.mark.parametrize("approval_type,override,bad_field", RELATIVE_DATE_INVALID_CASES)
def test_a09_relative_date_violations_are_422(client, approval_type, override, bad_field):
    configure_route(client, approval_type, APPROVER1)
    payload = {**VALID_PAYLOADS[approval_type], **override}
    resp = submit(client, approval_type=approval_type, payload=payload, expect=None)
    assert_field_error(resp, bad_field)


RELATIVE_DATE_VALID_CASES = [
    ("procurement", {"expected_delivery_date": "2026-01-01"}),   # = 今天（含等号）
    ("server_provisioning", {"expected_ready_date": "2026-01-02"}),  # = 今天+1（含等号）
    ("expense", {"items": [{"name": "taxi", "incurred_on": "2026-01-01", "amount_cents": 1000}]}),  # = 今天
    ("account_provisioning", {"valid_until": "2027-01-01"}),     # = 今天+365（含等号）
]


@pytest.mark.parametrize("approval_type,override", RELATIVE_DATE_VALID_CASES)
def test_a09_relative_date_boundaries_are_valid(client, approval_type, override):
    configure_route(client, approval_type, APPROVER1)
    payload = {**VALID_PAYLOADS[approval_type], **override}
    resp = submit(client, approval_type=approval_type, payload=payload)
    assert_status(resp, 201)


def test_a09_relative_dates_follow_injected_clock(client, clock):
    # 同一 payload：提交时 expected_delivery_date ≥ 今天 → 合法；
    # 时钟推进越过该日期后再提交 → 422（校验取 app.state.clock，D15/D17）。
    configure_route(client, "procurement", APPROVER1)
    payload = {**VALID_PAYLOADS["procurement"], "expected_delivery_date": "2026-01-05"}

    resp_now = submit(client, approval_type="procurement", payload=payload, expect=None)
    assert_status(resp_now, 201)

    clock.advance(days=10)  # 今天 = 2026-01-11 → 2026-01-05 已成过去
    resp_later = submit(client, approval_type="procurement", payload=payload, expect=None)
    assert_field_error(resp_later, "expected_delivery_date")


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
