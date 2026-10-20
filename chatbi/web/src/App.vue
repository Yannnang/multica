<script setup lang="ts">
import { RouterLink, RouterView, useRoute } from 'vue-router';
import { computed, onMounted } from 'vue';
import { useAuthStore, useUiStore } from '@/stores/auth';

const auth = useAuthStore();
const ui = useUiStore();
const route = useRoute();

const showNav = computed(
  () => auth.isLoggedIn && route.name !== 'login' && route.name !== 'auth-callback',
);

// 刷新页面后 profile 不在内存：静默补拉，头部显示用户名（失败则回落 union_id）
onMounted(() => {
  if (auth.isLoggedIn) void auth.ensureProfile();
});
</script>

<template>
  <div class="app-shell">
    <header v-if="showNav" class="app-header">
      <div class="app-header__brand">智能问数平台</div>
      <nav class="app-header__nav">
        <RouterLink to="/chat" class="nav-link">问数</RouterLink>
        <RouterLink v-if="auth.canManageSemantic" to="/semantic" class="nav-link">
          语义层管理
        </RouterLink>
      </nav>
      <div class="app-header__user">
        <span class="user-name" data-testid="user-name">{{ auth.displayName }}</span>
        <button class="btn btn--ghost" data-testid="logout-btn" @click="auth.doLogout()">
          退出
        </button>
      </div>
    </header>

    <!-- 403 页面级无权限提示（拦截器统一落点） -->
    <div v-if="ui.forbidden" class="forbidden-banner" role="alert" data-testid="forbidden-banner">
      <span>
        无权限执行该操作（{{ ui.forbidden.code }}）：{{ ui.forbidden.message }}
      </span>
      <button class="banner-close" aria-label="关闭" @click="ui.clearForbidden()">×</button>
    </div>

    <main class="app-main">
      <RouterView />
    </main>
  </div>
</template>
