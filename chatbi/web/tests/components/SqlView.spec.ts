import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import SqlView from '@/components/chat/SqlView.vue';

describe('SqlView（可折叠 SQL，纯文本渲染）', () => {
  const sql = "SELECT region AS 区域, SUM(amount) AS 销售额 FROM dbo.SalesOrders GROUP BY region;";

  it('默认收起，点击展开显示 SQL 原文', async () => {
    const w = mount(SqlView, { props: { sql } });
    const details = w.find('details').element as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(w.get('[data-testid="sql-toggle"]').text()).toContain('查看 SQL');

    await w.get('[data-testid="sql-toggle"]').trigger('click');
    expect(details.open).toBe(true);
    expect(w.get('[data-testid="sql-toggle"]').text()).toContain('收起 SQL');
    expect(w.get('[data-testid="sql-content"]').text()).toBe(sql);
    expect(w.get('[data-testid="sql-toggle"]').attributes('aria-expanded')).toBe('true');
  });

  it('XSS 红线：SQL 中的标签按纯文本渲染，不生成元素', () => {
    const w = mount(SqlView, {
      props: { sql: 'SELECT 1; <script>alert(1)</script><img src=x onerror=alert(2)>' },
    });
    expect(w.find('script').exists()).toBe(false);
    expect(w.find('img').exists()).toBe(false);
    expect(w.get('[data-testid="sql-content"]').text()).toContain('<script>alert(1)</script>');
  });
});
