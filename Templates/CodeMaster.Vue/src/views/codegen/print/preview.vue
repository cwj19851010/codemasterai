<template>
  <div class="app-container print-preview-page">
    <!-- 模板信息 + 参数 + 工具栏 -->
    <el-card shadow="never" class="toolbar-card">
      <div class="toolbar">
        <div class="meta">
          <span class="title">{{ templateName || '打印预览' }}</span>
          <el-tag size="small" type="success">HTML 预览</el-tag>
          <el-tag v-if="paperLabel" size="small" type="info">{{ paperLabel }}</el-tag>
          <el-tag v-if="layoutMode === 'absolute'" size="small" type="warning">套打</el-tag>
          <el-tag v-if="templateStatus === 1" size="small" type="danger">已停用</el-tag>
        </div>

        <div class="actions">
          <div v-if="rendered" class="copies">
            <span class="copies-label">份数</span>
            <el-input-number v-model="copies" :min="1" :max="20" size="small" style="width: 90px" />
          </div>
          <el-button v-if="paramFields.length" link type="primary" @click="paramPanelVisible = !paramPanelVisible">
            {{ paramPanelVisible ? '收起参数' : '修改参数' }}
          </el-button>
          <el-button :loading="rendering" icon="Refresh" @click="render">刷新</el-button>
          <el-button type="primary" icon="Printer" :disabled="!rendered" @click="doPrint">打印</el-button>
          <el-dropdown :disabled="!rendered || exporting" @command="doExport">
            <el-button type="success" :loading="exporting">
              导出<el-icon class="el-icon--right"><ArrowDown /></el-icon>
            </el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="pdf">PDF</el-dropdown-item>
                <el-dropdown-item command="excel" :disabled="layoutMode === 'absolute'">Excel</el-dropdown-item>
                <el-dropdown-item command="word" :disabled="layoutMode === 'absolute'">Word</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </div>

      <!-- 运行参数表单（模板声明了 params 时显示） -->
      <el-collapse-transition>
        <div v-show="paramPanelVisible && paramFields.length" class="param-form">
          <el-form :model="paramForm" label-width="120px" inline>
            <el-form-item
              v-for="p in paramFields"
              :key="p.name"
              :label="p.label || p.name"
              :required="p.required"
            >
              <el-input
                v-if="p.type !== 'dateTime'"
                v-model="paramForm[p.name]"
                :placeholder="`请输入${p.label || p.name}`"
                :clearable="!p.required"
                style="width: 200px"
                @keyup.enter="render"
              />
              <el-date-picker
                v-else
                v-model="paramForm[p.name]"
                type="datetime"
                value-format="YYYY-MM-DD HH:mm:ss"
                placeholder="选择日期时间"
              />
            </el-form-item>
            <el-form-item>
              <el-button type="primary" :loading="rendering" @click="render">查询并渲染</el-button>
            </el-form-item>
          </el-form>
        </div>
      </el-collapse-transition>
    </el-card>

    <!-- 状态提示 -->
    <el-alert
      v-if="loadError"
      :title="loadError"
      type="error"
      show-icon
      :closable="false"
      class="status-alert"
    >
      <template #default>
        <div v-if="permissionDenied" class="perm-tip">
          当前账号没有打印查看/导出权限（print:template:view / print:template:export），请联系管理员分配。
        </div>
        <el-button link type="primary" @click="loadTemplate">重试</el-button>
      </template>
    </el-alert>
    <el-alert
      v-else-if="renderError"
      :title="renderError"
      type="error"
      show-icon
      :closable="false"
      class="status-alert"
    />

    <!-- 报表 iframe 预览 -->
    <el-card v-if="!loadError" shadow="never" class="preview-card" v-loading="rendering" element-loading-text="正在取数渲染…">
      <iframe
        v-show="rendered"
        ref="previewFrame"
        class="preview-frame"
        title="print-preview"
        :srcdoc="previewHtml"
      />
      <el-empty v-if="!rendered && !rendering && !renderError" description="待渲染 — 提供参数后点击「查询并渲染」" />
    </el-card>
  </div>
</template>

