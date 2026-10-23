<template>
  <el-dialog
    :model-value="modelValue"
    title="导入 OpenAPI 数据源"
    width="1280px"
    top="5vh"
    destroy-on-close
    @update:model-value="$emit('update:modelValue', $event)"
  >
    <el-alert
      title="OpenAPI 会导入 API 契约、请求/返回模型、嵌套对象和数组关系，作为页面字段绑定元数据；不会生成后台、DTO、数据库或菜单代码。"
      type="info"
      :closable="false"
      show-icon
      class="mb-16"
    />

    <el-form :model="form" label-width="110px" class="import-form">
      <el-row :gutter="16">
        <el-col :span="12">
          <el-form-item label="已有文档">
            <el-select v-model="form.documentId" clearable filterable placeholder="新建文档" style="width: 100%" @change="selectDocument">
              <el-option v-for="item in documents" :key="item.id" :label="item.name" :value="item.id">
                <span>{{ item.name }}</span>
                <el-text type="info" class="option-meta">{{ item.openapiVersion }}</el-text>
              </el-option>
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="目标模块" required>
            <el-select v-model="form.moduleId" filterable style="width: 100%" :disabled="Boolean(form.documentId)">
              <el-option
                v-for="item in modules"
                :key="item.id"
                :label="item.moduleDescription ? `${item.moduleDescription} (${item.moduleName})` : item.moduleName"
                :value="item.id"
              />
            </el-select>
          </el-form-item>
        </el-col>
      </el-row>

      <el-form-item label="文档名称" required>
        <el-input v-model="form.name" maxlength="200" show-word-limit placeholder="例如：货运订单 API" />
      </el-form-item>

      <el-form-item label="导入方式">
        <el-radio-group v-model="sourceMode" @change="resetPreviewState">
          <el-radio-button value="url">Swagger 地址</el-radio-button>
          <el-radio-button value="content">文档内容</el-radio-button>
          <el-radio-button v-if="form.documentId" value="stored">使用已保存版本</el-radio-button>
        </el-radio-group>
      </el-form-item>

      <el-form-item v-if="sourceMode === 'url'" label="OpenAPI URL" required>
        <el-input v-model="form.sourceUrl" placeholder="http://localhost:5000/swagger/v1/swagger.json" clearable />
      </el-form-item>
      <el-form-item v-else-if="sourceMode === 'content'" label="JSON / YAML" required>
        <div class="content-input">
          <el-upload :auto-upload="false" :show-file-list="false" accept=".json,.yaml,.yml" :on-change="readFile">
            <el-button>选择 Swagger/OpenAPI 文件</el-button>
          </el-upload>
          <el-input
            v-model="form.documentContent"
            type="textarea"
            :rows="9"
            spellcheck="false"
            placeholder="粘贴 OpenAPI 3.x JSON 或 YAML"
          />
        </div>
      </el-form-item>
    </el-form>

    <div class="preview-actions">
      <el-button type="primary" :loading="previewing" @click="runPreview">解析并预览变更</el-button>
      <el-text v-if="preview" type="success">
        已识别 {{ preview.operations.length }} 个 API、{{ preview.schemas.length }} 个 DTO
      </el-text>
    </div>

    <template v-if="preview">
      <el-alert
        v-for="warning in preview.warnings || []"
        :key="warning"
        :title="warning"
        type="warning"
        :closable="false"
        show-icon
        class="mb-8"
      />

      <el-tabs class="preview-tabs">
        <el-tab-pane :label="`变更 (${preview.changes.length})`">
          <el-table :data="preview.changes" border max-height="400">
            <el-table-column label="状态" width="110">
              <template #default="{ row }"><el-tag :type="stateType(row.state)">{{ stateLabel(row.state) }}</el-tag></template>
            </el-table-column>
            <el-table-column label="对象" min-width="220">
              <template #default="{ row }">
                <span v-if="row.parentName">{{ row.parentName }}.</span>{{ row.targetName }}
              </template>
            </el-table-column>
            <el-table-column label="类型" prop="targetType" width="110" />
            <el-table-column label="说明" prop="description" min-width="330" show-overflow-tooltip />
            <el-table-column label="待删除处理" width="300">
              <template #default="{ row }">
                <el-radio-group v-if="Number(row.state) === 4" v-model="removalDecisions[row.changeKey]" size="small">
                  <el-radio-button value="pending">暂不处理</el-radio-button>
                  <el-radio-button value="remove">确认删除</el-radio-button>
                  <el-radio-button value="detach">本地保留</el-radio-button>
                </el-radio-group>
                <el-text v-else type="info">-</el-text>
              </template>
            </el-table-column>
          </el-table>
          <el-empty v-if="!preview.changes.length" description="契约没有变化" :image-size="70" />
          <el-alert
            v-if="pendingRemovalCount"
            class="mt-12"
            type="warning"
            :closable="false"
            show-icon
            title="确认删除会软删除未被引用的元数据；本地保留会解除 OpenAPI 所有权，并继续允许页面使用现有字段或 API 契约。"
          />
        </el-tab-pane>

        <el-tab-pane :label="`API (${preview.operations.length})`">
          <el-table
            :data="preview.operations"
            border
            highlight-current-row
            max-height="300"
            row-key="operationKey"
            @row-click="selectOperation"
          >
            <el-table-column label="查看" width="66">
              <template #default="{ row }">
                <el-button link type="primary" @click.stop="selectOperation(row)">{{ selectedOperation?.operationKey === row.operationKey ? '当前' : '详情' }}</el-button>
              </template>
            </el-table-column>
            <el-table-column label="状态" width="105">
              <template #default="{ row }"><el-tag :type="stateType(row.syncState)">{{ stateLabel(row.syncState) }}</el-tag></template>
            </el-table-column>
            <el-table-column label="HTTP" prop="httpMethod" width="80" />
            <el-table-column label="路由" prop="routePath" min-width="260" />
            <el-table-column label="OperationId" prop="operationId" min-width="180" />
            <el-table-column label="请求 DTO" prop="requestDtoName" min-width="170" />
            <el-table-column label="返回 DTO" prop="responseDtoName" min-width="170" />
          </el-table>

          <div v-if="selectedOperation" class="operation-detail">
            <el-descriptions :column="3" border>
              <el-descriptions-item label="API" :span="2">
                <el-tag size="small">{{ selectedOperation.httpMethod }}</el-tag>
                <code class="route-code">{{ selectedOperation.routePath }}</code>
              </el-descriptions-item>
              <el-descriptions-item label="数据形态">{{ dataShapeLabel(selectedOperation.dataShape) }}</el-descriptions-item>
              <el-descriptions-item label="摘要" :span="3">{{ selectedOperation.summary || '-' }}</el-descriptions-item>
              <el-descriptions-item label="说明" :span="3">{{ selectedOperation.description || '-' }}</el-descriptions-item>
              <el-descriptions-item label="请求根">{{ selectedOperation.requestDtoName || '-' }}</el-descriptions-item>
              <el-descriptions-item label="请求格式">{{ selectedContentTypes.request || '-' }}</el-descriptions-item>
              <el-descriptions-item label="返回根">{{ selectedOperation.responseEnvelopeDtoName || selectedOperation.responseDtoName || '-' }}</el-descriptions-item>
              <el-descriptions-item label="响应格式" :span="3">{{ selectedContentTypes.response || '-' }}</el-descriptions-item>
            </el-descriptions>

            <el-divider content-position="left">参数</el-divider>
            <el-table v-if="selectedParameters.length" :data="selectedParameters" size="small" border>
              <el-table-column label="名称" prop="name" min-width="150" />
              <el-table-column label="位置" prop="location" width="100" />
              <el-table-column label="类型" min-width="160">
                <template #default="{ row }">{{ row.dataType || schemaName(row.schemaKey) || 'object' }}{{ row.format ? ` (${row.format})` : '' }}</template>
              </el-table-column>
              <el-table-column label="必填" width="75"><template #default="{ row }">{{ row.isRequired ? '是' : '否' }}</template></el-table-column>
              <el-table-column label="说明" prop="description" min-width="260" show-overflow-tooltip />
            </el-table>
            <el-empty v-else description="没有参数" :image-size="56" />

            <el-row :gutter="16" class="schema-roots">
              <el-col :span="12">
                <div class="schema-panel">
                  <div class="schema-title">请求模型 · {{ selectedOperation.requestDtoName || '无' }}</div>
                  <el-tree v-if="requestSchemaTree.length" :data="requestSchemaTree" node-key="id" default-expand-all :expand-on-click-node="false">
                    <template #default="{ data }">
                      <span class="schema-node">
                        <span>{{ data.label }}</span>
                        <el-tag v-if="data.kind" size="small" type="info">{{ data.kind }}</el-tag>
                        <el-tag v-if="data.required" size="small" type="danger">必填</el-tag>
                      </span>
                    </template>
                  </el-tree>
                  <el-empty v-else description="无请求模型" :image-size="56" />
                </div>
              </el-col>
              <el-col :span="12">
                <div class="schema-panel">
                  <div class="schema-title">响应模型 · {{ selectedOperation.responseEnvelopeDtoName || selectedOperation.responseDtoName || '无' }}</div>
                  <el-tree v-if="responseSchemaTree.length" :data="responseSchemaTree" node-key="id" default-expand-all :expand-on-click-node="false">
                    <template #default="{ data }">
                      <span class="schema-node">
                        <span>{{ data.label }}</span>
                        <el-tag v-if="data.kind" size="small" type="info">{{ data.kind }}</el-tag>
                        <el-tag v-if="data.required" size="small" type="danger">必填</el-tag>
                      </span>
                    </template>
                  </el-tree>
                  <el-empty v-else description="无响应模型" :image-size="56" />
                </div>
              </el-col>
            </el-row>
          </div>
        </el-tab-pane>

        <el-tab-pane :label="`DTO (${preview.schemas.length})`">
          <el-table :data="preview.schemas" border max-height="360" row-key="schemaKey">
            <el-table-column type="expand">
              <template #default="{ row }">
                <el-table :data="row.members" size="small" border>
                  <el-table-column label="状态" width="100">
                    <template #default="{ row: member }"><el-tag :type="stateType(member.syncState)">{{ stateLabel(member.syncState) }}</el-tag></template>
                  </el-table-column>
                  <el-table-column label="JSON 字段" prop="jsonName" min-width="150" />
                  <el-table-column label="属性" prop="memberName" min-width="150" />
                  <el-table-column label="类型" min-width="210">
                    <template #default="{ row: member }">{{ member.targetDtoName || member.dataType || member.memberKind }}</template>
                  </el-table-column>
                  <el-table-column label="必填" width="75">
                    <template #default="{ row: member }">{{ member.isRequired ? '是' : '否' }}</template>
                  </el-table-column>
                </el-table>
              </template>
            </el-table-column>
            <el-table-column label="状态" width="105">
              <template #default="{ row }"><el-tag :type="stateType(row.syncState)">{{ stateLabel(row.syncState) }}</el-tag></template>
            </el-table-column>
            <el-table-column label="DTO" prop="dtoName" min-width="220" />
            <el-table-column label="Schema" prop="schemaName" min-width="220" />
            <el-table-column label="字段数" width="90">
              <template #default="{ row }">{{ row.members.length }}</template>
            </el-table-column>
            <el-table-column label="用途" width="150"><template #default>API 字段元数据</template></el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>

      <el-checkbox v-model="regenerateAffectedPages" class="removal-option">
        应用后自动同步并增量生成“自动同步”的受影响复杂页面（手动同步页面只提示，不自动修改）
      </el-checkbox>

      <el-card v-if="applyResult" shadow="never" class="apply-result">
        <template #header>
          <div class="result-header">
            <span>导入完成</span>
            <el-tag type="success">{{ applyResult.dataSourceIds?.length || 0 }} 个 API 数据源</el-tag>
            <el-tag>{{ applyResult.entityIds?.length || 0 }} 个模型</el-tag>
            <el-tag v-if="applyResult.conflictCount" type="danger">{{ applyResult.conflictCount }} 个删除冲突</el-tag>
          </div>
        </template>
        <el-table v-if="pageResults.length" :data="pageResults" size="small" border>
          <el-table-column label="受影响页面" min-width="220">
            <template #default="{ row }">{{ row.pageName || row.pageKey || row.pageId }}</template>
          </el-table-column>
          <el-table-column label="同步策略" width="100"><template #default="{ row }">{{ Number(row.syncMode) === 2 ? '手动' : '自动' }}</template></el-table-column>
          <el-table-column label="处理结果" width="120"><template #default="{ row }"><el-tag :type="pageResultType(row.status)">{{ pageResultLabel(row.status) }}</el-tag></template></el-table-column>
          <el-table-column label="说明" prop="message" min-width="360" show-overflow-tooltip />
        </el-table>
        <el-empty v-else description="没有页面受到影响" :image-size="58" />
      </el-card>
    </template>

    <template #footer>
      <el-button @click="$emit('update:modelValue', false)">{{ applyResult ? '关闭' : '取消' }}</el-button>
      <el-button v-if="applyResult" type="primary" @click="runPreview">重新预览</el-button>
      <el-button v-else type="primary" :disabled="!preview || hasConflicts" :loading="applying" @click="applyChanges">
        确认应用变更
      </el-button>
    </template>
  </el-dialog>
