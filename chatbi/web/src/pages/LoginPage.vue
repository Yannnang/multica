<script setup lang="ts">
import { ref } from 'vue';
import { useRoute } from 'vue-router';
import { buildFeishuLoginUrl } from '@/api/auth';

const route = useRoute();

/** 登录后回跳目标：仅接受站内相对路径（防开放重定向，基线 4.2） */
function safeRedirect(): string {
  const r = route.query.redirect;
  const value = typeof r === 'string' ? r : '/chat';
  return value.startsWith('/') && !value.startsWith('//') ? value : '/chat';
}

const mockRole = ref<'BusinessUser' | 'DataSteward' | 'SysAdmin'>('BusinessUser');
const isMock = import.meta.env.VITE_USE_MOCK === '1';
const mockError = ref(false);

async function login(): Promise<void> {
  const redirect = safeRedirect();
  if (isMock) {
    // msw 的 SW resolver 无法服务整页导航请求（导航中客户端往返通道被销毁）。
    // mock 模式改为：fetch 走完 mock 302 链（仍验证 login→callback 契约），再客户端导航到回调地址。
    try {
      const url = new URL(buildFeishuLoginUrl(redirect), window.location.origin);
      url.searchParams.set('mock_role', mockRole.value);
      const res = await fetch(url.pathname + url.search, { redirect: 'follow' });
      window.location.assign(res.url);
    } catch {
      mockError.value = true;
    }
    return;
  }
  window.location.assign(
    buildFeishuLoginUrl(redirect, import.meta.env.VITE_API_BASE ?? ''),
  );
}
</script>

<template>
  <div class="login-page">
    <div class="login-card card">
      <h1 class="login-title">智能问数平台</h1>
      <p class="muted login-sub">自然语言提问，图表 + 结论即刻返回</p>
      <button class="btn btn--primary login-btn" data-testid="login-btn" @click="login">
        飞书登录
      </button>
      <p v-if="mockError" class="alert alert--error login-error" role="alert">
        Mock 登录失败：请确认 mock 服务已启动（VITE_USE_MOCK=1）。
      </p>
      <div v-if="isMock" class="mock-panel">
        <label class="field">
          <span class="field__label">Mock 角色（仅本地联调）</span>
          <select v-model="mockRole" class="select" data-testid="mock-role-select">
            <option value="BusinessUser">BusinessUser</option>
            <option value="DataSteward">DataSteward</option>
            <option value="SysAdmin">SysAdmin</option>
          </select>
        </label>
      </div>
    </div>
  </div>
</template>

<style scoped>
.login-page {
  flex: 1; display: flex; align-items: center; justify-content: center;
  background: linear-gradient(160deg, #eff6ff 0%, #f5f7fa 60%);
}
.login-card { padding: 48px 40px; width: 380px; text-align: center; }
.login-title { font-size: 22px; margin: 0 0 6px; }
.login-sub { margin: 0 0 28px; }
.login-btn { width: 100%; padding: 10px; font-size: 15px; }
.mock-panel { margin-top: 24px; padding-top: 16px; border-top: 1px dashed var(--c-border); text-align: left; }
</style>
