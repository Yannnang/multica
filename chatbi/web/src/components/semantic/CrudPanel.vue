<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import {
  createSemantic,
  deleteSemantic,
  listSemantic,
  updateSemantic,
} from '@/api/semantic';
import { ApiRequestError } from '@/api/client';
import type { FieldSchema, ResourceSchema } from './schemas';
import { buildPayload, toFormValues, validateForm, type FormValues } from './validation';

/**
 * 通用语义层资源 CRUD 面板（S3 范围 5）：
 * 列表 + 分页 / 新建 / 编辑（rowVersion 并发令牌）/ 删除。
 * 错误处理：422 SEM_* 字段级提示；409 并发冲突明确提示；403 由全局拦截器页面级提示。
 */
const props = defineProps<{
  schema: ResourceSchema;
}>();

type Row = Record<string, unknown>;

const rows = ref<Row[]>([]);
const page = ref(1);
const total = ref(0);
const pageSize = 20;
const loading = ref(false);
const listError = ref<string | null>(null);

const dialogOpen = ref(false);
const editingId = ref<number | null>(null);
const editingRowVersion = ref<string | null>(null);
const form = reactive<FormValues>({});
const fieldErrors = reactive<Record<string, string>>({});
const formError = ref<string | null>(null);
const conflict = ref(false);
const saving = ref(false);
const deleting = ref<number | null>(null);

const tableFields = computed<FieldSchema[]>(() =>
  props.schema.fields.filter((f) => f.showInTable),
);
const totalPages = computed(() => Math.max(1, Math.ceil(total.value / pageSize)));

async function load(p = 1): Promise<void> {
  loading.value = true;
  listError.value = null;
  try {
    const res = await listSemantic<Row>(props.schema.kind, { page: p, pageSize });
    rows.value = res.items as Row[];
    page.value = res.page;
    total.value = res.total;
  } catch (e) {
    listError.value = describe(e);
  } finally {
    loading.value = false;
  }
}

function openCreate(): void {
  editingId.value = null;
  editingRowVersion.value = null;
  resetForm(toFormValues(props.schema.fields, null));
  dialogOpen.value = true;
}

function openEdit(row: Row): void {
  editingId.value = Number(row[props.schema.idKey]);
  editingRowVersion.value =
    props.schema.hasRowVersion && typeof row.rowVersion === 'string' ? row.rowVersion : null;
  resetForm(toFormValues(props.schema.fields, row));
  dialogOpen.value = true;
}

function resetForm(values: FormValues): void {
  for (const k of Object.keys(form)) delete form[k];
  Object.assign(form, values);
  for (const k of Object.keys(fieldErrors)) delete fieldErrors[k];
  formError.value = null;
  conflict.value = false;
}

async function save(): Promise<void> {
  const errors = validateForm(props.schema.fields, form);
  for (const k of Object.keys(fieldErrors)) delete fieldErrors[k];
  Object.assign(fieldErrors, errors);
  if (Object.keys(errors).length > 0) {
    formError.value = '请先修正表单中标红的字段';
    return;
  }
  saving.value = true;
  formError.value = null;
  conflict.value = false;
  try {
    const payload = buildPayload(props.schema.fields, form);
    if (editingId.value == null) {
      await createSemantic(props.schema.kind, payload);
    } else {
      if (props.schema.hasRowVersion && editingRowVersion.value) {
        payload.rowVersion = editingRowVersion.value;
      }
      await updateSemantic(props.schema.kind, editingId.value, payload);
    }
    dialogOpen.value = false;
    await load(page.value);
  } catch (e) {
    if (e instanceof ApiRequestError && e.status === 409) {
      conflict.value = true;
      formError.value = '并发冲突（409）：该记录已被他人修改，请关闭后刷新列表，基于最新数据重试。';
    } else if (e instanceof ApiRequestError && e.status === 422) {
      formError.value = `服务端校验失败（${e.code}）：${e.message}`;
      applyServerFieldErrors(e);
    } else {
      formError.value = describe(e);
    }
  } finally {
    saving.value = false;
  }
}

/** 422 ProblemDetails 可能带 errors 字段字典（ASP.NET 验证问题详情约定） */
function applyServerFieldErrors(e: ApiRequestError): void {
  const detailErrors = e.problem?.errors;
  if (detailErrors && typeof detailErrors === 'object') {
    for (const [key, msgs] of Object.entries(detailErrors as Record<string, unknown>)) {
      const text = Array.isArray(msgs) ? msgs.join('；') : String(msgs);
      if (text) fieldErrors[key] = text;
    }
  }
}

async function remove(row: Row): Promise<void> {
  const id = Number(row[props.schema.idKey]);
  deleting.value = id;
  listError.value = null;
  try {
    await deleteSemantic(props.schema.kind, id);
    await load(page.value);
  } catch (e) {
    listError.value = describe(e);
  } finally {
    deleting.value = null;
  }
}

function describe(e: unknown): string {
  return e instanceof ApiRequestError ? `${e.code}：${e.message}` : '操作失败，请稍后重试';
}

function displayCell(field: FieldSchema, row: Row): string {
  const v = row[field.key];
  if (v == null || v === '') return '—';
  if (typeof v === 'boolean') return v ? '是' : '否';
  if (field.type === 'json') return JSON.stringify(v);
  return String(v);
}

watch(() => props.schema.kind, () => load(1));
onMounted(() => load(1));
</script>

