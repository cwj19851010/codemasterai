<template>
  <div class="app-container view-source-page">
    <el-card shadow="never" class="mb-20">
      <el-form :inline="true">
        <el-form-item label="所属项目">
          <el-select v-model="projectId" filterable style="width: 280px" @change="handleProjectChange">
            <el-option v-for="item in projects" :key="item.id" :label="item.displayName || item.projectName" :value="item.id" />
          </el-select>
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Plus" :disabled="!projectId" @click="openBuilder('create')">新建数据库视图</el-button>
          <el-button type="success" plain :icon="Download" :disabled="!projectId" @click="openBuilder('import')">导入已有视图</el-button>
          <el-button :icon="Refresh" :disabled="!projectId" @click="loadAll">刷新</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <el-alert
        title="视图会注册成只读实体，同时生成项目级 View 数据源。实体字段来自目标数据库的真实视图列。"
        type="info"
        :closable="false"
        show-icon
        class="view-alert"
      />
      <el-table v-loading="loading" :data="sources" border>
        <el-table-column label="显示名称" prop="displayName" min-width="170" />
        <el-table-column label="数据库视图" prop="viewName" min-width="180" />
        <el-table-column label="SourceKey" prop="sourceKey" min-width="160" />
        <el-table-column label="只读服务" prop="serviceName" min-width="180" />
        <el-table-column label="查询方法" prop="methodName" min-width="170" />
        <el-table-column label="DTO" prop="dtoName" min-width="160" />
        <el-table-column label="Schema Hash" min-width="190">
          <template #default="{ row }"><el-text truncated type="info">{{ row.schemaHash || '-' }}</el-text></template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }"><el-tag :type="row.isEnabled ? 'success' : 'info'">{{ row.isEnabled ? '启用' : '停用' }}</el-tag></template>
        </el-table-column>
        <el-table-column label="操作" width="110" fixed="right">
          <template #default="{ row }"><el-button link type="danger" @click="removeSource(row)">删除配置</el-button></template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!loading && !sources.length" description="当前项目还没有注册视图数据源" />
    </el-card>

    <el-dialog v-model="dialogVisible" title="数据库视图管理" width="1080px" append-to-body destroy-on-close>
      <el-tabs v-model="mode">
        <el-tab-pane label="新建视图" name="create">
          <el-row :gutter="18">
            <el-col :span="9">
              <el-card shadow="never" class="field-card">
                <template #header>
                  <div class="card-header"><span>实体与字段</span><el-tag type="info">{{ checkedFieldCount }} 个字段</el-tag></div>
                </template>
                <el-tree
                  ref="fieldTreeRef"
                  :data="fieldTree"
                  node-key="key"
                  show-checkbox
                  default-expand-all
                  :props="{ label: 'label', children: 'children' }"
                  @check="syncSelections"
                />
              </el-card>
            </el-col>
            <el-col :span="15">
              <el-form label-width="100px">
                <registration-fields />
                <el-form-item label="关联关系">
                  <el-select v-model="form.joinKeys" multiple clearable style="width: 100%" placeholder="多实体时选择关联；不选则使用可推断关系">
                    <el-option v-for="item in joinOptions" :key="item.key" :label="item.label" :value="item.key" />
                  </el-select>
                </el-form-item>
                <el-form-item label="视图 SQL">
                  <el-input v-model="form.sql" type="textarea" :rows="13" spellcheck="false" placeholder="选择字段后生成 SQL，也可以手动编辑 SELECT 或 CTE" />
                </el-form-item>
                <el-form-item>
                  <el-button :loading="previewing" @click="previewSql">生成 SQL 预览</el-button>
                  <el-button type="primary" :loading="saving" @click="createAndRegister">创建视图并注册</el-button>
                </el-form-item>
              </el-form>
            </el-col>
          </el-row>
        </el-tab-pane>

        <el-tab-pane label="导入已有视图" name="import">
          <el-alert title="只读取目标数据库中的已有视图列，不会修改视图定义。" type="info" :closable="false" class="import-alert" />
          <el-form label-width="110px" class="import-form">
            <el-form-item label="已有视图">
              <el-select v-model="form.viewName" filterable style="width: 100%" @change="handleExistingViewChange">
                <el-option v-for="item in existingViews" :key="item.viewName" :label="item.viewName" :value="item.viewName" />
              </el-select>
            </el-form-item>
            <registration-fields :show-view-name="false" />
            <el-form-item>
              <el-button type="primary" :loading="saving" @click="importAndRegister">导入并注册只读实体</el-button>
            </el-form-item>
          </el-form>
        </el-tab-pane>
      </el-tabs>
      <template #footer><el-button @click="dialogVisible = false">关闭</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup>
