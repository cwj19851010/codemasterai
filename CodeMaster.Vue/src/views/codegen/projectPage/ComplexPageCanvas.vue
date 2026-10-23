<template>
  <div
    v-if="isRoot"
    class="complex-canvas-shell"
    :class="[`viewport-${viewport}`, { 'is-preview': previewMode }]"
  >
    <div class="complex-canvas-device">
      <ComplexPageCanvas
        :node="node"
        :selected-id="selectedId"
        :preview-mode="previewMode"
        :viewport="viewport"
        :bindings="bindings"
        :data-sources="dataSources"
        :capabilities="capabilities"
        :preview-state="previewState"
        :is-root="false"
        @select="value => $emit('select', value)"
        @drop-item="value => $emit('drop-item', value)"
        @trigger-event="value => $emit('trigger-event', value)"
      />
    </div>
  </div>

  <section
    v-else
    v-show="previewState[node.componentId] !== false"
    class="canvas-node"
    :class="[
      `node-${node.type}`,
      {
        selected: selectedId === node.componentId,
        bound: Boolean(primaryBinding),
        draggable: !previewMode && node.type !== 'page'
      }
    ]"
    :style="nodeStyle"
    :draggable="!previewMode && node.type !== 'page'"
    @click.stop="onNodeClick"
    @dragstart.stop="onDragStart"
    @dragover.prevent.stop="onDragOver"
    @drop.prevent.stop="onDropInside"
  >
    <header v-if="!previewMode" class="node-designer-header">
      <span class="node-type-badge">{{ nodeTypeText(node.type) }}</span>
      <span class="node-id">{{ node.componentId }}</span>
      <span v-if="bindingText" class="node-binding">{{ bindingText }}</span>
    </header>

    <template v-if="node.type === 'page'">
      <div v-if="!node.children?.length" class="canvas-empty page-empty">
        <strong>空白复杂页面</strong>
        <span>从左侧拖入“行”，或选择布局预设开始设计</span>
      </div>
    </template>

    <template v-else-if="node.type === 'card'">
      <div class="mock-card-title">{{ displayLabel || '卡片标题' }}</div>
    </template>

    <template v-else-if="node.type === 'search' || node.type === 'query-form'">
      <div class="section-title">查询条件</div>
    </template>

    <template v-else-if="node.type === 'table'">
      <div class="section-title">{{ displayLabel || '数据列表' }}</div>
      <div class="mock-table">
        <div class="mock-table-row mock-table-head">
          <span v-for="column in tableColumns" :key="column.componentId">{{ nodeDisplayLabel(column) }}</span>
          <span v-if="!tableColumns.length">将实体字段拖到这里生成表格列</span>
        </div>
        <div v-for="row in 3" :key="row" class="mock-table-row">
          <span v-for="column in tableColumns" :key="column.componentId">{{ mockCell(column, row) }}</span>
          <span v-if="!tableColumns.length" class="mock-placeholder">暂无列</span>
        </div>
      </div>
    </template>

    <template v-else-if="node.type === 'form'">
      <div class="section-title">{{ displayLabel || '业务表单' }}</div>
    </template>

    <template v-else-if="node.type === 'detail'">
      <div class="section-title">{{ displayLabel || '详情信息' }}</div>
    </template>

    <template v-else-if="node.type === 'form-field'">
      <label class="mock-field-label">{{ displayLabel || '表单字段' }}</label>
      <div class="mock-input">{{ fieldPlaceholder }}</div>
    </template>

    <template v-else-if="node.type === 'detail-field'">
      <div class="mock-detail-field">
        <span>{{ displayLabel || '详情字段' }}</span>
        <strong>{{ fieldPlaceholder }}</strong>
      </div>
    </template>

    <template v-else-if="node.type === 'table-column'">
      <div class="mock-column-chip">{{ displayLabel || '表格列' }}</div>
    </template>

    <template v-else-if="node.type === 'pagination'">
      <div class="mock-pagination">
        <button disabled>‹</button><button class="active" disabled>1</button><button disabled>2</button><button disabled>3</button><button disabled>›</button>
      </div>
    </template>

    <template v-else-if="node.type === 'button'">
      <button type="button" class="mock-action-button">{{ node.props?.text || '操作按钮' }}</button>
    </template>

    <template v-else-if="node.type === 'text'">
      <div class="mock-text-block">{{ node.props?.text || '文本内容' }}</div>
    </template>

    <template v-else-if="node.type === 'dialog'">
      <div class="mock-dialog-title">{{ node.props?.title || '对话框' }}</div>
    </template>

    <div
      v-if="canContainChildren"
      class="canvas-children"
      :class="{ 'row-children': node.type === 'row', 'field-grid': ['form', 'search', 'query-form', 'detail'].includes(node.type) }"
    >
      <div
        v-if="!previewMode"
        class="canvas-drop-zone"
        @dragover.prevent.stop
        @drop.prevent.stop="event => onDropAt(event, 0)"
      >拖到这里</div>
      <template v-for="(child, index) in visibleChildren" :key="child.componentId">
        <ComplexPageCanvas
          :node="child"
          :selected-id="selectedId"
          :preview-mode="previewMode"
          :viewport="viewport"
          :bindings="bindings"
          :data-sources="dataSources"
          :capabilities="capabilities"
          :preview-state="previewState"
          :is-root="false"
          @select="value => $emit('select', value)"
          @drop-item="value => $emit('drop-item', value)"
          @trigger-event="value => $emit('trigger-event', value)"
        />
        <div
          v-if="!previewMode"
          class="canvas-drop-zone"
          @dragover.prevent.stop
          @drop.prevent.stop="event => onDropAt(event, index + 1)"
        >拖到这里</div>
      </template>
      <div v-if="previewMode && !visibleChildren.length && node.type !== 'page'" class="canvas-empty">
        {{ emptyText }}
      </div>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue'

