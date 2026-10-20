# 审批管理系统 v1 契约决策（decisions.md）

状态：**契约冻结（v0.2.0，已对齐前端表单规格，见 D17）**。本文是测试落测与后端实现的唯一契约来源；OpenAPI（`GET /openapi.json`）与 `app/schemas/` 与本文一致。修订需在本 issue 线程确认后更新本文并递增版本。

修订记录：
- v0.2.0（2026-10-20）：对齐前端 PR 审查员《7 类审批提交表单字段规格》——金额改整数分、报销/采购改明细行、新增条件必填与全局查重、信封增加 title/urgent_reason；拍板其全部「待确认」项（D17）。
- v0.1.0（2026-10-20）：初版契约，拍板测试用例清单 v1 的状态机假设与开放项①~⑦（D1~D16）。

对应测试用例清单 v1（test，2026-10-20）中标注的全部开放项（①~⑦）与状态机假设，已由后端逐项拍板如下；报告人如不同意任何决策，回复线程即可覆盖。

## D1. 状态机（确认 test 的假设，全部成立）

- 审批单 instance：`PENDING` → `APPROVED` / `REJECTED` / `WITHDRAWN`；后三者为终态，终态不可变，任何操作 → `409 STATE_CONFLICT`（TC-C08）。
- 节点任务 task：`WAITING`（未轮到）→ `PENDING`（激活，记录 `activated_at`）→ `APPROVED` / `REJECTED` / `SKIPPED`。
  - `SKIPPED` 两种来源：单据进入终态后作废未激活任务（TC-C02/E01）；自审批回避自动跳过（D3）。
- 超时提醒是通知事件，**不改变**审批单/任务状态，不自动通过或驳回（TC-G05）。
- 多级审批：第 N 级 `APPROVED` 后自动激活第 N+1 级并路由（TC-B04），无需提交人操作；任一级 `REJECTED` → 单据立即 `REJECTED`，已 `APPROVED` 的历史级记录保留，未激活级永不激活（TC-C02/C03）。
- 单级审批与多级共用同一状态机（TC-C09）。

## D2. 路由与负责人缺配（开放项①）

- 每类审批的路由 = `PUT /api/v1/config/routes/{type}` 配置的**有序审批级**；提交时对配置做**快照**生成全部任务，在途单不受后续配置修改影响（TC-B02）。
- 提交时该类型**无路由配置**、或配置为空 → `422 ROUTING_NOT_CONFIGURED`，**不创建审批单**。不做静默兜底审批人（避免越权代批）。TC-B03 落测断言此行为。
- v1 无用户注册表/停用状态：路由配置中的 approver id 视为有效。「已停用负责人」场景不在 v1 范围（见 D16 范围外）。
- 加急不改变路由结果，仅附加标记与更短超时阈值（TC-B05）。

## D3. 自审批（开放项②）

- 策略：**禁止自审批，自动回避**。
- 任务激活时若 `approver_id == submitter_id` → 该任务自动 `SKIPPED`（审计 `SKIP`，comment=`AUTO_SKIP_SELF_APPROVAL`），立即顺延激活下一级。
- 提交时若**所有级**审批人均为提交人 → `422 ROUTING_ALL_SELF`，不创建单据（防止整链回避）。
- 兜底防御：审批人对自己提交的单执行 approve/reject → `403 SELF_APPROVAL_FORBIDDEN`（正常流程不会到达，防路由中途修改等边界）。TC-C07 按此落测。

## D4. 驳回意见（开放项③）

- **驳回意见必填**：`RejectRequest.comment` 最短 1 字符，缺失/空白 → `422 VALIDATION_ERROR`（TC-D02）。
- 同意意见可选；撤回原因可选。
- 驳回通知提交人时必须携带意见全文（TC-D01）。

## D5. 驳回后重提（开放项④）

- **生成新审批单**，从第 1 级重走；新单 `resubmit_of` 指向原单 id；原单保持 `REJECTED` 终态不变（终态不可变）。
- 入口：`POST /api/v1/instances/resubmit/{instance_id}`（仅原提交人；原单非 REJECTED → `409 STATE_CONFLICT`）。TC-D03 按此落测。

## D6. 中途加急（开放项⑤）

- **允许**，仅提交人、仅审批中（`PENDING`）、仅一次：`POST /api/v1/instances/{id}/escalate-urgent`。
- 已加急再升级 → `409 ALREADY_URGENT`；终态 → `409 STATE_CONFLICT`；不允许取消加急。
- 加急 ≠ 跳级：仍走完全部审批级（TC-F03）。升级后当前任务的超时阈值切换为加急阈值，已发提醒计数保留（TC-F04/F05）。审计 `ESCALATE_URGENT`。

