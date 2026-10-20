/**
 * 枚举归一化：后端 STJ 可能输出枚举名字符串或 tinyint 存储值（dbdesign 第 1 节字典），
 * 前端统一转为字符串枚举名后再进入视图层。
 */

type EnumNames<T extends string> = readonly T[];

/** names 顺序即 tinyint 值序（值 = 下标 + 1），与 dbdesign 枚举字典一致 */
export function normalizeEnum<T extends string>(
  names: EnumNames<T>,
  value: T | number | string | null | undefined,
  fallback?: T,
): T | null {
  if (value == null) return fallback ?? null;
  if (typeof value === 'number') {
    const mapped = names[value - 1];
    if (mapped) return mapped;
    return fallback ?? null;
  }
  const exact = names.find((n) => n === value);
  if (exact) return exact;
  const ci = names.find((n) => n.toLowerCase() === String(value).toLowerCase());
  if (ci) return ci;
  return fallback ?? null;
}

export const ASK_STATUS_NAMES = [
  'Success',
  'ClarifyNeeded',
  'Rejected',
  'Failed',
] as const;
export type AskStatusName = (typeof ASK_STATUS_NAMES)[number];

export const CHAT_ROLE_NAMES = ['User', 'Assistant'] as const;
export const FEEDBACK_VERDICT_NAMES = [
  'Correct',
  'IncorrectSql',
  'WrongCaliber',
  'AmbiguousQuestion',
] as const;
export const DATA_SOURCE_TYPE_NAMES = [
  'SqlServer',
  'MySql',
  'PostgreSql',
  'ClickHouse',
] as const;
export const DATA_SOURCE_STATUS_NAMES = ['Enabled', 'Disabled'] as const;
export const SEMANTIC_COLUMN_TYPE_NAMES = [
  'Dimension',
  'Measure',
  'Time',
  'Identifier',
] as const;
export const AGGREGATION_TYPE_NAMES = [
  'Sum',
  'Count',
  'Avg',
  'Ratio',
  'Custom',
] as const;
export const JOIN_TYPE_NAMES = ['Inner', 'Left'] as const;
export const SYNONYM_TARGET_TYPE_NAMES = [
  'Metric',
  'SemanticTable',
  'SemanticColumn',
] as const;

export const CHART_TYPE_NAMES = ['bar', 'line', 'pie'] as const;

/** AskStatus 未知值按 Failed 兜底渲染（保守，不猜答） */
export function normalizeAskStatus(value: unknown): AskStatusName {
  return normalizeEnum(ASK_STATUS_NAMES, value as never, 'Failed')!;
}

export function normalizeChartType(value: unknown): string {
  const t = String(value ?? '').toLowerCase();
  return (CHART_TYPE_NAMES as readonly string[]).includes(t) ? t : 'bar';
}
