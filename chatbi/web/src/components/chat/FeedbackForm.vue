<script setup lang="ts">
import { computed, ref } from 'vue';
import { submitFeedback } from '@/api/chat';
import { ApiRequestError } from '@/api/client';
import type { FeedbackVerdict } from '@/api/types';

/**
 * 反馈标注（S3 范围 4）：Verdict 四枚举 + 可选修正 SQL。
 * POST /api/chat/messages/{id}/feedback
 */
const props = defineProps<{
  messageId: number;
}>();

const VERDICT_OPTIONS: { value: FeedbackVerdict; label: string }[] = [
  { value: 'Correct', label: '结果正确' },
  { value: 'IncorrectSql', label: 'SQL 有误' },
  { value: 'WrongCaliber', label: '口径不对' },
  { value: 'AmbiguousQuestion', label: '问题有歧义' },
];

const verdict = ref<FeedbackVerdict | ''>('');
const correctedSql = ref('');
const note = ref('');
const submitting = ref(false);
const submitted = ref(false);
const errorMsg = ref<string | null>(null);

/** 修正 SQL 输入框仅在 IncorrectSql 时展示 */
const sqlVisible = computed(() => verdict.value === 'IncorrectSql');

async function submit(): Promise<void> {
  if (!verdict.value || submitting.value || submitted.value) return;
  submitting.value = true;
  errorMsg.value = null;
  try {
    await submitFeedback(props.messageId, {
      verdict: verdict.value,
      correctedSql: correctedSql.value.trim() || null,
      note: note.value.trim() || null,
    });
    submitted.value = true;
  } catch (e) {
    errorMsg.value =
      e instanceof ApiRequestError ? `${e.code}：${e.message}` : '提交失败，请稍后重试';
  } finally {
    submitting.value = false;
  }
}
</script>

<template>
  <div class="feedback-form" data-testid="feedback-form">
    <div v-if="submitted" class="alert alert--success" data-testid="feedback-success">
      感谢反馈，已记录标注。
    </div>
    <template v-else>
      <div class="feedback-row">
        <span class="feedback-label">这个回答：</span>
        <select
          v-model="verdict"
          class="select"
          data-testid="verdict-select"
          aria-label="反馈结论"
        >
          <option value="" disabled>选择结论</option>
          <option v-for="opt in VERDICT_OPTIONS" :key="opt.value" :value="opt.value">
            {{ opt.label }}（{{ opt.value }}）
          </option>
        </select>
      </div>
      <div v-if="sqlVisible" class="field">
        <label class="field__label" for="corrected-sql">修正 SQL（可选，将作为 few-shot 素材）</label>
        <textarea
          id="corrected-sql"
          v-model="correctedSql"
          class="textarea mono"
          rows="3"
          data-testid="corrected-sql"
          placeholder="SELECT …"
        ></textarea>
      </div>
      <div class="field">
        <label class="field__label" for="feedback-note">备注（可选）</label>
        <input id="feedback-note" v-model="note" class="input" data-testid="feedback-note" />
      </div>
      <p v-if="errorMsg" class="alert alert--error" role="alert" data-testid="feedback-error">
        {{ errorMsg }}
      </p>
      <button
        class="btn btn--sm"
        data-testid="feedback-submit"
        :disabled="!verdict || submitting"
        @click="submit"
      >
        {{ submitting ? '提交中…' : '提交反馈' }}
      </button>
    </template>
  </div>
</template>

<style scoped>
.feedback-form {
  margin-top: 12px; padding-top: 12px;
  border-top: 1px dashed var(--c-border);
}
.feedback-row { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; }
.feedback-label { color: var(--c-text-secondary); }
</style>