</template>

<script setup>
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getByProjectId } from '@/api/codegen/projectModule'
import {
  applyOpenApiImport,
  generate,
  getOpenApiDocuments,
  previewOpenApiImport,
  synchronize
} from '@/api/codegen/projectPage'

const props = defineProps({
  modelValue: { type: Boolean, default: false },
  projectId: { type: [Number, String], default: undefined },
  documentId: { type: [Number, String], default: undefined }
})
const emit = defineEmits(['update:modelValue', 'saved'])

const documents = ref([])
const modules = ref([])
const preview = ref(null)
const sourceMode = ref('url')
const previewing = ref(false)
const applying = ref(false)
const selectedOperation = ref(null)
const regenerateAffectedPages = ref(true)
const applyResult = ref(null)
const pageResults = ref([])
const removalDecisions = reactive({})
const form = reactive(defaultForm())

const hasConflicts = computed(() => (preview.value?.changes || []).some(item => Number(item.state) === 6))
const pendingRemovalCount = computed(() => (preview.value?.changes || []).filter(item => Number(item.state) === 4).length)
const selectedParameters = computed(() => parseJson(selectedOperation.value?.parametersJson, []))
const selectedContract = computed(() => parseJson(selectedOperation.value?.contractJson, {}))
const selectedContentTypes = computed(() => ({
  request: (selectedContract.value.requestContentTypes || []).join(', '),
  response: (selectedContract.value.responseContentTypes || []).join(', ')
}))
const requestSchemaTree = computed(() => buildSchemaTree(selectedOperation.value?.requestSchemaKey, 'request'))
const responseSchemaTree = computed(() => buildSchemaTree(
  selectedOperation.value?.responseEnvelopeSchemaKey || selectedOperation.value?.responseSchemaKey,
  'response'
))

