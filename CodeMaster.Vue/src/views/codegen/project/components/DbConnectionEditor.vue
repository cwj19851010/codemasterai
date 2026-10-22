<template>
  <div class="db-connection-editor">
    <template v-if="!manualMode">
      <div v-if="isSqlite" class="db-field-row">
        <span class="db-field-label required">{{ t('db_sqlite_file') }}</span>
        <el-input v-model="form.sqliteFile" placeholder="TradeErp.db（只填文件名，路径自动处理）" clearable />
      </div>
      <div v-else class="db-fields-grid">
        <div class="db-field-row">
          <span class="db-field-label required">{{ t('db_host') }}</span>
          <el-input v-model="form.host" placeholder="127.0.0.1" clearable />
        </div>
        <div class="db-field-row">
          <span class="db-field-label required">{{ t('db_port') }}</span>
          <el-input-number v-model="form.port" :min="1" :max="65535" controls-position="right" style="width: 100%" />
        </div>
        <div class="db-field-row">
          <span class="db-field-label required">{{ t('db_database') }}</span>
          <el-input v-model="form.database" :placeholder="defaultDatabase" clearable />
        </div>
        <div class="db-field-row">
          <span class="db-field-label required">{{ t('db_username') }}</span>
          <el-input v-model="form.username" placeholder="root" clearable autocomplete="off" />
        </div>
        <div class="db-field-row">
          <span class="db-field-label required">{{ t('db_password') }}</span>
          <el-input v-model="form.password" type="password" show-password autocomplete="new-password" />
        </div>
      </div>
      <div class="conn-preview">
        <span class="conn-preview-label">{{ t('connection_string_preview') }}</span>
        <div class="conn-preview-box">
          <code>{{ generated || '—' }}</code>
          <el-button v-if="generated" size="small" text type="primary" @click="handleCopy">{{ t('copy') }}</el-button>
        </div>
      </div>
    </template>
    <el-input v-else v-model="manualString" type="textarea" :rows="3" :placeholder="t('connection_string')" />
    <div class="conn-mode-row">
      <el-checkbox v-model="manualMode">{{ t('connection_manual_mode') }}</el-checkbox>
      <span v-if="!manualMode" class="conn-auto-tip">{{ t('connection_auto_tip') }}</span>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, computed, watch, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'

const props = defineProps({
  modelValue: { type: String, default: '' },
  databaseType: { type: Number, default: 2 }
})

const emit = defineEmits(['update:modelValue'])

const { t } = useI18n()

const DB_PRESETS = {
  1: { port: 1433, database: 'CodeMasterDB' },
  2: { port: 3306, database: 'CodeMasterDB' },
  3: { port: 5432, database: 'CodeMasterDB' },
  4: { port: 0, database: '' },
  5: { port: 1521, database: '' }
}

const isSqlite = computed(() => props.databaseType === 4)

const form = reactive({
  host: '127.0.0.1',
  port: 3306,
  database: 'CodeMasterDB',
  username: '',
  password: '',
  sqliteFile: ''
})

const manualMode = ref(false)
const manualString = ref(props.modelValue || '')

const defaultDatabase = computed(() => {
  const preset = DB_PRESETS[props.databaseType]
  return preset ? preset.db : ''
})

const normalizeSqliteFile = (value) => {
  if (!value) return ''
  let v = String(value).trim()
  // 剥离用户可能粘贴的完整连接串前缀（防止 Data Source=Data Source=xxx 双写）
  while (/^data\s*source\s*=\s*/i.test(v)) v = v.replace(/^data\s*source\s*=\s*/i, '').trim()
  // 剥离路径，只保留数据库文件名
  const name = v.split(/[\\/]/).pop() || ''
  return name.trim()
}

const buildConnection = (dbType, f) => {
  switch (dbType) {
    case 1:
      return `Server=${f.host || '127.0.0.1'},${f.port || 1433};Database=${f.database};User ID=${f.username};Password=${f.password};TrustServerCertificate=True;`
    case 3:
      return `Host=${f.host || '127.0.0.1'};Port=${f.port || 5432};Database=${f.database};Username=${f.username};Password=${f.password};`
    case 5:
      return `Data Source=${f.host || '127.0.0.1'}:${f.port || 1521}/${f.database};User ID=${f.username};Password=${f.password};`
    case 4:
      return `Data Source=${normalizeSqliteFile(f.sqliteFile) || 'CodeMaster.db'}`
    default:
      return `Server=${f.host || '127.0.0.1'};Port=${f.port || 3306};Database=${f.database};User ID=${f.username};Password=${f.password};Character Set=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True;`
  }
}