## D7. 超时提醒（开放项⑥）

- 阈值（v1 默认，可按类型覆盖留待后续）：**普通单 24h，加急单 4h**；按任务级独立计时，从该级 `activated_at` 起算，与前级耗时无关（TC-G04）。
- 触发条件：`now >= activated_at + 阈值`（**含等号**，TC-G02 边界方向以此为准）；之后每满一个阈值周期仍未处理再提醒一次，**每任务最多 3 次**；不抄送上级、不升级对象（v1）。TC-G06 按此落测。
- 提醒对象：当前 `PENDING` 任务的审批人；产生 `TIMEOUT_REMINDER` 通知 + `TIMEOUT_REMIND` 审计。
- 实现为**扫描式**：`POST /api/v1/system/timeout-scan` 以 `app.state.clock.now()` 计算，无后台线程/真实定时器；任务离开 `PENDING`（同意/驳回/撤回/跳过）后自然不再提醒（TC-G03）。
- 超时不改变任何状态（D1）。

## D8. 会签/或签（开放项⑦）

- **v1 不支持**：每级恰好一名审批人，`RouteConfigUpsert` 校验强制（每级 1 人，级数 1~10）。TC-I02 标记为 v1 范围外，不落测。

## D9. 提交幂等（TC-A11）

- 支持可选 `Idempotency-Key` 请求头：同 `(submitter_id, key)` **24h 窗口**内重复提交 → `200` 返回原单（不重复创建），响应头 `Idempotent-Replayed: true`；首次 → `201`。
- 未携带 key：允许重复提交（业务上同一人可多次请假/报销），不做去重。

## D10. 鉴权与操作人

- v1 以 `X-User-Id` 请求头标识操作人，缺失/空白 → `422 VALIDATION_ERROR`；无密码/token 认证。
- 角色规则：撤回/中途加急/重提 = 仅提交人（否则 `403 FORBIDDEN`，TC-E03）；approve/reject = 仅该任务当前级审批人（否则 `403 FORBIDDEN`，TC-C06）。
- 读接口 v1 不做数据范围隔离（任何登录用户可读单据/通知仅返回本人）。

## D11. 并发与一致性（TC-E04 / TC-I01）

- instance 与 task 各携带整数 `version`（乐观锁），每次成功写操作自增。
- approve/reject 请求体携带 **task** 的 `version`；withdraw/resubmit/escalate 针对 **instance** 的 `version`。
- 版本不匹配 → `409 VERSION_CONFLICT`，状态不变。并发双写恰好一方生效，无中间态泄漏。
- 请求体缺 `version` → `422 VALIDATION_ERROR`。
- 检查顺序（实现必须遵守）：`404 NOT_FOUND` → `403` → `409 STATE_CONFLICT` → `409 VERSION_CONFLICT`。越级操作（任务 `WAITING`）与重复审批（任务已终态）均归入 `STATE_CONFLICT`（TC-C04/C05）。

## D12. 统一错误封装

所有错误响应（含框架级请求校验）统一为：

```json
{"error": {"code": "<ERROR_CODE>", "message": "<可读信息>", "details": [{"loc": [...], "msg": "...", "type": "..."}]}}
```

错误码全集：`VALIDATION_ERROR` / `ROUTING_NOT_CONFIGURED` / `ROUTING_ALL_SELF` / `NOT_FOUND` / `FORBIDDEN` / `SELF_APPROVAL_FORBIDDEN` / `STATE_CONFLICT` / `VERSION_CONFLICT` / `ALREADY_URGENT` / `INVOICE_DUPLICATE` / `DUPLICATE_SERVICE_NAME` / `DUPLICATE_ACCOUNT` / `NOT_IMPLEMENTED`（脚手架期占位，实现完成后消失）。

HTTP 语义：422=请求不可接受（字段/类型/路由缺配）；403=身份不允许；404=不存在；409=状态/版本冲突；501=未实现。

## D13. 单据编号

`{PREFIX}-{YYYYMMDD}-{当日序号:04d}`，按创建日（注入时钟）计数。前缀：leave=LV、expense=EX、procurement=PR、contract=CT、permission=PM、server_provisioning=SV、account_provisioning=AC。示例：`LV-20261020-0001`。编号唯一、创建后不变。

## D14. 通知矩阵与审计

通知（`NotificationKind`，GET /api/v1/notifications 返回本人通知，时间倒序）：

