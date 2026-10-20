import { apiClient } from './client';
import {
  normalizeAskStatus,
  normalizeChartType,
  normalizeEnum,
  CHAT_ROLE_NAMES,
} from '@/utils/enums';
import type {
  AskResponseRaw,
  AskResult,
  ChatMessageRaw,
  ChatMessageView,
  ChartSpec,
  ChatSession,
  FeedbackRequest,
  PagedResult,
} from './types';

/** 分页约定（基线 1.5）：page 从 1，pageSize 默认 20 上限 100 */
export interface PageQuery {
  page?: number;
  pageSize?: number;
}

function pageParams(q: PageQuery): Record<string, number> {
  return { page: q.page ?? 1, pageSize: Math.min(q.pageSize ?? 20, 100) };
}

function normalizeChart(raw: ChartSpec | null | undefined): ChartSpec | null {
  if (!raw) return null;
  return {
    ...raw,
    chartType: normalizeChartType(raw.chartType),
    yFields: raw.yFields ?? [],
    rows: raw.rows ?? [],
  };
}

/** AskResponse 归一化：枚举双兼容 + 可空字段收敛 */
export function normalizeAskResponse(raw: AskResponseRaw): AskResult {
  return {
    messageId: raw.messageId,
    status: normalizeAskStatus(raw.status),
    sql: raw.sql ?? null,
    chart: normalizeChart(raw.chart),
    narrative: raw.narrative ?? null,
    clarifyOptions: raw.clarifyOptions ?? [],
    rowsReturned: raw.rowsReturned ?? 0,
    elapsedMs: raw.elapsedMs ?? 0,
    error: raw.error ?? null,
  };
}

/** 历史消息归一化 */
export function normalizeChatMessage(raw: ChatMessageRaw): ChatMessageView {
  const role = normalizeEnum(CHAT_ROLE_NAMES, raw.role, 'Assistant')!;
  const isUser = role === 'User';
  return {
    id: raw.id,
    sessionId: raw.sessionId,
    role,
    createdAtUtc: raw.createdAtUtc,
    questionText: raw.questionText ?? null,
    status: isUser ? null : normalizeAskStatus(raw.status),
    sql: raw.generatedSql ?? null,
    chart: normalizeChart(raw.chart),
    narrative: raw.narrative ?? null,
    clarifyOptions: raw.clarifyOptions ?? [],
    rowsReturned: raw.rowCountReturned ?? 0,
    elapsedMs: raw.elapsedMs ?? 0,
    error:
      !isUser && (raw.errorCode || raw.errorMessage)
        ? { code: raw.errorCode ?? 'UNKNOWN', message: raw.errorMessage ?? '' }
        : null,
    idempotencyKey: raw.idempotencyKey ?? undefined,
  };
}

/** POST /api/chat/sessions —— 新建会话 */
export async function createSession(): Promise<ChatSession> {
  const { data } = await apiClient.post<ChatSession>('/api/chat/sessions');
  return data;
}

/** GET /api/chat/sessions?page&pageSize（OrderBy LastMessageAtUtc DESC） */
export async function listSessions(q: PageQuery = {}): Promise<PagedResult<ChatSession>> {
  const { data } = await apiClient.get<PagedResult<ChatSession>>('/api/chat/sessions', {
    params: pageParams(q),
  });
  return data;
}

/** GET /api/chat/sessions/{id}/messages?page&pageSize（OrderBy CreatedAtUtc DESC） */
export async function listMessages(
  sessionId: number,
  q: PageQuery = {},
): Promise<PagedResult<ChatMessageView>> {
  const { data } = await apiClient.get<PagedResult<ChatMessageRaw>>(
    `/api/chat/sessions/${sessionId}/messages`,
    { params: pageParams(q) },
  );
  return { ...data, items: data.items.map(normalizeChatMessage) };
}

/**
 * POST /api/chat/sessions/{id}/messages —— 问数。
 * Idempotency-Key 走 Header（24h 窗口）；key 由调用方生成并在重试时复用。
 */
export async function ask(
  sessionId: number,
  question: string,
  idempotencyKey: string,
): Promise<AskResult> {
  const { data } = await apiClient.post<AskResponseRaw>(
    `/api/chat/sessions/${sessionId}/messages`,
    { question },
    { headers: { 'Idempotency-Key': idempotencyKey } },
  );
  return normalizeAskResponse(data);
}

/** POST /api/chat/messages/{id}/feedback —— 准确率闭环标注 */
export async function submitFeedback(
  messageId: number,
  body: FeedbackRequest,
): Promise<void> {
  await apiClient.post(`/api/chat/messages/${messageId}/feedback`, body);
}