const generated = computed(() => (manualMode.value ? manualString.value : buildConnection(props.databaseType, form)))

const resetConnectionPreset = (dbType) => {
  const preset = DB_PRESETS[dbType] || DB_PRESETS[2]
  form.host = '127.0.0.1'
  form.port = preset.port
  form.database = preset.db
  form.username = ''
  form.password = ''
  form.sqliteFile = ''
}

const apply = () => {
  emit('update:modelValue', generated.value)
}

const parseConnection = (dbType, cs) => {
  if (!cs) return null
  const parts = {}
  cs.split(';').forEach((part) => {
    const idx = part.indexOf('=')
    if (idx > 0) parts[part.slice(0, idx).trim().toLowerCase()] = part.slice(idx + 1).trim()
  })
  if (dbType === 4) {
    return { sqliteFile: normalizeSqliteFile(parts['file'] || parts['data source'] || '') }
  }
  const dataSource = parts['data source'] ? parts['data source'].replace(/^\/+/, '') : ''
  let host = parts['server'] || parts['host'] || ''
  let port = parts['port'] ? Number(parts['port']) : 0
  let database = parts['database'] || parts['initial catalog'] || ''
  if (!host && dataSource.includes(':')) {
    const hp = dataSource.split('/')[0].split(':')
    host = hp[0] || ''
    port = Number(hp[1]) || 0
    database = database || dataSource.split('/')[1] || ''
  }
  return {
    host: host || '127.0.0.1',
    port,
    database,
    username: parts['user id'] || parts['username'] || parts['user'] || '',
    password: parts['password'] || parts['pwd'] || '',
    sqliteFile: ''
  }
}

const publish = () => {
  emit('update:modelValue', generated.value)
}

const syncFromModel = () => {
  const v = props.modelValue || ''
  if (manualMode.value) {
    if (v !== manualString.value) manualString.value = v
    return
  }
  if (!v) return
  const current = buildConnection(props.databaseType, form)
  if (v === current) return
  const parsed = parseConnection(props.databaseType, v)
  if (parsed) {
    Object.assign(form, parsed)
  }
}

watch(() => props.databaseType, (dbType) => {
  if (props.modelValue) {
    syncFromModel()
    return
  }
  resetConnectionPreset(dbType)
  manualMode.value = false
  manualString.value = ''
  apply()
})

watch(form, () => {
  if (!manualMode.value) apply()
}, { deep: true })

watch(() => props.modelValue, (v) => {
  if (manualMode.value) {
    if (v !== manualString.value) manualString.value = v || ''
    return
  }
  syncFromModel()
})

watch(manualMode, (m) => {
  if (m) {
    manualString.value = props.modelValue || buildConnection(props.databaseType, form)
  }
  apply()
})

onMounted(() => {
  resetConnectionPreset(props.databaseType)
  syncFromModel()
  apply()
})

const handleCopy = async () => {
  try {
    await navigator.clipboard.writeText(generated.value)
    ElMessage.success(t('copy_success'))
  } catch {
    ElMessage.info(t('copy_request_denied'))
  }
}
</script>

<style scoped lang="scss">
.db-connection-editor {
  width: 100%;
}

.db-fields-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px 16px;
}

.db-field-row {
  display: flex;
  flex-direction: column;
  gap: 5px;

  .db-field-label {
    font-size: 12px;
    color: #909399;
    line-height: 1.4;

    &.required::before {
      content: '*';
      color: #f56c6c;
      margin-right: 4px;
    }
  }
}

.conn-preview {
  margin-top: 8px;

  .conn-preview-label {
    display: block;
    font-size: 12px;
    color: #909399;
    margin-bottom: 5px;
  }

  .conn-preview-box {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 8px 12px;
    background: var(--app-surface-soft, #f8fafc);
    border: 1px dashed var(--app-border, #d9e2f0);
    border-radius: 6px;

    code {
      flex: 1;
      min-width: 0;
      font-size: 12px;
      color: var(--app-text, #1f2937);
      word-break: break-all;
      font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace;
    }
  }
}

.conn-mode-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 6px;

  .conn-auto-tip {
    font-size: 12px;
    color: #a8abb2;
  }
}
</style>