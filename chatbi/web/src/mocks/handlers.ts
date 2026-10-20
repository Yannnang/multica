import { http, HttpResponse, delay } from 'msw';
import {
  CHART_BAR,
  CHART_LINE,
  CHART_PIE,
  CHART_TRUNCATED,
  mockColumns,
  mockDataSources,
  mockMessages,
  mockMetrics,
  mockRelations,
  mockSessions,
  mockSynonyms,
  mockTables,
  type MockMessage,
  type MockSession,
} from './fixtures';

/**
 * msw handlers —— 契约 mock（S1/S2 未合入期间契约先行开发 + E2E 冒烟）。
 * 响应字段严格对照《设计基线 v1.0》1.5：AskResponse / ChartSpec / PagedResult /
 * ApiError（ProblemDetails + code），错误码约定 401/403(PERM_*)/409/422(SEM_*,ASK_*)/429/502。
 */

/* ---------- mock 认证：伪 JWT（不验签，仅承载 role claim 供前端守卫） ----------
 * tokenVersion / refreshToken 集合持久化到 sessionStorage：
 * mock handler 运行在页面上下文，登录跳转是整页导航，模块态会随页面重载丢失，
 * 而真实后端（S1）该状态是服务端持久的——mock 必须同构，否则跨页面 token 校验失真。
 */

interface AuthMockState {
  tokenVersion: number;
  refreshTokens: string[];
}

const AUTH_STATE_KEY = 'chatbi.mock.auth';

function loadAuthState(): AuthMockState {
  try {
    const raw = globalThis.sessionStorage?.getItem(AUTH_STATE_KEY);
    if (raw) return JSON.parse(raw) as AuthMockState;
  } catch {
    /* 无 sessionStorage 的环境按初始态处理 */
  }
  return { tokenVersion: 1, refreshTokens: [] };
}

function saveAuthState(state: AuthMockState): void {
  try {
    globalThis.sessionStorage?.setItem(AUTH_STATE_KEY, JSON.stringify(state));
  } catch {
    /* ignore */
  }
}

