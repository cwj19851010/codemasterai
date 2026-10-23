<template>
  <div class="app-container api-source-page">
    <el-card shadow="never" class="mb-20">
      <el-form :inline="true" :model="query">
        <el-form-item label="所属项目">
          <el-select v-model="query.projectId" filterable style="width: 260px" @change="handleProjectChange">
            <el-option v-for="item in projects" :key="item.id" :label="item.displayName || item.projectName" :value="item.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="关键字">
          <el-input v-model="query.keyword" clearable placeholder="SourceKey、名称、服务或 DTO" @keyup.enter="loadSources" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Search" @click="loadSources">查询</el-button>
          <el-button :icon="Refresh" @click="resetQuery">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <div class="toolbar">
        <div>
          <el-button type="primary" :icon="Plus" :disabled="!query.projectId" @click="openCreate">新增 API 数据源</el-button>
          <el-button type="success" :disabled="!query.projectId" @click="openOpenApiImport()">导入 OpenAPI</el-button>
          <el-button :icon="Refresh" :disabled="!query.projectId" @click="refreshCatalog">刷新能力目录</el-button>
        </div>
        <el-tag type="info">仅列出可作为数据源调用的 GET 服务方法</el-tag>
      </div>

      <el-alert
        class="source-alert"
        title="API 数据源是项目级可复用配置，复杂页面和传统页面都可以引用；路由参数默认绑定到当前页面路由。"
        type="info"
        :closable="false"
        show-icon
      />

      <el-table v-loading="loading" :data="sources" border>
        <el-table-column label="SourceKey" prop="sourceKey" min-width="150" />
        <el-table-column label="显示名称" prop="displayName" min-width="160" />
        <el-table-column label="服务" prop="serviceName" min-width="190" />
        <el-table-column label="方法" prop="methodName" min-width="190" />
        <el-table-column label="路由" min-width="240">
          <template #default="{ row }">
            <el-text type="info" class="route-text">{{ row.routePath || methodForRow(row)?.routePath || '-' }}</el-text>
          </template>
        </el-table-column>
        <el-table-column label="DTO" prop="dtoName" min-width="170" />
        <el-table-column label="形态" width="100">
          <template #default="{ row }"><el-tag>{{ shapeLabel(row.dataShape) }}</el-tag></template>
        </el-table-column>
        <el-table-column label="状态" width="160">
          <template #default="{ row }">
            <el-tag :type="row.isEnabled ? 'success' : 'info'">{{ row.isEnabled ? '启用' : '停用' }}</el-tag>
            <el-tag v-if="row.openApiDocumentId" class="sync-state" :type="openApiStateType(row.openApiSyncState)">
              {{ openApiStateLabel(row.openApiSyncState) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="180" fixed="right">
          <template #default="{ row }">
            <template v-if="row.openApiDocumentId">
              <el-button link type="success" @click="openOpenApiImport(row.openApiDocumentId)">重新导入</el-button>
              <el-button v-if="Number(row.openApiSyncState) === 7" link type="danger" @click="removeSource(row)">删除</el-button>
            </template>
            <template v-else>
              <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
              <el-button link type="danger" @click="removeSource(row)">删除</el-button>
            </template>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="query.pageNum"
        v-model:page-size="query.pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100]"
        layout="total, sizes, prev, pager, next, jumper"
        @size-change="loadSources"
        @current-change="loadSources"
      />
    </el-card>

    <el-dialog v-model="dialogVisible" :title="editingId ? '编辑 API 数据源' : '新增 API 数据源'" width="760px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="110px">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="SourceKey" prop="sourceKey">
              <el-input v-model="form.sourceKey" placeholder="例如 monthlyReport" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="显示名称" prop="displayName">
              <el-input v-model="form.displayName" placeholder="例如 月度统计" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item label="服务" prop="serviceName">
          <el-select v-model="form.serviceName" filterable style="width: 100%" @change="handleServiceChange">
            <el-option
              v-for="item in supportedServices"
              :key="item.serviceName"
              :label="item.displayName ? `${item.displayName} (${item.serviceName})` : item.serviceName"
              :value="item.serviceName"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="方法" prop="methodName">
          <el-select v-model="form.methodName" filterable style="width: 100%" @change="handleMethodChange">
            <el-option v-for="item in currentMethods" :key="item.methodName" :label="methodLabel(item)" :value="item.methodName" />
          </el-select>
        </el-form-item>
        <el-descriptions v-if="selectedMethod" :column="2" border class="method-summary">
          <el-descriptions-item label="HTTP">{{ selectedMethod.httpMethod }}</el-descriptions-item>
          <el-descriptions-item label="数据形态">{{ shapeLabel(selectedMethod.dataShape) }}</el-descriptions-item>
          <el-descriptions-item label="路由" :span="2">{{ selectedMethod.routePath }}</el-descriptions-item>
          <el-descriptions-item label="返回类型" :span="2">{{ selectedMethod.resultType || form.dtoName }}</el-descriptions-item>
        </el-descriptions>
        <el-form-item label="参数 JSON" prop="paramsJson">
          <el-input v-model="form.paramsJson" type="textarea" :rows="7" spellcheck="false" />
          <div class="form-tip">可以使用 <code>{ "$ref": "$route.id" }</code> 引用页面路由参数，也可以填写固定值。</div>
        </el-form-item>
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="同步策略">
              <el-radio-group v-model="form.syncMode">
                <el-radio-button :value="1">自动</el-radio-button>
                <el-radio-button :value="2">手动</el-radio-button>
              </el-radio-group>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="启用">
              <el-switch v-model="form.isEnabled" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="saveSource">保存</el-button>
      </template>
    </el-dialog>

    <OpenApiImportDialog
      v-model="openApiVisible"
      :project-id="query.projectId"
      :document-id="openApiDocumentId"
      @saved="handleOpenApiSaved"
    />
  </div>
</template>

<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh, Search } from '@element-plus/icons-vue'
import { getList as getProjects } from '@/api/codegen/project'
import OpenApiImportDialog from './OpenApiImportDialog.vue'
import {
  createProjectDataSource,
  deleteProjectDataSource,
  getDataSourceCapabilityCatalog,
  getProjectDataSources,
  updateProjectDataSource
} from '@/api/codegen/projectPage'

defineOptions({ name: 'CodegenApiDataSource' })

const projects = ref([])
const sources = ref([])
const catalog = ref({ apiServices: [] })
const total = ref(0)
const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)
const editingId = ref(0)
const openApiVisible = ref(false)
const openApiDocumentId = ref(undefined)
const formRef = ref()

