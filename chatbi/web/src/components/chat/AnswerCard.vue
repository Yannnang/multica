<script setup lang="ts">
import ChartView from './ChartView.vue';
import SqlView from './SqlView.vue';
import FeedbackForm from './FeedbackForm.vue';
import type { ChatMessageView } from '@/api/types';

/**
 * 回答渲染四态（S3 范围 2）：
 * - Success：图表 + 文字结论 + 可折叠 SQL + 反馈标注
 * - ClarifyNeeded：澄清选项按钮，点击即以该选项追问（emit ask）
 * - Rejected / Failed：用户可读错误文案 + 错误码
 * 安全红线：Narrative / ErrorMessage / SQL 全部插值纯文本渲染，禁 v-html。
 */
const props = defineProps<{
  message: ChatMessageView;
}>();

const emit = defineEmits<{
  /** 点击澄清选项 → 以选项文本继续追问（沿用会话上下文） */
  ask: [question: string];
  /** 网络失败后重试（复用同一 Idempotency-Key） */
  retry: [message: ChatMessageView];
}>();

function clarify(option: string): void {
  emit('ask', option);
}
</script>

<template>
  <div class="answer-card card" :data-status="message.status ?? 'pending'" data-testid="answer-card">
    <!-- 本地 pending / 失败重试态 -->
    <div v-if="message.localState === 'pending'" class="answer-pending" data-testid="answer-pending">
      <span class="spinner" aria-hidden="true"></span> 正在生成回答…
    </div>
    <div v-else-if="message.localState === 'failed'" class="answer-failed">
      <p class="alert alert--error" data-testid="answer-local-failed">
        网络异常，回答未完成。重试将复用同一 Idempotency-Key，不会产生重复提问。
      </p>
      <button class="btn btn--sm" data-testid="retry-btn" @click="emit('retry', props.message)">
        重试
      </button>
    </div>

    <!-- Success -->
    <template v-else-if="message.status === 'Success'">
      <ChartView
        v-if="message.chart"
        :spec="message.chart"
        :rows-returned="message.rowsReturned"
      />
      <p v-else class="alert alert--info" data-testid="no-chart-hint">
        本次回答未附带图表规格。
      </p>
      <p class="narrative" data-testid="narrative">{{ message.narrative }}</p>
      <div class="answer-meta muted">
        <span>返回 {{ message.rowsReturned }} 行</span>
        <span>耗时 {{ message.elapsedMs }} ms</span>
      </div>
      <SqlView v-if="message.sql" :sql="message.sql" />
      <FeedbackForm :message-id="message.id" />
    </template>

    <!-- ClarifyNeeded -->
    <template v-else-if="message.status === 'ClarifyNeeded'">
      <p class="narrative" data-testid="clarify-text">
        {{ message.narrative ?? '问题存在歧义，请选择一个更具体的问法：' }}
      </p>
      <div class="clarify-options" data-testid="clarify-options">
        <button
          v-for="option in message.clarifyOptions"
          :key="option"
          class="btn clarify-option"
          data-testid="clarify-option"
          @click="clarify(option)"
        >
          {{ option }}
        </button>
      </div>
      <p v-if="message.clarifyOptions.length === 0" class="muted">
        （服务端未提供澄清选项，请补充更具体的描述后重新提问）
      </p>
    </template>

    <!-- Rejected / Failed -->
    <template v-else-if="message.status === 'Rejected' || message.status === 'Failed'">
      <div
        class="alert"
        :class="message.status === 'Rejected' ? 'alert--warn' : 'alert--error'"
        :data-testid="`answer-${message.status.toLowerCase()}`"
      >
        <p class="answer-error-msg">
          {{
            message.error?.message ||
            (message.status === 'Rejected'
              ? '该问题超出可回答范围（如涉及未授权数据或非查询类请求）。'
              : '回答生成失败，请稍后重试或调整问法。')
          }}
        </p>
        <span class="error-code" data-testid="answer-error-code">
          错误码：{{ message.error?.code ?? (message.status === 'Rejected' ? 'ASK_REJECTED' : 'ASK_FAILED') }}
        </span>
      </div>
      <FeedbackForm :message-id="message.id" />
    </template>
  </div>
</template>

<style scoped>
.answer-card { padding: 16px; }
.narrative { margin: 10px 0; white-space: pre-wrap; }
.answer-meta { display: flex; gap: 16px; font-size: 12px; }
.answer-pending { display: flex; align-items: center; gap: 10px; color: var(--c-text-secondary); padding: 8px 0; }
.spinner {
  width: 16px; height: 16px; border: 2px solid var(--c-border);
  border-top-color: var(--c-primary); border-radius: 50%;
  animation: spin 0.8s linear infinite;
}
@keyframes spin { to { transform: rotate(360deg); } }
.clarify-options { display: flex; flex-direction: column; gap: 8px; align-items: flex-start; margin-top: 8px; }
.clarify-option { text-align: left; }
.answer-error-msg { margin: 0 0 4px; }
</style>
