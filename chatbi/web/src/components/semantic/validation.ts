import type { FieldSchema } from './schemas';

/** 表单值：字段 key → 原始值（json 类型在编辑时为字符串）。动态表单，值类型随字段 schema 变化 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type FormValues = Record<string, any>;

/**
 * 语义层表单校验（S3 范围 5）：必填 / 枚举 / 数值范围 / JSON 结构。
 * 返回字段级错误映射；空对象 = 通过。
 */
export function validateForm(fields: FieldSchema[], values: FormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const f of fields) {
    const raw = values[f.key];

    // json 类型：编辑态为字符串，先解析
    let value: unknown = raw;
    if (f.type === 'json') {
      if (typeof raw === 'string') {
        if (!raw.trim()) {
          if (f.required) errors[f.key] = `${f.label}为必填项`;
          continue;
        }
        try {
          value = JSON.parse(raw);
        } catch {
          errors[f.key] = `${f.label}不是合法 JSON`;
          continue;
        }
      }
      if (f.validateJson) {
        const msg = f.validateJson(value);
        if (msg) {
          errors[f.key] = `${f.label}：${msg}`;
          continue;
        }
      }
    }

    // 必填
    if (f.required) {
      const empty =
        value == null ||
        (typeof value === 'string' && !value.trim()) ||
        (typeof value === 'number' && !Number.isFinite(value));
      if (empty) {
        errors[f.key] = `${f.label}为必填项`;
        continue;
      }
    } else if (value == null || value === '') {
      continue;
    }

    // 枚举
    if (f.type === 'select' && f.options && value != null && value !== '') {
      if (!f.options.includes(String(value))) {
        errors[f.key] = `${f.label}必须是：${f.options.join(' / ')}`;
        continue;
      }
    }

    // 数值范围
    if (f.type === 'number' && value != null && value !== '') {
      const n = typeof value === 'number' ? value : Number(value);
      if (!Number.isFinite(n)) {
        errors[f.key] = `${f.label}必须为数字`;
        continue;
      }
      if (f.min != null && n < f.min) errors[f.key] = `${f.label}不得小于 ${f.min}`;
      else if (f.max != null && n > f.max) errors[f.key] = `${f.label}不得大于 ${f.max}`;
    }
  }
  return errors;
}

/** 表单值 → 提交 payload：number 归一、json 解析、boolean 收敛 */
export function buildPayload(fields: FieldSchema[], values: FormValues): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const f of fields) {
    const raw = values[f.key];
    if (raw === '' || raw == null) {
      if (!f.required) payload[f.key] = null;
      continue;
    }
    if (f.type === 'number') payload[f.key] = Number(raw);
    else if (f.type === 'boolean') payload[f.key] = Boolean(raw);
    else if (f.type === 'json') payload[f.key] = typeof raw === 'string' ? JSON.parse(raw) : raw;
    else payload[f.key] = raw;
  }
  return payload;
}

/** 行数据 → 编辑表单初值（json 序列化为字符串便于编辑） */
export function toFormValues(fields: FieldSchema[], row: Record<string, unknown> | null): FormValues {
  const values: FormValues = {};
  for (const f of fields) {
    if (row == null) {
      values[f.key] = f.default ?? (f.type === 'boolean' ? false : f.type === 'json' ? '[]' : '');
      continue;
    }
    const v = row[f.key];
    values[f.key] = f.type === 'json' ? JSON.stringify(v ?? f.default ?? [], null, 2) : v ?? '';
  }
  return values;
}
