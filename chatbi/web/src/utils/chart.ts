import type { ChartSpec } from '@/api/types';

/**
 * ChartSpec → ECharts option 的纯函数映射（bar/line/pie，MVP 三类）。
 * 列顺序假定：rows 每行 = [xField 值, ...yFields 值]（PR 映射表标注假设）。
 * 纯函数便于单测（jsdom 无法渲染 canvas）。
 */
export interface ChartOption {
  tooltip: Record<string, unknown>;
  legend?: { data: string[] };
  grid?: Record<string, unknown>;
  xAxis?: Record<string, unknown>;
  yAxis?: Record<string, unknown>;
  series: Record<string, unknown>[];
}

export function buildChartOption(spec: ChartSpec): ChartOption {
  const xIndex = 0;
  const xLabels = spec.rows.map((r) => String(r[xIndex] ?? ''));

  if (spec.chartType === 'pie') {
    // pie：xField 为分类名，第一个 yField 为值
    const valueIndex = 1;
    return {
      tooltip: { trigger: 'item' },
      legend: { data: xLabels },
      series: [
        {
          type: 'pie',
          name: spec.yFields[0] ?? '',
          radius: '60%',
          data: spec.rows.map((r) => ({
            name: String(r[xIndex] ?? ''),
            value: toNumber(r[valueIndex]),
          })),
        },
      ],
    };
  }

  // bar / line：x 轴类目 + 每个 yField 一个系列
  const series = spec.yFields.map((field, i) => ({
    name: field,
    type: spec.chartType === 'line' ? 'line' : 'bar',
    smooth: spec.chartType === 'line',
    data: spec.rows.map((r) => toNumber(r[i + 1])),
  }));

  return {
    tooltip: { trigger: 'axis' },
    legend: { data: [...spec.yFields] },
    grid: { left: 48, right: 24, top: 40, bottom: 32 },
    xAxis: { type: 'category', data: xLabels, name: spec.xField },
    yAxis: { type: 'value' },
    series,
  };
}

function toNumber(v: string | number | null | undefined): number | null {
  if (v == null || v === '') return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}

/** 图表采样上限（基线：ChartSpec 采样 ≤200 行，超出截断标注） */
export const CHART_SAMPLE_LIMIT = 200;

/**
 * 截断判定：基线未给显式 truncated 字段，
 * 以 rowsReturned（AskResponse）> chart.rows.length 或采样达上限推导。
 */
export function isChartTruncated(spec: ChartSpec, rowsReturned: number): boolean {
  return rowsReturned > spec.rows.length || spec.rows.length >= CHART_SAMPLE_LIMIT;
}

export function isChartEmpty(spec: ChartSpec): boolean {
  return spec.rows.length === 0;
}
