import { describe, expect, it } from 'vitest';
import { RESOURCE_SCHEMAS, type FieldSchema } from '@/components/semantic/schemas';
import { buildPayload, toFormValues, validateForm } from '@/components/semantic/validation';

const ds = RESOURCE_SCHEMAS.find((s) => s.kind === 'datasources')!;
const relations = RESOURCE_SCHEMAS.find((s) => s.kind === 'relations')!;
const onConditionsField = relations.fields.find((f) => f.key === 'onConditions')!;

function dsValues(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    name: '业务库',
    dbType: 'SqlServer',
    host: 'db.internal',
    port: 1433,
    databaseName: 'BizDB',
    credentialRef: 'BIZ_DB_READONLY_CONNECTION',
    queryTimeoutSeconds: 30,
    maxRowsPerQuery: 5000,
    status: 'Enabled',
    ...overrides,
  };
}

describe('语义层表单校验（验收：必填/枚举/数值范围）', () => {
  it('完整合法值 → 无错误', () => {
    expect(validateForm(ds.fields, dsValues())).toEqual({});
  });

  it('必填缺失 → 字段级错误（逐字段）', () => {
    const errors = validateForm(ds.fields, dsValues({ name: '', host: '  ' }));
    expect(errors.name).toContain('必填');
    expect(errors.host).toContain('必填');
    expect(Object.keys(errors)).toHaveLength(2);
  });

  it('枚举非法值 → 提示合法集合', () => {
    const errors = validateForm(ds.fields, dsValues({ dbType: 'Oracle' }));
    expect(errors.dbType).toContain('SqlServer');
    expect(errors.dbType).toContain('ClickHouse');
  });

  it('端口越界 → min/max 错误', () => {
    expect(validateForm(ds.fields, dsValues({ port: 0 })).port).toContain('不得小于');
    expect(validateForm(ds.fields, dsValues({ port: 70000 })).port).toContain('不得大于');
  });

  it('非数字 → 类型错误', () => {
    expect(validateForm(ds.fields, dsValues({ port: 'abc' })).port).toContain('数字');
  });

  it('onConditions 结构化 JSON 校验（dbdesign 3.5：禁 SQL 片段）', () => {
    const fields: FieldSchema[] = [onConditionsField];
    expect(validateForm(fields, { onConditions: '[{"leftColumnId":1,"rightColumnId":2}]' })).toEqual({});
    expect(validateForm(fields, { onConditions: 'not-json' }).onConditions).toContain('JSON');
    expect(validateForm(fields, { onConditions: '[{"leftColumnId":"x"}]' }).onConditions).toBeTruthy();
    expect(validateForm(fields, { onConditions: '"SELECT 1"' }).onConditions).toContain('数组');
  });
});

describe('payload 构造与表单初值', () => {
  it('number/boolean/json 归一化', () => {
    const payload = buildPayload(relations.fields, {
      leftSemanticTableId: '3',
      rightSemanticTableId: '4',
      joinType: 'Left',
      onConditions: '[{"leftColumnId":1,"rightColumnId":2}]',
    });
    expect(payload).toEqual({
      leftSemanticTableId: 3,
      rightSemanticTableId: 4,
      joinType: 'Left',
      onConditions: [{ leftColumnId: 1, rightColumnId: 2 }],
    });
  });

  it('可选字段留空 → null（不清空后端字段时由 S2 契约决定，前端显式传 null）', () => {
    const metrics = RESOURCE_SCHEMAS.find((s) => s.kind === 'metrics')!;
    const payload = buildPayload(metrics.fields, {
      code: 'c',
      displayName: 'n',
      expression: 'SUM(1)',
      aggregationType: 'Sum',
      unit: '',
      description: '',
      defaultTimeColumnId: '',
      isEnabled: true,
      ownerUserId: '',
    });
    expect(payload.unit).toBeNull();
    expect(payload.defaultTimeColumnId).toBeNull();
  });

  it('编辑初值：json 字段序列化为字符串，缺省值生效', () => {
    const values = toFormValues(ds.fields, null);
    expect(values.dbType).toBe('SqlServer');
    expect(values.port).toBe(1433);
    const editing = toFormValues(relations.fields, {
      leftSemanticTableId: 1,
      rightSemanticTableId: 2,
      joinType: 'Inner',
      onConditions: [{ leftColumnId: 5, rightColumnId: 6 }],
    });
    expect(editing.onConditions).toContain('"leftColumnId": 5');
  });
});
