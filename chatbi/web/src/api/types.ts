/**
 * 契约类型 —— 与 MYIT-15《设计基线 v1.0》1.5 节接口契约逐字段对应。
 * 后端为 .NET record + System.Text.Json（camelCase）。
 * 枚举：基线以 C# 枚举名标注（如 Success|ClarifyNeeded），STJ 默认可能序列化为
 * tinyint 存储值，故运行时统一经 utils/enums.ts 归一化，双兼容。
 */

/** AskStatus（dbdesign 枚举字典：Success=1, ClarifyNeeded=2, Rejected=3, Failed=4） */
export type AskStatus = 'Success' | 'ClarifyNeeded' | 'Rejected' | 'Failed';

/** ChartSpec.ChartType（MVP 三类） */
export type ChartType = 'bar' | 'line' | 'pie';

/** FeedbackVerdict（Correct=1, IncorrectSql=2, WrongCaliber=3, AmbiguousQuestion=4） */
export type FeedbackVerdict =
  | 'Correct'
  | 'IncorrectSql'
  | 'WrongCaliber'
  | 'AmbiguousQuestion';

/** ChatRole（User=1, Assistant=2） */
export type ChatRole = 'User' | 'Assistant';

/** 角色 Code（基线 3.1） */
export type RoleCode = 'BusinessUser' | 'DataSteward' | 'SysAdmin';

/** 错误走 ProblemDetails + code 字段（基线 1.5） */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  /** 机器可读错误码：PERM_* / SEM_* / ASK_* / AUTH_* / LLM_UPSTREAM / DATASOURCE_TIMEOUT */
  code?: string;
  [key: string]: unknown;
}

/** ApiError(Code, Message) */
export interface ApiError {
  code: string;
  message: string;
}

/**
 * ChartSpec(ChartType, XField, YFields, Rows)
 * Rows 为采样 ≤200 行的二维数组；列顺序假定与 [XField, ...YFields] 一致（映射表已标注假设）。
 * 截断标记：基线未给出显式字段，前端以 rowsReturned > rows.length || rows.length >= 200 推导。
 */
export interface ChartSpec {
  chartType: ChartType | string;
  xField: string;
  yFields: string[];
  rows: (string | number | null)[][];
}

/** AskResponse —— 原始线上格式（枚举可能为数字） */
export interface AskResponseRaw {
  messageId: number;
  status: AskStatus | number;
  sql?: string | null;
  chart?: ChartSpecRaw | null;
  narrative?: string | null;
  clarifyOptions?: string[] | null;
  rowsReturned: number;
  elapsedMs: number;
  error?: ApiError | null;
}

export interface ChartSpecRaw {
  chartType: string;
  xField: string;
  yFields: string[];
  rows: (string | number | null)[][];
}

/** AskResponse —— 前端归一化后视图模型 */
export interface AskResult {
  messageId: number;
  status: AskStatus;
  sql: string | null;
  chart: ChartSpec | null;
  narrative: string | null;
  clarifyOptions: string[];
  rowsReturned: number;
  elapsedMs: number;
  error: ApiError | null;
}

/** PagedResult<T>(Items, Page, PageSize, Total)；page 从 1，pageSize 默认 20 上限 100 */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

/** 会话（字段对应 dbdesign ChatSessions） */
export interface ChatSession {
  id: number;
  title: string;
  lastMessageAtUtc: string;
}

/**
 * 历史消息 DTO —— 字段对应 dbdesign ChatMessages（camelCase）。
 * Json 列（ClarifyOptionsJson/ChartSpecJson）假定服务端已反序列化为对象数组，
 * S1 联调时逐字段核对（映射表标注假设）。
 */
export interface ChatMessageRaw {
  id: number;
  sessionId: number;
  role: ChatRole | number;
  questionText?: string | null;
  status?: AskStatus | number | null;
  generatedSql?: string | null;
  clarifyOptions?: string[] | null;
  chart?: ChartSpecRaw | null;
  narrative?: string | null;
  rowCountReturned?: number | null;
  elapsedMs?: number | null;
  errorCode?: string | null;
  errorMessage?: string | null;
  idempotencyKey?: string | null;
  createdAtUtc: string;
}

