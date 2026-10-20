"""Per-type submission payload contracts (v0.2.0 — reconciled with frontend form spec).

对齐依据：前端 PR 审查员《7 类审批提交表单字段规格》(2026-10-20) + docs/decisions.md D17。
约定：
- API 字段一律 snake_case；前端 camelCase 由前端层映射（映射表见 decisions.md D17）。
- 金额一律**整数分（int cents）**，避免浮点误差（D17-2）。
- 服务端计算字段（leave.duration_days、expense/procurement 的 total_amount_cents）
  **不接受入参**：payload extra=forbid，提交即 422；服务端重算后写入存储 payload 并在响应返回。
- 相对日期校验（≥今天、≥今天+1、≤今天+365 等）依赖注入时钟，由**服务层**执行（实现阶段），
  错误仍为 422 VALIDATION_ERROR 且 details 指明字段；schema 层只做静态约束。
- 校验失败一律 422 VALIDATION_ERROR（而非前端规格建议的 400，见 D17-6），details.loc 即字段路径。
"""
from __future__ import annotations

import re
from datetime import date
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

from app.models.enums import ApprovalType

# 单据金额上限（分）：报销 5 万元、采购 1000 万元、合同 1 亿元
EXPENSE_TOTAL_CAP_CENTS = 5_000_000
PROCUREMENT_TOTAL_CAP_CENTS = 1_000_000_000
CONTRACT_AMOUNT_CAP_CENTS = 10_000_000_000

INVOICE_PATTERN = re.compile(r"^[A-Za-z0-9]{8,20}$")
SERVICE_NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]{1,63}$")
PAYEE_ACCOUNT_PATTERN = re.compile(r"^[0-9]{10,32}$")


