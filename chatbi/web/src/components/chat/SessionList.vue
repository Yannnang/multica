<script setup lang="ts">
import type { ChatSession } from '@/api/types';

/**
 * 会话列表（S3 范围 2）：分页加载（Load More）、新建会话、选中态。
 * 展示型组件：数据获取由 ChatPage 容器负责。
 */
defineProps<{
  sessions: ChatSession[];
  activeId: number | null;
  hasMore: boolean;
  loading: boolean;
}>();

const emit = defineEmits<{
  select: [session: ChatSession];
  create: [];
  loadMore: [];
}>();

function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
</script>

<template>
  <aside class="session-list">
    <button class="btn btn--primary new-session" data-testid="new-session-btn" @click="emit('create')">
      + 新建会话
    </button>
    <ul class="sessions">
      <li
        v-for="s in sessions"
        :key="s.id"
        class="session-item"
        :class="{ 'session-item--active': s.id === activeId }"
        :data-testid="`session-item-${s.id}`"
        @click="emit('select', s)"
      >
        <div class="session-title">{{ s.title }}</div>
        <div class="session-time muted">{{ formatTime(s.lastMessageAtUtc) }}</div>
      </li>
    </ul>
    <button
      v-if="hasMore"
      class="btn btn--ghost load-more"
      data-testid="load-more-btn"
      :disabled="loading"
      @click="emit('loadMore')"
    >
      {{ loading ? '加载中…' : '加载更多' }}
    </button>
  </aside>
</template>

<style scoped>
.session-list {
  width: 240px; flex: none; display: flex; flex-direction: column;
  border-right: 1px solid var(--c-border); background: var(--c-surface); padding: 12px;
}
.new-session { width: 100%; }
.sessions { list-style: none; margin: 12px 0; padding: 0; overflow-y: auto; flex: 1; }
.session-item {
  padding: 8px 10px; border-radius: 6px; cursor: pointer; margin-bottom: 4px;
  border: 1px solid transparent;
}
.session-item:hover { background: #f8fafc; }
.session-item--active { background: #eff6ff; border-color: #bfdbfe; }
.session-title {
  font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.session-time { font-size: 12px; }
.load-more { width: 100%; }
</style>