function b64url(obj: unknown): string {
  const json = JSON.stringify(obj);
  const b64 = btoa(unescape(encodeURIComponent(json)));
  return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function issueTokens(role: string): { accessToken: string; refreshToken: string } {
  const state = loadAuthState();
  const now = Math.floor(Date.now() / 1000);
  const accessToken = [
    b64url({ alg: 'HS256', typ: 'JWT' }),
    b64url({
      sub: '1',
      union_id: `mock_union_${role.toLowerCase()}`,
      role: [role],
      dept_path: '/总部/技术部',
      token_ver: state.tokenVersion,
      exp: now + 3600,
    }),
    'mock-signature',
  ].join('.');
  const refreshToken = `mock-refresh.${role}.${state.tokenVersion}.${Math.random().toString(36).slice(2)}`;
  state.refreshTokens.push(refreshToken);
  saveAuthState(state);
  return { accessToken, refreshToken };
}

function readBearer(request: Request): string | null {
  const h = request.headers.get('Authorization');
  return h?.startsWith('Bearer ') ? h.slice(7) : null;
}

function claimsOf(token: string | null): Record<string, unknown> | null {
  if (!token) return null;
  try {
    const part = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(decodeURIComponent(escape(atob(part)))) as Record<string, unknown>;
  } catch {
    return null;
  }
}

const MOCK_NAMES: Record<string, string> = {
  businessuser: '业务用户（Mock）',
  datasteward: '数据管理员（Mock）',
  sysadmin: '系统管理员（Mock）',
};

function unauthorized(code = 'AUTH_TOKEN_EXPIRED', detail = 'token 缺失或已过期'): Response {
  return HttpResponse.json(
    { type: 'https://chatbi.local/problems/unauthorized', title: 'Unauthorized', status: 401, code, detail },
    { status: 401 },
  );
}

function requireAuth(request: Request): { claims: Record<string, unknown> } | { error: Response } {
  const claims = claimsOf(readBearer(request));
  if (!claims) return { error: unauthorized() };
  const exp = claims.exp;
  if (typeof exp === 'number' && exp * 1000 < Date.now()) return { error: unauthorized() };
  if (claims.token_ver !== loadAuthState().tokenVersion) {
    return { error: unauthorized('AUTH_TOKEN_REVOKED', 'token 已被撤销（登出/禁用）') };
  }
  return { claims };
}

/* ---------- mock 内存数据 ---------- */

const sessions: MockSession[] = [...mockSessions];
const messages: MockMessage[] = [...mockMessages];
let nextSessionId = 100;
let nextMessageId = 1000;

/** 幂等缓存：(sessionId, Idempotency-Key) → AskResponse（24h 窗口，mock 内为进程期） */
const idempotencyCache = new Map<string, Record<string, unknown>>();
/** 401 演练：同一 key 首次 401，重试放行 */
const firstAttempt401 = new Set<string>();

const feedbacks: { messageId: number; verdict: string; correctedSql?: string | null; note?: string | null }[] = [];

interface SemanticStore {
  rows: Record<string, unknown>[];
  nextId: number;
  hasRowVersion: boolean;
  required: string[];
}

function store(rows: Record<string, unknown>[], required: string[], hasRowVersion = true): SemanticStore {
  return { rows: [...rows], nextId: rows.length + 100, hasRowVersion, required };
}

const semanticStores: Record<string, SemanticStore> = {
  datasources: store(mockDataSources as unknown as Record<string, unknown>[], ['name', 'dbType', 'host', 'port', 'databaseName', 'credentialRef', 'queryTimeoutSeconds', 'maxRowsPerQuery', 'status']),
  tables: store(mockTables as unknown as Record<string, unknown>[], ['dataSourceId', 'schemaName', 'tableName', 'displayName']),
  columns: store(mockColumns as unknown as Record<string, unknown>[], ['semanticTableId', 'columnName', 'displayName', 'dbTypeName', 'semanticType']),
  metrics: store(mockMetrics as unknown as Record<string, unknown>[], ['code', 'displayName', 'expression', 'aggregationType']),
  relations: store(mockRelations as unknown as Record<string, unknown>[], ['leftSemanticTableId', 'rightSemanticTableId', 'joinType', 'onConditions']),
  synonyms: store(mockSynonyms as unknown as Record<string, unknown>[], ['term', 'targetType', 'targetId'], false),
};

function paged<T>(items: T[], page: number, pageSize: number): { items: T[]; page: number; pageSize: number; total: number } {
  const start = (page - 1) * pageSize;
  return { items: items.slice(start, start + pageSize), page, pageSize, total: items.length };
}

function pageOf(request: Request): { page: number; pageSize: number } {
  const url = new URL(request.url);
  const page = Math.max(1, Number(url.searchParams.get('page') ?? 1) || 1);
  const pageSize = Math.min(100, Math.max(1, Number(url.searchParams.get('pageSize') ?? 20) || 20));
  return { page, pageSize };
}

/* ---------- 提问场景（四态 + 边界提示） ---------- */

interface Scenario {
  status: 'Success' | 'ClarifyNeeded' | 'Rejected' | 'Failed';
  sql?: string | null;
  chart?: (typeof CHART_BAR) | null;
  narrative?: string | null;
  clarifyOptions?: string[] | null;
  rowsReturned?: number;
  elapsedMs?: number;
  errorCode?: string | null;
  errorMessage?: string | null;
}

function scenarioFor(question: string): Scenario {
  const q = question.toLowerCase();
  if (q.includes('触发401') || q.includes('过期')) {
    return { status: 'Failed', errorCode: 'AUTH_TOKEN_EXPIRED', errorMessage: '登录已过期' };
  }
  if (q.includes('触发403') || q.includes('薪酬') || q.includes('工资')) {
    return {
      status: 'Rejected',
      errorCode: 'PERM_FORBIDDEN_TABLE',
      errorMessage: '该问题涉及未授权的数据范围（列级敏感或表级白名单外），已被拒绝。',
      narrative: null,
    };
  }
  if (q.includes('歧义') || q.includes('那个') || q.includes('再看下')) {
    return {
      status: 'ClarifyNeeded',
      narrative: '问题存在歧义，请选择一个更具体的问法：',
      clarifyOptions: ['近 6 个月各区域销售额趋势', '本月销售额环比变化', '各渠道销售额占比'],
      elapsedMs: 380,
    };
  }
  if (q.includes('失败') || q.includes('故障')) {
    return {
      status: 'Failed',
      errorCode: 'LLM_UPSTREAM',
      errorMessage: '上游模型服务暂时不可用，请稍后重试。',
    };
  }
  if (q.includes('空')) {
    return {
      status: 'Success',
      sql: 'SELECT region AS 区域, SUM(amount) AS 销售额 FROM dbo.SalesOrders WHERE 1 = 0 GROUP BY region;',
      chart: { chartType: 'bar', xField: '区域', yFields: ['销售额'], rows: [] },
      narrative: '查询执行成功，但没有符合条件的数据。',
      rowsReturned: 0,
      elapsedMs: 640,
    };
  }
  if (q.includes('截断') || q.includes('明细')) {
    return {
      status: 'Success',
      sql: 'SELECT TOP 5000 sku AS 商品, qty AS 销量 FROM dbo.SalesOrderLines ORDER BY qty DESC;',
      chart: CHART_TRUNCATED,
      narrative: '结果共 5000 行，图表按采样上限展示前 200 行。',
      rowsReturned: 5000,
      elapsedMs: 4200,
    };
  }
  if (q.includes('趋势') || q.includes('月')) {
    return {
      status: 'Success',
      sql: "SELECT FORMAT(created_at, 'yyyy-MM') AS 月份, SUM(amount) AS 销售额\nFROM dbo.SalesOrders\nWHERE created_at >= DATEADD(MONTH, -6, GETUTCDATE())\nGROUP BY FORMAT(created_at, 'yyyy-MM')\nORDER BY 1;",
      chart: CHART_LINE,
      narrative: '近 6 个月销售额整体上行，10 月达到峰值 1230（万元），环比 9 月增长 3.4%。',
      rowsReturned: 6,
      elapsedMs: 1520,
    };
  }
  if (q.includes('占比') || q.includes('份额') || q.includes('渠道')) {
    return {
      status: 'Success',
      sql: 'SELECT channel AS 渠道, SUM(amount) AS 销售额 FROM dbo.SalesOrders GROUP BY channel ORDER BY 2 DESC;',
      chart: CHART_PIE,
      narrative: '直销渠道占比最高（40.8%），电商渠道占比 21.9%，仍有提升空间。',
      rowsReturned: 4,
      elapsedMs: 1310,
    };
  }
  return {
    status: 'Success',
    sql: "SELECT region AS 区域, SUM(amount) AS 销售额\nFROM dbo.SalesOrders\nWHERE dept_path LIKE @prefix + '%'\nGROUP BY region\nORDER BY SUM(amount) DESC;",
    chart: CHART_BAR,
    narrative: '华东区域销售额最高（120.0 万），领先第二名华南约 8.6%；东北区域最低。',
    rowsReturned: 5,
    elapsedMs: 1830,
  };
}

/* ---------- handlers ---------- */

export const handlers = [
  /* ===== auth ===== */

  // GET /api/auth/feishu/login?redirect=&mock_role= → 302 回调页。
  // 注意：SW 响应的 302 会按 fetch 规范剥离 Location fragment，故 mock 走 query 传递；
  // 真实后端（非 SW）302 建议用 fragment（回调页 fragment 优先解析）。
  http.get('/api/auth/feishu/login', ({ request }) => {
    const url = new URL(request.url);
    const role = url.searchParams.get('mock_role') ?? 'BusinessUser';
    const redirect = url.searchParams.get('redirect') ?? '/chat';
    const safeRedirect = redirect.startsWith('/') && !redirect.startsWith('//') ? redirect : '/chat';
    const { accessToken, refreshToken } = issueTokens(role);
    const location = `/auth/callback?redirect=${encodeURIComponent(safeRedirect)}&access_token=${accessToken}&refresh_token=${refreshToken}`;
    return new HttpResponse(null, { status: 302, headers: { Location: location } });
  }),

  // POST /api/auth/refresh
  http.post('/api/auth/refresh', async ({ request }) => {
    const body = (await request.json().catch(() => ({}))) as { refreshToken?: string };
    const state = loadAuthState();
    if (!body.refreshToken || !state.refreshTokens.includes(body.refreshToken)) {
      return unauthorized('AUTH_REFRESH_INVALID', 'refresh token 无效或已失效');
    }
    // 滑动续期：旧 refresh 作废，签发新对
    state.refreshTokens = state.refreshTokens.filter((t) => t !== body.refreshToken);
    saveAuthState(state);
    const claims = claimsOf(readBearer(request));
    const role = Array.isArray(claims?.role) ? String(claims.role[0]) : 'BusinessUser';
    const tokens = issueTokens(role || 'BusinessUser');
    return HttpResponse.json({ accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, expiresIn: 3600 });
  }),

  // POST /api/auth/logout → TokenVersion++（全部旧 token 失效，基线 3.4/4.6）
  http.post('/api/auth/logout', () => {
    const state = loadAuthState();
    state.tokenVersion += 1;
    state.refreshTokens = [];
    saveAuthState(state);
    return new HttpResponse(null, { status: 204 });
  }),

  // GET /api/auth/me
  http.get('/api/auth/me', ({ request }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const claims = auth.claims;
    const roles = Array.isArray(claims.role) ? claims.role : [];
    const roleKey = String(roles[0] ?? 'businessuser').toLowerCase();
    return HttpResponse.json({
      id: Number(claims.sub ?? 1),
      name: MOCK_NAMES[roleKey] ?? 'Mock 用户',
      avatarUrl: null,
      email: null,
      departmentPath: claims.dept_path ?? null,
      roles,
    });
  }),

  /* ===== chat ===== */

  http.post('/api/chat/sessions', ({ request }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const s: MockSession = {
      id: ++nextSessionId,
      title: '新会话',
      lastMessageAtUtc: new Date().toISOString(),
    };
    sessions.unshift(s);
    return HttpResponse.json(s, { status: 201 });
  }),

  // GET /api/chat/sessions?page&pageSize（OrderBy LastMessageAtUtc DESC）
  http.get('/api/chat/sessions', ({ request }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const { page, pageSize } = pageOf(request);
    const sorted = [...sessions].sort(
      (a, b) => new Date(b.lastMessageAtUtc).getTime() - new Date(a.lastMessageAtUtc).getTime(),
    );
    return HttpResponse.json(paged(sorted, page, pageSize));
  }),

  // GET /api/chat/sessions/:id/messages?page&pageSize（OrderBy CreatedAtUtc DESC）
  http.get('/api/chat/sessions/:id/messages', ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const sessionId = Number(params.id);
    const { page, pageSize } = pageOf(request);
    const scoped = messages
      .filter((m) => m.sessionId === sessionId)
      .sort((a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime());
    return HttpResponse.json(paged(scoped, page, pageSize));
  }),

  // POST /api/chat/sessions/:id/messages（Header Idempotency-Key，24h 窗口）
  http.post('/api/chat/sessions/:id/messages', async ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    await delay(150);
    const sessionId = Number(params.id);
    const idemKey = request.headers.get('Idempotency-Key');
    const body = (await request.json().catch(() => ({}))) as { question?: string };
    const question = body.question?.trim() ?? '';
    if (!question) {
      return HttpResponse.json(
        { title: 'Validation', status: 422, code: 'ASK_EMPTY_QUESTION', detail: '问题不能为空' },
        { status: 422 },
      );
    }
    if (!idemKey) {
      return HttpResponse.json(
        { title: 'Validation', status: 422, code: 'ASK_MISSING_IDEMPOTENCY_KEY', detail: '缺少 Idempotency-Key Header' },
        { status: 422 },
      );
    }

    const cacheKey = `${sessionId}:${idemKey}`;

    // 幂等命中：直接返回缓存响应，不重复落库
    const cached = idempotencyCache.get(cacheKey);
    if (cached) {
      return HttpResponse.json(cached);
    }

    // 401 演练场景：首次 401，前端静默续期后重试（同一 key）放行
    if (question.includes('触发401') && !firstAttempt401.has(cacheKey)) {
      firstAttempt401.add(cacheKey);
      return unauthorized('AUTH_TOKEN_EXPIRED', '模拟 access token 过期，前端应静默续期后重试');
    }

    const scenario = scenarioFor(question);
    const now = new Date().toISOString();

    const userMsg: MockMessage = {
      id: ++nextMessageId,
      sessionId,
      role: 'User',
      questionText: question,
      idempotencyKey: idemKey,
      createdAtUtc: now,
    };
    const assistantMsg: MockMessage = {
      id: ++nextMessageId,
      sessionId,
      role: 'Assistant',
      status: scenario.status,
      generatedSql: scenario.sql ?? null,
      clarifyOptions: scenario.clarifyOptions ?? null,
      chart: scenario.chart ?? null,
      narrative: scenario.narrative ?? null,
      rowCountReturned: scenario.rowsReturned ?? 0,
      elapsedMs: scenario.elapsedMs ?? 500,
      errorCode: scenario.errorCode ?? null,
      errorMessage: scenario.errorMessage ?? null,
      createdAtUtc: now,
    };
    messages.push(userMsg, assistantMsg);

    const session = sessions.find((s) => s.id === sessionId);
    if (session) {
      session.lastMessageAtUtc = now;
      if (session.title === '新会话') {
        session.title = question.length > 30 ? `${question.slice(0, 30)}…` : question;
      }
    }

    // AskResponse（契约 1.5：camelCase；枚举以名称输出，前端归一化双兼容）
    const askResponse = {
      messageId: assistantMsg.id,
      status: assistantMsg.status,
      sql: assistantMsg.generatedSql,
      chart: assistantMsg.chart,
      narrative: assistantMsg.narrative,
      clarifyOptions: assistantMsg.clarifyOptions,
      rowsReturned: assistantMsg.rowCountReturned,
      elapsedMs: assistantMsg.elapsedMs,
      error:
        assistantMsg.errorCode || assistantMsg.errorMessage
          ? { code: assistantMsg.errorCode ?? 'ASK_FAILED', message: assistantMsg.errorMessage ?? '' }
          : null,
    };
    idempotencyCache.set(cacheKey, askResponse);
    return HttpResponse.json(askResponse, { status: 201 });
  }),

  // POST /api/chat/messages/:id/feedback
  http.post('/api/chat/messages/:id/feedback', async ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const body = (await request.json().catch(() => ({}))) as {
      verdict?: string;
      correctedSql?: string | null;
      note?: string | null;
    };
    const valid = ['Correct', 'IncorrectSql', 'WrongCaliber', 'AmbiguousQuestion'];
    if (!body.verdict || !valid.includes(body.verdict)) {
      return HttpResponse.json(
        { title: 'Validation', status: 422, code: 'ASK_INVALID_VERDICT', detail: `verdict 必须为 ${valid.join('/')}` },
        { status: 422 },
      );
    }
    feedbacks.push({
      messageId: Number(params.id),
      verdict: body.verdict,
      correctedSql: body.correctedSql ?? null,
      note: body.note ?? null,
    });
    return new HttpResponse(null, { status: 204 });
  }),

  /* ===== semantic（六资源通用 CRUD；409 并发冲突 / 422 SEM_* 校验） ===== */

  http.get('/api/semantic/:kind', ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const store = semanticStores[String(params.kind)];
    if (!store) return HttpResponse.json({ title: 'Not Found', status: 404, code: 'SEM_UNKNOWN_RESOURCE' }, { status: 404 });
    const { page, pageSize } = pageOf(request);
    const url = new URL(request.url);
    let items = store.rows;
    // 可选过滤（如 columns?semanticTableId=1）
    for (const [k, v] of url.searchParams.entries()) {
      if (k !== 'page' && k !== 'pageSize') {
        items = items.filter((r) => String(r[k]) === v);
      }
    }
    return HttpResponse.json(paged(items, page, pageSize) as unknown as Record<string, unknown>);
  }),

  http.post('/api/semantic/:kind', async ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const claims = auth.claims;
    const roles = (Array.isArray(claims.role) ? claims.role : []) as string[];
    if (!roles.includes('DataSteward') && !roles.includes('SysAdmin')) {
      return HttpResponse.json(
        { title: 'Forbidden', status: 403, code: 'PERM_SEMANTIC_MANAGE', detail: '需要 DataSteward 或 SysAdmin 角色（semantic:manage）' },
        { status: 403 },
      );
    }
    const store = semanticStores[String(params.kind)];
    if (!store) return HttpResponse.json({ title: 'Not Found', status: 404, code: 'SEM_UNKNOWN_RESOURCE' }, { status: 404 });
    const body = (await request.json().catch(() => ({}))) as Record<string, unknown>;
    const missing = store.required.filter((k) => body[k] == null || body[k] === '');
    if (missing.length > 0) {
      return HttpResponse.json(
        {
          title: 'Validation',
          status: 422,
          code: 'SEM_VALIDATION',
          detail: `缺少必填字段：${missing.join(', ')}`,
          errors: Object.fromEntries(missing.map((k) => [k, ['为必填项']])),
        },
        { status: 422 },
      );
    }
    const row: Record<string, unknown> = { ...body, id: ++store.nextId };
    if (store.hasRowVersion) row.rowVersion = `rv-${Date.now()}-${row.id}`;
    store.rows.push(row);
    return HttpResponse.json(row, { status: 201 });
  }),

  http.put('/api/semantic/:kind/:id', async ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const claims = auth.claims;
    const roles = (Array.isArray(claims.role) ? claims.role : []) as string[];
    if (!roles.includes('DataSteward') && !roles.includes('SysAdmin')) {
      return HttpResponse.json(
        { title: 'Forbidden', status: 403, code: 'PERM_SEMANTIC_MANAGE', detail: '需要 DataSteward 或 SysAdmin 角色（semantic:manage）' },
        { status: 403 },
      );
    }
    const store = semanticStores[String(params.kind)];
    if (!store) return HttpResponse.json({ title: 'Not Found', status: 404, code: 'SEM_UNKNOWN_RESOURCE' }, { status: 404 });
    const id = Number(params.id);
    const idx = store.rows.findIndex((r) => Number(r.id) === id);
    if (idx < 0) {
      return HttpResponse.json({ title: 'Not Found', status: 404, code: 'SEM_NOT_FOUND', detail: '记录不存在' }, { status: 404 });
    }
    const body = (await request.json().catch(() => ({}))) as Record<string, unknown>;
    const missing = store.required.filter((k) => body[k] == null || body[k] === '');
    if (missing.length > 0) {
      return HttpResponse.json(
        {
          title: 'Validation',
          status: 422,
          code: 'SEM_VALIDATION',
          detail: `缺少必填字段：${missing.join(', ')}`,
          errors: Object.fromEntries(missing.map((k) => [k, ['为必填项']])),
        },
        { status: 422 },
      );
    }
    // RowVersion 并发令牌：不匹配 → 409（基线错误码约定）
    if (store.hasRowVersion) {
      const current = store.rows[idx].rowVersion;
      const sent = body.rowVersion;
      if (current != null && sent != null && sent !== current) {
        return HttpResponse.json(
          { title: 'Conflict', status: 409, code: 'SEM_CONCURRENCY_CONFLICT', detail: '记录已被他人修改，请刷新后重试' },
          { status: 409 },
        );
      }
    }
    const updated: Record<string, unknown> = { ...store.rows[idx], ...body, id };
    if (store.hasRowVersion) updated.rowVersion = `rv-${Date.now()}-${id}`;
    store.rows[idx] = updated;
    return HttpResponse.json(updated);
  }),

  http.delete('/api/semantic/:kind/:id', ({ request, params }) => {
    const auth = requireAuth(request);
    if ('error' in auth) return auth.error;
    const claims = auth.claims;
    const roles = (Array.isArray(claims.role) ? claims.role : []) as string[];
    if (!roles.includes('DataSteward') && !roles.includes('SysAdmin')) {
      return HttpResponse.json(
        { title: 'Forbidden', status: 403, code: 'PERM_SEMANTIC_MANAGE', detail: '需要 DataSteward 或 SysAdmin 角色（semantic:manage）' },
        { status: 403 },
      );
    }
    const store = semanticStores[String(params.kind)];
    if (!store) return HttpResponse.json({ title: 'Not Found', status: 404, code: 'SEM_UNKNOWN_RESOURCE' }, { status: 404 });
    const id = Number(params.id);
    const idx = store.rows.findIndex((r) => Number(r.id) === id);
    if (idx >= 0) store.rows.splice(idx, 1);
    return new HttpResponse(null, { status: 204 });
  }),

  /* ===== health ===== */
  http.get('/health', () => HttpResponse.json({ status: 'Healthy' })),
];

/** 测试辅助：重置 mock 内存态（避免用例间串扰） */
export function __resetMockState(): void {
  saveAuthState({ tokenVersion: 1, refreshTokens: [] });
  sessions.length = 0;
  sessions.push(...mockSessions);
  messages.length = 0;
  messages.push(...mockMessages);
  idempotencyCache.clear();
  firstAttempt401.clear();
  feedbacks.length = 0;
  nextSessionId = 100;
  nextMessageId = 1000;
}