defineOptions({ name: 'ComplexPageCanvas' })

const props = defineProps({
  node: { type: Object, required: true },
  selectedId: { type: String, default: '' },
  previewMode: { type: Boolean, default: false },
  viewport: { type: String, default: 'desktop' },
  bindings: { type: Array, default: () => [] },
  dataSources: { type: Array, default: () => [] },
  capabilities: { type: Array, default: () => [] },
  previewState: { type: Object, default: () => ({}) },
  isRoot: { type: Boolean, default: true }
})

const emit = defineEmits(['select', 'drop-item', 'trigger-event'])

const leafTypes = new Set(['pagination', 'table-column', 'form-field', 'detail-field', 'button', 'text'])
const canContainChildren = computed(() => !leafTypes.has(props.node.type))
const primaryBinding = computed(() => props.bindings.find(item =>
  item.componentId === props.node.componentId && Number(item.bindingRole) !== 8
))
const source = computed(() => props.dataSources.find(item =>
  String(item.sourceKey || '') === String(primaryBinding.value?.dataSourceKey || '') ||
  String(item.id || '') === String(primaryBinding.value?.dataSourceId || props.node.dataSourceId || '')
))
const capability = computed(() => props.capabilities.find(item =>
  String(item.entityId || '') === String(source.value?.entityId || '')
))
const field = computed(() => capability.value?.fields?.find(item =>
  String(item.fieldId || '') === String(primaryBinding.value?.fieldId || props.node.fieldId || '')
))
const displayLabel = computed(() => primaryBinding.value?.labelOverride || field.value?.name || props.node.props?.header || props.node.props?.title || '')
const bindingText = computed(() => {
  if (!primaryBinding.value) return ''
  const sourceKey = primaryBinding.value.dataSourceKey || source.value?.sourceKey || '数据源'
  return field.value ? `${sourceKey}.${field.value.name}` : sourceKey
})
const fieldPlaceholder = computed(() => field.value ? `{{ ${field.value.name} }}` : '未绑定字段')
const nodeStyle = computed(() => props.node.type === 'column'
  ? { '--column-span': String(props.node.grid?.span || props.node.span || 24) }
  : undefined
)
const tableColumns = computed(() => (props.node.children || []).filter(item => item.type === 'table-column'))
const visibleChildren = computed(() => props.node.type === 'table'
  ? (props.previewMode ? (props.node.children || []).filter(item => item.type !== 'table-column') : (props.node.children || []))
  : (props.node.children || [])
)
const emptyText = computed(() => ({
  row: '将列拖到这里',
  column: '将业务组件拖到这里',
  card: '将组件拖到卡片中',
  search: '将字段拖到这里生成查询条件',
  'query-form': '将字段拖到这里生成查询条件',
  form: '将字段拖到这里生成表单项',
  detail: '将字段拖到这里生成详情项'
}[props.node.type] || '拖入组件'))

const nodeTypeText = type => ({
  page: '页面', row: '行', column: '列', card: '卡片', search: '查询区', 'query-form': '查询区',
  table: '表格', 'table-column': '表格列', form: '表单', 'form-field': '表单字段',
  detail: '详情', 'detail-field': '详情字段', pagination: '分页'
}[type] || type)

