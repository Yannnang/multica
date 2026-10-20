# ChatBi 后端（智能问数平台 · S1 基座）

.NET 8 / ASP.NET Core Web API。本目录为 MYIT-20（S1 后端基座）交付：解决方案骨架、平台库全部实体与迁移、
飞书 SSO（mock 可切换）、JWT 会话、RBAC + 行级权限基础服务。设计基线见 MYIT-15 评论与《数据库表设计 v1.0》。

## 解决方案结构

```
ChatBi.sln
├─ src/ChatBi.Api            # Controller、DI 组合根、中间件、Policy 授权、Serilog/OTel/HealthChecks
├─ src/ChatBi.Application    # 用例服务（AuthService、DataPermissionEvaluator）、DTO(record)、接口定义
├─ src/ChatBi.Domain         # 实体、枚举、PermissionMatrix；不引用任何其他项目
├─ src/ChatBi.Infrastructure # EF Core（DbContext/配置/迁移/种子）、飞书客户端（真实+mock）、JWT、仓储
└─ tests/ChatBi.Tests        # xUnit 单测 + WebApplicationFactory 集成测试（飞书 mock + 真实 SQL Server）
```

依赖方向：`Api → Application → Domain`；`Infrastructure → Application`（实现其接口）；Api 作为组合根引用 Infrastructure。

## 本地运行

前置：.NET 8 SDK；SQL Server（LocalDB / Docker / 实例均可）。

```bash
# 环境变量（红线：JWT 密钥仅来自环境变量，仓库零真实密钥）
export JWT_SIGNING_KEY='<随机 >=32 字符>'        # 例：openssl rand -base64 48
export ConnectionStrings__PlatformDb='Server=(localdb)\MSSQLLocalDB;Database=ChatBi_Platform;Trusted_Connection=True;Encrypt=False'
export Feishu__UseMock=true                      # S0 完成前用 mock；真实联调改 false 并配 AppId/AppSecret/RedirectUri

dotnet run --project src/ChatBi.Api --urls http://localhost:5199
curl http://localhost:5199/health                # 200 Healthy（含平台库 DB 检查）
```

启动时默认自动应用迁移（`Database:ApplyMigrationsOnStart`，可关）。

### mock 模式 SSO 冒烟

```bash
# 1) 发起登录，从 302 Location 里取 state
curl -i 'http://localhost:5199/api/auth/feishu/login?redirect=/chat'
# 2) 用 mock code 完成回调（JSON 流）
curl -H 'Accept: application/json' \
  'http://localhost:5199/api/auth/feishu/callback?code=mock-ou_yang&state=<STATE>'
# 3) 带 token 访问
curl -H 'Authorization: Bearer <accessToken>' http://localhost:5199/api/auth/me
```

mock code 约定（`MockFeishuAuthClient`）：`mock-{unionId}` 成功建档；`mock-upstream-down` 模拟飞书不可用（502）；其余 → 401 AUTH_CODE_INVALID。

## Docker Compose

```bash
JWT_SIGNING_KEY='<随机 >=32 字符>' SA_PASSWORD='<强密码>' docker compose up -d --build
curl http://localhost:8080/health
```

## 迁移

```bash
# 幂等脚本（部署产物，可重复执行）
dotnet ef migrations script --idempotent --project src/ChatBi.Infrastructure --startup-project src/ChatBi.Api -o deploy/idempotent_migration.sql
# 应用（sqlcmd 需 -I：过滤唯一索引要求 QUOTED_IDENTIFIER ON）
sqlcmd -S <server> -d <db> -I -b -i deploy/idempotent_migration.sql
# 或直接：./deploy/apply-migrations.sh（需 PLATFORM_DB_CONNECTION / CHATBI_SQL_SERVER）
# 回滚到空库：
dotnet ef database update 0 --project src/ChatBi.Infrastructure --startup-project src/ChatBi.Api --connection '<连接串>'
```

## 测试

```bash
dotnet test --collect:"XPlat Code Coverage"
```

- 单测：授权 Handler / PermissionMatrix、JwtTokenService（篡改/过期/alg=none/密钥校验）、AuthService 全分支、
  DataPermissionEvaluator、state 一次性、Serilog 脱敏 enricher。
- 集成（WebApplicationFactory + 飞书 mock）：需要 SQL Server；默认 `(localdb)\MSSQLLocalDB`
  （无 Docker 环境的既定偏离，见 PR 说明；有 Docker 的 CI 可换 Testcontainers，工厂只改连接串）。
  覆盖：登录建档、再次登录、state 错误/重用、code 无效、上游 502、禁用 403、401/403 区分、
  JWT 篡改/过期/alg=none/TokenVersion、refresh 滑动、logout 撤销、/health、CHECK 约束、过滤唯一索引。

## 配置项（全占位符，真实值仅经环境变量注入）

| 配置 | 环境变量 | 说明 |
|---|---|---|
| Jwt:SigningKey | `JWT_SIGNING_KEY` | HS256 密钥，>=32 字符，唯一来源环境变量 |
| Jwt:Issuer / Audience | `Jwt__Issuer` / `Jwt__Audience` | 默认 chatbi / chatbi-web |
| Jwt:AccessTokenMinutes / RefreshTokenDays | 同名 | 60 / 7（假设值，配置化） |
| ConnectionStrings:PlatformDb | `ConnectionStrings__PlatformDb`（`<PLATFORM_DB_CONNECTION>`） | 平台库 |
| Feishu:UseMock | `Feishu__UseMock` | true=mock 客户端 |
| Feishu:AppId/AppSecret/RedirectUri/ScopeList | `<FEISHU_APP_ID>` 等 | S0 完成后填真实值 |
| Feishu:AdminUnionId | `<ADMIN_UNION_ID>` | 首个 SysAdmin 的 union_id |
| Database:ApplyMigrationsOnStart | 同名 | 默认 true |
| OTEL_EXPORTER_OTLP_ENDPOINT | 同名 | 配置后 OTLP 导出（骨架已接 AspNetCore/Http 埋点） |

## 安全红线落点

- 密钥/连接串：仅环境变量；仓库与 appsettings 全占位符；`SensitiveDataRedactionEnricher` 兜底脱敏日志。
- JWT：仅 HS256（`ValidAlgorithms` 白名单 + `RequireSignedTokens`，拒 alg=none/算法混淆）；
  验签名/Issuer/Audience/过期；`token_ver` 对库校验（登出/禁用即全端失效）。
- user_access_token 用后即弃，不持久化不进日志；业务凭证一律本地 JWT。
- 回跳白名单：仅配置前缀内相对路径，防开放重定向。
- 数据权限规则只存结构化 JSON，评估器白名单 op，SQL 片段拒收（改写器 S4）。
