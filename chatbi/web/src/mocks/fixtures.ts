/**
 * Mock fixtures —— 字段严格对照《设计基线 v1.0》1.5 契约与 dbdesign v1.0 枚举字典。
 * 仅本地开发/E2E 冒烟用，不进生产构建（VITE_USE_MOCK 门控 + 动态 import）。
 */

export interface MockMessage {
  id: number;
  sessionId: number;
  role: 'User' | 'Assistant';
  questionText?: string | null;
  status?: 'Success' | 'ClarifyNeeded' | 'Rejected' | 'Failed' | null;
  generatedSql?: string | null;
  clarifyOptions?: string[] | null;
  chart?: {
    chartType: string;
    xField: string;
    yFields: string[];
    rows: (string | number | null)[][];
  } | null;
  narrative?: string | null;
  rowCountReturned?: number | null;
  elapsedMs?: number | null;
  errorCode?: string | null;
  errorMessage?: string | null;
  idempotencyKey?: string | null;
  createdAtUtc: string;
}

export interface MockSession {
  id: number;
  title: string;
  lastMessageAtUtc: string;
}

export const CHART_BAR = {
  chartType: 'bar',
  xField: '区域',
  yFields: ['销售额'],
  rows: [
    ['华东', 1200000],
    ['华北', 980000],
    ['华南', 1105000],
    ['西南', 640000],
    ['东北', 410000],
  ] as (string | number)[][],
};

export const CHART_LINE = {
  chartType: 'line',
  xField: '月份',
  yFields: ['销售额'],
  rows: [
    ['2026-05', 820],
    ['2026-06', 932],
    ['2026-07', 901],
    ['2026-08', 1034],
    ['2026-09', 1190],
    ['2026-10', 1230],
  ] as (string | number)[][],
};

export const CHART_PIE = {
  chartType: 'pie',
  xField: '渠道',
  yFields: ['销售额'],
  rows: [
    ['直销', 4350],
    ['分销', 3100],
    ['电商', 2340],
    ['其他', 850],
  ] as (string | number)[][],
};

function truncatedRows(count: number): (string | number)[][] {
  return Array.from({ length: count }, (_, i) => [`SKU-${String(i + 1).padStart(4, '0')}`, 1000 - i]);
}

export const CHART_TRUNCATED = {
  chartType: 'bar',
  xField: '商品',
  yFields: ['销量'],
  rows: truncatedRows(200),
};

export const mockSessions: MockSession[] = [
  { id: 1, title: '各区域销售额分析', lastMessageAtUtc: '2026-10-19T08:30:00Z' },
  { id: 2, title: '月度趋势跟踪', lastMessageAtUtc: '2026-10-18T06:12:00Z' },
  { id: 3, title: '渠道占比复盘', lastMessageAtUtc: '2026-10-17T09:45:00Z' },
];

export const mockMessages: MockMessage[] = [
  {
    id: 101,
    sessionId: 1,
    role: 'User',
    questionText: '各区域销售额对比',
    idempotencyKey: 'seed-key-101',
    createdAtUtc: '2026-10-19T08:29:00Z',
  },
  {
    id: 102,
    sessionId: 1,
    role: 'Assistant',
    status: 'Success',
    generatedSql:
      "SELECT region AS 区域, SUM(amount) AS 销售额\nFROM dbo.SalesOrders\nWHERE dept_path LIKE @prefix + '%'\nGROUP BY region\nORDER BY SUM(amount) DESC;",
    chart: CHART_BAR,
    narrative:
      '近 12 个月华东区域销售额最高（120.0 万），领先第二名华南约 8.6%；东北区域最低，仅为华东的 34%。',
    rowCountReturned: 5,
    elapsedMs: 1830,
    createdAtUtc: '2026-10-19T08:29:03Z',
  },
  {
    id: 103,
    sessionId: 2,
    role: 'User',
    questionText: '那个数据再看下',
    idempotencyKey: 'seed-key-103',
    createdAtUtc: '2026-10-18T06:11:00Z',
  },
  {
    id: 104,
    sessionId: 2,
    role: 'Assistant',
    status: 'ClarifyNeeded',
    narrative: '问题存在歧义，请选择一个更具体的问法：',
    clarifyOptions: [
      '近 6 个月各区域销售额趋势',
      '本月销售额环比变化',
      '各渠道销售额占比',
    ],
    rowCountReturned: 0,
    elapsedMs: 420,
    createdAtUtc: '2026-10-18T06:11:01Z',
  },
];