function defaultForm() {
  return { documentId: undefined, moduleId: undefined, name: '', sourceUrl: '', documentContent: '' }
}

watch(
  () => [props.modelValue, props.projectId, props.documentId],
  async ([visible]) => {
    if (!visible || !props.projectId) return
    Object.assign(form, defaultForm())
    resetPreviewState()
    regenerateAffectedPages.value = true
    const [documentList, moduleList] = await Promise.all([
      getOpenApiDocuments(props.projectId),
      getByProjectId(props.projectId)
    ])
    documents.value = documentList || []
    modules.value = moduleList || []
    form.moduleId = modules.value[0]?.id
    if (props.documentId) {
      form.documentId = props.documentId
      selectDocument(form.documentId)
    }
  },
  { immediate: true }
)

function selectDocument(documentId) {
  resetPreviewState()
  const document = documents.value.find(item => String(item.id) === String(documentId))
  if (!document) {
    Object.assign(form, defaultForm(), { moduleId: modules.value[0]?.id })
    sourceMode.value = 'url'
    return
  }
  form.documentId = document.id
  form.moduleId = document.moduleId
  form.name = document.name
  form.sourceUrl = document.sourceUrl || ''
  form.documentContent = ''
  sourceMode.value = document.sourceUrl ? 'url' : 'stored'
}