import { computed, defineComponent, h, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, ElOption, ElSelect } from 'element-plus'
import { Download, Plus, Refresh } from '@element-plus/icons-vue'
import { getList as getProjects } from '@/api/codegen/project'
import { getByProjectId as getProjectModules } from '@/api/codegen/projectModule'
import {
  createProjectView,
  deleteProjectDataSource,
  getExistingViews,
  getProjectDataSources,
  getViewBuilderCatalog,
  importExistingView,
  previewViewSql,
  registerReadOnlyViewEntity
} from '@/api/codegen/projectPage'

defineOptions({ name: 'CodegenViewDataSource' })

const projects = ref([])
const modules = ref([])
const sources = ref([])
const existingViews = ref([])
const catalog = ref({ entities: [], joinCandidates: [] })
const projectId = ref()
const loading = ref(false)
const saving = ref(false)
const previewing = ref(false)
const dialogVisible = ref(false)
const mode = ref('create')
const fieldTreeRef = ref()
const form = reactive(defaultForm())

const RegistrationFields = defineComponent({
  name: 'RegistrationFields',
  props: { showViewName: { type: Boolean, default: true } },
  setup(props) {
    const item = (label, child) => h('div', { class: 'inline-form-item' }, [h('label', label), child])
    return () => h('div', { class: 'registration-grid' }, [
      props.showViewName ? item('视图名', h('input', {
        class: 'el-input__inner native-input', value: form.viewName,
        placeholder: 'vw_order_summary', onInput: event => { form.viewName = event.target.value }
      })) : null,
      item('目标模块', h(ElSelect, {
        modelValue: form.moduleId, 'onUpdate:modelValue': value => { form.moduleId = value }, style: 'width:100%'
      }, () => modules.value.map(module => h(ElOption, {
        key: module.id, label: module.moduleDescription || module.moduleName, value: module.id
      })))),
      item('实体名', h('input', {
        class: 'el-input__inner native-input', value: form.entityName,
        placeholder: 'OrderSummary', onInput: event => { form.entityName = event.target.value }
      })),
      item('显示名称', h('input', {
        class: 'el-input__inner native-input', value: form.displayName,
        placeholder: '订单汇总', onInput: event => { form.displayName = event.target.value }
      })),
      item('主键列', h('input', {
        class: 'el-input__inner native-input', value: form.keyColumn,
        placeholder: '默认首列，必须为整数列', onInput: event => { form.keyColumn = event.target.value }
      })),
      item('生成前端', h('input', {
        type: 'checkbox', checked: form.generateFrontend,
        onChange: event => { form.generateFrontend = event.target.checked }
      }))
    ].filter(Boolean))
  }
})

const fieldTree = computed(() => (catalog.value.entities || []).map(entity => ({
  key: `entity:${entity.entityId}`,
  label: `${entity.displayName || entity.entityName} · ${entity.tableName}`,
  children: (entity.fields || []).filter(field => !field.isSystemField).map(field => ({
    key: `field:${field.fieldId}`,
    label: `${field.displayName || field.name} · ${field.dataType}`,
    entityId: entity.entityId,
    fieldId: field.fieldId
  }))
})))

const joinOptions = computed(() => (catalog.value.joinCandidates || []).map((item, index) => ({
  ...item,
  key: joinKey(item, index),
  label: `${entityName(item.sourceEntityId)}.${item.sourceField} → ${entityName(item.targetEntityId)}.${item.targetField}`
})))

