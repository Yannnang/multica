"""Section J — 飞书终态弹窗通知 (J1~J7).

契约（本子 issue 拍板的注入约定，实现阶段 S2 提供）：
- instance 到达终态 **APPROVED 或 REJECTED** → 飞书客户端向**提交人**发交互卡片
  （send_card）+ 应用内加急弹窗（urgent_app）；**WITHDRAWN 不发**；中间级通过不发。
- 注入：``create_app(clock=..., database_url=..., feishu_client=FakeFeishu())``。
  ``FeishuClient`` 协议：
    ``send_card(open_id: str, card: dict) -> str``（返回 message_id）
    ``urgent_app(open_id: str, message_id: str) -> None``
- 提交人 → open_id 映射 ``FEISHU_USER_MAP``（测试经 app.state.feishu_user_map 注入 dict；
  实现若以 settings 提供，请同时支持 state 覆盖以便测试）。
- 失败语义：飞书异常或映射缺失**不阻塞**终态与站内通知，不重试；
  审计新增 ``FEISHU_SENT`` / ``FEISHU_FAILED`` / ``FEISHU_SKIPPED``（AuditAction 随之扩展）。
- 终态幂等：每单至多发送一次。

脚手架阶段（当前）create_app 尚无 feishu_client 形参 → build_client 退化为
app.state 注入，保证收集通过；红因与 A~I 一致 = 业务端点 501 级联。
"""
from __future__ import annotations

import json

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.main import create_app
from tests.conftest import FakeClock
from tests.helpers import (
    APPROVER1,
    APPROVER2,
    APPROVER3,
    SUBMITTER,
    approve,
    assert_error,
    audit_log,
    escalate,
    get_instance,
    hdr,
    kinds_of,
    notifications,
    reject,
    submit_configured,
    task_at_level,
    withdraw,
)

DB_URL = "sqlite+pysqlite:///:memory:"

OU_SUBMITTER = "ou-submit-001"
FEISHU_USER_MAP = {SUBMITTER: OU_SUBMITTER, APPROVER1: "ou-approver-001"}


class FakeFeishu:
    """FeishuClient 协议测试替身：记录调用；可注入 send_card 失败。"""

    def __init__(self, fail_send_card: bool = False) -> None:
        self.fail_send_card = fail_send_card
        self.send_attempts = 0                      # 含失败的尝试次数（验证不重试）
        self.cards: list[tuple[str, dict]] = []     # (open_id, card)
        self.urgents: list[tuple[str, str]] = []    # (open_id, message_id)
        self.sent_message_ids: list[str] = []
        self._seq = 0

    def send_card(self, open_id: str, card: dict) -> str:
        self.send_attempts += 1
        if self.fail_send_card:
            raise RuntimeError("feishu open api unavailable")
        self._seq += 1
        message_id = f"om-fake-{self._seq:04d}"
        self.cards.append((open_id, card))
        self.sent_message_ids.append(message_id)
        return message_id

    def urgent_app(self, open_id: str, message_id: str) -> None:
        self.urgents.append((open_id, message_id))


def build_client(
    clock: FakeClock,
    feishu: FakeFeishu,
    user_map: dict[str, str] | None = None,
) -> TestClient:
    """按 issue 注入约定构造 TestClient。

    实现阶段 create_app 将提供 feishu_client 形参；脚手架阶段形参缺失（TypeError）
    时退化为 app.state.feishu_client 注入——两种路径下断言的业务行为一致。
    """
    try:
        app: FastAPI = create_app(
            clock=clock, database_url=DB_URL, feishu_client=feishu
        )
    except TypeError:  # scaffold: kwarg not yet provided
        app = create_app(clock=clock, database_url=DB_URL)
        app.state.feishu_client = feishu
    app.state.feishu_user_map = dict(
        FEISHU_USER_MAP if user_map is None else user_map
    )
    return TestClient(app)


@pytest.fixture()
def feishu() -> FakeFeishu:
    return FakeFeishu()


@pytest.fixture()
def jclient(clock: FakeClock, feishu: FakeFeishu) -> TestClient:
    return build_client(clock, feishu)


