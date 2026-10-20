<script setup lang="ts">
import { ref, useTemplateRef } from 'vue';

/**
 * 可折叠 SQL 视图。
 * 安全红线：SQL 一律以插值纯文本渲染（{{ }}），禁 v-html（XSS）。
 */
defineProps<{
  sql: string;
}>();

const detailsEl = useTemplateRef<HTMLDetailsElement>('detailsEl');
const open = ref(false);

function toggle(): void {
  open.value = !open.value;
  if (detailsEl.value) detailsEl.value.open = open.value;
}
</script>

<template>
  <div class="sql-view">
    <button
      class="btn btn--ghost btn--sm sql-toggle"
      data-testid="sql-toggle"
      :aria-expanded="open"
      @click="toggle"
    >
      {{ open ? '收起 SQL' : '查看 SQL' }}
    </button>
    <details ref="detailsEl" class="sql-details">
      <pre class="sql-pre" data-testid="sql-content">{{ sql }}</pre>
    </details>
  </div>
</template>

<style scoped>
.sql-view { margin-top: 8px; }
.sql-toggle { padding-left: 0; }
.sql-pre {
  margin: 6px 0 0; padding: 12px;
  background: #0f172a; color: #e2e8f0; border-radius: 6px;
  font-size: 12.5px; line-height: 1.5; overflow-x: auto; white-space: pre-wrap;
  word-break: break-all;
}
</style>