| 事件 | 接收人 | kind |
|---|---|---|
| 提交成功 | 提交人 | `SUBMITTED` |
| 某级激活（含提交时第 1 级） | 该级审批人 | `TASK_ASSIGNED` |
| 终审通过 | 提交人 | `APPROVED` |
| 任一级驳回 | 提交人（消息含驳回意见全文） | `REJECTED` |
| 撤回 | 撤回时处于 PENDING 的审批人 | `WITHDRAWN` |
| 超时提醒 | 当前 PENDING 任务审批人 | `TIMEOUT_REMINDER` |

通知内容必含：单据类型、单据编号、动作人（TC-H01）。中间级通过不通知提交人（避免噪音），审批进度经单据详情可见。

审计（`AuditAction`，GET /api/v1/instances/{id}/audit 时间升序）：`SUBMIT` / `APPROVE`（含意见）/ `REJECT`（含意见）/ `WITHDRAW`（含原因）/ `SKIP`（自审批回避或终态作废，系统 actor=null）/ `ESCALATE_URGENT` / `TIMEOUT_REMIND`（系统）。每条含 actor、时间戳（注入时钟）、动作、意见；只追加，无更新/删除接口（TC-H02）。

## D15. 时间与测试约定

- 一切时间逻辑经 `app.state.clock`（`Clock` 协议）；测试注入 `tests/conftest.py::FakeClock`，禁止 sleep、禁止直接 `datetime.now()`。
- 所有时间戳为 aware UTC。
- 超时驱动方式为显式调用 `POST /api/v1/system/timeout-scan`（生产由 cron 周期调用）。

## D16. v1 范围外

会签/或签（D8）、真实认证与用户管理、负责人停用检测、催办/转办/委托、退回修改续走原单（D5 采用新单）、通知外部渠道（站内通知表即全部）、前端 UI（表单规格由前端 PR 审查员另行产出）、以及 D17-7 列出的前端规格延期项。

## D17. 前端表单规格对齐（v0.2.0）

前端 PR 审查员规格已采纳为契约输入，按下述规则合入；权威字段定义 = `app/schemas/payloads.py` + 附录 A。

**D17-1 命名与枚举映射**：API 一律 snake_case、枚举值沿用后端既有冻结值（避免破坏已分发的 v0.1.0 落测基线），前端 camelCase 由前端层映射：

| 前端规格 | 后端契约 |
|---|---|
| approvalType=`reimbursement` / `purchase` / `server` / `account` | type=`expense` / `procurement` / `server_provisioning` / `account_provisioning`（`leave`/`contract`/`permission` 同名） |
| applicantId（登录态取） | `X-User-Id` 请求头（D10），不接收 body 传入 |
| requestId（幂等键） | `Idempotency-Key` 请求头（D9，24h 窗口） |
| urgency=`normal`/`urgent` | `is_urgent: bool` |
| envType=`production`/`test` | environment=`prod`/`dev`（`staging` 同名） |
| expenseType=`entertainment` | 已并入 expense_type 枚举（其余值 travel/office/other 同名，另有 meal/transport/training） |

**D17-2 金额单位**：一律**整数分（int64 cents）**——`items[].amount_cents`、`items[].unit_price_cents`、`contract_amount_cents`。上限：报销单总额 ≤ 5,000,000 分（5 万元）；采购总额 ≤ 1,000,000,000 分；合同 ≤ 10,000,000,000 分。超限 → 422。

**D17-3 服务端计算字段**：`duration_days`（请假，日历日含首尾；工作日历 v2）、`total_amount_cents`（报销 = Σ items、采购 = Σ quantity×unit_price）**不接受入参**（payload extra=forbid，传入即 422）；服务端重算写入存储 payload 并随响应返回。

**D17-4 信封公共层**：`title` 可选（1~100），留空服务端兜底生成 `{类型中文名}-{提交人id}-{YYYYMMDD}`；`urgent_reason` 在 `is_urgent=true` 时必填（5~200），否则 422；departmentId v1 无组织档案，延期。

**D17-5 全局查重（新增 3 个 409 错误码）**：
- 报销 `invoice_numbers`（8~20 位字母数字，请求内去重 + 全局查重）→ `409 INVOICE_DUPLICATE`；
- 服务器 `environment + service_name`（regex `^[a-z0-9][a-z0-9-]{1,63}$`）存在非 REJECTED/WITHDRAWN 在途或已通过单 → `409 DUPLICATE_SERVICE_NAME`；
- 账号：同申请人同 `system_name` 已有非 REJECTED/WITHDRAWN 的 personal 账号单 → `409 DUPLICATE_ACCOUNT`。

**D17-6 校验错误状态码**：统一 **422** + D12 封装（`details[].loc` 即前端规格的 `field`），不采用前端规格建议的 400——与已冻结契约及 FastAPI 惯例一致。