const checkedFieldCount = computed(() => form.selections.reduce((count, item) => count + item.fieldIds.length, 0))

function defaultForm() {
  return {
    projectId: undefined,
    moduleId: undefined,
    viewName: '',
    entityName: '',
    displayName: '',
    keyColumn: '',
    generateFrontend: true,
    sql: '',
    expectedColumns: [],
    selections: [],
    joinKeys: []
  }
}

async function loadProjects() {
  projects.value = await getProjects({}) || []
  if (!projectId.value && projects.value.length) projectId.value = projects.value[0].id
}

async function loadAll() {
  if (!projectId.value) return
  loading.value = true
  try {
    const [moduleItems, sourceResult] = await Promise.all([
      getProjectModules(projectId.value),
      getProjectDataSources({ projectId: projectId.value, sourceType: 2, pageNum: 1, pageSize: 200 })
    ])
    modules.value = moduleItems || []
    sources.value = sourceResult?.items || []
  } finally {
    loading.value = false
  }
}

async function handleProjectChange() {
  await loadAll()
}

async function openBuilder(targetMode) {
  mode.value = targetMode
  Object.assign(form, defaultForm(), { projectId: projectId.value })
  const [catalogResult, existingResult, moduleItems] = await Promise.all([
    getViewBuilderCatalog(projectId.value),
    getExistingViews(projectId.value),
    getProjectModules(projectId.value)
  ])
  catalog.value = catalogResult || { entities: [], joinCandidates: [] }
  existingViews.value = existingResult || []
  modules.value = moduleItems || []
  form.moduleId = modules.value[0]?.id
  if (targetMode === 'import' && existingViews.value.length) {
    form.viewName = existingViews.value[0].viewName
    handleExistingViewChange(form.viewName)
  }
  dialogVisible.value = true
}

function syncSelections() {
  const checked = fieldTreeRef.value?.getCheckedNodes(true, false) || []
  const grouped = new Map()
  for (const field of checked.filter(item => item.fieldId)) {
    if (!grouped.has(field.entityId)) grouped.set(field.entityId, [])
    grouped.get(field.entityId).push(field.fieldId)
  }
  form.selections = [...grouped.entries()].map(([entityId, fieldIds]) => ({ entityId, fieldIds }))
  const selectedIds = new Set(form.selections.map(item => item.entityId))
  form.joinKeys = form.joinKeys.filter(key => {
    const join = joinOptions.value.find(item => item.key === key)
    return join && selectedIds.has(join.sourceEntityId) && selectedIds.has(join.targetEntityId)
  })
}

function joinKey(item, index) {
  return `${item.relationId || 'inferred'}:${item.sourceEntityId}:${item.targetEntityId}:${item.sourceField}:${item.targetField}:${index}`
}

function entityName(entityId) {
  const entity = (catalog.value.entities || []).find(item => String(item.entityId) === String(entityId))
  return entity?.displayName || entity?.entityName || entityId
}

function selectedJoins() {
  return form.joinKeys.map(key => joinOptions.value.find(item => item.key === key)).filter(Boolean).map(item => ({
    sourceEntityId: item.sourceEntityId,
    targetEntityId: item.targetEntityId,
    sourceField: item.sourceField,
    targetField: item.targetField,
    joinType: item.defaultJoinType || 'Left',
    relationName: item.relationName || null,
    isInferred: Boolean(item.isInferred)
  }))
}

async function previewSql() {
  syncSelections()
  if (!form.selections.length) return ElMessage.warning('请至少选择一个实体字段')
  previewing.value = true
  try {
    const result = await previewViewSql({
      projectId: projectId.value,
      entities: form.selections,
      joins: selectedJoins(),
      includeInferredJoins: form.joinKeys.length === 0
    })
    form.sql = result?.sql || ''
    form.expectedColumns = (result?.selectedFields || []).map(item => item.alias)
    ElMessage.success('SQL 预览已生成，可以继续编辑')
    return result
  } finally {
    previewing.value = false
  }
}

