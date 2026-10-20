import { afterAll, afterEach, beforeAll, beforeEach } from 'vitest';
import { config } from '@vue/test-utils';
import { server } from '@/mocks/server';
import { __resetMockState } from '@/mocks/handlers';

/* jsdom 缺失的浏览器 API 打桩 */
class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}
(globalThis as Record<string, unknown>).ResizeObserver ??= ResizeObserverStub;

if (typeof window !== 'undefined') {
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  window.scrollTo = () => {};
  sessionStorage.clear();
}

/* VTU 全局静默 */
config.global.renderStubDefaultSlot = true;

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
beforeEach(() => {
  __resetMockState();
  server.resetHandlers();
  sessionStorage.clear();
});
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