class PayloadBase(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class LeavePayload(PayloadBase):
    """请假。duration_days 由服务端按日历日计算（含首尾），不接受入参（D17-3）。

    允许补提过去的假（D17 拍板①）；年假余额校验 v1 不做（D17 拍板②）。
    请假超过 3 个日历日必须指定工作交接人（handover_person）。
    """

    leave_type: Literal["annual", "sick", "personal", "marriage", "maternity", "bereavement", "other"]
    start_date: date
    end_date: date
    reason: str = Field(min_length=5, max_length=500)
    handover_person: str | None = Field(default=None, min_length=1, max_length=64)

    @model_validator(mode="after")
    def _cross_field(self) -> "LeavePayload":
        if self.end_date < self.start_date:
            raise ValueError("end_date must not be earlier than start_date")
        duration_days = (self.end_date - self.start_date).days + 1
        if duration_days > 3 and not self.handover_person:
            raise ValueError("handover_person is required when leave spans more than 3 calendar days")
        return self


class ExpenseItem(PayloadBase):
    """报销明细行。incurred_on 不得晚于提交日（服务层按注入时钟校验，D17 拍板③）。"""

    name: str = Field(min_length=1, max_length=100)
    incurred_on: date
    amount_cents: int = Field(gt=0, le=EXPENSE_TOTAL_CAP_CENTS)


class ExpensePayload(PayloadBase):
    """报销。total_amount_cents = Σ items.amount_cents 由服务端计算，上限 50,000 元（D17 拍板③）。

    invoice_numbers 服务端全局查重，重复 → 409 INVOICE_DUPLICATE（D17-5）。
    发票/小票影像附件 v1 不做真实上传（D17-7 延期项），以 receipt_url 可选过渡。
    """

    expense_type: Literal["travel", "meal", "office", "transport", "training", "entertainment", "other"]
    items: list[ExpenseItem] = Field(min_length=1, max_length=50)
    description: str = Field(min_length=5, max_length=1000)
    payee_account: str = Field(pattern=PAYEE_ACCOUNT_PATTERN)
    invoice_numbers: list[Annotated[str, Field(pattern=INVOICE_PATTERN)]] = Field(min_length=1, max_length=20)
    receipt_url: str | None = Field(default=None, max_length=500)

    @model_validator(mode="after")
    def _totals_and_uniqueness(self) -> "ExpensePayload":
        seen: set[str] = set()
        for inv in self.invoice_numbers:
            if inv in seen:
                raise ValueError(f"duplicate invoice number within request: {inv}")
            seen.add(inv)
        total = sum(item.amount_cents for item in self.items)
        if total > EXPENSE_TOTAL_CAP_CENTS:
            raise ValueError(
                f"total amount {total} cents exceeds cap {EXPENSE_TOTAL_CAP_CENTS} cents (50,000 CNY)"
            )
        return self


class ProcurementItem(PayloadBase):
    """采购明细行。小计 quantity × unit_price_cents 由服务端计算。"""

    name: str = Field(min_length=1, max_length=100)
    spec: str | None = Field(default=None, max_length=200)
    quantity: int = Field(ge=1, le=9999)
    unit_price_cents: int = Field(ge=0, le=100_000_000)


class ProcurementPayload(PayloadBase):
    """采购。total_amount_cents = Σ quantity × unit_price_cents 服务端计算。

    expected_delivery_date ≥ 今天（服务层校验）。按金额分级的多级审批路由 v1 不做（D17 拍板④，
    路由仍为静态配置 D2）；金额阈值附件 v1 不做（D17-7 延期项）。
    """

    item_category: Literal["equipment", "consumable", "service", "software"]
    items: list[ProcurementItem] = Field(min_length=1, max_length=50)
    supplier: str | None = Field(default=None, min_length=2, max_length=200)
    expected_delivery_date: date
    reason: str = Field(min_length=5, max_length=500)

    @model_validator(mode="after")
    def _total_cap(self) -> "ProcurementPayload":
        total = sum(i.quantity * i.unit_price_cents for i in self.items)
        if total <= 0:
            raise ValueError("procurement total amount must be greater than 0")
        if total > PROCUREMENT_TOTAL_CAP_CENTS:
            raise ValueError(
                f"total amount {total} cents exceeds cap {PROCUREMENT_TOTAL_CAP_CENTS} cents"
            )
        return self


class ContractPayload(PayloadBase):
    """合同。contract_amount_cents 允许为 0（框架协议），此时 amount_note 必填。

    contract_number（归档编号）由服务端在终审通过时分配——v1 延期（D17-7）；
    合同正文附件以 document_url 可选过渡（真实上传 v2）。
    """

    contract_type: Literal["sales", "procurement", "nda", "labor", "other"]
    contract_name: str = Field(min_length=1, max_length=200)
    counterparty: str = Field(min_length=2, max_length=200)
    contract_amount_cents: int = Field(ge=0, le=CONTRACT_AMOUNT_CAP_CENTS)
    amount_note: str | None = Field(default=None, min_length=5, max_length=500)
    currency: Literal["CNY", "USD", "EUR", "HKD", "JPY", "GBP"] = "CNY"
    start_date: date
    end_date: date
    summary: str = Field(min_length=5, max_length=2000)
    document_url: str | None = Field(default=None, max_length=500)

    @model_validator(mode="after")
    def _cross_field(self) -> "ContractPayload":
        if self.end_date <= self.start_date:
            raise ValueError("end_date must be later than start_date")
        if self.contract_amount_cents == 0 and not self.amount_note:
            raise ValueError("amount_note is required when contract_amount_cents is 0 (framework contract)")
        return self


class PermissionPayload(PayloadBase):
    """权限申请。duration_days 空 = 长期；一律 ≤ 365 天（D17 拍板⑤）。

    targetSystemCode/roleCode 白名单依赖系统注册表，v1 不做（D17-7 延期项），沿用自由文本。
    """

    system_name: str = Field(min_length=1, max_length=100)
    permission_name: str = Field(min_length=1, max_length=100)
    resource_scope: str | None = Field(default=None, max_length=500)
    reason: str = Field(min_length=5, max_length=500)
    duration_days: int | None = Field(default=None, ge=1, le=365)


class ServerProvisioningPayload(PayloadBase):
    """服务器开通。规格为枚举档位；service_name 同环境服务端查重 → 409 DUPLICATE_SERVICE_NAME（D17-5）。

    expected_ready_date ≥ 今天+1（服务层校验）。open_ports 含 22/3389 时 network_note 必填。
    production 环境的强制多级路由属审批流配置（D2 静态级数），不作为 payload 语义。
    """

    environment: Literal["dev", "staging", "prod"]
    service_name: str = Field(pattern=SERVICE_NAME_PATTERN)
    cpu_cores: Literal[2, 4, 8, 16, 32]
    memory_gb: Literal[4, 8, 16, 32, 64, 128]
    disk_gb: int = Field(ge=50, le=10000)
    os_type: Literal["ubuntu-22.04", "centos-7", "windows-server-2022"]
    open_ports: list[Annotated[int, Field(ge=1, le=65535)]] = Field(default_factory=list, max_length=20)
    network_note: str | None = Field(default=None, max_length=500)
    expected_ready_date: date
    purpose: str = Field(min_length=5, max_length=500)

    @model_validator(mode="after")
    def _cross_field(self) -> "ServerProvisioningPayload":
        if self.disk_gb % 50 != 0:
            raise ValueError("disk_gb must be a multiple of 50 (step 50, D17 拍板⑥)")
        if len(set(self.open_ports)) != len(self.open_ports):
            raise ValueError("open_ports contains duplicates")
        if {22, 3389} & set(self.open_ports) and not self.network_note:
            raise ValueError("network_note is required when open_ports includes 22 or 3389")
        return self


class AccountProvisioningPayload(PayloadBase):
    """账号申请。shared/service 必须有责任人 owner_id 与 valid_until（≤ 今天+365，服务层校验）；
    personal 可空 = 长期。同申请人同系统已有生效 personal 账号 → 409 DUPLICATE_ACCOUNT（D17-5）。
    requested_roles 白名单依赖系统注册表，v1 不做（D17-7 延期项）。
    """

    system_name: str = Field(min_length=1, max_length=100)
    account_type: Literal["personal", "shared", "service"]
    owner_id: str | None = Field(default=None, min_length=1, max_length=64)
    valid_until: date | None = None
    requested_roles: list[Annotated[str, Field(min_length=1, max_length=100)]] = Field(
        default_factory=list, max_length=20
    )
    purpose: str = Field(min_length=5, max_length=500)

    @model_validator(mode="after")
    def _cross_field(self) -> "AccountProvisioningPayload":
        if self.account_type in ("shared", "service"):
            if not self.owner_id:
                raise ValueError(f"owner_id is required when account_type is {self.account_type}")
            if self.valid_until is None:
                raise ValueError(f"valid_until is required when account_type is {self.account_type}")
        return self


PAYLOAD_SCHEMAS: dict[ApprovalType, type[PayloadBase]] = {
    ApprovalType.LEAVE: LeavePayload,
    ApprovalType.EXPENSE: ExpensePayload,
    ApprovalType.PROCUREMENT: ProcurementPayload,
    ApprovalType.CONTRACT: ContractPayload,
    ApprovalType.PERMISSION: PermissionPayload,
    ApprovalType.SERVER_PROVISIONING: ServerProvisioningPayload,
    ApprovalType.ACCOUNT_PROVISIONING: AccountProvisioningPayload,
}