def _actions(client: TestClient, iid: int) -> list[str]:
    return [a["action"] for a in audit_log(client, iid)]


def _drive_approve_all(client, approvers, clock) -> dict:
    """提交（路由=approvers）并逐级同意到终态 APPROVED，返回最终 instance。"""
    inst = submit_configured(client, "leave", *approvers)
    for level, who in enumerate(approvers, start=1):
        clock.advance(minutes=5)
        t = task_at_level(inst, level)
        inst = approve(client, t["id"], t["version"], hdr(who)).json()
    return inst


def _assert_card_matches_instance(card: dict, inst: dict, result: str) -> None:
    """卡片含单据类型 / 编号 / 结果（J1/J2 语义；类型允许后端枚举值或中文名）。"""
    card_json = json.dumps(card, ensure_ascii=False)
    assert inst["number"] in card_json, f"card missing instance number: {card}"
    assert result in card_json, f"card missing result {result}: {card}"
    assert ("leave" in card_json) or ("请假" in card_json), (
        f"card missing approval type: {card}"
    )


# ---------------------------------------------------------------------------
# J1 (P0): 三级全通过 → 恰好 1 次 send_card + 1 次 urgent_app；审计 FEISHU_SENT
# ---------------------------------------------------------------------------

def test_j1_final_approval_sends_card_and_urgent_once(jclient, feishu, clock):
    inst = _drive_approve_all(jclient, (APPROVER1, APPROVER2, APPROVER3), clock)
    assert inst["status"] == "APPROVED"

    assert len(feishu.cards) == 1, f"expected exactly 1 card, got {feishu.cards}"
    open_id, card = feishu.cards[0]
    assert open_id == OU_SUBMITTER  # 发给提交人的 open_id 映射
    _assert_card_matches_instance(card, inst, "APPROVED")

    assert len(feishu.urgents) == 1
    assert feishu.urgents[0] == (OU_SUBMITTER, feishu.sent_message_ids[0])

    assert "FEISHU_SENT" in _actions(jclient, inst["id"])
    # 中间级通过不发送：全程只有终态这一次
    assert feishu.send_attempts == 1


# ---------------------------------------------------------------------------
# J2 (P0): 任一级驳回 → 同样发送；card 结果=REJECTED 且含驳回意见
# ---------------------------------------------------------------------------

def test_j2_rejection_sends_card_with_comment(jclient, feishu, clock):
    inst = submit_configured(jclient, "leave", APPROVER1, APPROVER2, APPROVER3)
    clock.advance(minutes=5)
    t1 = task_at_level(inst, 1)
    approve(jclient, t1["id"], t1["version"], hdr(APPROVER1))

    # 重新读取以拿到第 2 级任务，然后驳回
    clock.advance(minutes=5)
    refreshed = get_instance(jclient, inst["id"]).json()
    t2 = task_at_level(refreshed, 2)
    comment = "budget evidence missing"
    final = reject(jclient, t2["id"], t2["version"], comment, hdr(APPROVER2)).json()
    assert final["status"] == "REJECTED"

    assert len(feishu.cards) == 1
    open_id, card = feishu.cards[0]
    assert open_id == OU_SUBMITTER
    _assert_card_matches_instance(card, final, "REJECTED")
    assert comment in json.dumps(card, ensure_ascii=False), (
        f"card missing reject comment: {card}"
    )

    assert len(feishu.urgents) == 1
    assert "FEISHU_SENT" in _actions(jclient, final["id"])
    assert feishu.send_attempts == 1  # 第 1 级通过（中间级）未触发发送


# ---------------------------------------------------------------------------
# J3 (P0): 撤回 → 零调用（WITHDRAWN 终态不发飞书）
# ---------------------------------------------------------------------------

def test_j3_withdraw_sends_nothing(jclient, feishu, clock):
    inst = submit_configured(jclient, "leave", APPROVER1, APPROVER2)
    clock.advance(minutes=5)
    withdrawn = withdraw(
        jclient, inst["id"], inst["version"], "plan changed", hdr(SUBMITTER)
    ).json()
    assert withdrawn["status"] == "WITHDRAWN"

    assert feishu.send_attempts == 0
    assert feishu.cards == []
    assert feishu.urgents == []
    assert not [a for a in _actions(jclient, inst["id"]) if a.startswith("FEISHU")]


