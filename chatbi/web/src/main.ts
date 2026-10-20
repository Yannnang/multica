import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import { router } from './router';
import { useAuthStore } from './stores/auth';
import './styles/main.css';

async function bootstrap(): Promise<void> {
  // mock 模式：契约先行，msw 拦截 /api（S1/S2 未合入期间开发与 E2E 冒烟用）
  if (import.meta.env.VITE_USE_MOCK === '1') {
    const { worker } = await import('./mocks/browser');
    await worker.start({ onUnhandledRequest: 'bypass', quiet: true });
  }

  const app = createApp(App);
  const pinia = createPinia();
  app.use(pinia);
  app.use(router);

  const auth = useAuthStore(pinia);
  auth.bindClientHooks();

  app.mount('#app');
}

void bootstrap();
