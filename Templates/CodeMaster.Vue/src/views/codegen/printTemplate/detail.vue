<template>
  <div class="app-container print-template-detail">
    <el-card shadow="never" class="mb-20">
      <div class="head">
        <div class="meta">
          <span class="title">{{ template?.templateName || '报表模板详情' }}</span>
          <el-tag v-if="template?.paperSize" size="small" type="info">{{ template.paperSize }}</el-tag>
          <el-tag v-if="template?.layoutMode === 'absolute'" size="small" type="warning">套打</el-tag>
          <el-tag v-if="template?.status === 1" size="small" type="danger">已停用</el-tag>
        </div>
        <div class="actions">
          <el-button icon="Refresh" :loading="rendering" @click="renderStyle">刷新样式</el-button>
          <el-button type="primary" icon="Printer" :disabled="!template" @click="handleOpen">打开报表</el-button>
          <el-button @click="goBack">返回</el-button>
        </div>
      </div>
    </el-card>

    <el-alert
      v-if="loadError"
      :title="loadError"
      type="error"
      show-icon
      :closable="false"
      class="mb-20"
    />

    <!-- 报表样式：后端按模板 JSON 渲染整册 HTML（不取业务数据） -->
    <el-card shadow="never" v-loading="rendering" element-loading-text="正在渲染模板样式…">
      <iframe
        v-show="previewHtml"
        class="style-frame"
        title="print-template-style"
        :srcdoc="previewHtml"
      />
      <el-empty v-if="!previewHtml && !rendering && !loadError" description="暂无模板样式" />
    </el-card>
  </div>
</template>

<script setup>
/**
 * 报表模板详情（生成项目自带报表运行时）
 * 只读查看报表样式：调用本项目后端 renderpreview 按模板 JSON 渲染整册 HTML，
 * 不取业务数据；「打开报表」进入运行页传参渲染/打印/导出。
 */
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getById, renderPreview } from '@/api/codegen/printTemplate'
import { openPrintReport } from '@/utils/printReport'

defineOptions({
  name: 'CodegenPrintTemplateDetail'
})

const route = useRoute()
const router = useRouter()

const template = ref(null)
const previewHtml = ref('')
const rendering = ref(false)
const loadError = ref('')

onMounted(loadTemplate)

async function loadTemplate() {
  const id = route.query.id
  if (!id) {
    loadError.value = '缺少模板 ID 参数'
    return
  }
  loadError.value = ''
  rendering.value = true
  try {
    template.value = await getById(id)
    await renderStyle()
  } catch (error) {
    loadError.value = error?.response?.data?.message || error?.message || '报表模板加载失败'
  } finally {
    rendering.value = false
  }
}

async function renderStyle() {
  if (!template.value?.templateJson) {
    previewHtml.value = ''
    return
  }
  rendering.value = true
  try {
    previewHtml.value = (await renderPreview({ templateJson: template.value.templateJson })) || ''
  } catch (error) {
    loadError.value = error?.response?.data?.message || error?.message || '模板样式渲染失败'
  } finally {
    rendering.value = false
  }
}

function handleOpen() {
  if (!template.value?.id) return
  openPrintReport(template.value.id, {})
}

function goBack() {
  router.back()
}
</script>

<style scoped>
.mb-20 {
  margin-bottom: 20px;
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.meta {
  display: flex;
  align-items: center;
  gap: 8px;
}

.title {
  font-size: 16px;
  font-weight: 600;
}

.style-frame {
  width: 100%;
  min-height: 720px;
  border: none;
  background: #fff;
}
</style>