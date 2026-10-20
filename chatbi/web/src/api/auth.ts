import { apiClient } from './client';
import type { TokenResponse, UserProfile } from './types';

/** 飞书 SSO 入口：GET /api/auth/feishu/login → 302（整页跳转，非 XHR） */
export function buildFeishuLoginUrl(redirect: string, apiBase = ''): string {
  return `${apiBase}/api/auth/feishu/login?redirect=${encodeURIComponent(redirect)}`;
}

/** POST /api/auth/refresh —— body 携带 refreshToken（S1 定案前假设，见 PR 映射表） */
export async function refreshToken(refreshTokenValue: string): Promise<TokenResponse> {
  const { data } = await apiClient.post<TokenResponse>('/api/auth/refresh', {
    refreshToken: refreshTokenValue,
  });
  return data;
}

/** POST /api/auth/logout */
export async function logout(): Promise<void> {
  await apiClient.post('/api/auth/logout');
}

/** GET /api/auth/me（假设端点，S1 联调核对；角色以 JWT role claim 为准） */
export async function fetchProfile(): Promise<UserProfile> {
  const { data } = await apiClient.get<UserProfile>('/api/auth/me');
  return data;
}