**「待确认」项拍板**（后端决策，报告人可在线程回复覆盖）：
1. 请假**允许补提**（无 start_date ≥ 今天限制）——病假/紧急事假事后补单是真实场景；
2. 年假余额校验 v1 **不做**（无余额体系）；
3. 报销明细 `incurred_on` ≤ 提交日（注入时钟校验），**6 个月时限窗 v1 不做**；单笔总额上限 5 万元，超限 422（**不设特殊审批通道**，v2 再议）；
4. 采购**按金额分级的动态多级路由 v1 不做**——审批级数仍为按类型静态配置（D2）；金额阈值附件同延期；
5. 权限 `duration_days` 一律 ≤ 365（不区分管理员级）；shared/service 账号 `valid_until` 必填且 ≤ 今天+365（服务层校验）；personal 可空=长期；
6. 磁盘采纳 **50 ≤ disk_gb ≤ 10000 且步进 50**；cpu/memory 采纳枚举档位 {2,4,8,16,32} / {4,8,16,32,64,128}。

**D17-7 延期至 v2 的前端规格项**（v1 契约不含，落测勿覆盖）：真实文件上传与附件校验（v1 以 `receipt_url` / `document_url` 可选字段过渡；病假证明、报价单、合同扫描件附件规则随之延期）、工作日历版 durationDays、系统注册表与 targetSystemCode/roleCode 白名单、contract_number 归档分配、年假余额、按金额阈值路由、departmentId 组织档案。

**相对日期校验的执行层**：`expected_delivery_date ≥ 今天`、`expected_ready_date ≥ 今天+1`、`incurred_on ≤ 今天`、`valid_until ≤ 今天+365` 等依赖注入时钟，由**服务层**在提交时校验（错误仍 422 VALIDATION_ERROR，details 指明字段）；schema 层只做静态约束。测试用 FakeClock 推进后断言。

## 附录 A：7 类审批 payload 字段（v0.2.0，权威定义 = `app/schemas/payloads.py`）

公共信封：`{"type": <ApprovalType>, "title": str?, "is_urgent": bool=false, "urgent_reason": str?, "payload": {...}}`；payload 多传未知字段（含服务端计算字段）→ 422（extra=forbid）。`* 必填`，`(条件)` 为条件必填。

| 类型 | 字段 | 关键校验 |
|---|---|---|
| leave 请假 | leave_type*、start_date*、end_date*、reason*(5~500)、handover_person(条件) | end ≥ start；跨 3 个日历日以上 handover_person 必填；duration_days 服务端算 |
| expense 报销 | expense_type*、items*[1~50]{name*、incurred_on*、amount_cents*(>0)}、description*(5~1000)、payee_account*(10~32 位数字)、invoice_numbers*[1~20]（8~20 位字母数字）、receipt_url | 总额=Σ items ≤ 5,000,000 分；发票号请求内去重+全局查重(409) |
| procurement 采购 | item_category*(equipment/consumable/service/software)、items*[1~50]{name*、spec、quantity*(1~9999)、unit_price_cents*(≥0)}、supplier(2~200)、expected_delivery_date*(≥今天)、reason*(5~500) | 总额=Σ qty×price，0 < 总额 ≤ 1,000,000,000 分 |
| contract 合同 | contract_type*(sales/procurement/nda/labor/other)、contract_name*、counterparty*(2~200)、contract_amount_cents*(0~10,000,000,000)、amount_note(条件)、currency*(默认 CNY)、start_date*、end_date*、summary*(5~2000)、document_url | end > start；金额=0 时 amount_note 必填 |
| permission 权限申请 | system_name*、permission_name*、resource_scope(≤500)、reason*(5~500)、duration_days(1~365，空=长期) | — |
| server_provisioning 服务器开通 | environment*(dev/staging/prod)、service_name*(regex)、cpu_cores*{2,4,8,16,32}、memory_gb*{4,8,16,32,64,128}、disk_gb*(50~10000 步进 50)、os_type*(ubuntu-22.04/centos-7/windows-server-2022)、open_ports[≤20](1~65535 去重)、network_note(条件)、expected_ready_date*(≥今天+1)、purpose*(5~500) | 端口含 22/3389 → network_note 必填；env+service_name 全局查重(409) |
| account_provisioning 账号申请 | system_name*、account_type*(personal/shared/service)、owner_id(条件)、valid_until(条件)、requested_roles[≤20]、purpose*(5~500) | shared/service → owner_id 与 valid_until 必填（≤今天+365）；personal 账号查重(409) |
