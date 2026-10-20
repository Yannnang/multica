<script setup lang="ts">
import { useAuthStore } from '@/stores/auth';

const auth = useAuthStore();
</script>

<template>
  <div class="forbidden-page">
    <div class="card forbidden-card">
      <h2>无权限访问</h2>
      <p class="muted">
        当前账号角色（{{ auth.roles.join('、') || '无' }}）不具备访问该页面的权限。
        语义层管理仅对 DataSteward / SysAdmin 开放，如需权限请联系系统管理员。
      </p>
      <RouterLink class="btn btn--primary" to="/chat">返回问数</RouterLink>
    </div>
  </div>
</template>

<style scoped>
.forbidden-page { flex: 1; display: flex; align-items: center; justify-content: center; }
.forbidden-card { padding: 36px; width: 460px; text-align: center; }
.forbidden-card h2 { margin-top: 0; }
</style>
