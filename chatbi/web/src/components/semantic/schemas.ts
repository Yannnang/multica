import type { SemanticResourceKind } from '@/api/types';
import {
  AGGREGATION_TYPE_NAMES,
  DATA_SOURCE_STATUS_NAMES,
  DATA_SOURCE_TYPE_NAMES,
  JOIN_TYPE_NAMES,
  SEMANTIC_COLUMN_TYPE_NAMES,
  SYNONYM_TARGET_TYPE_NAMES,
} from '@/utils/enums';

/**
 * 语义层六资源表单 schema —— 字段与 dbdesign v1.0 第 3 节逐一对应。
 * 枚举选项即 C# 枚举名（tinyint 值 = 下标 + 1）。
 */

export type FieldType = 'text' | 'textarea' | 'number' | 'boolean' | 'select' | 'json';

export interface FieldSchema {
  key: string;
  label: string;
  type: FieldType;
  required?: boolean;
  options?: string[];
  default?: unknown;
  min?: number;
  max?: number;
  placeholder?: string;
  help?: string;
  /** json 类型的结构校验器 */
  validateJson?: (value: unknown) => string | null;
  showInTable?: boolean;
}

export interface ResourceSchema {
  kind: SemanticResourceKind;
  title: string;
  idKey: string;
  fields: FieldSchema[];
  /** 更新时是否携带 rowVersion 并发令牌（dbdesign：可编辑配置类表带 RowVersion） */
  hasRowVersion: boolean;
}

const joinConditionsValidator = (value: unknown): string | null => {
  if (!Array.isArray(value)) return '必须为数组';
  for (const item of value) {
    if (
      typeof item !== 'object' ||
      item === null ||
      typeof (item as Record<string, unknown>).leftColumnId !== 'number' ||
      typeof (item as Record<string, unknown>).rightColumnId !== 'number'
    ) {
      return '每项必须为 {"leftColumnId": <number>, "rightColumnId": <number>}';
    }
  }
  return null;
};

