import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '@/mocks/server';
import {
  ApiRequestError,
  __resetClientState,
  apiClient,
  setAuthHooks,
} from '@/api/client';
import { makeMockJwt } from '../helpers';

/**
 * 拦截器测试（验收：401/403 处理）。
 * 401 → 静默续期一次并重试原请求；续期失败 → onUnauthorized（跳登录）。
 * 403 → onForbidden（页面级提示），不跳转不重试。
 */
function bindHooks(overrides: Partial<Parameters<typeof setAuthHooks>[0]> = {}) {
  const hooks = {
    getAccessToken: () => makeMockJwt('BusinessUser'),
    refreshAccessToken: vi.fn(async () => makeMockJwt('BusinessUser')),
    onUnauthorized: vi.fn(),
    onForbidden: vi.fn(),
    ...overrides,
  };
  setAuthHooks(hooks);
  return hooks;
}

beforeEach(() => {
  __resetClientState();
});

describe('401 处理', () => {
  it('首次 401 → 静默续期后自动重试原请求', async () => {
    let hits = 0;
    server.use(
      http.get('/api/chat/sessions', () => {
        hits += 1;
        if (hits === 1) {
          return HttpResponse.json(
            { title: 'Unauthorized', status: 401, code: 'AUTH_TOKEN_EXPIRED', detail: '过期' },
            { status: 401 },
          );
        }
        return HttpResponse.json({ items: [], page: 1, pageSize: 20, total: 0 });
      }),
    );
    const hooks = bindHooks();
    const res = await apiClient.get('/api/chat/sessions');
    expect(res.status).toBe(200);
    expect(hits).toBe(2);
    expect(hooks.refreshAccessToken).toHaveBeenCalledTimes(1);
    expect(hooks.onUnauthorized).not.toHaveBeenCalled();
  });

  it('续期失败 → onUnauthorized（跳登录），错误归一化为 ApiRequestError', async () => {
    server.use(
      http.get('/api/chat/sessions', () =>
        HttpResponse.json({ title: 'Unauthorized', status: 401, code: 'AUTH_TOKEN_EXPIRED' }, { status: 401 }),
      ),
      http.post('/api/auth/refresh', () =>
        HttpResponse.json({ title: 'Unauthorized', status: 401, code: 'AUTH_REFRESH_INVALID' }, { status: 401 }),
      ),
    );
    const hooks = bindHooks({ refreshAccessToken: vi.fn(async () => null) });
    const err = await apiClient.get('/api/chat/sessions').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiRequestError);
    expect((err as ApiRequestError).status).toBe(401);
    expect(hooks.onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('并发 401 → refresh 单飞只触发一次', async () => {
    let hits = 0;
    server.use(
      http.get('/api/chat/sessions', () => {
        hits += 1;
        if (hits <= 2) {
          return HttpResponse.json({ title: 'Unauthorized', status: 401, code: 'AUTH_TOKEN_EXPIRED' }, { status: 401 });
        }
        return HttpResponse.json({ items: [], page: 1, pageSize: 20, total: 0 });
      }),
    );
    const hooks = bindHooks();
    const [r1, r2] = await Promise.all([
      apiClient.get('/api/chat/sessions'),
      apiClient.get('/api/chat/sessions'),
    ]);
    expect(r1.status).toBe(200);
    expect(r2.status).toBe(200);
    expect(hooks.refreshAccessToken).toHaveBeenCalledTimes(1);
  });

  it('auth 端点自身 401 不触发续期循环', async () => {
    const hooks = bindHooks();
    const err = await apiClient.post('/api/auth/refresh', { refreshToken: 'x' }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiRequestError);
    expect(hooks.refreshAccessToken).not.toHaveBeenCalled();
    expect(hooks.onUnauthorized).not.toHaveBeenCalled();
  });
});

describe('403 处理', () => {
  it('403 → onForbidden（携带 PERM_* code），不跳转不重试', async () => {
    server.use(
      http.post('/api/semantic/datasources', () =>
        HttpResponse.json(
          { title: 'Forbidden', status: 403, code: 'PERM_SEMANTIC_MANAGE', detail: '需要 DataSteward 或 SysAdmin 角色' },
          { status: 403 },
        ),
      ),
    );
    const hooks = bindHooks();
    const err = await apiClient.post('/api/semantic/datasources', {}).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiRequestError);
    expect((err as ApiRequestError).status).toBe(403);
    expect((err as ApiRequestError).code).toBe('PERM_SEMANTIC_MANAGE');
    expect(hooks.onForbidden).toHaveBeenCalledTimes(1);
    expect(hooks.onUnauthorized).not.toHaveBeenCalled();
    expect(hooks.refreshAccessToken).not.toHaveBeenCalled();
  });
});

describe('错误归一化', () => {
  it('422 SEM_* / 409 冲突透传 code 与 ProblemDetails', async () => {
    server.use(
      http.post('/api/semantic/metrics', () =>
        HttpResponse.json({ title: 'Validation', status: 422, code: 'SEM_VALIDATION', detail: '缺少必填字段' }, { status: 422 }),
      ),
      http.put('/api/semantic/metrics/1', () =>
        HttpResponse.json({ title: 'Conflict', status: 409, code: 'SEM_CONCURRENCY_CONFLICT', detail: '记录已被他人修改' }, { status: 409 }),
      ),
    );
    const bindErr = await apiClient.post('/api/semantic/metrics', {}).catch((e: unknown) => e);
    expect((bindErr as ApiRequestError).code).toBe('SEM_VALIDATION');
    const conflict = await apiClient.put('/api/semantic/metrics/1', {}).catch((e: unknown) => e);
    expect((conflict as ApiRequestError).status).toBe(409);
    expect((conflict as ApiRequestError).problem?.detail).toBe('记录已被他人修改');
  });
});