# ---------------------------------------------------------------------------
# J4 (P1): 中间级通过未到终态 → 零调用
# ---------------------------------------------------------------------------

def test_j4_intermediate_approval_sends_nothing(jclient, feishu, clock):
    inst = submit_configured(jclient, "leave", APPROVER1, APPROVER2, APPROVER3)
    clock.advance(minutes=5)
    t1 = task_at_level(inst, 1)
    updated = approve(jclient, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert updated["status"] == "PENDING"  # 未到终态

    assert feishu.send_attempts == 0
    assert feishu.cards == []
    assert feishu.urgents == []
    assert not [a for a in _actions(jclient, inst["id"]) if a.startswith("FEISHU")]


# ---------------------------------------------------------------------------
# J5 (P1): 无 open_id 映射 → 终态与站内通知正常、零调用、审计 FEISHU_SKIPPED
# ---------------------------------------------------------------------------

def test_j5_missing_open_id_mapping_skips_feishu(clock):
    feishu = FakeFeishu()
    client = build_client(clock, feishu, user_map={})  # 映射缺失

    inst = submit_configured(client, "leave", APPROVER1)
    clock.advance(minutes=5)
    t1 = task_at_level(inst, 1)
    final = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()

    assert final["status"] == "APPROVED"                     # 终态不受阻塞
    assert "APPROVED" in kinds_of(notifications(client, hdr(SUBMITTER)))  # 站内通知正常
    assert feishu.send_attempts == 0
    assert feishu.urgents == []
    assert "FEISHU_SKIPPED" in _actions(client, final["id"])


# ---------------------------------------------------------------------------
# J6 (P1): send_card 抛异常 → 终态正常、审计 FEISHU_FAILED、不重试
# ---------------------------------------------------------------------------

def test_j6_send_card_failure_does_not_block_terminal(clock):
    feishu = FakeFeishu(fail_send_card=True)
    client = build_client(clock, feishu)

    inst = submit_configured(client, "leave", APPROVER1)
    clock.advance(minutes=5)
    t1 = task_at_level(inst, 1)
    final = approve(client, t1["id"], t1["version"], hdr(APPROVER1)).json()

    assert final["status"] == "APPROVED"                     # 飞书失败不阻塞终态
    assert "APPROVED" in kinds_of(notifications(client, hdr(SUBMITTER)))
    assert feishu.send_attempts == 1, "must not retry after failure"
    assert feishu.urgents == []                              # 无 message_id 可加急
    assert "FEISHU_FAILED" in _actions(client, final["id"])


# ---------------------------------------------------------------------------
# J7 (P2): 终态事件重复触发 → send_card 总计恰好 1 次（幂等）
# ---------------------------------------------------------------------------

def test_j7_terminal_notification_is_idempotent(jclient, feishu, clock):
    inst = submit_configured(jclient, "leave", APPROVER1)
    clock.advance(minutes=5)
    t1 = task_at_level(inst, 1)
    final = approve(jclient, t1["id"], t1["version"], hdr(APPROVER1)).json()
    assert final["status"] == "APPROVED"
    assert len(feishu.cards) == 1

    # 终态后任何重复动作（均 409，D1/C08）不得再次触发飞书发送
    resp = approve(jclient, t1["id"], t1["version"], hdr(APPROVER1), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    resp = reject(jclient, t1["id"], t1["version"], "late", hdr(APPROVER1), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    resp = withdraw(jclient, final["id"], final["version"], None, hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")
    resp = escalate(jclient, final["id"], hdr(SUBMITTER), expect=None)
    assert_error(resp, 409, "STATE_CONFLICT")

    assert feishu.send_attempts == 1
    assert len(feishu.cards) == 1
    assert len(feishu.urgents) == 1
    assert _actions(jclient, final["id"]).count("FEISHU_SENT") == 1