function validateRegistration() {
  if (!projectId.value || !form.moduleId) return ElMessage.warning('请选择项目和目标模块'), false
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(form.viewName.trim())) return ElMessage.warning('视图名必须是合法的数据库标识符'), false
  if (!/^[A-Za-z][A-Za-z0-9]*$/.test(form.entityName.trim())) return ElMessage.warning('实体名必须是 PascalCase 标识符'), false
  if (!form.displayName.trim()) return ElMessage.warning('请输入显示名称'), false
  return true
}

function registrationPayload() {
  return {
    projectId: projectId.value,
    moduleId: form.moduleId,
    viewName: form.viewName.trim(),
    entityName: form.entityName.trim(),
    displayName: form.displayName.trim(),
    keyColumn: form.keyColumn.trim() || null,
    generateFrontend: form.generateFrontend,
    dataSourceType: 2
  }
}

async function createAndRegister() {
  if (!validateRegistration()) return
  if (!form.sql.trim()) {
    const result = await previewSql()
    if (!result?.sql) return
  }
  saving.value = true
  try {
    const created = await createProjectView({
      projectId: projectId.value,
      viewName: form.viewName.trim(),
      sql: form.sql.trim(),
      expectedColumns: form.expectedColumns
    })
    if (created?.requiresColumnConfirmation) {
      const differences = [
        created.missingColumns?.length ? `缺少列：${created.missingColumns.join(', ')}` : '',
        created.extraColumns?.length ? `新增列：${created.extraColumns.join(', ')}` : ''
      ].filter(Boolean).join('；')
      await ElMessageBox.confirm(`实际视图列与预览不一致（${differences}）。视图已经创建，是否按实际列继续注册？`, '确认视图列', { type: 'warning' })
    }
    const registered = await registerReadOnlyViewEntity(registrationPayload())
    await completeRegistration(registered)
  } finally {
    saving.value = false
  }
}

async function importAndRegister() {
  if (!validateRegistration()) return
  saving.value = true
  try {
    const registered = await importExistingView(registrationPayload())
    await completeRegistration(registered)
  } finally {
    saving.value = false
  }
}

async function completeRegistration(registered) {
  dialogVisible.value = false
  await loadAll()
  ElMessage.success(`视图 ${registered?.viewName || form.viewName} 已注册为只读实体和 View 数据源`)
}

function handleExistingViewChange(viewName) {
  const entity = toPascal(String(viewName || '').replace(/^vw_/i, ''))
  form.entityName = entity
  form.displayName = entity
}

function toPascal(value) {
  return String(value || '').split(/[^A-Za-z0-9]+/).filter(Boolean)
    .map(item => item.charAt(0).toUpperCase() + item.slice(1)).join('')
}

async function removeSource(row) {
  await ElMessageBox.confirm(
    `确定删除“${row.displayName}”的数据源配置吗？数据库视图和已注册只读实体不会被删除。`,
    '删除数据源配置',
    { type: 'warning' }
  )
  const result = await deleteProjectDataSource(row.id)
  ElMessage.success(result?.affectedPageCount ? `配置已删除，影响 ${result.affectedPageCount} 个页面` : '配置已删除')
  await loadAll()
}

onMounted(async () => {
  await loadProjects()
  await loadAll()
})
</script>

<style scoped>
.view-alert { margin-bottom: 16px; }
.field-card { min-height: 610px; max-height: 610px; overflow: auto; }
.card-header { display: flex; justify-content: space-between; align-items: center; }
.import-alert { margin-bottom: 20px; }
.import-form { max-width: 760px; margin: 0 auto; }
:deep(.registration-grid) { display: grid; grid-template-columns: 1fr 1fr; gap: 14px 18px; margin-bottom: 18px; }
:deep(.inline-form-item) { display: grid; grid-template-columns: 82px 1fr; align-items: center; gap: 10px; }
:deep(.inline-form-item > label) { color: var(--el-text-color-regular); text-align: right; }
:deep(.native-input) { box-sizing: border-box; width: 100%; height: 32px; padding: 1px 11px; border: 1px solid var(--el-border-color); border-radius: var(--el-border-radius-base); outline: none; }
:deep(.native-input:focus) { border-color: var(--el-color-primary); }
</style>
