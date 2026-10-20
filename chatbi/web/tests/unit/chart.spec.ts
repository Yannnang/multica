import { describe, expect, it } from 'vitest';
import {
  CHART_SAMPLE_LIMIT,
  buildChartOption,
  isChartEmpty,
  isChartTruncated,
} from '@/utils/chart';
import type { ChartSpec } from '@/api/types';

const barSpec: ChartSpec = {
  chartType: 'bar',
  xField: '区域',
  yFields: ['销售额'],
  rows: [
    ['华东', 120],
    ['华北', 98],
  ],
};

const multiYLine: ChartSpec = {
  chartType: 'line',
  xField: '月份',
  yFields: ['销售额', '成本'],
  rows: [
    ['2026-09', 100, 60],
    ['2026-10', 120, 70],
  ],
};

const pieSpec: ChartSpec = {
  chartType: 'pie',
  xField: '渠道',
  yFields: ['销售额'],
  rows: [
    ['直销', 435],
    ['电商', 234],
  ],
};

describe('图表三类型（验收：bar/line/pie）', () => {
  it('bar：x 轴类目来自 xField 列，每 yField 一个 bar 系列', () => {
    const opt = buildChartOption(barSpec);
    expect(opt.xAxis).toMatchObject({ type: 'category', data: ['华东', '华北'] });
    expect(opt.series).toHaveLength(1);
    expect(opt.series[0]).toMatchObject({ name: '销售额', type: 'bar', data: [120, 98] });
  });

  it('line：多 yField → 多系列，smooth 开启', () => {
    const opt = buildChartOption(multiYLine);
    expect(opt.series).toHaveLength(2);
    expect(opt.series[0]).toMatchObject({ type: 'line', data: [100, 120], smooth: true });
    expect(opt.series[1]).toMatchObject({ name: '成本', data: [60, 70] });
    expect(opt.legend).toEqual({ data: ['销售额', '成本'] });
  });

  it('pie：xField 为分类名，第一个 yField 为值', () => {
    const opt = buildChartOption(pieSpec);
    expect(opt.series[0]).toMatchObject({ type: 'pie', name: '销售额' });
    expect((opt.series[0] as { data: unknown[] }).data).toEqual([
      { name: '直销', value: 435 },
      { name: '电商', value: 234 },
    ]);
  });

  it('非法数值转 null（ECharts 断点），不抛错', () => {
    const opt = buildChartOption({
      ...barSpec,
      rows: [['华东', 'N/A'], ['华北', null]],
    });
    expect(opt.series[0]).toMatchObject({ data: [null, null] });
  });
});

describe('空结果与截断提示（验收：S3 范围 3）', () => {
  it('空 rows 判定为空结果', () => {
    expect(isChartEmpty({ ...barSpec, rows: [] })).toBe(true);
    expect(isChartEmpty(barSpec)).toBe(false);
  });

  it('rowsReturned > 采样行数 → 截断', () => {
    expect(isChartTruncated(barSpec, 5000)).toBe(true);
    expect(isChartTruncated(barSpec, 2)).toBe(false);
  });

  it('采样达上限 200 行 → 截断', () => {
    const rows = Array.from({ length: CHART_SAMPLE_LIMIT }, (_, i) => [`S${i}`, i]);
    expect(isChartTruncated({ ...barSpec, rows }, CHART_SAMPLE_LIMIT)).toBe(true);
  });
});
