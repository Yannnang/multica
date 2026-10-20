import { describe, expect, it } from 'vitest';
import {
  AGGREGATION_TYPE_NAMES,
  DATA_SOURCE_TYPE_NAMES,
  FEEDBACK_VERDICT_NAMES,
  JOIN_TYPE_NAMES,
  SEMANTIC_COLUMN_TYPE_NAMES,
  normalizeAskStatus,
  normalizeChartType,
  normalizeEnum,
} from '@/utils/enums';

describe('枚举归一化（契约：STJ 字符串名 / tinyint 双兼容）', () => {
  it('AskStatus：数字（dbdesign 枚举字典）→ 名称', () => {
    expect(normalizeAskStatus(1)).toBe('Success');
    expect(normalizeAskStatus(2)).toBe('ClarifyNeeded');
    expect(normalizeAskStatus(3)).toBe('Rejected');
    expect(normalizeAskStatus(4)).toBe('Failed');
  });

  it('AskStatus：字符串 → 原样；未知值兜底 Failed（不猜答）', () => {
    expect(normalizeAskStatus('Success')).toBe('Success');
    expect(normalizeAskStatus('clarifyneeded')).toBe('ClarifyNeeded');
    expect(normalizeAskStatus(99)).toBe('Failed');
    expect(normalizeAskStatus(null)).toBe('Failed');
    expect(normalizeAskStatus('Bogus')).toBe('Failed');
  });

  it('dbdesign 全部枚举字典顺序正确（值 = 下标 + 1）', () => {
    expect(DATA_SOURCE_TYPE_NAMES).toEqual(['SqlServer', 'MySql', 'PostgreSql', 'ClickHouse']);
    expect(SEMANTIC_COLUMN_TYPE_NAMES).toEqual(['Dimension', 'Measure', 'Time', 'Identifier']);
    expect(AGGREGATION_TYPE_NAMES).toEqual(['Sum', 'Count', 'Avg', 'Ratio', 'Custom']);
    expect(JOIN_TYPE_NAMES).toEqual(['Inner', 'Left']);
    expect(FEEDBACK_VERDICT_NAMES).toEqual([
      'Correct',
      'IncorrectSql',
      'WrongCaliber',
      'AmbiguousQuestion',
    ]);
    expect(normalizeEnum(DATA_SOURCE_TYPE_NAMES, 4)).toBe('ClickHouse');
    expect(normalizeEnum(SEMANTIC_COLUMN_TYPE_NAMES, 3)).toBe('Time');
  });

  it('ChartType：大小写不敏感，未知回落 bar', () => {
    expect(normalizeChartType('Line')).toBe('line');
    expect(normalizeChartType('PIE')).toBe('pie');
    expect(normalizeChartType('scatter')).toBe('bar');
  });
});
