<template>
  <div class="app-container">
    <el-card shadow="never" v-loading="loading">
      <template #header>
        <div class="card-header">
          <span>编辑项目模块</span>
        </div>
      </template>

      <el-form
        ref="formRef"
        :model="form"
        :rules="rules"
        label-width="120px"
        class="detail-form"
      >
        <el-form-item label="所属项目" prop="projectId">
          <el-select v-model="form.projectId" placeholder="请选择项目" style="width: 100%">
            <el-option
              v-for="project in projectList"
              :key="project.id"
              :label="getProjectOptionLabel(project)"
              :value="project.id"
            />
          </el-select>
        </el-form-item>

        <el-form-item label="模块名称" prop="moduleName">
          <el-input v-model="form.moduleName" placeholder="请输入技术名称(英文 PascalCase)" />
          <div class="form-tip">
            用于权限规则、命名空间和代码生成目录。必须是英文 PascalCase,例如
            <el-tag size="small" type="info">Engineering</el-tag>
            <el-tag size="small" type="info">ProjectManagement</el-tag>
            。中文显示名请填写到模块描述。
          </div>
        </el-form-item>

        <el-form-item label="模块描述" prop="moduleDescription">
          <el-input v-model="form.moduleDescription" placeholder="请输入模块描述（中文）" />
          <div class="form-tip">用于菜单显示，如：销售管理、订单管理</div>
        </el-form-item>

        <el-form-item label="图标" prop="icon">
          <el-input v-model="form.icon" placeholder="请输入图标��称" />
        </el-form-item>

        <el-form-item label="排序" prop="orderNum">
          <el-input-number
            v-model="form.orderNum"
            :min="isReportsModule ? 9999 : 3"
            :max="isReportsModule ? 9999 : 9998"
          />
          <div class="form-tip">普通模块使用 3–9998；Reporting/报表管理固定使用 9999</div>
        </el-form-item>

        <el-form-item>
          <el-button type="primary" @click="handleSubmit" :loading="submitLoading">保存</el-button>
          <el-button @click="handleBack">取消</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<script setup>
import { computed, ref, reactive, onMounted, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { t2 } from '@/i18n'
import { getById, update } from '@/api/codegen/projectModule'
import { getList } from '@/api/codegen/project'

const router = useRouter()
const route = useRoute()
const formRef = ref(null)
const loading = ref(false)
const submitLoading = ref(false)
const projectList = ref([])

const form = reactive({
  id: null,
  projectId: null,
  moduleName: '',
  moduleDescription: '',
  icon: '',
  orderNum: 0
})

const isReportsModule = computed(() => {
  const name = String(form.moduleName || '').trim().toLowerCase()
  return name === 'reporting' || name === 'reports' || String(form.moduleDescription || '').trim() === '报表管理'
})

watch(isReportsModule, value => {
  form.orderNum = value ? 9999 : (form.orderNum === 9999 ? 3 : form.orderNum)
})

const rules = {
  projectId: [{ required: true, message: t2('please_select', 'project'), trigger: 'change' }],
  moduleName: [
    { required: true, message: t2('please_input', 'module_name'), trigger: 'blur' },
    {
      pattern: /^[A-Z][A-Za-z0-9]*$/,
      message: '技术名称必须是 ASCII PascalCase,以大写字母开头,不含空格或下划线。示例:Engineering',
      trigger: 'blur'
    }
  ],
  moduleDescription: [{ required: true, message: t2('please_input', 'module_description'), trigger: 'blur' }]
}

function getProjectOptionLabel(project) {
  return project?.description || project?.displayName || '未设置项目描述'
}

onMounted(() => {
  loadProjectList()
  loadData()
})

async function loadProjectList() {
  try {
    const res = await getList()
    projectList.value = res || []
  } catch (error) {
    console.error('加载项目列表失败:', error)
  }
}

async function loadData() {
  loading.value = true
  try {
    const data = await getById(route.query.id)
    Object.assign(form, data)
  } catch (error) {
    ElMessage.error('加载数据失败')
    router.back()
  } finally {
    loading.value = false
  }
}

async function handleSubmit() {
  try {
    await formRef.value.validate()
    submitLoading.value = true
    await update(form.id, form)
    ElMessage.success('更新成功')
    router.back()
  } catch (error) {
    if (error !== false) {
      ElMessage.error('更新失败')
      console.error(error)
    }
  } finally {
    submitLoading.value = false
  }
}

function handleBack() {
  router.back()
}
</script>

<style scoped lang="scss">
.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.form-tip {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
}
</style>
