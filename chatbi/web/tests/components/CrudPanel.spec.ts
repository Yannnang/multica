import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import CrudPanel from '@/components/semantic/CrudPanel.vue';
import { RESOURCE_SCHEMAS } from '@/components/semantic/schemas';
import { __resetClientState, setAuthHooks } from '@/api/client';
import { server } from '@/mocks/server';
import { http, HttpResponse } from 'msw';
import { makeMockJwt } from '../helpers';

/**
 * 语义层表单校验（验收项）：必填/枚举/范围字段级提示、
 * 422 SEM_* 服务端校验回显、409 并发冲突明确提示。
 */
const dsSchema = RESOURCE_SCHEMAS.find((s) => s.kind === 'datasources')!;

function mountPanel() {
  return mount(CrudPanel, { props: { schema: dsSchema } });
}

beforeEach(() => {
  __resetClientState();
  setAuthHooks({
    getAccessToken: () => makeMockJwt('DataSteward'),
    refreshAccessToken: vi.fn(async () => null),
    onUnauthorized: vi.fn(),
    onForbidden: vi.fn(),
  });
});

describe('CrudPanel 列表', () => {
  it('加载并展示 fixture 行', async () => {
    const w = mountPanel();
    await vi.waitFor(() => {
      expect(w.text()).toContain('业务库只读实例');
    });
    expect(w.findAll('[data-testid="resource-table"] tbody tr').length).toBeGreaterThanOrEqual(1);
  });
});

describe('CrudPanel 表单校验', () => {
  it('新建对话框：空表单保存 → 必填字段级错误提示', async () => {
    const w = mountPanel();
    await vi.waitFor(() => expect(w.find('[data-testid="create-btn"]').exists()).toBe(true));
    await w.get('[data-testid="create-btn"]').trigger('click');
    await w.get('[data-testid="resource-form"]').trigger('submit');
    expect(w.find('[data-testid="error-name"]').exists()).toBe(true);
    expect(w.get('[data-testid="error-name"]').text()).toContain('必填');
    expect(w.find('[data-testid="error-host"]').exists()).toBe(true);
    expect(w.get('[data-testid="form-error"]').text()).toContain('修正');
  });

  it('端口越界 → 范围错误；修正后可保存成功（POST 契约字段）', async () => {
    let captured: Record<string, unknown> | null = null;
    const w = mountPanel();
    await vi.waitFor(() => expect(w.find('[data-testid="create-btn"]').exists()).toBe(true));
    await w.get('[data-testid="create-btn"]').trigger('click');

    await w.get('[data-testid="field-name"]').setValue('测试库');
    await w.get('[data-testid="field-host"]').setValue('db.test');
    await w.get('[data-testid="field-databaseName"]').setValue('TestDB');
    await w.get('[data-testid="field-credentialRef"]').setValue('TEST_DB_CONN');
    await w.get('[data-testid="field-port"]').setValue('99999');
    await w.get('[data-testid="resource-form"]').trigger('submit');
    expect(w.get('[data-testid="error-port"]').text()).toContain('不得大于 65535');

    server.use(
      http.post('/api/semantic/datasources', async ({ request }) => {
        captured = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ ...captured, id: 42, rowVersion: 'rv-new' }, { status: 201 });
      }),
    );
    await w.get('[data-testid="field-port"]').setValue('1433');
    await w.get('[data-testid="resource-form"]').trigger('submit');
    await flushPromises();
    expect(captured).toMatchObject({
      name: '测试库',
      dbType: 'SqlServer',
      host: 'db.test',
      port: 1433,
      databaseName: 'TestDB',
      credentialRef: 'TEST_DB_CONN',
      status: 'Enabled',
    });
    expect(w.find('[data-testid="form-dialog"]').exists()).toBe(false);
  });

  it('枚举字段为 select，选项 = dbdesign 枚举字典', async () => {
    const w = mountPanel();
    await vi.waitFor(() => expect(w.find('[data-testid="create-btn"]').exists()).toBe(true));
    await w.get('[data-testid="create-btn"]').trigger('click');
    const options = w.get('[data-testid="field-dbType"]').findAll('option').map((o) => o.text());
    expect(options).toEqual(expect.arrayContaining(['SqlServer', 'MySql', 'PostgreSql', 'ClickHouse']));
  });

  it('422 SEM_VALIDATION：服务端字段错误回显到对应字段', async () => {
    server.use(
      http.post('/api/semantic/datasources', () =>
        HttpResponse.json(
          {
            title: 'Validation',
            status: 422,
            code: 'SEM_VALIDATION',
            detail: '名称已存在',
            errors: { name: ['名称已存在（UQ 冲突）'] },
          },
          { status: 422 },
        ),
      ),
    );
    const w = mountPanel();
    await vi.waitFor(() => expect(w.find('[data-testid="create-btn"]').exists()).toBe(true));
    await w.get('[data-testid="create-btn"]').trigger('click');
    for (const [key, value] of Object.entries({
      name: '重复名',
      host: 'h',
      databaseName: 'd',
      credentialRef: 'c',
    })) {
      await w.get(`[data-testid="field-${key}"]`).setValue(value);
    }
    await w.get('[data-testid="resource-form"]').trigger('submit');
    await flushPromises();
    expect(w.get('[data-testid="form-error"]').text()).toContain('SEM_VALIDATION');
    expect(w.get('[data-testid="error-name"]').text()).toContain('UQ 冲突');
  });

  it('409 并发冲突：编辑保存给出明确冲突提示', async () => {
    server.use(
      http.put('/api/semantic/datasources/:id', () =>
        HttpResponse.json(
          { title: 'Conflict', status: 409, code: 'SEM_CONCURRENCY_CONFLICT', detail: '记录已被他人修改，请刷新后重试' },
          { status: 409 },
        ),
      ),
    );
    const w = mountPanel();
    await vi.waitFor(() => expect(w.find('[data-testid="edit-btn"]').exists()).toBe(true));
    await w.get('[data-testid="edit-btn"]').trigger('click');
    // 编辑态表单已预填 fixture 值，直接保存触发 409
    await w.get('[data-testid="resource-form"]').trigger('submit');
    await flushPromises();
    // 冲突提示与通用表单错误互斥展示（conflict 优先），对话框保持打开供用户处理
    expect(w.find('[data-testid="conflict-hint"]').exists()).toBe(true);
    expect(w.get('[data-testid="conflict-hint"]').text()).toContain('409');
    expect(w.get('[data-testid="conflict-hint"]').text()).toContain('刷新');
    expect(w.find('[data-testid="form-dialog"]').exists()).toBe(true);
  });

  it('BusinessUser 写操作 403 → 列表错误可见（全局拦截器路径由 client.spec 覆盖）', async () => {
    __resetClientState();
    setAuthHooks({
      getAccessToken: () => makeMockJwt('BusinessUser'),
      refreshAccessToken: vi.fn(async () => null),
      onUnauthorized: vi.fn(),
      onForbidden: vi.fn(),
    });
    const w = mountPanel();
    await vi.waitFor(() => expect(w.find('[data-testid="create-btn"]').exists()).toBe(true));
    await w.get('[data-testid="create-btn"]').trigger('click');
    for (const [key, value] of Object.entries({
      name: '越权库',
      host: 'h',
      databaseName: 'd',
      credentialRef: 'c',
    })) {
      await w.get(`[data-testid="field-${key}"]`).setValue(value);
    }
    await w.get('[data-testid="resource-form"]').trigger('submit');
    await flushPromises();
    expect(w.get('[data-testid="form-error"]').text()).toContain('PERM_SEMANTIC_MANAGE');
  });
});
