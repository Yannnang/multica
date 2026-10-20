<script setup lang="ts">
import { onMounted, ref } from 'vue';
import SessionList from '@/components/chat/SessionList.vue';
import AskInput from '@/components/chat/AskInput.vue';
import AnswerCard from '@/components/chat/AnswerCard.vue';
import {
  ask,
  createSession,
  listMessages,
  listSessions,
} from '@/api/chat';
import { ApiRequestError } from '@/api/client';
import { uuidv4 } from '@/utils/uuid';
import type { ChatMessageView, ChatSession } from '@/api/types';

const PAGE_SIZE = 20;

/* ---------- 会话列表（分页加载） ---------- */
const sessions = ref<ChatSession[]>([]);
const sessionsPage = ref(1);
const sessionsTotal = ref(0);
const sessionsLoading = ref(false);
const activeSessionId = ref<number | null>(null);
const sessionsHasMore = ref(false);

async function loadSessions(page = 1): Promise<void> {
  sessionsLoading.value = true;
  try {
    const res = await listSessions({ page, pageSize: PAGE_SIZE });
    sessions.value = page === 1 ? res.items : [...sessions.value, ...res.items];
    sessionsPage.value = res.page;
    sessionsTotal.value = res.total;
    sessionsHasMore.value = sessions.value.length < res.total;
  } catch (e) {
    listError.value = errorMessage(e);
  } finally {
    sessionsLoading.value = false;
  }
}

async function newSession(): Promise<void> {
  try {
    const s = await createSession();
    sessions.value = [s, ...sessions.value];
    sessionsTotal.value += 1;
    await selectSession(s);
  } catch (e) {
    listError.value = errorMessage(e);
  }
}

/* ---------- 消息历史 ---------- */
const messages = ref<ChatMessageView[]>([]);
const messagesPage = ref(1);
const messagesTotal = ref(0);
const messagesLoading = ref(false);
const messagesHasMore = ref(false);
const listError = ref<string | null>(null);
const pending = ref(false);

async function selectSession(s: ChatSession): Promise<void> {
  activeSessionId.value = s.id;
  messages.value = [];
  messagesPage.value = 1;
  listError.value = null;
  await loadMessages(1);
}

/** 服务端 OrderBy CreatedAtUtc DESC；前端反转为时间正序渲染 */
async function loadMessages(page = 1): Promise<void> {
  if (activeSessionId.value == null) return;
  messagesLoading.value = true;
  try {
    const res = await listMessages(activeSessionId.value, { page, pageSize: PAGE_SIZE });
    const asc = [...res.items].reverse();
    messages.value = page === 1 ? asc : [...asc, ...messages.value];
    messagesPage.value = res.page;
    messagesTotal.value = res.total;
    messagesHasMore.value = page * PAGE_SIZE < res.total;
  } catch (e) {
    listError.value = errorMessage(e);
  } finally {
    messagesLoading.value = false;
  }
}

/* ---------- 提问（Idempotency-Key：前端生成 UUID，重试复用） ---------- */
async function askQuestion(question: string): Promise<void> {
  if (pending.value) return;
  let sessionId = activeSessionId.value;
  if (sessionId == null) {
    // 无会话时先建会话再提问
    try {
      const s = await createSession();
      sessions.value = [s, ...sessions.value];
      activeSessionId.value = s.id;
      sessionId = s.id;
    } catch (e) {
      listError.value = errorMessage(e);
      return;
    }
  }

  const idempotencyKey = uuidv4();
  const userMsg: ChatMessageView = {
    id: -Date.now(),
    sessionId,
    role: 'User',
    createdAtUtc: new Date().toISOString(),
    questionText: question,
    status: null,
    sql: null,
    chart: null,
    narrative: null,
    clarifyOptions: [],
    rowsReturned: 0,
    elapsedMs: 0,
    error: null,
  };
  const placeholder: ChatMessageView = {
    id: -Date.now() - 1,
    sessionId,
    role: 'Assistant',
    createdAtUtc: new Date().toISOString(),
    questionText: null,
    status: null,
    sql: null,
    chart: null,
    narrative: null,
    clarifyOptions: [],
    rowsReturned: 0,
    elapsedMs: 0,
    error: null,
    localState: 'pending',
    idempotencyKey,
    question,
  };
  messages.value = [...messages.value, userMsg, placeholder];
  pending.value = true;
  await runAsk(sessionId, placeholder);
}

/** 执行提问；失败保留占位消息（含 idempotencyKey），重试时复用同一 key。
 * 注意：reactive 数组中的对象会被包成 Proxy，不能用引用相等定位占位消息，
 * 以 idempotencyKey + localState 匹配（同一 key 的已答消息 localState 为 undefined）。 */