const query = reactive({ projectId: undefined, keyword: '', sourceType: 4, pageNum: 1, pageSize: 20 })
const form = reactive(defaultForm())
const shapes = [
  { value: 1, label: '单条数据' },
  { value: 2, label: '集合' },
  { value: 3, label: '分页数据' },
  { value: 4, label: '选项集合' },
  { value: 5, label: '聚合结果' }
]

const supportedServices = computed(() => (catalog.value.apiServices || [])
  .map(service => ({ ...service, methods: (service.methods || []).filter(isSupportedMethod) }))
  .filter(service => service.methods.length))
const selectedService = computed(() => supportedServices.value.find(item => item.serviceName === form.serviceName))
const currentMethods = computed(() => selectedService.value?.methods || [])
const selectedMethod = computed(() => currentMethods.value.find(item => item.methodName === form.methodName))

const rules = {
  sourceKey: [{ required: true, pattern: /^[A-Za-z][A-Za-z0-9_-]*$/, message: '必须以字母开头，只能包含字母、数字、下划线或短横线', trigger: 'blur' }],
  displayName: [{ required: true, message: '请输入显示名称', trigger: 'blur' }],
  serviceName: [{ required: true, message: '请选择服务', trigger: 'change' }],
  methodName: [{ required: true, message: '请选择方法', trigger: 'change' }],
  paramsJson: [{ validator: validateJson, trigger: 'blur' }]
}

function defaultForm() {
  return {
    projectId: undefined,
    sourceKey: '',
    displayName: '',
    sourceType: 4,
    entityId: null,
    serviceName: '',
    methodName: '',
    dtoName: '',
    dataShape: 1,
    syncMode: 1,
    paramsJson: '{}',
    viewName: null,
    orderNum: 0,
    isEnabled: true
  }
}

function validateJson(_rule, value, callback) {
  try {
    const parsed = JSON.parse(value || '{}')
    if (!parsed || !['object'].includes(typeof parsed)) return callback(new Error('参数必须是 JSON 对象或数组'))
    callback()
  } catch {
    callback(new Error('请输入有效的 JSON'))
  }
}

function isSupportedMethod(method) {
  const routeParams = (method?.parameters || []).filter(item => item.isRouteParameter)
  if (String(method?.httpMethod || 'GET').toUpperCase() !== 'GET' || routeParams.length > 1) return false
  return !routeParams.length || String(method.routePath || '').toLowerCase().endsWith(`/{${routeParams[0].name}}`.toLowerCase())
}

function shapeLabel(value) {
  return shapes.find(item => item.value === Number(value))?.label || value || '-'
}

function openApiStateLabel(value) {
  return ({ 1: '已同步', 2: '新增', 3: '已修改', 4: '待删除', 5: '疑似重命名', 6: '冲突', 7: '本地保留', 8: '缺失' })[Number(value)] || '未知'
}

function openApiStateType(value) {
  return ({ 1: 'success', 2: 'success', 3: 'warning', 4: 'danger', 5: 'warning', 6: 'danger', 7: 'info', 8: 'danger' })[Number(value)] || 'info'
}

function methodLabel(method) {
  return `${method.methodName} · ${shapeLabel(method.dataShape)} · ${method.routePath}`
}