function nodeBinding(node) {
  return props.bindings.find(item => item.componentId === node.componentId && Number(item.bindingRole) !== 8)
}

function nodeDisplayLabel(node) {
  const binding = nodeBinding(node)
  const nodeSource = props.dataSources.find(item =>
    String(item.sourceKey || '') === String(binding?.dataSourceKey || '') ||
    String(item.id || '') === String(binding?.dataSourceId || node.dataSourceId || '')
  )
  const nodeCapability = props.capabilities.find(item => String(item.entityId || '') === String(nodeSource?.entityId || ''))
  const nodeField = nodeCapability?.fields?.find(item => String(item.fieldId || '') === String(binding?.fieldId || node.fieldId || ''))
  return binding?.labelOverride || nodeField?.name || nodeTypeText(node.type)
}

function mockCell(column, row) {
  const label = nodeDisplayLabel(column)
  if (/amount|price|total|金额|价格/i.test(label)) return `¥ ${(row * 128.5).toFixed(2)}`
  if (/date|time|日期|时间/i.test(label)) return `2026-08-${20 + row}`
  return `${label} ${row}`
}

function onDragStart(event) {
  event.dataTransfer.effectAllowed = 'move'
  event.dataTransfer.setData('application/x-codemaster-layout-node', props.node.componentId)
}

function onNodeClick() {
  if (!props.previewMode) {
    emit('select', props.node)
    return
  }
  const eventName = configuredEvents()[0]?.eventName || defaultPreviewEvent(props.node.type)
  if (eventName) emit('trigger-event', { node: props.node, eventName, event: {} })
}

function configuredEvents() {
  try { return JSON.parse(props.node.props?._events || '[]') } catch { return [] }
}

function defaultPreviewEvent(type) {
  if (type === 'table') return 'row-click'
  if (['form', 'search', 'query-form'].includes(type)) return 'submit.prevent'
  if (['form-field', 'detail-field'].includes(type)) return 'change'
  if (type === 'dialog') return 'close'
  if (type === 'button') return 'click'
  return null
}

function readDragPayload(event) {
  const fieldText = event.dataTransfer.getData('application/x-codemaster-layout-field')
  if (fieldText) {
    try { return { kind: 'field', value: JSON.parse(fieldText) } } catch { return null }
  }
  const nodeType = event.dataTransfer.getData('application/x-codemaster-layout-type')
  if (nodeType) return { kind: 'component', value: nodeType }
  const nodeId = event.dataTransfer.getData('application/x-codemaster-layout-node')
  if (nodeId) return { kind: 'move', value: nodeId }
  return null
}

function onDragOver(event) {
  if (!props.previewMode && canContainChildren.value) event.dataTransfer.dropEffect = 'move'
}

function onDropInside(event) {
  if (props.previewMode || !canContainChildren.value) return
  const payload = readDragPayload(event)
  if (payload) emit('drop-item', { ...payload, targetId: props.node.componentId, index: null })
}

function onDropAt(event, index) {
  const payload = readDragPayload(event)
  if (payload) emit('drop-item', { ...payload, targetId: props.node.componentId, index })
}
</script>

