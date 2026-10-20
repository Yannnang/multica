"""Section J — 飞书终态弹窗通知 (J1~J7, decisions.md D18).

Contract refs: D18 (触发点=终态 APPROVED/REJECTED，WITHDRAWN 不发；弹窗=交互卡片
send_card + 应用内加急 urgent_app；失败/无映射降级不阻塞终态，审计
FEISHU_SENT/FEISHU_FAILED/FEISHU_SKIPPED；每单至多发送一次)。

注入约定（MYIT-12 落测规格）：`create_app(clock=..., database_url=...,
feishu_client=FakeFeishu(), feishu_user_map={...})`；FeishuClient 协议：
`send_card(open_id, card) -> message_id`、`urgent_app(open_id, message_id) -> None`。
"""
from __future__ import annotations

import json

import pytest
from fastapi.testclient import TestClient

from app.main import create_app
from tests.helpers import (
    APPROVER1,
    APPROVER2,
    APPROVER3,
    SUBMITTER,
    approve,
    audit_log,
    configure_route,
    hdr,
    kinds_of,
    notifications,
    reject,
    submit,
    submit_configured,
    task_at_level,
    withdraw,
)

SUBMITTER_OPEN_ID = "ou-submitter-001"


class FakeFeishu:
    """Recording stand-in for app.services.feishu.FeishuClient (J 节落测规格)."""

    def __init__(self, fail_send: bool = False, fail_urgent: bool = False) -> None:
        self.send_calls: list[tuple[str, dict]] = []     # (open_id, card)
        self.urgent_calls: list[tuple[str, str]] = []    # (open_id, message_id)
        self.send_attempts = 0
        self._fail_send = fail_send
        self._fail_urgent = fail_urgent

    def send_card(self, open_id: str, card: dict) -> str:
        self.send_attempts += 1
        if self._fail_send:
            raise RuntimeError("feishu open platform unreachable")
        self.send_calls.append((open_id, card))
        return f"om_fake_{len(self.send_calls):04d}"

    def urgent_app(self, open_id: str, message_id: str) -> None:
        if self._fail_urgent:
            raise RuntimeError("urgent api rejected")
        self.urgent_calls.append((open_id, message_id))


@pytest.fixture()
def feishu_factory(clock):
    """Build (client, fake) with an injected FakeFeishu + user map."""

    def _make(fake: FakeFeishu | None = None, user_map: dict | None = None):
        fake = fake or FakeFeishu()
        app = create_app(
            clock=clock,
            database_url="sqlite+pysqlite:///:memory:",
            feishu_client=fake,
            feishu_user_map={SUBMITTER: SUBMITTER_OPEN_ID} if user_map is None else user_map,
        )
        return TestClient(app), fake

    return _make


def _card_text(card: dict) -> str:
    return json.dumps(card, ensure_ascii=False)


def _feishu_audits(client, iid: int) -> list[dict]:
    return [a for a in audit_log(client, iid) if a["action"].startswith("FEISHU_")]


# ---------------------------------------------------------------------------
# J1 (P0): 三级全通过 → 恰好 1 次 send_card + 1 次 urgent_app；审计 FEISHU_SENT
# ---------------------------------------------------------------------------

def test_j1_full_approval_sends_one_card_and_urgent(feishu_factory):
    client, fake = feishu_factory()
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2, APPROVER3)

    for level, approver in [(1, APPROVER1), (2, APPROVER2), (3, APPROVER3)]:
        t = task_at_level(inst, level)
        inst = approve(client, t["id"], t["version"], hdr(approver)).json()
    assert inst["status"] == "APPROVED"

    assert len(fake.send_calls) == 1
    open_id, card = fake.send_calls[0]
    assert open_id == SUBMITTER_OPEN_ID
    text = _card_text(card)
    assert inst["number"] in text          # 编号
    assert "leave" in text                 # 类型（raw value）
    assert "APPROVED" in text              # 结果
    assert fake.urgent_calls == [(SUBMITTER_OPEN_ID, "om_fake_0001")]

    audits = _feishu_audits(client, inst["id"])
    assert [a["action"] for a in audits] == ["FEISHU_SENT"]
    assert audits[0]["actor_id"] is None   # 系统渠道动作

    # 站内终态通知不受影响（D14）
    assert "APPROVED" in kinds_of(notifications(client, hdr(SUBMITTER)))


# ---------------------------------------------------------------------------
# J2 (P0): 任一级驳回 → 1 次 send_card + urgent_app；卡片结果=REJECTED 含意见
# ---------------------------------------------------------------------------

