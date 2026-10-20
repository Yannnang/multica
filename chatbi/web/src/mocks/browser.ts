import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

/** 浏览器端 msw worker：main.ts 在 VITE_USE_MOCK=1 时动态加载（不进生产构建主链路） */
export const worker = setupWorker(...handlers);
