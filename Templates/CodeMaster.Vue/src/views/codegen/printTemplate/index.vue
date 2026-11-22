<template>
  <div class="app-container">
    <el-card shadow="never" class="mb-20">
      <!-- 搜索栏 -->
      <el-form :inline="true" :model="queryParams" class="demo-form-inline">
        <el-form-item label="模板名称">
          <el-input
            v-model="queryParams.templateName"
            placeholder="请输入模板名称"
            clearable
            @keyup.enter="handleQuery"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="handleQuery">查询</el-button>
          <el-button @click="handleReset">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <!-- 列表：模板ID + 模板名称 -->
      <el-table v-loading="loading" :data="pagedList" border>
        <el-table-column label="模板ID" prop="id" width="220" show-overflow-tooltip />
        <el-table-column label="模板名称" prop="templateName" show-overflow-tooltip />
        <el-table-column label="操作" width="180" fixed="right">
          <template #default="{ row }">
            <el-button type="primary" link @click="handleDetail(row)">详情</el-button>
            <el-button type="success" link @click="handleOpen(row)">打开报表</el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- 分页 -->
      <el-pagination
        v-model:current-page="queryParams.pageNum"
        v-model:page-size="queryParams.pageSize"
        :total="filteredList.length"
        :page-sizes="[10, 20, 50, 100]"
        layout="total, sizes, prev, pager, next, jumper"
        @size-change="handleQuery"
      />
    </el-card>
  </div>
</template>

<script setup>
/**
 * 报表模板列表（生成项目自带报表运行时）
 * 模板由 CodeMaster 设计器设计并下发到本项目数据库，运行期与 CodeMaster 无关。
 * 列表仅展示模板ID与模板名称；详情查看报表样式，打开报表进入运行页传参渲染。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getList } from '@/api/codegen/printTemplate'
import { openPrintReport } from '@/utils/printReport'

defineOptions({
  name: 'CodegenPrintTemplate'
})

const route = useRoute()
const router = useRouter()

const loading = ref(false)
const templates = ref([])

const queryParams = reactive({
  pageNum: 1,
  pageSize: 10,
  templateName: ''
})

const filteredList = computed(() => {
  const keyword = (queryParams.templateName || '').trim().toLowerCase()
  if (!keyword) return templates.value
  return templates.value.filter(item => (item.templateName || '').toLowerCase().includes(keyword))
})

const pagedList = computed(() => {
  const start = (queryParams.pageNum - 1) * queryParams.pageSize
  return filteredList.value.slice(start, start + queryParams.pageSize)
})

onMounted(loadList)

async function loadList() {
  loading.value = true
  try {
    templates.value = (await getList()) || []
  } catch (error) {
    ElMessage.error(error?.response?.data?.message || error?.message || '报表模板加载失败')
  } finally {
    loading.value = false
  }
}

function handleQuery() {
  queryParams.pageNum = 1
}

function handleReset() {
  queryParams.templateName = ''
  handleQuery()
}

function handleDetail(row) {
  const basePath = route.path.endsWith('/') ? route.path.slice(0, -1) : route.path
  router.push({ path: `${basePath}/detail`, query: { id: String(row.id) } })
}

function handleOpen(row) {
  openPrintReport(String(row.id), {})
}
</script>

<style scoped>
.mb-20 {
  margin-bottom: 20px;
}
</style>
