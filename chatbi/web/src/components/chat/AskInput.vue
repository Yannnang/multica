<script setup lang="ts">
import { ref } from 'vue';

/**
 * 提问输入框：Enter 发送 / Shift+Enter 换行；pending 期间禁用防重复提交。
 * Idempotency-Key 的生成与重试复用在容器（ChatPage）层管理。
 */
defineProps<{
  disabled?: boolean;
  pending?: boolean;
}>();

const emit = defineEmits<{
  ask: [question: string];
}>();

const text = ref('');

function send(): void {
  const q = text.value.trim();
  if (!q) return;
  emit('ask', q);
  text.value = '';
}

function onKeydown(e: KeyboardEvent): void {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    send();
  }
}
</script>

<template>
  <div class="ask-input">
    <textarea
      v-model="text"
      class="textarea ask-textarea"
      rows="2"
      data-testid="ask-textarea"
      :disabled="disabled"
      placeholder="用自然语言提问，例如：近 6 个月各区域销售额趋势"
      @keydown="onKeydown"
    ></textarea>
    <button
      class="btn btn--primary ask-btn"
      data-testid="ask-btn"
      :disabled="disabled || pending || !text.trim()"
      @click="send"
    >
      {{ pending ? '生成中…' : '提问' }}
    </button>
  </div>
</template>

<style scoped>
.ask-input { display: flex; gap: 10px; align-items: flex-end; padding: 12px 16px; border-top: 1px solid var(--c-border); background: var(--c-surface); }
.ask-textarea { flex: 1; resize: none; }
.ask-btn { height: 40px; }
</style>