/** 前端归一化消息视图模型 */
export interface ChatMessageView {
  id: number;
  sessionId: number;
  role: ChatRole;
  createdAtUtc: string;
  /** Role=User */
  questionText: string | null;
  /** Role=Assistant：AskResult 同构 */
  status: AskStatus | null;
  sql: string | null;
  chart: ChartSpec | null;
  narrative: string | null;
  clarifyOptions: string[];
  rowsReturned: number;
  elapsedMs: number;
  error: ApiError | null;
  /** 本地态：pending（等待应答）/ failed-retryable（网络失败可重试） */
  localState?: 'pending' | 'failed';
  /** 提问时生成并复用的幂等键 */
  idempotencyKey?: string;
  question?: string;
}

/** POST /api/chat/sessions/{id}/messages 请求体：AskRequest(Question) */
export interface AskRequest {
  question: string;
}

/** POST /api/chat/messages/{id}/feedback 请求体（dbdesign FeedbackAnnotations） */
export interface FeedbackRequest {
  verdict: FeedbackVerdict;
  correctedSql?: string | null;
  note?: string | null;
}

/* ---------- auth ---------- */

/** POST /api/auth/refresh 响应（S1 定案前假设，映射表标注） */
export interface TokenResponse {
  accessToken: string;
  refreshToken?: string | null;
  /** 秒 */
  expiresIn?: number;
}

/** GET /api/auth/me 响应（字段对应 dbdesign Users；S1 联调核对） */
export interface UserProfile {
  id: number;
  name: string;
  avatarUrl?: string | null;
  email?: string | null;
  departmentPath?: string | null;
  roles: RoleCode[];
}

/** JWT payload claims（基线 3.4：sub/union_id/role[]/dept_path/token_ver/exp） */
export interface JwtClaims {
  sub: string;
  union_id?: string;
  role?: RoleCode[] | string;
  dept_path?: string;
  token_ver?: number;
  exp?: number;
  [key: string]: unknown;
}

/* ---------- semantic（字段与 dbdesign v1.0 第 3 节逐一对应） ---------- */

export type DataSourceType = 'SqlServer' | 'MySql' | 'PostgreSql' | 'ClickHouse';
export type DataSourceStatus = 'Enabled' | 'Disabled';
export type SemanticColumnType = 'Dimension' | 'Measure' | 'Time' | 'Identifier';
export type AggregationType = 'Sum' | 'Count' | 'Avg' | 'Ratio' | 'Custom';
export type JoinType = 'Inner' | 'Left';
export type SynonymTargetType = 'Metric' | 'SemanticTable' | 'SemanticColumn';

export interface DataSource {
  id: number;
  name: string;
  dbType: DataSourceType | number;
  host: string;
  port: number;
  databaseName: string;
  /** 环境变量/Key Vault 引用名，不落明文连接串 */
  credentialRef: string;
  queryTimeoutSeconds: number;
  maxRowsPerQuery: number;
  status: DataSourceStatus | number;
  rowVersion?: string | null;
}

export interface SemanticTable {
  id: number;
  dataSourceId: number;
  schemaName: string;
  tableName: string;
  displayName: string;
  description?: string | null;
  isEnabled: boolean;
  rowVersion?: string | null;
}

export interface SemanticColumn {
  id: number;
  semanticTableId: number;
  columnName: string;
  displayName: string;
  dbTypeName: string;
  semanticType: SemanticColumnType | number;
  isSensitive: boolean;
  description?: string | null;
  rowVersion?: string | null;
}

export interface Metric {
  id: number;
  code: string;
  displayName: string;
  description?: string | null;
  expression: string;
  aggregationType: AggregationType | number;
  unit?: string | null;
  defaultTimeColumnId?: number | null;
  isEnabled: boolean;
  ownerUserId?: number | null;
  rowVersion?: string | null;
}

/** OnConditionsJson 结构化元素 */
export interface JoinCondition {
  leftColumnId: number;
  rightColumnId: number;
}

export interface SemanticRelation {
  id: number;
  leftSemanticTableId: number;
  rightSemanticTableId: number;
  joinType: JoinType | number;
  onConditions: JoinCondition[];
  rowVersion?: string | null;
}

export interface Synonym {
  id: number;
  term: string;
  targetType: SynonymTargetType | number;
  targetId: number;
}

export type SemanticResourceKind =
  | 'datasources'
  | 'tables'
  | 'columns'
  | 'metrics'
  | 'relations'
  | 'synonyms';
