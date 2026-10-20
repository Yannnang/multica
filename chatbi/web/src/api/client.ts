import axios, {
  AxiosError,
  type AxiosRequestConfig,
  type InternalAxiosRequestConfig,
} from 'axios';
import type { ProblemDetails } from './types';

/**
 * 统一请求错误：所有非 2xx 响应归一化为 ApiRequestError。
 * status/code 来自 ProblemDetails + code 字段（基线 1.5 错误码约定）。
 */
export class ApiRequestError extends Error {
  readonly status: number;
  readonly code: string;
  readonly problem: ProblemDetails | null;

  constructor(status: number, code: string, message: string, problem: ProblemDetails | null) {
    super(message);
    this.name = 'ApiRequestError';
    this.status = status;
    this.code = code;
    this.problem = problem;
  }
}

/**
 * 认证协作钩子：由 main.ts 注入（绑定 auth store），
 * 避免 client <-> store 循环依赖，也便于单测直接打桩。
 */
export interface AuthHooks {
  /** 当前内存 access token */
  getAccessToken(): string | null;
  /** 静默续期；成功返回新 token，失败返回 null（调用方负责登出跳转） */
  refreshAccessToken(): Promise<string | null>;
  /** 401 且续期失败：跳登录 */
  onUnauthorized(): void;
  /** 403 已认证无权限：页面级提示（不跳转） */
  onForbidden(err: ApiRequestError): void;
}

let authHooks: AuthHooks | null = null;
export function setAuthHooks(hooks: AuthHooks): void {
  authHooks = hooks;
}

interface RetriableConfig extends InternalAxiosRequestConfig {
  _retriedAfterRefresh?: boolean;
}

/** refresh 单飞：并发 401 只触发一次 /api/auth/refresh */
let refreshInFlight: Promise<string | null> | null = null;

export const apiClient = axios.create({
  baseURL: import.meta.env.VITE_API_BASE ?? '',
  timeout: 60_000,
  headers: { 'Content-Type': 'application/json' },
});

const AUTH_PATH_PREFIXES = ['/api/auth/'];

function isAuthPath(url?: string): boolean {
  return !!url && AUTH_PATH_PREFIXES.some((p) => url.includes(p));
}

function toApiRequestError(error: AxiosError): ApiRequestError {
  const status = error.response?.status ?? 0;
  const data = error.response?.data as ProblemDetails | undefined;
  // 网络失败/超时：status 0，给可读文案，不泄漏内部细节
  if (!error.response) {
    return new ApiRequestError(0, 'NETWORK_ERROR', '网络异常，请稍后重试', null);
  }
  const code = data?.code ?? `HTTP_${status}`;
  const message =
    data?.detail ?? data?.title ?? error.message ?? '请求失败';
  return new ApiRequestError(status, code, message, data ?? null);
}

apiClient.interceptors.request.use((config) => {
  const token = authHooks?.getAccessToken();
  if (token && !config.headers.Authorization) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

apiClient.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const apiError = toApiRequestError(error);
    const config = error.config as RetriableConfig | undefined;

    // 401 → 静默续期一次后重试；auth 端点自身与已重试过的请求不再续期
    if (
      apiError.status === 401 &&
      authHooks &&
      config &&
      !config._retriedAfterRefresh &&
      !isAuthPath(config.url)
    ) {
      config._retriedAfterRefresh = true;
      refreshInFlight ??= authHooks
        .refreshAccessToken()
        .finally(() => {
          refreshInFlight = null;
        });
      const newToken = await refreshInFlight;
      if (newToken) {
        config.headers.set('Authorization', `Bearer ${newToken}`);
        return apiClient.request(config as AxiosRequestConfig);
      }
      authHooks.onUnauthorized();
      return Promise.reject(apiError);
    }

    // 403 → 页面级无权限提示（PERM_*），不跳转不重试
    if (apiError.status === 403) {
      authHooks?.onForbidden(apiError);
    }

    return Promise.reject(apiError);
  },
);

/** 测试辅助：重置模块级状态 */
export function __resetClientState(): void {
  refreshInFlight = null;
  authHooks = null;
}
