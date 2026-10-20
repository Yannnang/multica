import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { http, HttpResponse } from 'msw';
import FeedbackForm from '@/components/chat/FeedbackForm.vue';
import { __resetClientState, setAuthHooks } from '@/api/client';
import { server } from '@/mocks/server';
import { makeMockJwt } from '../helpers';

function bindAuth(): void {
  setAuthHooks({
    getAccessToken: () => makeMockJwt('BusinessUser'),
    refreshAccessToken: vi.fn(async () => null),
    onUnauthorized: vi.fn(),
    onForbidden: vi.fn(),
  });
}

beforeEach(() => {
  __resetClientState();
  bindAuth();
});

describe('反馈标注（验收：S3 范围 4）', () => {
  it('未选结论时提交按钮禁用', () => {
    const w = mount(FeedbackForm, { props: { messageId: 102 } });
    expect((w.get('[data-testid="feedback-submit"]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('选择 IncorrectSql → 出现修正 SQL 输入框；其他结论不出现', async () => {
    const w = mount(FeedbackForm, { props: { messageId: 102 } });
    expect(w.find('[data-testid="corrected-sql"]').exists()).toBe(false);
    await w.get('[data-testid="verdict-select"]').setValue('IncorrectSql');
    expect(w.find('[data-testid="corrected-sql"]').exists()).toBe(true);
    await w.get('[data-testid="verdict-select"]').setValue('WrongCaliber');
    expect(w.find('[data-testid="corrected-sql"]').exists()).toBe(false);
  });

  it('提交成功：POST /api/chat/messages/{id}/feedback 携带契约字段，显示成功态', async () => {
    let captured: Record<string, unknown> | null = null;
    server.use(
      http.post('/api/chat/messages/:id/feedback', async ({ request, params }) => {
        captured = { id: params.id, ...((await request.json()) as Record<string, unknown>) };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const w = mount(FeedbackForm, { props: { messageId: 102 } });
    await w.get('[data-testid="verdict-select"]').setValue('IncorrectSql');
    await w.get('[data-testid="corrected-sql"]').setValue('SELECT 1;');
    await w.get('[data-testid="feedback-note"]').setValue('口径应为不含税');
    await w.get('[data-testid="feedback-submit"]').trigger('click');
    await vi.waitFor(() => {
      expect(w.find('[data-testid="feedback-success"]').exists()).toBe(true);
    });
    expect(captured).toMatchObject({
      id: '102',
      verdict: 'IncorrectSql',
      correctedSql: 'SELECT 1;',
      note: '口径应为不含税',
    });
  });

  it('提交失败：显示错误码与文案，表单可重试', async () => {
    server.use(
      http.post('/api/chat/messages/:id/feedback', () =>
        HttpResponse.json({ title: 'Validation', status: 422, code: 'ASK_INVALID_VERDICT', detail: 'verdict 非法' }, { status: 422 }),
      ),
    );
    const w = mount(FeedbackForm, { props: { messageId: 102 } });
    await w.get('[data-testid="verdict-select"]').setValue('Correct');
    await w.get('[data-testid="feedback-submit"]').trigger('click');
    await vi.waitFor(() => {
      expect(w.find('[data-testid="feedback-error"]').exists()).toBe(true);
    });
    expect(w.get('[data-testid="feedback-error"]').text()).toContain('ASK_INVALID_VERDICT');
    // 未进入成功态，仍可修改重试
    expect(w.find('[data-testid="feedback-success"]').exists()).toBe(false);
    expect(w.find('[data-testid="verdict-select"]').exists()).toBe(true);
  });
});