<style scoped>
.complex-canvas-shell {
  min-height: 520px;
  padding: 24px;
  overflow: auto;
  background:
    linear-gradient(rgba(148, 163, 184, .11) 1px, transparent 1px),
    linear-gradient(90deg, rgba(148, 163, 184, .11) 1px, transparent 1px),
    #eef2f7;
  background-size: 18px 18px;
}
.complex-canvas-device { min-height: 470px; margin: 0 auto; padding: 18px; border: 1px solid #d8e0ea; border-radius: 10px; background: #fff; box-shadow: 0 12px 30px rgba(15, 23, 42, .09); transition: width .2s ease; }
.viewport-desktop .complex-canvas-device { width: min(100%, 1180px); }
.viewport-tablet .complex-canvas-device { width: 768px; }
.viewport-mobile .complex-canvas-device { width: 390px; }
.canvas-node { position: relative; min-width: 0; border: 1px dashed transparent; border-radius: 6px; transition: border-color .15s, background .15s, box-shadow .15s; }
.canvas-node:not(.node-page) { padding: 10px; }
.canvas-node.draggable { cursor: move; }
.canvas-node:hover { border-color: #a8c7fa; }
.canvas-node.selected { border-color: #409eff; box-shadow: 0 0 0 2px rgba(64, 158, 255, .13); }
.canvas-node.bound > .node-designer-header .node-binding { color: #409eff; }
.node-designer-header { display: flex; align-items: center; gap: 6px; min-height: 22px; margin: -4px 0 8px; font-size: 11px; }
.node-type-badge { padding: 2px 6px; border-radius: 4px; background: #ecf5ff; color: #337ecc; font-weight: 600; }
.node-id { color: #909399; }
.node-binding { min-width: 0; margin-left: auto; overflow: hidden; color: #67c23a; text-overflow: ellipsis; white-space: nowrap; }
.canvas-children { display: flex; flex-direction: column; gap: 8px; min-height: 42px; }
.row-children { display: grid; grid-template-columns: repeat(24, minmax(0, 1fr)); align-items: stretch; }
.row-children > .node-column { grid-column: span var(--column-span); }
.row-children > .canvas-drop-zone { grid-column: 1 / -1; }
.field-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
.field-grid > .canvas-drop-zone { grid-column: 1 / -1; }
.canvas-drop-zone { min-height: 8px; border: 1px dashed transparent; border-radius: 4px; color: transparent; font-size: 10px; line-height: 16px; text-align: center; }
.canvas-drop-zone:hover { min-height: 24px; border-color: #409eff; background: #ecf5ff; color: #409eff; }
.canvas-empty { display: flex; min-height: 70px; flex-direction: column; align-items: center; justify-content: center; gap: 5px; border: 1px dashed #cbd5e1; border-radius: 6px; color: #94a3b8; font-size: 12px; }
.page-empty { min-height: 360px; }
.node-row { padding: 8px !important; background: rgba(248, 250, 252, .72); }
.node-column { min-height: 90px; background: #fff; }
.node-card { border-style: solid; border-color: #e4e7ed; background: #fff; box-shadow: 0 2px 8px rgba(15, 23, 42, .05); }
.mock-card-title, .section-title { margin-bottom: 10px; color: #303133; font-size: 14px; font-weight: 600; }
.node-search, .node-query-form { background: #f8fafc; }
.node-table { overflow: hidden; border-style: solid; border-color: #e4e7ed; }
.mock-table { overflow: hidden; border: 1px solid #ebeef5; border-radius: 4px; }
.mock-table-row { display: grid; grid-auto-columns: minmax(100px, 1fr); grid-auto-flow: column; min-height: 34px; border-top: 1px solid #ebeef5; }
.mock-table-row:first-child { border-top: 0; }
.mock-table-row span { padding: 8px 10px; overflow: hidden; color: #606266; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.mock-table-head { background: #f5f7fa; font-weight: 600; }
.mock-placeholder { color: #c0c4cc !important; text-align: center; }
.mock-field-label { display: block; margin-bottom: 6px; color: #606266; font-size: 12px; }
.mock-input { min-height: 30px; padding: 6px 10px; border: 1px solid #dcdfe6; border-radius: 4px; color: #a8abb2; background: #fff; font-size: 12px; }
.mock-detail-field { display: grid; grid-template-columns: 110px minmax(0, 1fr); gap: 8px; padding: 8px; border-bottom: 1px solid #ebeef5; font-size: 12px; }
.mock-detail-field span { color: #909399; }
.mock-detail-field strong { color: #303133; font-weight: 500; }
.mock-column-chip { padding: 7px 9px; border-radius: 4px; background: #f5f7fa; color: #606266; font-size: 12px; text-align: center; }
.mock-pagination { display: flex; justify-content: flex-end; gap: 5px; }
.mock-pagination button { width: 28px; height: 28px; border: 0; border-radius: 4px; color: #606266; }
.mock-pagination .active { background: #409eff; color: #fff; }
.mock-action-button { padding: 8px 15px; border: 1px solid #409eff; border-radius: 4px; background: #409eff; color: #fff; cursor: pointer; }
.mock-text-block { min-height: 24px; color: #606266; line-height: 1.6; }
.node-dialog { min-height: 180px; border-style: solid; border-color: #c6e2ff; background: #fff; box-shadow: 0 12px 32px rgba(15, 23, 42, .13); }
.mock-dialog-title { margin: -10px -10px 12px; padding: 12px 14px; border-bottom: 1px solid #ebeef5; color: #303133; font-weight: 600; }
.is-preview .canvas-node { border-color: transparent; box-shadow: none; cursor: default; }
.is-preview .canvas-drop-zone { display: none; }
.viewport-mobile .row-children, .viewport-mobile .field-grid { display: flex; flex-direction: column; }
.viewport-mobile .mock-table { overflow-x: auto; }
</style>