function methodForRow(row) {
  return (catalog.value.apiServices || [])
    .find(item => item.serviceName === row.serviceName)?.methods
    ?.find(item => item.methodName === row.methodName)
}

async function loadProjects() {
  projects.value = await getProjects({}) || []
  if (!query.projectId && projects.value.length) query.projectId = projects.value[0].id
}

async function loadCatalog() {
  catalog.value = query.projectId
    ? (await getDataSourceCapabilityCatalog(query.projectId) || { apiServices: [] })
    : { apiServices: [] }
}

async function loadSources() {
  if (!query.projectId) {
    sources.value = []
    total.value = 0
    return
  }
  loading.value = true
  try {
    const result = await getProjectDataSources(query)
    sources.value = result?.items || []
    total.value = result?.total || 0
  } finally {
    loading.value = false
  }
}

async function handleProjectChange() {
  query.pageNum = 1
  await Promise.all([loadCatalog(), loadSources()])
}

async function refreshCatalog() {
  await loadCatalog()
  ElMessage.success('API 能力目录已刷新')
}

function resetQuery() {
  query.keyword = ''
  query.pageNum = 1
  loadSources()
}

function openOpenApiImport(documentId) {
  openApiDocumentId.value = documentId || undefined
  openApiVisible.value = true
}

async function handleOpenApiSaved() {
  await Promise.all([loadCatalog(), loadSources()])
}

async function openCreate() {
  editingId.value = 0
  Object.assign(form, defaultForm(), { projectId: query.projectId })
  if (!catalog.value.apiServices?.length) await loadCatalog()
  if (supportedServices.value.length) {
    form.serviceName = supportedServices.value[0].serviceName
    handleServiceChange(form.serviceName)
  }
  dialogVisible.value = true
}

async function openEdit(row) {
  editingId.value = row.id
  Object.assign(form, defaultForm(), row, { projectId: query.projectId, sourceType: 4, entityId: row.entityId || null })
  if (!catalog.value.apiServices?.length) await loadCatalog()
  dialogVisible.value = true
}

function handleServiceChange() {
  const method = currentMethods.value[0]
  form.methodName = method?.methodName || ''
  applyMethod(method, true)
}

function handleMethodChange() {
  applyMethod(selectedMethod.value, true)
}

function applyMethod(method, resetParams) {
  form.dtoName = method?.dtoName || ''
  form.dataShape = method?.dataShape || 1
  if (!resetParams) return
  const params = {}
  for (const item of method?.parameters || []) {
    if (item.isRouteParameter) params[item.name] = { $ref: `$route.${item.name}` }
  }
  form.paramsJson = JSON.stringify(params, null, 2)
  if (!form.sourceKey && method) {
    const service = String(form.serviceName || '').replace(/Service$/i, '')
    const action = String(method.methodName || '').replace(/Async$/i, '').replace(/^Get/i, '')
    form.sourceKey = lowerCamel(`${service} ${action}`)
  }
  if (!form.displayName && method) form.displayName = `${selectedService.value?.displayName || form.serviceName} ${method.methodName}`
}

function lowerCamel(value) {
  const parts = String(value || '').split(/[^A-Za-z0-9]+/).filter(Boolean)
  if (!parts.length) return 'apiSource'
  const pascal = parts.map(item => item.charAt(0).toUpperCase() + item.slice(1)).join('')
  return pascal.charAt(0).toLowerCase() + pascal.slice(1)
}

async function saveSource() {
  await formRef.value.validate()
  saving.value = true
  try {
    const payload = { ...form, projectId: query.projectId, sourceType: 4, viewName: null }
    const result = editingId.value
      ? await updateProjectDataSource(editingId.value, payload)
      : await createProjectDataSource(payload)
    const impact = Number(result?.affectedPageCount || 0)
    ElMessage.success(editingId.value && impact ? `保存成功，${impact} 个引用页面需要复核` : '保存成功')
    dialogVisible.value = false
    await loadSources()
  } finally {
    saving.value = false
  }
}

async function removeSource(row) {
  await ElMessageBox.confirm(`确定删除 API 数据源“${row.displayName}”吗？引用它的页面会被标记为失效。`, '删除确认', { type: 'warning' })
  const result = await deleteProjectDataSource(row.id)
  ElMessage.success(result?.affectedPageCount ? `已删除，影响 ${result.affectedPageCount} 个页面` : '已删除')
  await loadSources()
}

onMounted(async () => {
  await loadProjects()
  await Promise.all([loadCatalog(), loadSources()])
})
</script>

<style scoped>
.toolbar { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-bottom: 16px; }
.source-alert { margin-bottom: 16px; }
.method-summary { margin: 0 0 18px 110px; width: calc(100% - 110px); }
.form-tip { color: var(--el-text-color-secondary); font-size: 12px; line-height: 20px; }
.route-text { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; }
.sync-state { margin-left: 6px; }
.el-pagination { margin-top: 16px; justify-content: flex-end; }
</style>