<script setup>
/**
 * 通用打印报表运行页（OpenSpec add-print-template-designer 任务 5.5）
 * 路由：/print-preview/:templateId?{paramName}={value}...
 * - 按 templateId 从数据库加载模板 JSON，取真实数据渲染整册 HTML（iframe srcdoc）
 * - 模板声明的 dataSources.params 缺失时弹出参数表单
 * - 浏览器直接打印 / PDF / Excel / Word 导出（base64 → blob 下载）
 * - 加载失败、模板停用、权限不足均有显式提示
 * 不为每个模板生成独立 Vue 文件 —— 所有模板共用本页。
 */
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { ArrowDown } from '@element-plus/icons-vue'
import {
  getById as getTemplateById,
  renderHtml as apiRenderHtml,
  exportPdf as apiExportPdf,
  exportExcel as apiExportExcel,
  exportWord as apiExportWord
} from '@/api/codegen/printTemplate'

defineOptions({ name: 'PrintPreview' })

const route = useRoute()

const templateId = computed(() => String(route.params.templateId || ''))
const templateName = ref('')
const templateStatus = ref(0)
const layoutMode = ref('flow')
const paperLabel = ref('')
const paramFields = ref([]) // [{ name, label, type, required, defaultValue }]
const paramPanelVisible = ref(false)
const paramForm = reactive({})

const loadingTemplate = ref(false)
const loadError = ref('')
const permissionDenied = ref(false)

const rendering = ref(false)
const renderError = ref('')
const rendered = ref(false)
const previewHtml = ref('')
const previewFrame = ref(null)
const exporting = ref(false)

// ---------- 模板加载 ----------
async function loadTemplate() {
  loadError.value = ''
  permissionDenied.value = false
  if (!templateId.value) {
    loadError.value = '缺少 templateId 路由参数'
    return
  }
  loadingTemplate.value = true
  try {
    const tpl = await getTemplateById(templateId.value)
    templateName.value = tpl.templateName || ''
    templateStatus.value = tpl.status ?? 0
    layoutMode.value = tpl.layoutMode || 'flow'
    paperLabel.value = [
      tpl.paperSize === 'CUSTOM' ? `${tpl.paperWidth || '?'}×${tpl.paperHeight || '?'}mm` : tpl.paperSize,
      tpl.orientation === 'landscape' ? '横向' : '纵向'
    ].join(' · ')

    parseParams(tpl.templateJson)
    fillParamsFromQuery()

    if (templateStatus.value === 1) {
      renderError.value = `模板「${templateName.value}」已停用，渲染结果可能不符合预期`
    }

    // 必填参数齐备（或模板未声明参数）→ 直接渲染；否则展开参数表单
    const missing = paramFields.value.filter(p => p.required && !paramForm[p.name])
    if (missing.length === 0) {
      await render()
    } else {
      paramPanelVisible.value = true
    }
  } catch (error) {
    loadError.value = `模板加载失败：${error?.response?.data?.message || error?.message || '未知错误'}`
    permissionDenied.value = error?.response?.status === 403
  } finally {
    loadingTemplate.value = false
  }
}

/** 解析模板 JSON 中声明的运行时参数（dataSources.params，v1/v2 通用） */
function parseParams(templateJson) {
  paramFields.value = []
  try {
    const json = JSON.parse(templateJson || '{}')
    const params = json?.dataSources?.params || json?.params || []
    if (Array.isArray(params)) {
      paramFields.value = params.map(p => ({
        name: p.name,
        label: p.label || p.name,
        type: p.type || 'string',
        required: !!p.required,
        defaultValue: p.defaultValue ?? ''
      }))
    }
  } catch {
    // 模板 JSON 解析失败不阻塞页面 —— 渲染接口会给出具体报错
  }
}

/** 路由 query 中与参数同名的键作为初值；未提供的用 defaultValue */
function fillParamsFromQuery() {
  paramFields.value.forEach(p => {
    const fromQuery = route.query[p.name]
    paramForm[p.name] = fromQuery !== undefined && fromQuery !== null && fromQuery !== ''
      ? String(fromQuery)
      : (paramForm[p.name] ?? (p.defaultValue || ''))
  })
  // id 主键参数别名：首个声明参数未显式提供时以 query.id 兜底
  // （生成页打印按钮统一传 id，无需感知模板参数命名）
  const first = paramFields.value[0]
  const qid = route.query.id
  if (first && route.query[first.name] === undefined && qid !== undefined && qid !== null && qid !== '') {
    paramForm[first.name] = String(qid)
  }
}