async function readFile(file) {
  form.documentContent = await file.raw.text()
  sourceMode.value = 'content'
  resetPreviewState()
}

function buildPayload() {
  return {
    // Snowflake IDs are serialized as strings by the backend. Keep them as-is so
    // JavaScript never rounds values larger than Number.MAX_SAFE_INTEGER.
    projectId: props.projectId,
    moduleId: form.moduleId,
    documentId: form.documentId || null,
    name: form.name.trim(),
    sourceUrl: sourceMode.value === 'url' ? form.sourceUrl.trim() : null,
    documentContent: sourceMode.value === 'content' ? form.documentContent : null
  }
}

function validateInput() {
  if (!form.moduleId) throw new Error('请选择目标模块')
  if (!form.name.trim()) throw new Error('请输入文档名称')
  if (sourceMode.value === 'url' && !form.sourceUrl.trim()) throw new Error('请输入 OpenAPI URL')
  if (sourceMode.value === 'content' && !form.documentContent.trim()) throw new Error('请粘贴或选择 OpenAPI 文档')
}

function resetPreviewState() {
  preview.value = null
  selectedOperation.value = null
  applyResult.value = null
  pageResults.value = []
  for (const key of Object.keys(removalDecisions)) delete removalDecisions[key]
}

function initializePreviewState(value) {
  selectedOperation.value = value?.operations?.[0] || null
  applyResult.value = null
  pageResults.value = []
  for (const key of Object.keys(removalDecisions)) delete removalDecisions[key]
  for (const change of value?.changes || []) {
    if (Number(change.state) === 4) removalDecisions[change.changeKey] = 'pending'
  }
}