export const RESOURCE_SCHEMAS: ResourceSchema[] = [
  {
    kind: 'datasources',
    title: '数据源',
    idKey: 'id',
    hasRowVersion: true,
    fields: [
      { key: 'name', label: '名称', type: 'text', required: true, showInTable: true, placeholder: '业务库只读实例' },
      { key: 'dbType', label: '数据库类型', type: 'select', required: true, options: [...DATA_SOURCE_TYPE_NAMES], default: 'SqlServer', showInTable: true, help: 'MVP 仅实现 SqlServer，其余预留' },
      { key: 'host', label: '主机', type: 'text', required: true, showInTable: true },
      { key: 'port', label: '端口', type: 'number', required: true, default: 1433, min: 1, max: 65535, showInTable: true },
      { key: 'databaseName', label: '数据库名', type: 'text', required: true, showInTable: true },
      { key: 'credentialRef', label: '凭证引用名', type: 'text', required: true, showInTable: true, placeholder: 'BIZ_DB_READONLY_CONNECTION', help: '环境变量/Key Vault 引用名，禁止填写明文连接串' },
      { key: 'queryTimeoutSeconds', label: '查询超时（秒）', type: 'number', required: true, default: 30, min: 1, max: 300 },
      { key: 'maxRowsPerQuery', label: '单次行数上限', type: 'number', required: true, default: 5000, min: 1, max: 50000 },
      { key: 'status', label: '状态', type: 'select', required: true, options: [...DATA_SOURCE_STATUS_NAMES], default: 'Enabled', showInTable: true },
    ],
  },
  {
    kind: 'tables',
    title: '语义表',
    idKey: 'id',
    hasRowVersion: true,
    fields: [
      { key: 'dataSourceId', label: '数据源 ID', type: 'number', required: true, showInTable: true, min: 1 },
      { key: 'schemaName', label: 'Schema', type: 'text', required: true, default: 'dbo', showInTable: true },
      { key: 'tableName', label: '物理表名', type: 'text', required: true, showInTable: true },
      { key: 'displayName', label: '业务名', type: 'text', required: true, showInTable: true, help: '给 LLM 与人看的业务名称' },
      { key: 'description', label: '业务口径描述', type: 'textarea', placeholder: '进入 prompt 的口径说明' },
      { key: 'isEnabled', label: '启用', type: 'boolean', default: true, showInTable: true, help: '关闭即不参与 schema linking' },
    ],
  },
  {
    kind: 'columns',
    title: '语义列',
    idKey: 'id',
    hasRowVersion: true,
    fields: [
      { key: 'semanticTableId', label: '语义表 ID', type: 'number', required: true, showInTable: true, min: 1 },
      { key: 'columnName', label: '物理列名', type: 'text', required: true, showInTable: true },
      { key: 'displayName', label: '业务名', type: 'text', required: true, showInTable: true },
      { key: 'dbTypeName', label: '物理类型', type: 'text', required: true, showInTable: true, placeholder: 'nvarchar(64) / decimal(18,2)' },
      { key: 'semanticType', label: '语义类型', type: 'select', required: true, options: [...SEMANTIC_COLUMN_TYPE_NAMES], default: 'Dimension', showInTable: true },
      { key: 'isSensitive', label: '敏感列', type: 'boolean', default: false, showInTable: true, help: '敏感列对未授权用户在生成 SQL 时剔除' },
      { key: 'description', label: '口径/枚举值说明', type: 'textarea' },
    ],
  },
  {
    kind: 'metrics',
    title: '指标',
    idKey: 'id',
    hasRowVersion: true,
    fields: [
      { key: 'code', label: '指标代码', type: 'text', required: true, showInTable: true, placeholder: 'approval_pass_rate' },
      { key: 'displayName', label: '显示名', type: 'text', required: true, showInTable: true, placeholder: '审批通过率' },
      { key: 'description', label: '口径说明', type: 'textarea', placeholder: '进入 prompt 的口径说明' },
      { key: 'expression', label: '聚合表达式', type: 'textarea', required: true, placeholder: 'SUM(CASE WHEN {t}.status = 2 THEN 1 ELSE 0 END) * 1.0 / COUNT(*)', help: '{t} 为基表别名占位符' },
      { key: 'aggregationType', label: '聚合类型', type: 'select', required: true, options: [...AGGREGATION_TYPE_NAMES], default: 'Sum', showInTable: true },
      { key: 'unit', label: '单位', type: 'text', showInTable: true, placeholder: '% / 元 / 件' },
      { key: 'defaultTimeColumnId', label: '默认时间列 ID', type: 'number', min: 1 },
      { key: 'isEnabled', label: '启用', type: 'boolean', default: true, showInTable: true },
      { key: 'ownerUserId', label: '口径负责人 ID', type: 'number', min: 1 },
    ],
  },
  {
    kind: 'relations',
    title: '表关联',
    idKey: 'id',
    hasRowVersion: true,
    fields: [
      { key: 'leftSemanticTableId', label: '左表 ID', type: 'number', required: true, showInTable: true, min: 1 },
      { key: 'rightSemanticTableId', label: '右表 ID', type: 'number', required: true, showInTable: true, min: 1 },
      { key: 'joinType', label: 'JOIN 类型', type: 'select', required: true, options: [...JOIN_TYPE_NAMES], default: 'Inner', showInTable: true },
      {
        key: 'onConditions',
        label: 'ON 条件（结构化 JSON）',
        type: 'json',
        required: true,
        showInTable: true,
        validateJson: joinConditionsValidator,
        default: [],
        help: '[{"leftColumnId": 1, "rightColumnId": 2}]，列 ID 必须来自语义列白名单',
      },
    ],
  },
  {
    kind: 'synonyms',
    title: '同义词',
    idKey: 'id',
    hasRowVersion: false,
    fields: [
      { key: 'term', label: '同义词', type: 'text', required: true, showInTable: true, placeholder: '通过率' },
      { key: 'targetType', label: '目标类型', type: 'select', required: true, options: [...SYNONYM_TARGET_TYPE_NAMES], default: 'Metric', showInTable: true },
      { key: 'targetId', label: '目标 ID', type: 'number', required: true, showInTable: true, min: 1 },
    ],
  },
];