// ---------- 渲染 ----------
async function render() {
  renderError.value = ''
  rendering.value = true
  try {
    const parameters = {}
    paramFields.value.forEach(p => {
      const v = paramForm[p.name]
      if (v !== undefined && v !== null && String(v).trim() !== '') parameters[p.name] = String(v).trim()
    })
    // 路由 query 中的额外参数也一并透传（模板未声明但调用方传入）
    Object.entries(route.query).forEach(([k, v]) => {
      if (parameters[k] === undefined && v !== undefined && v !== null && v !== '') parameters[k] = String(v)
    })

    const html = await apiRenderHtml({ templateId: templateId.value, parameters })
    previewHtml.value = html
    rendered.value = true
    await nextTick()
  } catch (error) {
    rendered.value = false
    const msg = error?.response?.data?.message || error?.message || '渲染失败'
    renderError.value = error?.response?.status === 403
      ? `无打印查看权限：${msg}`
      : `渲染失败：${msg}`
  } finally {
    rendering.value = false
  }
}

// ---------- 浏览器打印 ----------
const copies = ref(1)

async function doPrint() {
  const win = previewFrame.value?.contentWindow
  if (!win || !rendered.value) return
  // 份数：每份触发一次打印对话框（window.print 阻塞至对话框关闭，串行安全）
  const total = Math.min(20, Math.max(1, Number(copies.value) || 1))
  for (let i = 0; i < total; i++) {
    win.focus()
    win.print()
    if (total > 1 && i < total - 1) {
      await new Promise(resolve => setTimeout(resolve, 200))
    }
  }
}

// ---------- 导出（base64 → blob 下载） ----------
const exportApis = {
  pdf: { api: apiExportPdf, ext: 'pdf', mime: 'application/pdf' },
  excel: { api: apiExportExcel, ext: 'xlsx', mime: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' },
  word: { api: apiExportWord, ext: 'docx', mime: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }
}

async function doExport(format) {
  if (layoutMode.value === 'absolute' && format !== 'pdf') {
    ElMessage.warning('套打模板仅支持打印 / PDF')
    return
  }
  const conf = exportApis[format]
  if (!conf) return
  exporting.value = true
  try {
    const parameters = collectParams()
    const base64 = await conf.api({ templateId: templateId.value, parameters })
    downloadBase64(base64, `${templateName.value || '打印报表'}.${conf.ext}`, conf.mime)
  } catch (error) {
    const msg = error?.response?.data?.message || error?.message || '导出失败'
    if (error?.response?.status === 403) {
      ElMessage.error(`无导出权限（print:template:export）：${msg}`)
    } else if (format === 'pdf' && String(msg).includes('Chromium')) {
      ElMessage.warning(`PDF 导出需要浏览器内核支持：${msg}`)
    } else {
      ElMessage.error(msg)
    }
  } finally {
    exporting.value = false
  }
}

function collectParams() {
  const parameters = {}
  paramFields.value.forEach(p => {
    const v = paramForm[p.name]
    if (v !== undefined && v !== null && String(v).trim() !== '') parameters[p.name] = String(v).trim()
  })
  Object.entries(route.query).forEach(([k, v]) => {
    if (parameters[k] === undefined && v !== undefined && v !== null && v !== '') parameters[k] = String(v)
  })
  return parameters
}

function downloadBase64(base64, fileName, mime) {
  const bin = atob(base64)
  const bytes = new Uint8Array(bin.length)
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i)
  const url = URL.createObjectURL(new Blob([bytes], { type: mime }))
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  a.click()
  URL.revokeObjectURL(url)
}

onMounted(loadTemplate)
</script>

<style scoped>
.print-preview-page {
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: calc(100vh - 84px);
}

.toolbar-card :deep(.el-card__body) {
  padding: 12px 16px;
}

.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
}

.meta {
  display: flex;
  align-items: center;
  gap: 8px;
}

.meta .title {
  font-size: 16px;
  font-weight: 600;
}

.actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.copies {
  display: flex;
  align-items: center;
  gap: 4px;
}

.copies-label {
  font-size: 13px;
  color: var(--el-text-color-regular);
}

.param-form {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px dashed var(--el-border-color);
}

.status-alert {
  flex-shrink: 0;
}

.perm-tip {
  margin: 4px 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.preview-card {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.preview-card :deep(.el-card__body) {
  flex: 1;
  min-height: 0;
  display: flex;
}

.preview-frame {
  flex: 1;
  width: 100%;
  height: 100%;
  border: none;
  background: #fff;
}
</style>