def test_j2_reject_sends_card_with_comment(feishu_factory):
    client, fake = feishu_factory()
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)
    comment = "quota exceeded for this quarter"

    updated = reject(client, t1["id"], t1["version"], comment, hdr(APPROVER1)).json()
    assert updated["status"] == "REJECTED"

    assert len(fake.send_calls) == 1
    text = _card_text(fake.send_calls[0][1])
    assert updated["number"] in text
    assert "REJECTED" in text
    assert comment in text                 # 卡片携带驳回意见全文
    assert len(fake.urgent_calls) == 1

    assert [a["action"] for a in _feishu_audits(client, updated["id"])] == ["FEISHU_SENT"]


# ---------------------------------------------------------------------------
# J3 (P0): 撤回 → 零调用（WITHDRAWN 不发弹窗）
# ---------------------------------------------------------------------------

def test_j3_withdraw_sends_nothing(feishu_factory):
    client, fake = feishu_factory()
    inst = submit_configured(client, "leave", APPROVER1)

    withdrawn = withdraw(client, inst["id"], inst["version"], "changed mind", hdr(SUBMITTER)).json()
    assert withdrawn["status"] == "WITHDRAWN"

    assert fake.send_calls == []
    assert fake.urgent_calls == []
    assert fake.send_attempts == 0
    assert _feishu_audits(client, inst["id"]) == []


# ---------------------------------------------------------------------------
# J4 (P1): 中间级通过未到终态 → 零调用
# ---------------------------------------------------------------------------

def test_j4_mid_level_approval_sends_nothing(feishu_factory):
    client, fake = feishu_factory()
    inst = submit_configured(client, "leave", APPROVER1, APPROVER2)
    t1 = task_at_level(inst, 1)

    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "PENDING"  # 未到终态

    assert fake.send_calls == []
    assert fake.urgent_calls == []
    assert _feishu_audits(client, inst["id"]) == []


# ---------------------------------------------------------------------------
# J5 (P1): 无 open_id 映射 → 终态与站内通知正常、零调用、审计 FEISHU_SKIPPED
# ---------------------------------------------------------------------------

def test_j5_missing_open_id_mapping_degrades(feishu_factory):
    client, fake = feishu_factory(user_map={})  # 提交人无映射
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "APPROVED"               # 终态不受阻塞
    assert "APPROVED" in kinds_of(notifications(client, hdr(SUBMITTER)))  # 站内通知正常

    assert fake.send_calls == []
    assert fake.urgent_calls == []
    assert fake.send_attempts == 0
    assert [a["action"] for a in _feishu_audits(client, inst["id"])] == ["FEISHU_SKIPPED"]


# ---------------------------------------------------------------------------
# J6 (P1): send_card 抛异常 → 正常终态、审计 FEISHU_FAILED、不重试
# ---------------------------------------------------------------------------

def test_j6_send_failure_degrades_without_blocking(feishu_factory):
    fake = FakeFeishu(fail_send=True)
    client, fake = feishu_factory(fake=fake)
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    updated = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "APPROVED"               # 终态不受阻塞
    assert "APPROVED" in kinds_of(notifications(client, hdr(SUBMITTER)))

    assert fake.send_attempts == 1                        # 不重试
    assert fake.urgent_calls == []
    audits = _feishu_audits(client, inst["id"])
    assert [a["action"] for a in audits] == ["FEISHU_FAILED"]
    assert audits[0]["comment"]                           # 失败原因入审计


# ---------------------------------------------------------------------------
# J7 (P2): 终态事件重复触发 → send_card 总计恰好 1 次
# ---------------------------------------------------------------------------

def test_j7_terminal_popup_sent_at_most_once(feishu_factory):
    client, fake = feishu_factory()
    inst = submit_configured(client, "leave", APPROVER1)
    t1 = task_at_level(inst, 1)

    approved = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert approved["status"] == "APPROVED"
    assert len(fake.send_calls) == 1

    # 终态不可变（D1）：任何重复动作 → 409，不得再次触发弹窗
    approve(client, t1["id"], t1["version"], hdr(APPROVER1), expect=409)
    reject(client, t1["id"], t1["version"], "late reject", hdr(APPROVER1), expect=409)
    withdraw(client, inst["id"], approved["version"], None, hdr(SUBMITTER), expect=409)

    assert len(fake.send_calls) == 1
    assert fake.send_attempts == 1
    assert [a["action"] for a in _feishu_audits(client, inst["id"])] == ["FEISHU_SENT"]