/* ---------- 语义层 fixtures（dbdesign v1.0 第 3 节字段） ---------- */

export const mockDataSources = [
  {
    id: 1,
    name: '业务库只读实例',
    dbType: 'SqlServer',
    host: 'sqlserver-biz.internal',
    port: 1433,
    databaseName: 'BizDB',
    credentialRef: 'BIZ_DB_READONLY_CONNECTION',
    queryTimeoutSeconds: 30,
    maxRowsPerQuery: 5000,
    status: 'Enabled',
    rowVersion: 'rv-ds-1',
  },
];

export const mockTables = [
  {
    id: 1,
    dataSourceId: 1,
    schemaName: 'dbo',
    tableName: 'SalesOrders',
    displayName: '销售订单',
    description: '订单粒度事实表，一行一订单；金额为含税口径。',
    isEnabled: true,
    rowVersion: 'rv-t-1',
  },
  {
    id: 2,
    dataSourceId: 1,
    schemaName: 'dbo',
    tableName: 'Employees',
    displayName: '员工',
    description: '员工维度表，含部门路径。',
    isEnabled: true,
    rowVersion: 'rv-t-2',
  },
];

export const mockColumns = [
  {
    id: 1,
    semanticTableId: 1,
    columnName: 'region',
    displayName: '区域',
    dbTypeName: 'nvarchar(64)',
    semanticType: 'Dimension',
    isSensitive: false,
    description: '销售大区：华东/华北/华南/西南/东北',
    rowVersion: 'rv-c-1',
  },
  {
    id: 2,
    semanticTableId: 1,
    columnName: 'amount',
    displayName: '销售额',
    dbTypeName: 'decimal(18,2)',
    semanticType: 'Measure',
    isSensitive: false,
    description: '含税金额（元）',
    rowVersion: 'rv-c-2',
  },
  {
    id: 3,
    semanticTableId: 1,
    columnName: 'created_at',
    displayName: '下单时间',
    dbTypeName: 'datetime2(3)',
    semanticType: 'Time',
    isSensitive: false,
    description: null,
    rowVersion: 'rv-c-3',
  },
  {
    id: 4,
    semanticTableId: 2,
    columnName: 'salary',
    displayName: '薪酬',
    dbTypeName: 'decimal(18,2)',
    semanticType: 'Measure',
    isSensitive: true,
    description: '列级受限：未授权用户生成 SQL 时剔除',
    rowVersion: 'rv-c-4',
  },
];

export const mockMetrics = [
  {
    id: 1,
    code: 'sales_amount',
    displayName: '销售额',
    description: '含税订单金额合计',
    expression: 'SUM({t}.amount)',
    aggregationType: 'Sum',
    unit: '元',
    defaultTimeColumnId: 3,
    isEnabled: true,
    ownerUserId: null,
    rowVersion: 'rv-m-1',
  },
  {
    id: 2,
    code: 'approval_pass_rate',
    displayName: '审批通过率',
    description: '通过单据数 / 全部单据数',
    expression: 'SUM(CASE WHEN {t}.status = 2 THEN 1 ELSE 0 END) * 1.0 / COUNT(*)',
    aggregationType: 'Ratio',
    unit: '%',
    defaultTimeColumnId: null,
    isEnabled: true,
    ownerUserId: null,
    rowVersion: 'rv-m-2',
  },
];

export const mockRelations = [
  {
    id: 1,
    leftSemanticTableId: 1,
    rightSemanticTableId: 2,
    joinType: 'Left',
    onConditions: [{ leftColumnId: 5, rightColumnId: 6 }],
    rowVersion: 'rv-r-1',
  },
];

export const mockSynonyms = [
  { id: 1, term: '通过率', targetType: 'Metric', targetId: 2 },
  { id: 2, term: '营收', targetType: 'Metric', targetId: 1 },
  { id: 3, term: '订单表', targetType: 'SemanticTable', targetId: 1 },
  { id: 4, term: '大区', targetType: 'SemanticColumn', targetId: 1 },
];
