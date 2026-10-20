import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AnswerCard from '@/components/chat/AnswerCard.vue';
import type { ChatMessageView } from '@/api/types';
import { CHART_BAR } from '@/mocks/fixtures';

function baseMessage(overrides: Partial<ChatMessageView> = {}): ChatMessageView {
  return {
    id: 1,
    sessionId: 1,
    role: 'Assistant',
    createdAtUtc: '2026-10-19T08:29:03Z',
    questionText: null,
    status: 'Success',
    sql: null,
    chart: null,
    narrative: null,
    clarifyOptions: [],
    rowsReturned: 0,
    elapsedMs: 0,
    error: null,
    ...overrides,
  };
}

function mountCard(message: ChatMessageView) {
  return mount(AnswerCard, {
    props: { message },
    global: {
      // ChartView 依赖 canvas（jsdom 无），以桩替；option 映射由 chart.spec 单测覆盖
      stubs: { ChartView: { template: '<div class="chart-stub" />' } },
    },
  });
}

describe('回答渲染四态（验收：四态回答渲染）', () => {
  it('Success：图表 + 文字结论 + 元信息 + 反馈入口', () => {
    const w = mountCard(
      baseMessage({
        status: 'Success',
        chart: CHART_BAR,
        narrative: '华东区域销售额最高（120.0 万）。',
        sql: 'SELECT region FROM dbo.SalesOrders',
        rowsReturned: 5,
        elapsedMs: 1830,
      }),
    );
    expect(w.find('.chart-stub').exists()).toBe(true);
    expect(w.get('[data-testid="narrative"]').text()).toContain('华东区域销售额最高');
    expect(w.text()).toContain('返回 5 行');
    expect(w.text()).toContain('耗时 1830 ms');
    expect(w.find('[data-testid="sql-toggle"]').exists()).toBe(true);
    expect(w.find('[data-testid="feedback-form"]').exists()).toBe(true);
  });

  it('Success 可折叠 SQL：默认收起，点击展开显示纯文本 SQL', async () => {
    const sql = 'SELECT region AS 区域 FROM dbo.SalesOrders;';
    const w = mountCard(baseMessage({ status: 'Success', sql, chart: CHART_BAR }));
    const details = w.find('details');
    expect((details.element as HTMLDetailsElement).open).toBe(false);
    await w.get('[data-testid="sql-toggle"]').trigger('click');
    expect((details.element as HTMLDetailsElement).open).toBe(true);
    expect(w.get('[data-testid="sql-content"]').text()).toBe(sql);
  });

  it('ClarifyNeeded：澄清选项按钮渲染，点击即以选项追问（emit ask）', async () => {
    const options = ['近 6 个月各区域销售额趋势', '本月销售额环比变化', '各渠道销售额占比'];
    const w = mountCard(
      baseMessage({ status: 'ClarifyNeeded', clarifyOptions: options, narrative: '问题存在歧义，请选择一个更具体的问法：' }),
    );
    const buttons = w.findAll('[data-testid="clarify-option"]');
    expect(buttons).toHaveLength(3);
    expect(buttons[1].text()).toBe('本月销售额环比变化');
    await buttons[1].trigger('click');
    expect(w.emitted('ask')).toBeTruthy();
    expect(w.emitted('ask')![0]).toEqual(['本月销售额环比变化']);
  });

  it('Rejected：用户可读文案 + 错误码，且提供反馈入口', () => {
    const w = mountCard(
      baseMessage({
        status: 'Rejected',
        error: { code: 'PERM_FORBIDDEN_TABLE', message: '该问题涉及未授权的数据范围，已被拒绝。' },
      }),
    );
    expect(w.get('[data-testid="answer-rejected"]').text()).toContain('该问题涉及未授权的数据范围');
    expect(w.get('[data-testid="answer-error-code"]').text()).toContain('PERM_FORBIDDEN_TABLE');
    expect(w.find('[data-testid="feedback-form"]').exists()).toBe(true);
  });

  it('Failed：用户可读文案 + 错误码（LLM_UPSTREAM）', () => {
    const w = mountCard(
      baseMessage({
        status: 'Failed',
        error: { code: 'LLM_UPSTREAM', message: '上游模型服务暂时不可用，请稍后重试。' },
      }),
    );
    expect(w.get('[data-testid="answer-failed"]').text()).toContain('上游模型服务暂时不可用');
    expect(w.get('[data-testid="answer-error-code"]').text()).toContain('LLM_UPSTREAM');
  });

  it('Rejected/Failed 缺失 error 时给出默认文案，不空白', () => {
    const w = mountCard(baseMessage({ status: 'Failed', error: null }));
    expect(w.get('[data-testid="answer-failed"]').text()).toContain('回答生成失败');
    expect(w.get('[data-testid="answer-error-code"]').text()).toContain('ASK_FAILED');
  });

  it('本地 pending / failed 态（网络层）', async () => {
    const pending = mountCard(baseMessage({ status: null, localState: 'pending' }));
    expect(pending.find('[data-testid="answer-pending"]').exists()).toBe(true);

    const failed = mountCard(
      baseMessage({ status: null, localState: 'failed', idempotencyKey: 'k-1', question: '各区域销售额' }),
    );
    expect(failed.find('[data-testid="answer-local-failed"]').exists()).toBe(true);
    await failed.get('[data-testid="retry-btn"]').trigger('click');
    expect(failed.emitted('retry')).toBeTruthy();
  });
});

describe('XSS 红线（验收：后端文本纯文本渲染，禁 v-html）', () => {
  it('Narrative 中的 HTML 按纯文本渲染，不产生元素', () => {
    const w = mountCard(
      baseMessage({
        status: 'Success',
        narrative: '<img src=x onerror=alert(1)>恶意结论',
        chart: CHART_BAR,
      }),
    );
    expect(w.get('[data-testid="narrative"]').text()).toContain('<img src=x onerror=alert(1)>');
    expect(w.find('img').exists()).toBe(false);
  });

  it('SQL 与 ErrorMessage 同样纯文本渲染', () => {
    const w = mountCard(
      baseMessage({
        status: 'Rejected',
        error: { code: 'X', message: '<script>alert(1)</script>' },
      }),
    );
    expect(w.find('script').exists()).toBe(false);
    expect(w.get('[data-testid="answer-rejected"]').text()).toContain('<script>');
  });
});
