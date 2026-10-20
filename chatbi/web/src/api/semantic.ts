import { apiClient } from './client';
import type { PagedResult, SemanticResourceKind } from './types';

/**
 * 语义层六资源 CRUD（基线 1.5：/api/semantic/datasources|tables|columns|metrics|relations|synonyms）。
 * 泛型 client：DTO 字段与 dbdesign v1.0 第 3 节一致，由调用方类型参数约束。
 * 更新携带 rowVersion（并发令牌），冲突返回 409；校验失败 422（SEM_*）。
 */

export interface SemanticPageQuery {
  page?: number;
  pageSize?: number;
  /** 可选过滤（如 columns?semanticTableId=1），S2 契约联调时核对 */
  [filter: string]: string | number | undefined;
}

export async function listSemantic<T>(
  kind: SemanticResourceKind,
  q: SemanticPageQuery = {},
): Promise<PagedResult<T>> {
  const params: Record<string, string | number> = {
    page: q.page ?? 1,
    pageSize: Math.min(q.pageSize ?? 20, 100),
  };
  for (const [k, v] of Object.entries(q)) {
    if (k !== 'page' && k !== 'pageSize' && v !== undefined) params[k] = v;
  }
  const { data } = await apiClient.get<PagedResult<T>>(`/api/semantic/${kind}`, { params });
  return data;
}

export async function createSemantic<T>(
  kind: SemanticResourceKind,
  body: Partial<T>,
): Promise<T> {
  const { data } = await apiClient.post<T>(`/api/semantic/${kind}`, body);
  return data;
}

export async function updateSemantic<T>(
  kind: SemanticResourceKind,
  id: number,
  body: Partial<T>,
): Promise<T> {
  const { data } = await apiClient.put<T>(`/api/semantic/${kind}/${id}`, body);
  return data;
}

export async function deleteSemantic(
  kind: SemanticResourceKind,
  id: number,
): Promise<void> {
  await apiClient.delete(`/api/semantic/${kind}/${id}`);
}
