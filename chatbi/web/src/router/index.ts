import { createRouter, createWebHistory } from 'vue-router';
import { useAuthStore } from '@/stores/auth';
import type { RoleCode } from '@/api/types';

declare module 'vue-router' {
  interface RouteMeta {
    requiresAuth?: boolean;
    roles?: RoleCode[];
    title?: string;
  }
}

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('@/pages/LoginPage.vue'),
      meta: { title: '登录' },
    },
    {
      path: '/auth/callback',
      name: 'auth-callback',
      component: () => import('@/pages/AuthCallbackPage.vue'),
      meta: { title: '登录中' },
    },
    {
      path: '/',
      redirect: '/chat',
    },
    {
      path: '/chat',
      name: 'chat',
      component: () => import('@/pages/ChatPage.vue'),
      meta: { requiresAuth: true, title: '智能问数' },
    },
    {
      path: '/semantic',
      name: 'semantic',
      component: () => import('@/pages/SemanticPage.vue'),
      // 路由守卫：仅 DataSteward/SysAdmin 可见（S3 范围 5）
      meta: { requiresAuth: true, roles: ['DataSteward', 'SysAdmin'], title: '语义层管理' },
    },
    {
      path: '/forbidden',
      name: 'forbidden',
      component: () => import('@/pages/ForbiddenPage.vue'),
      meta: { title: '无权限' },
    },
    { path: '/:pathMatch(.*)*', redirect: '/chat' },
  ],
});

router.beforeEach((to) => {
  const auth = useAuthStore();
  if (to.meta.requiresAuth && !auth.isLoggedIn) {
    return { name: 'login', query: { redirect: to.fullPath } };
  }
  if (to.meta.roles && to.meta.roles.length > 0) {
    const allowed = to.meta.roles.some((r) => auth.roles.includes(r));
    if (!allowed) return { name: 'forbidden' };
  }
  return true;
});