<template>
  <div class="crud-panel">
    <div class="crud-toolbar">
      <span class="muted">共 {{ total }} 条</span>
      <button class="btn btn--primary btn--sm" data-testid="create-btn" @click="openCreate">
        + 新建{{ schema.title }}
      </button>
    </div>

    <p v-if="listError" class="alert alert--error" role="alert" data-testid="list-error">
      {{ listError }}
    </p>

    <table class="data-table" data-testid="resource-table">
      <thead>
        <tr>
          <th>ID</th>
          <th v-for="f in tableFields" :key="f.key">{{ f.label }}</th>
          <th class="col-actions">操作</th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="rows.length === 0 && !loading">
          <td :colspan="tableFields.length + 2" class="muted empty-row">暂无数据</td>
        </tr>
        <tr v-for="row in rows" :key="String(row[schema.idKey])">
          <td class="mono">{{ row[schema.idKey] }}</td>
          <td v-for="f in tableFields" :key="f.key" class="cell">
            {{ displayCell(f, row) }}
          </td>
          <td class="col-actions">
            <button class="btn btn--ghost btn--sm" data-testid="edit-btn" @click="openEdit(row)">
              编辑
            </button>
            <button
              class="btn btn--ghost btn--sm btn--danger"
              data-testid="delete-btn"
              :disabled="deleting === Number(row[schema.idKey])"
              @click="remove(row)"
            >
              删除
            </button>
          </td>
        </tr>
      </tbody>
    </table>

    <div class="pager">
      <button class="btn btn--sm" :disabled="page <= 1 || loading" data-testid="prev-page" @click="load(page - 1)">
        上一页
      </button>
      <span class="muted">{{ page }} / {{ totalPages }}</span>
      <button class="btn btn--sm" :disabled="page >= totalPages || loading" data-testid="next-page" @click="load(page + 1)">
        下一页
      </button>
    </div>

    <!-- 新建/编辑对话框 -->
    <div v-if="dialogOpen" class="modal-mask" data-testid="form-dialog" @click.self="dialogOpen = false">
      <div class="modal" role="dialog" :aria-label="`${editingId == null ? '新建' : '编辑'}${schema.title}`">
        <h3 class="modal__title">
          {{ editingId == null ? '新建' : '编辑' }}{{ schema.title }}
        </h3>

        <p v-if="conflict" class="alert alert--warn" data-testid="conflict-hint">
          并发冲突（409）：数据已被他人修改，请刷新后基于最新版本重试。
        </p>
        <p v-else-if="formError" class="alert alert--error" role="alert" data-testid="form-error">
          {{ formError }}
        </p>

        <form data-testid="resource-form" @submit.prevent="save">
          <div v-for="f in schema.fields" :key="f.key" class="field">
            <label class="field__label" :for="`f-${f.key}`">
              {{ f.label }}<span v-if="f.required" class="required">*</span>
            </label>

            <select
              v-if="f.type === 'select'"
              :id="`f-${f.key}`"
              v-model="form[f.key]"
              class="select"
              :class="{ 'select--invalid': fieldErrors[f.key] }"
              :data-testid="`field-${f.key}`"
            >
              <option value="" disabled>请选择</option>
              <option v-for="opt in f.options" :key="opt" :value="opt">{{ opt }}</option>
            </select>

            <textarea
              v-else-if="f.type === 'textarea' || f.type === 'json'"
              :id="`f-${f.key}`"
              v-model="form[f.key]"
              class="textarea"
              :class="{ 'textarea--invalid': fieldErrors[f.key], mono: f.type === 'json' }"
              rows="3"
              :placeholder="f.placeholder"
              :data-testid="`field-${f.key}`"
            ></textarea>

            <label v-else-if="f.type === 'boolean'" class="checkbox-row">
              <input
                :id="`f-${f.key}`"
                v-model="form[f.key]"
                type="checkbox"
                :data-testid="`field-${f.key}`"
              />
              <span>{{ form[f.key] ? '是' : '否' }}</span>
            </label>

            <input
              v-else
              :id="`f-${f.key}`"
              v-model="form[f.key]"
              class="input"
              :class="{ 'input--invalid': fieldErrors[f.key] }"
              :type="f.type === 'number' ? 'number' : 'text'"
              :min="f.min"
              :max="f.max"
              :placeholder="f.placeholder"
              :data-testid="`field-${f.key}`"
            />

            <span v-if="f.help" class="muted field-help">{{ f.help }}</span>
            <span v-if="fieldErrors[f.key]" class="field__error" :data-testid="`error-${f.key}`">
              {{ fieldErrors[f.key] }}
            </span>
          </div>

          <div class="modal__footer">
            <button type="button" class="btn" @click="dialogOpen = false">取消</button>
            <button type="submit" class="btn btn--primary" data-testid="save-btn" :disabled="saving">
              {{ saving ? '保存中…' : '保存' }}
            </button>
          </div>
        </form>
      </div>
    </div>
  </div>
</template>

<style scoped>
.crud-toolbar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
.col-actions { white-space: nowrap; text-align: right; }
.cell { max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.empty-row { text-align: center; padding: 24px; }
.pager { display: flex; justify-content: center; align-items: center; gap: 12px; margin-top: 12px; }
.checkbox-row { display: flex; align-items: center; gap: 8px; }
.field-help { font-size: 12px; }
</style>