function selectOperation(operation) {
  selectedOperation.value = operation
}

function parseJson(value, fallback) {
  if (!value) return fallback
  try {
    const parsed = typeof value === 'string' ? JSON.parse(value) : value
    return parsed ?? fallback
  } catch {
    return fallback
  }
}

function schemaName(schemaKey) {
  return (preview.value?.schemas || []).find(item => item.schemaKey === schemaKey)?.dtoName
}

function buildSchemaTree(schemaKey, prefix) {
  if (!schemaKey) return []
  const schemas = new Map((preview.value?.schemas || []).map(item => [item.schemaKey, item]))
  const root = schemas.get(schemaKey)
  if (!root) return []
  return [buildSchemaNode(root, schemas, new Set(), `${prefix}:${schemaKey}`, 0)]
}

function buildSchemaNode(schema, schemas, path, id, depth) {
  const nextPath = new Set(path)
  const cyclic = nextPath.has(schema.schemaKey)
  nextPath.add(schema.schemaKey)
  const children = cyclic || depth >= 10
    ? []
    : (schema.members || []).map((member, index) => {
        const target = member.targetSchemaKey ? schemas.get(member.targetSchemaKey) : null
        const type = member.targetDtoName || member.dataType || member.memberKind || 'object'
        return {
          id: `${id}:${member.memberKey || member.jsonName}:${index}`,
          label: `${member.jsonName} · ${member.memberName}`,
          kind: `${member.isCollection ? 'array<' : ''}${type}${member.isCollection ? '>' : ''}`,
          required: member.isRequired,
          children: target
            ? [buildSchemaNode(target, schemas, nextPath, `${id}:${member.memberKey || index}:target`, depth + 1)]
            : []
        }
      })
  return {
    id,
    label: `${schema.dtoName} (${schema.schemaName})${cyclic ? ' · 循环引用' : ''}`,
    kind: 'DTO',
    required: false,
    children
  }
}

async function runPreview() {
  try {
    validateInput()
  } catch (error) {
    ElMessage.warning(error.message)
    return
  }
  previewing.value = true
  try {
    const value = await previewOpenApiImport(buildPayload())
    preview.value = value
    initializePreviewState(value)
    if (hasConflicts.value) ElMessage.warning('预览中存在冲突，需先处理后才能应用')
  } finally {
    previewing.value = false
  }
}

async function applyChanges() {
  if (!preview.value) return
  const removeChangeKeys = Object.entries(removalDecisions)
    .filter(([, action]) => action === 'remove')
    .map(([key]) => key)
  const detachChangeKeys = Object.entries(removalDecisions)
    .filter(([, action]) => action === 'detach')
    .map(([key]) => key)
  if (removeChangeKeys.length) {
    await ElMessageBox.confirm(
      `将确认删除 ${removeChangeKeys.length} 项未被保护引用的字段、DTO 或 API。被引用的项目会转为冲突并保留，是否继续？`,
      '确认 OpenAPI 删除',
      { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '返回检查' }
    )
  }
  applying.value = true
  try {
    const result = await applyOpenApiImport({
      ...buildPayload(),
      expectedDocumentHash: preview.value.documentHash,
      expectedChangeSetHash: preview.value.changeSetHash,
      applyRemovals: false,
      removeChangeKeys,
      detachChangeKeys
    })
    applyResult.value = result
    await processAffectedPages(result)
    ElMessage.success(`已导入 ${result.entityIds?.length || 0} 个 API 模型元数据和 ${result.dataSourceIds?.length || 0} 个 API 数据源`)
    emit('saved', result)
  } finally {
    applying.value = false
  }
}

