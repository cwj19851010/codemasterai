<template>
  <el-dialog
    v-model="visible"
    title="创建项目"
    width="680px"
    :close-on-click-modal="false"
    :close-on-press-escape="false"
    class="project-create-wizard"
    @update:modelValue="value => emit('update:modelValue', value)"
  >
    <div v-if="stage === 'form'" class="wizard-step">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="110px">
        <el-form-item label="项目名称" prop="projectName">
          <el-input v-model="form.projectName" placeholder="英文名（如 ForeignTrade）" />
          <div class="form-tip">以大写字母开头，只能包含字母和数字</div>
        </el-form-item>
        <el-form-item label="显示名称" prop="displayName">
          <el-input v-model="form.displayName" placeholder="如 外贸管理软件" />
        </el-form-item>
        <el-form-item label="生成路径" prop="projectPath">
          <el-input v-model="form.projectPath" placeholder="如 D:\Works\ForeignTrade" clearable />
          <div class="form-tip">初始化时生成的 .NET + Vue 工程将写入这里</div>
        </el-form-item>
        <el-form-item label="数据库类型" prop="databaseType">
          <el-select v-model="form.databaseType" style="width: 100%">
            <el-option label="SQLite（简洁，无需服务器）" :value="4" />
            <el-option label="MySQL" :value="2" />
            <el-option label="SQL Server" :value="1" />
            <el-option label="PostgreSQL" :value="3" />
            <el-option label="Oracle" :value="5" />
          </el-select>
        </el-form-item>
        <el-form-item label="数据库连接">
          <DbConnectionEditor v-model="form.connectionString" :database-type="form.databaseType" />
        </el-form-item>
        <el-form-item label="前端端口">
          <el-input-number v-model="form.frontendPort" :min="1000" :max="65535" style="width: 100%" />
        </el-form-item>
        <el-form-item label="后端端口">
          <el-input-number v-model="form.backendPort" :min="1000" :max="65535" style="width: 100%" />
        </el-form-item>
      </el-form>
      <div class="wizard-actions">
        <el-button @click="handleCancel">取消</el-button>
        <el-button type="primary" :loading="creating" @click="handleCreate">创建并继续</el-button>
      </div>
    </div>

    <div v-else-if="stage === 'created'" class="wizard-step created">
      <div class="created-title">
        <el-icon><CircleCheckFilled /></el-icon>
        <span>项目已创建，请确认以下信息</span>
      </div>
      <el-descriptions :column="1" border class="created-desc">
        <el-descriptions-item label="项目名称">{{ project.projectName }}</el-descriptions-item>
        <el-descriptions-item label="显示名称">{{ project.displayName }}</el-descriptions-item>
        <el-descriptions-item label="数据库类型">{{ databaseTypeLabel(project.databaseType) }}</el-descriptions-item>
        <el-descriptions-item label="生成路径">{{ project.projectPath }}</el-descriptions-item>
        <el-descriptions-item label="连接字符串">
          <code class="conn-code">{{ project.connectionString }}</code>
        </el-descriptions-item>
      </el-descriptions>
      <div class="wizard-actions">
        <el-button @click="handleEditProject">修改信息</el-button>
        <el-button @click="handleCancel">稍后再说</el-button>
        <el-button type="primary" :loading="initializing" @click="handleInitialize">确认并初始化</el-button>
      </div>
      <div class="form-tip">“修改信息”将跳转到项目编辑页，保存后返回这里继续。</div>
    </div>

    <div v-else class="wizard-step initializing">
      <el-progress :percentage="progress" :status="initFailed ? 'exception' : undefined" />
      <p class="init-message">{{ initMessage }}</p>
      <div class="wizard-actions" v-if="initFailed">
        <el-button @click="handleCancel">关闭</el-button>
      </div>
    </div>
  </el-dialog>
</template>