async function runAsk(sessionId: number, placeholder: ChatMessageView): Promise<void> {
  const key = placeholder.idempotencyKey!;
  const matches = (m: ChatMessageView) => m.idempotencyKey === key && m.localState != null;
  pending.value = true;
  try {
    // 直接透传 Idempotency-Key Header（契约：POST /api/chat/sessions/{id}/messages）
    const result = await ask(sessionId, placeholder.question!, key);
    const answered: ChatMessageView = {
      ...placeholder,
      id: result.messageId,
      localState: undefined,
      status: result.status,
      sql: result.sql,
      chart: result.chart,
      narrative: result.narrative,
      clarifyOptions: result.clarifyOptions,
      rowsReturned: result.rowsReturned,
      elapsedMs: result.elapsedMs,
      error: result.error,
    };
    messages.value = messages.value.map((m) => (matches(m) ? answered : m));
    refreshSessionTitle(sessionId, placeholder.question!);
  } catch (e) {
    const err = toApiError(e);
    messages.value = messages.value.map((m) =>
      matches(m) ? { ...m, localState: 'failed' as const, error: err } : m,
    );
  } finally {
    pending.value = false;
  }
}

/** 重试：复用占位消息里保存的同一 Idempotency-Key（24h 幂等窗口） */
async function retryAsk(placeholder: ChatMessageView): Promise<void> {
  if (pending.value || activeSessionId.value == null || !placeholder.idempotencyKey) return;
  const key = placeholder.idempotencyKey;
  const restored: ChatMessageView = { ...placeholder, localState: 'pending', error: null };
  messages.value = messages.value.map((m) =>
    m.idempotencyKey === key && m.localState != null ? restored : m,
  );
  await runAsk(activeSessionId.value, restored);
}

function refreshSessionTitle(sessionId: number, question: string): void {
  const s = sessions.value.find((x) => x.id === sessionId);
  if (s && (s.title === '新会话' || !s.title)) {
    s.title = question.length > 30 ? `${question.slice(0, 30)}…` : question;
    s.lastMessageAtUtc = new Date().toISOString();
  }
}

function toApiError(e: unknown): { code: string; message: string } {
  return e instanceof ApiRequestError
    ? { code: e.code, message: e.message }
    : { code: 'NETWORK_ERROR', message: '网络异常，请稍后重试' };
}

function errorMessage(e: unknown): string {
  const err = toApiError(e);
  return `${err.code}：${err.message}`;
}

onMounted(async () => {
  await loadSessions(1);
  if (sessions.value.length > 0) {
    await selectSession(sessions.value[0]);
  }
});
</script>

<template>
  <div class="chat-page">
    <SessionList
      :sessions="sessions"
      :active-id="activeSessionId"
      :has-more="sessionsHasMore"
      :loading="sessionsLoading"
      @select="selectSession"
      @create="newSession"
      @load-more="loadSessions(sessionsPage + 1)"
    />
    <section class="chat-main">
      <div v-if="listError" class="alert alert--error chat-error" role="alert">{{ listError }}</div>
      <div class="messages" data-testid="message-list">
        <div v-if="activeSessionId == null" class="empty-hint muted">
          新建会话后开始提问。
        </div>
        <button
          v-if="messagesHasMore"
          class="btn btn--ghost"
          data-testid="load-earlier-btn"
          :disabled="messagesLoading"
          @click="loadMessages(messagesPage + 1)"
        >
          加载更早的消息
        </button>
        <template v-for="(m, i) in messages" :key="m.id ?? i">
          <div v-if="m.role === 'User'" class="msg msg--user" data-testid="user-message">
            <div class="bubble bubble--user">{{ m.questionText }}</div>
          </div>
          <div v-else class="msg msg--assistant">
            <AnswerCard :message="m" @ask="askQuestion" @retry="retryAsk" />
          </div>
        </template>
      </div>
      <AskInput :pending="pending" @ask="askQuestion" />
    </section>
  </div>
</template>

<style scoped>
.chat-page { flex: 1; display: flex; min-height: 0; }
.chat-main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.chat-error { margin: 12px 16px 0; }
.messages { flex: 1; overflow-y: auto; padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.empty-hint { text-align: center; margin-top: 48px; }
.msg { display: flex; }
.msg--user { justify-content: flex-end; }
.msg--assistant { justify-content: flex-start; }
.msg--assistant > * { width: 100%; max-width: 760px; }
.bubble--user {
  background: var(--c-primary); color: #fff; padding: 8px 14px;
  border-radius: 12px 12px 2px 12px; max-width: 70%; white-space: pre-wrap;
}
</style>
