import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

const setOption = vi.fn();
const resize = vi.fn();
const dispose = vi.fn();
const init = vi.fn((_el?: unknown) => ({ setOption, resize, dispose }));

vi.mock('echarts', () => ({
  init: (el?: unknown) => init(el),
}));

// 在 mock 之后再导入被测组件
const { default: ChartView } = await import('@/components/chat/ChartView.vue');
const { CHART_BAR, CHART_LINE, CHART_PIE, CHART_TRUNCATED } = await import('@/mocks/fixtures');

beforeEach(() => {
  init.mockClear();
  setOption.mockClear();
});

describe('图表三类型渲染（验收：bar/line/pie）', () => {
  it.each([
    ['bar', CHART_BAR],
    ['line', CHART_LINE],
    ['pie', CHART_PIE],
  ])('%s：init ECharts 并 setOption 对应类型', (_type, spec) => {
    const w = mount(ChartView, { props: { spec, rowsReturned: spec.rows.length } });
    expect(w.find('[data-testid="chart-canvas"]').exists()).toBe(true);
    expect(init).toHaveBeenCalledTimes(1);
    expect(setOption).toHaveBeenCalledTimes(1);
    const option = setOption.mock.calls[0][0] as { series: { type: string }[] };
    expect(option.series[0].type).toBe(_type);
  });

  it('组件卸载时 dispose，避免内存泄漏', () => {
    const w = mount(ChartView, { props: { spec: CHART_BAR, rowsReturned: 5 } });
    w.unmount();
    expect(dispose).toHaveBeenCalled();
  });
});

describe('空结果与截断提示（验收：S3 范围 3）', () => {
  it('空 rows → 提示查询结果为空，不初始化图表', () => {
    const w = mount(ChartView, {
      props: { spec: { ...CHART_BAR, rows: [] }, rowsReturned: 0 },
    });
    expect(w.find('[data-testid="chart-empty"]').exists()).toBe(true);
    expect(w.text()).toContain('查询结果为空');
    expect(init).not.toHaveBeenCalled();
  });

  it('rowsReturned(5000) > 采样行数(200) → 截断提示带两个数字', () => {
    const w = mount(ChartView, { props: { spec: CHART_TRUNCATED, rowsReturned: 5000 } });
    const hint = w.get('[data-testid="chart-truncated"]');
    expect(hint.text()).toContain('200');
    expect(hint.text()).toContain('5000');
    expect(w.find('[data-testid="chart-canvas"]').exists()).toBe(true);
  });

  it('未截断 → 不显示截断提示', () => {
    const w = mount(ChartView, { props: { spec: CHART_BAR, rowsReturned: 5 } });
    expect(w.find('[data-testid="chart-truncated"]').exists()).toBe(false);
  });
});
