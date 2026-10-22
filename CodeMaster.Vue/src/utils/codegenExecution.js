import axios from 'axios'
import { getToken } from '@/utils/auth'
import { isTauriRuntime } from '@/utils/tauriBridge'

const CLIENT_CONFIG_KEY = 'codeMasterClientConfig'
const CLIENT_CONFIG_VERSION = 3
export const DEFAULT_CODEMASTER_SERVER_BASE_URL =
  import.meta.env.VITE_CODEMASTER_SERVER_BASE_URL || 'http://101.37.24.154:9000'

export function normalizeCodeMasterServerBaseUrl(value) {
  let raw = String(value || '').trim()
  if (!raw) {
    return ''
  }

  const markdownUrl = raw.match(/\]\((https?:\/\/[^)]+)\)/i)
  if (markdownUrl?.[1]) {
    raw = markdownUrl[1]
  }

  const candidate = /^https?:\/\//i.test(raw) ? raw : `http://${raw}`
  const url = new URL(candidate)
  if (!['http:', 'https:'].includes(url.protocol)) {
    throw new Error('服务器地址只支持 http 或 https')
  }

  url.search = ''
  url.hash = ''
  url.pathname = url.pathname.replace(/\/api(?:\/.*)?$/i, '').replace(/\/+$/, '')

  return url.toString().replace(/\/$/, '')
}

export function getCodeMasterClientConfig() {
  const raw = localStorage.getItem(CLIENT_CONFIG_KEY)
  if (!raw) {
    return {}
  }

  try {
    const config = JSON.parse(raw) || {}
    if (isTauriRuntime() && Number(config.configVersion || 0) < CLIENT_CONFIG_VERSION) {
      const migrated = {
        ...config,
        configVersion: CLIENT_CONFIG_VERSION
      }
      if (!config.serverBaseUrl || isLegacyLocalServerBaseUrl(config.serverBaseUrl)) {
        migrated.serverBaseUrl = DEFAULT_CODEMASTER_SERVER_BASE_URL
      }
      localStorage.setItem(CLIENT_CONFIG_KEY, JSON.stringify(migrated))
      return migrated
    }
    return config
  } catch (error) {
    console.error('Failed to parse CodeMaster client config:', error)
    return {}
  }
}

export async function saveCodeMasterClientConfig(config) {
  const normalizedConfig = {
    ...(config || {}),
    configVersion: CLIENT_CONFIG_VERSION
  }
  localStorage.setItem(CLIENT_CONFIG_KEY, JSON.stringify(normalizedConfig))

  if (isTauriRuntime() && window.CodeMasterClientConfig?.save) {
    return window.CodeMasterClientConfig.save(normalizedConfig)
  }

  return normalizedConfig
}

export function getCodeMasterClientConfigFilePath() {
  return window.__CODEMASTER_CLIENT_CONFIG_FILE__ || ''
}

function isLegacyLocalServerBaseUrl(value) {
  try {
    const normalized = normalizeCodeMasterServerBaseUrl(value)
    const hostname = new URL(normalized).hostname.toLowerCase()
    return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]'
  } catch {
    return true
  }
}

function getClientConfig() {
  return {
    ...getCodeMasterClientConfig(),
    ...(window.__CODEMASTER_CLIENT__ || {})
  }
}

function getWebViewBridge() {
  return window.chrome?.webview?.hostObjects?.jsbridge
}

function readServerAccessToken() {
  const userStore = localStorage.getItem('user')
  if (userStore) {
    try {
      const user = JSON.parse(userStore)
      if (user?.token) {
        return user.token
      }
    } catch (error) {
      console.error('Failed to parse user store:', error)
    }
  }

  return getToken()
}

function withLocalOverrides(payload = {}) {
  const config = getClientConfig()
  const projectId = payload.projectId || payload.id
  const projectOverrides = config.projectOverrides?.[projectId] || config.projects?.[projectId] || {}

  const overrides = {}
  const targetPath = projectOverrides.targetPath || projectOverrides.projectPath || config.targetPath || config.projectPath
  const databaseType = projectOverrides.databaseType || config.databaseType
  const connectionString = projectOverrides.connectionString || config.connectionString

  if (targetPath) {
    overrides.targetPath = targetPath
  }
  if (databaseType) {
    overrides.databaseType = databaseType
  }
  if (connectionString) {
    overrides.connectionString = connectionString
  }

  return {
    ...overrides,
    ...payload
  }
}

export function isCodeMasterClient() {
  const config = getClientConfig()
  return Boolean(config.agentUrl || window.CodeMasterBridge?.invoke || isTauriRuntime() || getWebViewBridge()?.ExecuteCodegenAction)
}

export function shouldUseLocalCodegenExecution() {
  const config = getClientConfig()
  return isCodeMasterClient() && config.localExecution !== false
}

