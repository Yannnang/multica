"""Domain enums — the state-machine vocabulary of the contract (decisions.md D1)."""
from __future__ import annotations

from enum import Enum


class ApprovalType(str, Enum):
    """The seven approval categories required by the issue."""

    LEAVE = "leave"                                    # 请假
    EXPENSE = "expense"                                # 报销
    PROCUREMENT = "procurement"                        # 采购
    CONTRACT = "contract"                              # 合同
    PERMISSION = "permission"                          # 权限申请
    SERVER_PROVISIONING = "server_provisioning"        # 服务器开通
    ACCOUNT_PROVISIONING = "account_provisioning"      # 账号申请


TYPE_PREFIX: dict[ApprovalType, str] = {
    ApprovalType.LEAVE: "LV",
    ApprovalType.EXPENSE: "EX",
    ApprovalType.PROCUREMENT: "PR",
    ApprovalType.CONTRACT: "CT",
    ApprovalType.PERMISSION: "PM",
    ApprovalType.SERVER_PROVISIONING: "SV",
    ApprovalType.ACCOUNT_PROVISIONING: "AC",
}


class InstanceStatus(str, Enum):
    """审批单状态。APPROVED / REJECTED / WITHDRAWN 为终态，终态不可变（D1）。"""

    PENDING = "PENDING"
    APPROVED = "APPROVED"
    REJECTED = "REJECTED"
    WITHDRAWN = "WITHDRAWN"


TERMINAL_INSTANCE_STATUSES: frozenset[InstanceStatus] = frozenset(
    {InstanceStatus.APPROVED, InstanceStatus.REJECTED, InstanceStatus.WITHDRAWN}
)


class TaskStatus(str, Enum):
    """审批节点任务状态（D1）。SKIPPED 覆盖：单据终态后作废 / 自审批回避自动跳过。"""

    WAITING = "WAITING"      # 未轮到
    PENDING = "PENDING"      # 已激活，待当前审批人处理（激活时刻起算超时）
    APPROVED = "APPROVED"
    REJECTED = "REJECTED"
    SKIPPED = "SKIPPED"


class NotificationKind(str, Enum):
    """通知事件类型（通知矩阵见 decisions.md D14）。"""

    SUBMITTED = "SUBMITTED"                  # 提交回执 → 提交人
    TASK_ASSIGNED = "TASK_ASSIGNED"          # 某级激活 → 该级审批人
    APPROVED = "APPROVED"                    # 终审通过 → 提交人
    REJECTED = "REJECTED"                    # 驳回（含意见） → 提交人
    WITHDRAWN = "WITHDRAWN"                  # 撤回告知 → 当前待办审批人
    TIMEOUT_REMINDER = "TIMEOUT_REMINDER"    # 超时提醒 → 当前审批人


class AuditAction(str, Enum):
    """审计动作类型（D14）。审计条目按时间升序、只追加、不可修改删除。"""

    SUBMIT = "SUBMIT"
    APPROVE = "APPROVE"
    REJECT = "REJECT"
    WITHDRAW = "WITHDRAW"
    SKIP = "SKIP"                            # 自审批回避自动跳过 / 终态作废
    ESCALATE_URGENT = "ESCALATE_URGENT"
    TIMEOUT_REMIND = "TIMEOUT_REMIND"
