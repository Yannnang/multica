"""Per-type submission payload contracts (v1 minimal field set).

字段集为后端 v1 草案；待前端表单规格（前端 PR 审查员产出）到达后按其增量对齐，
对齐前以本文件 + docs/decisions.md 附录 A 为准。校验失败一律 422 VALIDATION_ERROR，
details 中带字段级错误（loc 含 payload 内字段路径）。
"""
from __future__ import annotations

from datetime import date
from decimal import Decimal
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

from app.models.enums import ApprovalType


class PayloadBase(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class LeavePayload(PayloadBase):
    """请假。"""

    leave_type: Literal["annual", "sick", "personal", "marriage", "maternity", "bereavement", "other"]
    start_date: date
    end_date: date
    reason: str = Field(min_length=1, max_length=500)

    @model_validator(mode="after")
    def _end_not_before_start(self) -> "LeavePayload":
        if self.end_date < self.start_date:
            raise ValueError("end_date must not be earlier than start_date")
        return self


class ExpensePayload(PayloadBase):
    """报销。金额上限 50000（含），超限/非正数 → 422。"""

    expense_type: Literal["travel", "meal", "office", "transport", "training", "other"]
    amount: Decimal = Field(gt=0, le=Decimal("50000"))
    description: str = Field(min_length=1, max_length=1000)
    occurred_on: date | None = None
    receipt_url: str | None = Field(default=None, max_length=500)


class ProcurementPayload(PayloadBase):
    """采购。"""

    item_name: str = Field(min_length=1, max_length=200)
    quantity: int = Field(ge=1, le=100000)
    estimated_amount: Decimal = Field(gt=0, le=Decimal("10000000"))
    supplier: str | None = Field(default=None, max_length=200)
    reason: str = Field(min_length=1, max_length=500)


class ContractPayload(PayloadBase):
    """合同。"""

    contract_name: str = Field(min_length=1, max_length=200)
    counterparty: str = Field(min_length=1, max_length=200)
    contract_amount: Decimal = Field(gt=0, le=Decimal("100000000"))
    start_date: date | None = None
    end_date: date | None = None
    summary: str | None = Field(default=None, max_length=2000)

    @model_validator(mode="after")
    def _end_not_before_start(self) -> "ContractPayload":
        if self.start_date and self.end_date and self.end_date < self.start_date:
            raise ValueError("end_date must not be earlier than start_date")
        return self


class PermissionPayload(PayloadBase):
    """权限申请。duration_days 为空 = 长期权限。"""

    system_name: str = Field(min_length=1, max_length=100)
    permission_name: str = Field(min_length=1, max_length=100)
    reason: str = Field(min_length=1, max_length=500)
    duration_days: int | None = Field(default=None, ge=1, le=3650)


class ServerProvisioningPayload(PayloadBase):
    """服务器开通。"""

    environment: Literal["dev", "staging", "prod"]
    cpu_cores: int = Field(ge=1, le=256)
    memory_gb: int = Field(ge=1, le=2048)
    disk_gb: int = Field(ge=1, le=100000)
    purpose: str = Field(min_length=1, max_length=500)


class AccountProvisioningPayload(PayloadBase):
    """账号申请。"""

    system_name: str = Field(min_length=1, max_length=100)
    account_type: str = Field(min_length=1, max_length=100)
    purpose: str = Field(min_length=1, max_length=500)


PAYLOAD_SCHEMAS: dict[ApprovalType, type[PayloadBase]] = {
    ApprovalType.LEAVE: LeavePayload,
    ApprovalType.EXPENSE: ExpensePayload,
    ApprovalType.PROCUREMENT: ProcurementPayload,
    ApprovalType.CONTRACT: ContractPayload,
    ApprovalType.PERMISSION: PermissionPayload,
    ApprovalType.SERVER_PROVISIONING: ServerProvisioningPayload,
    ApprovalType.ACCOUNT_PROVISIONING: AccountProvisioningPayload,
}
