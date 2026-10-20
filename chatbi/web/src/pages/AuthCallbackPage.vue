<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useAuthStore } from '@/stores/auth';

/**
 * SSO 回调页（S3 范围 1）。
 * 假定后端 callback 校验 state/code 后 302 至本页并在 URL fragment 携带
 * #access_token=...&refresh_token=...（S1 定案前假设，PR 映射表标注；
 * fragment 不进服务端日志，优于 query）。
 * 接收后：token 存内存 + sessionStorage → 立即清除地址栏痕迹 → 拉 profile → 回跳。
 */
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const error = ref<string | null>(null);

function parseTokens(): { access: string | null; refresh: string | null; err: string | null } {
  const params = new URLSearchParams(window.location.hash.replace(/^#/, ''));
  let access = params.get('access_token');
  let refresh = params.get('refresh_token');
  if (!access) {
    // 兼容 query 传递（以 S1 实测为准）
    access = typeof route.query.access_token === 'string' ? route.query.access_token : null;
    refresh = typeof route.query.refresh_token === 'string' ? route.query.refresh_token : null;
  }
  const err = params.get('error');
  return { access, refresh, err };
}

function safeRedirect(): string {
  const r = route.query.redirect;
  const value = typeof r === 'string' ? r : '/chat';
  return value.startsWith('/') && !value.startsWith('//') ? value : '/chat';
}

onMounted(async () => {
  const { access, refresh, err } = parseTokens();
  // 清除地址栏中的 token 痕迹（history 替换，避免刷新重放/泄漏；redirect 已由 router 捕获）
  window.history.replaceState({}, '', window.location.pathname);

  if (err) {
    error.value = `登录失败（${err}），请重试`;
    return;
  }
  if (!access) {
    error.value = '登录回调缺少凭证，请重新登录';
    return;
  }
  auth.setTokens(access, refresh);
  await auth.ensureProfile();
  await router.replace(safeRedirect());
});
</script>

<template>
  <div class="callback-page">
    <div v-if="error" class="card callback-card" role="alert">
      <p class="alert alert--error" data-testid="callback-error">{{ error }}</p>
      <RouterLink class="btn" to="/login">返回登录</RouterLink>
    </div>
    <div v-else class="muted" data-testid="callback-loading">正在完成登录…</div>
  </div>
</template>

<style scoped>
.callback-page { flex: 1; display: flex; align-items: center; justify-content: center; }
.callback-card { padding: 32px; width: 420px; text-align: center; }
</style>