async function processAffectedPages(result) {
  const affectedPages = result?.affectedPages?.length
    ? result.affectedPages
    : (result?.affectedPageIds || []).map(pageId => ({ pageId, pageName: '', pageKey: '', syncMode: 1 }))
  pageResults.value = affectedPages.map(page => ({ ...page, status: 'pending', message: '等待同步' }))
  if (!regenerateAffectedPages.value) {
    pageResults.value.forEach(item => Object.assign(item, { status: 'pending', message: '已标记需要同步，请在复杂页面管理中处理' }))
    return
  }

  for (const item of pageResults.value) {
    if (Number(item.syncMode) === 2) {
      Object.assign(item, { status: 'manual', message: '页面使用手动同步策略，未自动修改或生成' })
      continue
    }
    try {
      const syncResult = await synchronize(item.pageId)
      if (!syncResult?.success) {
        Object.assign(item, { status: 'failed', message: syncResult?.error || '页面同步失败' })
        continue
      }
      const reviewCount = Number(syncResult.invalidBindings || 0) + Number(syncResult.reviewBindings || 0)
      if (reviewCount > 0) {
        Object.assign(item, {
          status: 'review',
          message: `同步后有 ${syncResult.invalidBindings || 0} 个失效绑定、${syncResult.reviewBindings || 0} 个待复核绑定，已停止自动生成`
        })
        continue
      }
      const generationResult = await generate(item.pageId, props.projectId, true)
      if (!generationResult?.success || generationResult?.hasConflicts) {
        Object.assign(item, {
          status: generationResult?.hasConflicts ? 'review' : 'failed',
          message: generationResult?.error || (generationResult?.hasConflicts ? '生成区域存在外部修改冲突' : '增量生成失败')
        })
        continue
      }
      Object.assign(item, {
        status: 'generated',
        message: `已同步并增量生成，新增 ${syncResult.addedBindings || 0} 个绑定`
      })
    } catch (error) {
      Object.assign(item, { status: 'failed', message: error?.message || '同步或生成失败' })
    }
  }
}

function dataShapeLabel(value) {
  return ({ 1: '单条数据', 2: '集合', 3: '分页数据', 4: '选项集合', 5: '聚合结果' })[Number(value)] || value || '-'
}

function pageResultLabel(value) {
  return ({ pending: '待手动同步', manual: '手动处理', review: '需要复核', generated: '已生成', failed: '失败' })[value] || value
}

function pageResultType(value) {
  return ({ pending: 'info', manual: 'warning', review: 'warning', generated: 'success', failed: 'danger' })[value] || 'info'
}

function stateLabel(value) {
  return ({ 1: '正常', 2: '新增', 3: '修改', 4: '待删除', 5: '疑似重命名', 6: '冲突', 7: '已移除', 8: '缺失' })[Number(value)] || value
}

function stateType(value) {
  return ({ 1: 'success', 2: 'success', 3: 'warning', 4: 'danger', 5: 'warning', 6: 'danger', 7: 'info', 8: 'danger' })[Number(value)] || 'info'
}
</script>

<style scoped>
.mb-16 { margin-bottom: 16px; }
.mb-8 { margin-bottom: 8px; }
.mt-12 { margin-top: 12px; }
.option-meta { float: right; margin-left: 16px; }
.content-input { display: grid; gap: 10px; width: 100%; }
.preview-actions { display: flex; align-items: center; gap: 16px; margin: 4px 0 16px 110px; }
.preview-tabs { border-top: 1px solid var(--el-border-color-lighter); margin-top: 8px; }
.removal-option { margin-top: 16px; }
.operation-detail { margin-top: 16px; }
.route-code { margin-left: 10px; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; }
.schema-roots { margin-top: 16px; }
.schema-panel { min-height: 230px; border: 1px solid var(--el-border-color); padding: 12px; }
.schema-title { margin-bottom: 10px; font-weight: 600; }
.schema-node { display: inline-flex; align-items: center; gap: 8px; min-width: 0; }
.apply-result { margin-top: 16px; }
.result-header { display: flex; align-items: center; gap: 10px; }
</style>
