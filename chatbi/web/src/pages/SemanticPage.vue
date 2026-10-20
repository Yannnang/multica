<script setup lang="ts">
import { ref } from 'vue';
import CrudPanel from '@/components/semantic/CrudPanel.vue';
import { RESOURCE_SCHEMAS } from '@/components/semantic/schemas';

/**
 * 语义层管理页（S3 范围 5）。
 * 访问控制在 router meta.roles（DataSteward/SysAdmin）+ 服务端 semantic:manage 双重把关，
 * 前端守卫只是体验优化，最终权限以服务端 403 为准。
 */
const activeKind = ref(RESOURCE_SCHEMAS[0].kind);
</script>

<template>
  <div class="semantic-page">
    <div class="semantic-tabs" role="tablist">
      <button
        v-for="s in RESOURCE_SCHEMAS"
        :key="s.kind"
        role="tab"
        class="tab"
        :class="{ 'tab--active': activeKind === s.kind }"
        :aria-selected="activeKind === s.kind"
        :data-testid="`tab-${s.kind}`"
        @click="activeKind = s.kind"
      >
        {{ s.title }}
      </button>
    </div>
    <div class="semantic-body card">
      <CrudPanel
        v-for="s in RESOURCE_SCHEMAS"
        v-show="activeKind === s.kind"
        :key="s.kind"
        :schema="s"
      />
    </div>
  </div>
</template>

<style scoped>
.semantic-page { flex: 1; padding: 16px 20px; overflow-y: auto; }
.semantic-tabs { display: flex; gap: 4px; margin-bottom: 12px; flex-wrap: wrap; }
.tab {
  padding: 7px 18px; border: 1px solid var(--c-border); border-bottom: none;
  border-radius: 6px 6px 0 0; background: #eef1f5; cursor: pointer; font-size: 14px;
  color: var(--c-text-secondary);
}
.tab--active { background: var(--c-surface); color: var(--c-primary); font-weight: 600; }
.semantic-body { padding: 16px; }
</style>
