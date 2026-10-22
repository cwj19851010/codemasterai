<template>
  <el-tooltip content="检查更新" placement="bottom">
    <el-button
      class="update-trigger"
      text
      circle
      aria-label="检查更新"
      :icon="Refresh"
      @click="handleCheck"
    />
  </el-tooltip>

  <el-dialog
    v-model="dialogVisible"
    title="检查更新"
    width="480px"
    :close-on-click-modal="false"
  >
    <div v-if="checking" class="update-state">
      <el-icon class="spin"><Loading /></el-icon>
      <span>正在检查更新…</span>
    </div>

    <div v-else-if="error" class="update-state">
      <el-icon><WarningFilled /></el-icon>
      <span>{{ error }}</span>
      <el-button size="small" @click="handleCheck">重试</el-button>
    </div>

    <div v-else-if="!update" class="update-state">
      <el-icon><CircleCheckFilled /></el-icon>
      <span>当前已是最新版本（v{{ currentVersion }}）</span>
    </div>

    <template v-else>
      <div class="update-summary">
        <el-icon><Promotion /></el-icon>
        <div>
          <strong>发现新版本 v{{ update.version }}</strong>
          <span v-if="publishedAt">发布于 {{ publishedAt }}</span>
          <span v-if="totalSize">大小约 {{ formatSize(totalSize) }}</span>
        </div>
      </div>

      <div v-if="downloading" class="download-progress">
        <el-progress :percentage="progressPercent" :status="downloadFailed ? 'exception' : undefined" />
        <span class="progress-text">{{ downloadText }}</span>
      </div>
    </template>

    <template #footer>
      <el-button v-if="update && !downloading" @click="dialogVisible = false">稍后</el-button>
      <el-button
        v-if="update && !downloading && !downloadFailed"
        type="primary"
        :loading="startingDownload"
        @click="handleDownload"
      >
        立即更新
      </el-button>
      <el-button v-if="downloadFailed" type="primary" @click="handleDownload">重试下载</el-button>
      <el-button v-if="downloading && !downloadFailed" disabled>下载中，完成后自动安装…</el-button>
    </template>
  </el-dialog>
</template>

<script setup>
import { ref, computed, onMounted } from 'vue'
import { ElMessage, ElNotification } from 'element-plus'
import {
  CircleCheckFilled,
  Loading,
  Promotion,
  Refresh,
  WarningFilled
} from '@element-plus/icons-vue'
import { getVersion } from '@tauri-apps/api/app'

const checking = ref(false)
const dialogVisible = ref(false)
const error = ref('')
const update = ref(null)
const currentVersion = ref('')
const startingDownload = ref(false)
const downloading = ref(false)
const downloadFailed = ref(false)
const progressPercent = ref(0)
const downloadText = ref('')
const totalSize = ref(0)

const publishedAt = computed(() => {
  if (!update.value?.pub_date) return ''
  const date = new Date(update.value.pub_date)
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString()
})

const handleCheck = async (silent = false) => {
  checking.value = true
  error.value = ''
  update.value = null
  downloading.value = false
  downloadFailed.value = false
  progressPercent.value = 0
  if (!silent) dialogVisible.value = true

  try {
    const [{ check }] = await Promise.all([
      import('@tauri-apps/plugin-updater'),
      getVersion().then(version => { currentVersion.value = version })
    ])
    update.value = await check()
    if (update.value) {
      totalSize.value = update.value.totalSize || 0
      if (silent) {
        notifyUpdateAvailable()
      }
    }
  } catch (err) {
    error.value = err?.message || '检查更新失败，请确认网络连接'
  } finally {
    checking.value = false
  }
}

const notifyUpdateAvailable = () => {
  ElNotification({
    title: '发现新版本 v' + update.value.version,
    message: 'CodeMaster 客户端有新版本，点击立即更新',
    type: 'success',
    duration: 0,
    position: 'bottom-right',
    onClick: () => { dialogVisible.value = true }
  })
}

const handleDownload = async () => {
  if (!update.value) return
  startingDownload.value = true
  downloading.value = true
  downloadFailed.value = false
  progressPercent.value = 0
  downloadText.value = '正在连接服务器…'

  try {
    await update.value.downloadAndInstall(event => {
      if (event.event === 'Started') {
        downloadText.value = '正在下载更新包…'
      } else if (event.event === 'Progress') {
        const downloaded = event.data?.downloaded || 0
        const total = event.data?.totalLength || 0
        if (total > 0) {
          progressPercent.value = Math.round((downloaded / total) * 100)
          downloadText.value = `已下载 ${formatSize(downloaded)} / ${formatSize(total)}`
        }
      } else if (event.event === 'Finished') {
        downloadText.value = '下载完成，正在安装…'
      }
    })
    ElMessage.success('更新下载完成，正在安装')
    setTimeout(() => { dialogVisible.value = false }, 1500)
  } catch (err) {
    downloadFailed.value = true
    downloadText.value = ''
    ElMessage.error('更新失败：' + (err?.message || '未知错误'))
  } finally {
    startingDownload.value = false
    downloading.value = false
  }
}

const formatSize = bytes => {
  if (!bytes || bytes <= 0) return ''
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB'
  return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB'
}

onMounted(() => {
  const timer = setTimeout(() => {
    handleCheck(true)
  }, 8000)
  const cancel = () => clearTimeout(timer)
  window.addEventListener('beforeunload', cancel)
})
</script>

<style scoped lang="scss">
.spin {
  animation: rotating 1.2s linear infinite;
}

@keyframes rotating {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.update-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 24px 0;
  color: var(--app-text-muted, #64748b);
  font-size: 14px;

  .el-icon {
    font-size: 32px;
    color: var(--app-accent, #059669);
  }
}

.update-summary {
  display: flex;
  align-items: flex-start;
  gap: 10px;

  .el-icon {
    font-size: 22px;
    color: var(--app-accent, #059669);
    margin-top: 2px;
  }

  strong {
    display: block;
    font-size: 15px;
  }

  span {
    display: block;
    margin-top: 4px;
    font-size: 12px;
    color: var(--app-text-muted, #64748b);
  }
}

.download-progress {
  margin-top: 18px;

  .progress-text {
    display: block;
    margin-top: 6px;
    font-size: 12px;
    color: var(--app-text-muted, #64748b);
  }
}
</style>