<script setup>
import { ref, reactive, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import { CircleCheckFilled } from '@element-plus/icons-vue'
import { create, getById } from '@/api/codegen/project'
import {
  initializeStep1,
  initializeStep2,
  initializeStep3,
  initializeStep4,
  initializeStep5,
  initializeStep6,
  initializeStep7,
  initializeStep8,
  initializeStep9,
  initializeStep10,
  initializeStep11
} from '@/api/codegen/project'
import DbConnectionEditor from '@/views/codegen/project/components/DbConnectionEditor.vue'

const props = defineProps({
  modelValue: { type: Boolean, default: false }
})

const emit = defineEmits(['update:modelValue', 'initialized'])

const router = useRouter()
const visible = ref(props.modelValue)
const stage = ref('form')
const creating = ref(false)
const initializing = ref(false)
const initFailed = ref(false)
const progress = ref(0)
const initMessage = ref('')
const formRef = ref(null)

const form = reactive({
  projectName: '',
  displayName: '',
  projectPath: '',
  databaseType: 4,
  connectionString: 'Data Source=CodeMaster.db',
  frontendPort: 5173,
  backendPort: 5000
})

const project = ref(null)

watch(() => props.modelValue, value => {
  visible.value = value
  if (value) {
    stage.value = 'form'
    initFailed.value = false
    progress.value = 0
  }
})

const rules = {
  projectName: [
    { required: true, message: '请输入项目名称', trigger: 'blur' },
    { pattern: /^[A-Z][a-zA-Z0-9]*$/, message: '项目名称必须以大写字母开头，只能包含字母和数字', trigger: 'blur' }
  ],
  displayName: [{ required: true, message: '请输入显示名称', trigger: 'blur' }],
  projectPath: [{ required: true, message: '请输入生成路径', trigger: 'blur' }]
}

const databaseTypeLabel = type => ({
  1: 'SQL Server', 2: 'MySQL', 3: 'PostgreSQL', 4: 'SQLite', 5: 'Oracle'
}[type] ?? 'SQLite')

const handleCancel = () => {
  emit('update:modelValue', false)
}

const handleCreate = async () => {
  try {
    await formRef.value.validate()
    creating.value = true
    const id = await create({
      projectName: form.projectName,
      displayName: form.displayName,
      databaseType: form.databaseType,
      connectionString: form.connectionString,
      projectPath: form.projectPath,
      frontendPort: form.frontendPort,
      backendPort: form.backendPort,
      projectType: 0
    })
    const detail = await getById(id)
    project.value = detail
    stage.value = 'created'
    ElMessage.success('项目创建成功')
  } catch (error) {
    if (error !== false) ElMessage.error('创建失败：' + (error?.message || ''))
  } finally {
    creating.value = false
  }
}

const handleEditProject = () => {
  if (!project.value?.id) return
  router.push({ path: '/codegen/project/edit', query: { id: project.value.id } })
}

const handleInitialize = async () => {
  if (!project.value?.id) return
  const projectId = project.value.id
  initializing.value = true
  initFailed.value = false
  progress.value = 0

  const steps = [
    { fn: initializeStep1, name: '解压模板', progress: 20 },
    { fn: initializeStep2, name: '生成解决方案', progress: 30 },
    { fn: initializeStep3, name: '写入数据库配置', progress: 40 },
    { fn: initializeStep4, name: '更新端口配置', progress: 50 },
    { fn: initializeStep5, name: '创建数据库迁移', progress: 60 },
    { fn: initializeStep6, name: '应用数据库迁移', progress: 70 },
    { fn: initializeStep7, name: '还原依赖', progress: 75 },
    { fn: initializeStep8, name: '写入多语言数据', progress: 80 },
    { fn: initializeStep9, name: '安装前端依赖', progress: 90 },
    { fn: initializeStep10, name: '启动后端', progress: 95 },
    { fn: initializeStep11, name: '启动前端', progress: 100 }
  ]

  try {
    for (const step of steps) {
      progress.value = step.progress - 5
      initMessage.value = step.name + '…'
      const result = await step.fn({ projectId })
      if (!result.success) throw new Error(result.error || result.message)
      progress.value = step.progress
      initMessage.value = result.message || step.name
    }
    initMessage.value = '初始化完成'
    ElMessage.success('项目初始化完成')
    emit('initialized', project.id)
    setTimeout(() => {
      visible.value = false
    }, 600)
  } catch (error) {
    initFailed.value = true
    initMessage.value = '初始化失败：' + (error?.message || error)
    ElMessage.error('初始化失败：' + (error?.message || error))
  } finally {
    initializing.value = false
  }
}
</script>

<style scoped lang="scss">
.wizard-step {
  min-height: 120px;
}

.created-title {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 16px;
  font-weight: 600;
  color: var(--app-text, #1f2937);

  .el-icon {
    color: var(--app-accent, #059669);
    font-size: 20px;
  }
}

.created-desc {
  margin-bottom: 16px;
}

.conn-code {
  word-break: break-all;
  font-size: 12px;
  font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace;
}

.form-tip {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
}

.wizard-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 16px;
}

.init-message {
  margin-top: 14px;
  color: var(--app-text-muted, #64748b);
  font-size: 13px;
}
</style>