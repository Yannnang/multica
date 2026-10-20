import { setupServer } from 'msw/node';
import { handlers } from '@/mocks/handlers';

/** Node 端 msw server：组件测试与拦截器测试共用 mock 契约 */
export const server = setupServer(...handlers);