function unwrapResponse(data) {
  if (typeof data === 'string') {
    try {
      data = JSON.parse(data)
    } catch {
      return data
    }
  }

  if (data && typeof data === 'object' && !Array.isArray(data) && typeof data.code === 'number') {
    if (data.code === 200) {
      return data.data
    }

    throw new Error(getFailureMessage(data))
  }

  if (data && typeof data === 'object' && !Array.isArray(data) && typeof data.success === 'boolean') {
    if (data.success) {
      return data.dataOnly ? data.data : data
    }

    const error = new Error(getFailureMessage(data))
    error.diagnosticOutput = data.output || data.message || ''
    throw error
  }

  return data
}

function getFailureMessage(data) {
  if (!data || typeof data !== 'object') {
    return 'Local code generation failed'
  }

  return data.message || data.error || firstOutputLine(data.output) || 'Local code generation failed'
}

function firstOutputLine(output) {
  if (typeof output !== 'string') {
    return ''
  }

  return output
    .split(/\r?\n/)
    .map(line => line.trim())
    .find(Boolean) || ''
}

export async function writeClientDiagnostic(event, details = {}) {
  if (!isTauriRuntime()) return

  try {
    const { invoke } = await import('@tauri-apps/api/core')
    await invoke('client_diagnostic', {
      entry: {
        level: details.level || 'information',
        event,
        message: details.message || '',
        requestId: details.requestId || '',
        transportId: details.transportId || '',
        executionId: details.executionId ? String(details.executionId) : '',
        action: details.action || '',
        stage: details.stage || '',
        durationMs: Number.isFinite(details.durationMs) ? Math.max(0, Math.round(details.durationMs)) : null
      }
    })
  } catch (error) {
    console.error('Failed to write CodeMaster diagnostic log:', error)
  }
}

export async function getClientDiagnosticLogDirectory() {
  if (!isTauriRuntime()) return ''
  const { invoke } = await import('@tauri-apps/api/core')
  return invoke('get_diagnostic_log_directory')
}

export async function openClientDiagnosticLogDirectory() {
  if (!isTauriRuntime()) return ''
  const { invoke } = await import('@tauri-apps/api/core')
  return invoke('open_diagnostic_log_directory')
}

export async function cancelLocalCodegenExecution() {
  if (!isTauriRuntime()) return false
  const { invoke } = await import('@tauri-apps/api/core')
  return invoke('cancel_codegen_execution')
}

export async function invalidateLocalAgentContext(conversationId) {
  if (!isTauriRuntime() || !conversationId) return false
  return invokeLocalCodegen('invalidateAgentContext', { conversationId })
}

function createTransportRequestId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID()
  return `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function formatDiagnosticError(error) {
  const message = error?.message || String(error || 'Unknown client error')
  return error?.stack && !message.includes(error.stack)
    ? `${message}\n${error.stack}`
    : message
}

async function invokeLocalCodegen(action, payload = {}, options = {}) {
  const config = getClientConfig()
  const serverBaseUrl = normalizeCodeMasterServerBaseUrl(
    config.serverBaseUrl || DEFAULT_CODEMASTER_SERVER_BASE_URL || window.location.origin
  )
  const requestPayload = {
    id: createTransportRequestId(),
    action,
    payload: withLocalOverrides(payload),
    serverBaseUrl,
    accessToken: readServerAccessToken()
  }

  const startedAt = performance.now()
  let unlistenProgress
  try {
    let result
    if (window.CodeMasterBridge?.invoke) {
      result = await window.CodeMasterBridge.invoke('codegenExecution', requestPayload)
    } else if (isTauriRuntime()) {
      if (typeof options.onProgress === 'function') {
        const { listen } = await import('@tauri-apps/api/event')
        unlistenProgress = await listen('codegen-execution-progress', event => {
          const progress = event.payload
          if (progress?.id === requestPayload.id) {
            options.onProgress(progress)
          }
        })
      }
      const { invoke } = await import('@tauri-apps/api/core')
      result = await invoke('codegen_execution', { request: requestPayload })
    } else {
      const webViewBridge = getWebViewBridge()
      if (webViewBridge?.ExecuteCodegenAction) {
        result = await webViewBridge.ExecuteCodegenAction(JSON.stringify(requestPayload))
      } else {
        if (!config.agentUrl) {
          throw new Error('Local code generation agent is not configured')
        }

        const agentUrl = config.agentUrl.replace(/\/$/, '')
        const response = await axios.post(
          `${agentUrl}/api/codegen/execution/${action}`,
          requestPayload,
          {
            timeout: config.timeout || 300000,
            headers: {
              'X-CodeMaster-Client-Token': config.token || ''
            }
          }
        )
        result = response.data
      }
    }

    return unwrapResponse(result)
  } catch (error) {
    await writeClientDiagnostic('client.local_execution.failed', {
      level: 'error',
      requestId: payload?.requestId || '',
      transportId: requestPayload.id,
      action,
      stage: 'invoke',
      durationMs: performance.now() - startedAt,
      message: formatDiagnosticError(error)
    })
    throw error
  } finally {
    if (unlistenProgress) unlistenProgress()
  }
}

export function executeCodegenAction(action, payload, serverExecutor, options = {}) {
  if (shouldUseLocalCodegenExecution()) {
    return invokeLocalCodegen(action, payload, options)
  }

  return serverExecutor()
}
