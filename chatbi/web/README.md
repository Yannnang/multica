# chatbi-web — 智能问数平台前端（S3 / MYIT-22）

Vue 3 + TypeScript + Vite + ECharts 的对话式问数前端：飞书 SSO 登录、会话问答（四态回答渲染）、
图表渲染（bar/line/pie）、反馈标注、语义层管理页。

契约以 MYIT-15《设计基线 v1.0》为准（**契约先行**）：后端 S1/S2/S4 未合入期间，
以 msw mock 契约开发，字段映射与假设见 PR 描述的映射表。

## 快速开始

```bash
npm install
npm run dev          # 开发（默认 VITE_USE_MOCK=1，走 msw mock 契约）
npm run test         # Vitest 组件/单元测试
npm run build        # 生产构建（vue-tsc 类型检查 + vite build，不含 mock）
npm run build:mock   # mock 构建（E2E 冒烟用）+ npm run preview
```

真实后端联调：`.env.development` 中置 `VITE_USE_MOCK=`（关闭 mock），
vite proxy 已将 `/api` 指向 `http://localhost:5000`（ChatBi.Api 默认地址）。

## Mock 登录

mock 模式下登录页可选角色（BusinessUser / DataSteward / SysAdmin），
点击「飞书登录」走完整的 `/api/auth/feishu/login → 302 → /auth/callback → /chat` 链路
（msw 拦截；token 经 URL 传递后由回调页存入内存 + sessionStorage 并立即清除地址栏痕迹）。

问答场景按关键词触发（详见 `src/mocks/handlers.ts`）：

| 提问包含 | 场景 |
|---|---|
| 趋势 / 月 | Success + line 图 |
| 占比 / 份额 / 渠道 | Success + pie 图 |
| （默认，如“销售额”） | Success + bar 图 |
| 空 | Success + 空结果提示 |
| 截断 / 明细 | Success + 5000 行截断提示（采样 200 行） |
| 歧义 / 那个 / 再看下 | ClarifyNeeded + 澄清选项（点击即追问） |
| 薪酬 / 工资 / 触发403 | Rejected（`PERM_FORBIDDEN_TABLE`） |
| 失败 / 故障 | Failed（`LLM_UPSTREAM`） |
| 触发401 | 首次 401 → 前端静默续期 → 自动重试成功 |

## 目录结构

```
src/
├── api/            # 契约类型（types.ts）+ axios client（401/403 拦截、refresh 单飞）+ 端点模块
├── components/
│   ├── chat/       # AnswerCard（四态）/ ChartView（ECharts）/ SqlView / FeedbackForm / SessionList / AskInput
│   └── semantic/   # schema 驱动的 CrudPanel + 六资源字段 schema + 表单校验
├── mocks/          # msw handlers + fixtures（仅 mock 构建/开发进入 bundle，生产构建剔除）
├── pages/          # Login / AuthCallback / Chat / Semantic / Forbidden
├── router/         # 路由守卫：requiresAuth + roles（语义层仅 DataSteward/SysAdmin）
├── stores/         # auth（token 内存+sessionStorage，JWT claims 解码）+ ui（403 页面级提示）
└── utils/          # enums（STJ 字符串/tinyint 双兼容归一化）/ chart / jwt / uuid
```

## 安全红线（本工程约定）

- 后端文本（Narrative / ErrorMessage / SQL）一律 `{{ }}` 插值纯文本渲染，**禁 v-html**。
- token 只存内存 + sessionStorage，不落 localStorage；回调后立即清除地址栏 token 痕迹。
- token 与用户输入不进 console.log；构建产物不含任何环境密钥。
- Idempotency-Key 前端生成 UUID，网络失败重试复用同一 key（24h 幂等窗口）。

## E2E 冒烟

`npm run build:mock && npm run preview` 后按 `docs/e2e/` 截图中的路径手动/自动走查：
登录 → 提问 → 图表渲染（bar/line/pie）→ 澄清追问 → 反馈提交 → 语义层 CRUD → 守卫拦截。

## 待与后端联调核对的假设

见 PR 描述「契约字段映射表」中标注 *(假设)* 的条目：回调 token 传递方式、
`POST /api/auth/refresh` 请求/响应形状、`GET /api/auth/me`、历史消息 DTO 字段名、
ChartSpec.Rows 列顺序、截断判定、rowVersion 并发令牌字段。
