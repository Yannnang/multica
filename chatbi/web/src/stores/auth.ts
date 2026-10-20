import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import * as authApi from '@/api/auth';
import { setAuthHooks, ApiRequestError } from '@/api/client';
import type { RoleCode, UserProfile } from '@/api/types';
import { decodeJwtClaims, extractRoles, isTokenExpired } from '@/utils/jwt';
import { router } from '@/router';

/**
 * token 存储策略（S3 范围 1）：内存 + sessionStorage，不落 localStorage 长存。
 * sessionStorage 键仅存本标签页会话，关闭即失效。
 */
const SS_ACCESS = 'chatbi.access_token';
const SS_REFRESH = 'chatbi.refresh_token';

export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string | null>(sessionStorage.getItem(SS_ACCESS));
  const refreshTokenValue = ref<string | null>(sessionStorage.getItem(SS_REFRESH));
  const profile = ref<UserProfile | null>(null);
  let profileLoading: Promise<UserProfile | null> | null = null;

  const claims = computed(() =>
    accessToken.value ? decodeJwtClaims(accessToken.value) : null,
  );
  /** 角色：JWT role claim 为准（基线 3.4）；/me 兜底 */
  const roles = computed<RoleCode[]>(() => {
    const fromJwt = extractRoles(claims.value);
    if (fromJwt.length > 0) return fromJwt;
    return profile.value?.roles ?? [];
  });
  const isLoggedIn = computed(
    () => !!accessToken.value && !isTokenExpired(claims.value, 0),
  );
  const canManageSemantic = computed(
    () => roles.value.includes('DataSteward') || roles.value.includes('SysAdmin'),
  );
  const displayName = computed(
    () => profile.value?.name ?? claims.value?.union_id ?? '用户',
  );

  function setTokens(access: string, refresh?: string | null): void {
    accessToken.value = access;
    sessionStorage.setItem(SS_ACCESS, access);
    if (refresh) {
      refreshTokenValue.value = refresh;
      sessionStorage.setItem(SS_REFRESH, refresh);
    }
  }

  function clearSession(): void {
    accessToken.value = null;
    refreshTokenValue.value = null;
    profile.value = null;
    sessionStorage.removeItem(SS_ACCESS);
    sessionStorage.removeItem(SS_REFRESH);
  }

  async function ensureProfile(): Promise<UserProfile | null> {
    if (profile.value) return profile.value;
    if (!accessToken.value) return null;
    profileLoading ??= authApi
      .fetchProfile()
      .then((p) => {
        profile.value = p;
        return p;
      })
      .catch(() => null)
      .finally(() => {
        profileLoading = null;
      });
    return profileLoading;
  }

  /** 静默续期：成功返回新 access token，失败清空会话返回 null */
  async function refreshAccessToken(): Promise<string | null> {
    if (!refreshTokenValue.value) return null;
    try {
      const res = await authApi.refreshToken(refreshTokenValue.value);
      setTokens(res.accessToken, res.refreshToken ?? refreshTokenValue.value);
      return res.accessToken;
    } catch {
      clearSession();
      return null;
    }
  }

  function goToLogin(): void {
    clearSession();
    const current = router.currentRoute.value;
    void router.replace({
      name: 'login',
      query:
        current.name && current.name !== 'login'
          ? { redirect: current.fullPath }
          : undefined,
    });
  }

  async function doLogout(): Promise<void> {
    try {
      await authApi.logout();
    } catch {
      // 登出失败不阻塞本地清理
    }
    clearSession();
    void router.replace({ name: 'login' });
  }

  /** main.ts 启动时注入拦截器钩子 */
  function bindClientHooks(): void {
    setAuthHooks({
      getAccessToken: () => accessToken.value,
      refreshAccessToken,
      onUnauthorized: goToLogin,
      onForbidden: (err: ApiRequestError) => {
        useUiStore().showForbidden(err.code, err.message);
      },
    });
  }

  return {
    accessToken,
    refreshTokenValue,
    profile,
    claims,
    roles,
    isLoggedIn,
    canManageSemantic,
    displayName,
    setTokens,
    clearSession,
    ensureProfile,
    refreshAccessToken,
    goToLogin,
    doLogout,
    bindClientHooks,
  };
});

/** UI 全局提示（403 页面级无权限提示落点） */
export const useUiStore = defineStore('ui', () => {
  const forbidden = ref<{ code: string; message: string } | null>(null);

  function showForbidden(code: string, message: string): void {
    forbidden.value = { code, message };
  }
  function clearForbidden(): void {
    forbidden.value = null;
  }

  return { forbidden, showForbidden, clearForbidden };
});
