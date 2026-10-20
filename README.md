# Approval System (审批管理系统) — Backend

线上审批流服务：请假 / 报销 / 采购 / 合同 / 权限申请 / 服务器开通 / 账号申请 七类审批，
提交后自动路由到对应负责人，支持多级审批、加急、超时提醒、撤回、审计日志与通知。

技术栈：**Python 3.11+ / FastAPI / SQLAlchemy 2 / SQLite / pytest**（契约见 `docs/decisions.md`）。

## 当前状态：v0.2.1 脚手架（契约冻结，业务未实现）

- 契约已对齐前端表单规格（`docs/decisions.md` D17）：金额整数分、报销/采购明细行、条件必填、全局查重错误码。
- 所有业务路由为 stub，统一返回 `501 {"error": {"code": "NOT_IMPLEMENTED", ...}}`。
- 请求体 / 响应模型 / 错误封装 / 状态枚举已按契约完整定义（`app/schemas/`、`app/core/errors.py`、`app/models/enums.py`），
  输入校验类断言（422 + `VALIDATION_ERROR`）在脚手架阶段即已生效。
- 目的：供测试先行落 **可执行的失败测试**（pytest + FakeClock），实现阶段再逐条转绿。

## 快速开始

```bash
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt      # Windows
# .venv/bin/pip install -r requirements.txt        # Linux/macOS

.venv\Scripts\python -m pytest                     # 运行测试（当前仅 smoke）
.venv\Scripts\python -m uvicorn app.main:app       # 启动服务，/docs 查看 OpenAPI 契约
```

## 契约入口

| 位置 | 内容 |
|---|---|
| `docs/decisions.md` | v1 契约决策全文（状态机、路由、超时、幂等、并发、错误码、通知矩阵） |
| `GET /openapi.json`（运行后） | 机器可读 API 契约 |
| `app/schemas/payloads.py` | 7 类审批的提交表单字段与校验规则（v1 最小字段集） |
| `app/core/errors.py` | 统一错误封装与错误码 |
| `tests/conftest.py` | `FakeClock` / `client` / `auth_headers` fixtures |

## 目录结构

```
app/
  main.py            # create_app 工厂（可注入 clock / database_url）
  core/clock.py      # Clock 协议 + SystemClock（时间逻辑一律经 app.state.clock）
  core/errors.py     # ErrorCode / ApiError / 统一错误封装 handler
  models/enums.py    # ApprovalType / InstanceStatus / TaskStatus / NotificationKind
  models/db.py       # SQLAlchemy Base 与 engine/session 工具
  models/models.py   # ORM 表：instances/tasks/route_configs/notifications/audit/idempotency
  schemas/payloads.py# 7 类审批 payload 契约
  schemas/api.py     # 请求/响应模型
  api/               # 路由（当前全部 stub，health 除外）
tests/
  conftest.py        # FakeClock、client fixtures
  test_smoke.py      # 脚手架健康检查 + stub/校验行为基线
```

## 约定

- 提交遵循 conventional commits（`feat:` / `fix:` / `test:` / `docs:` / `chore:`），小步提交。
- 先写失败测试，再实现至通过；时间相关测试一律使用 `FakeClock`，禁止 sleep。
- v1 鉴权：`X-User-Id` 请求头标识操作人，无真实认证（见 decisions.md D10）。
