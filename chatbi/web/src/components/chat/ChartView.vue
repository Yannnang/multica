<script setup lang="ts">
import { onBeforeUnmount, onMounted, shallowRef, useTemplateRef, watch } from 'vue';
import * as echarts from 'echarts';
import type { ChartSpec } from '@/api/types';
import { buildChartOption, isChartEmpty, isChartTruncated } from '@/utils/chart';

const props = defineProps<{
  spec: ChartSpec;
  /** AskResponse.RowsReturned：用于截断提示 */
  rowsReturned?: number;
}>();

const chartEl = useTemplateRef<HTMLDivElement>('chartEl');
const instance = shallowRef<echarts.ECharts | null>(null);
let resizeObserver: ResizeObserver | null = null;

const empty = isChartEmpty(props.spec);
const truncated = isChartTruncated(props.spec, props.rowsReturned ?? props.spec.rows.length);

function render(): void {
  if (!chartEl.value || empty) return;
  instance.value ??= echarts.init(chartEl.value);
  const option = buildChartOption(props.spec) as unknown as echarts.EChartsCoreOption;
  instance.value.setOption(option, true);
}

onMounted(() => {
  render();
  if (chartEl.value && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => instance.value?.resize());
    resizeObserver.observe(chartEl.value);
  }
});

watch(() => props.spec, render, { deep: false });

onBeforeUnmount(() => {
  resizeObserver?.disconnect();
  instance.value?.dispose();
  instance.value = null;
});
</script>

<template>
  <div class="chart-view">
    <div v-if="empty" class="alert alert--info" data-testid="chart-empty">
      查询结果为空，没有可渲染的数据。
    </div>
    <template v-else>
      <div
        v-if="truncated"
        class="alert alert--warn chart-truncated"
        data-testid="chart-truncated"
      >
        结果行数已达采样上限，图表仅展示前 {{ spec.rows.length }} 行（共 {{ rowsReturned }} 行）。
      </div>
      <div ref="chartEl" class="chart-canvas" data-testid="chart-canvas"></div>
    </template>
  </div>
</template>

<style scoped>
.chart-view { width: 100%; }
.chart-canvas { width: 100%; height: 320px; }
.chart-truncated { margin-bottom: 6px; }
</style>
