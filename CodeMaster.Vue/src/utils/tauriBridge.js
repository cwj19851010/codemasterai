const CLIENT_CONFIG_KEY = 'codeMasterClientConfig'
const CLIENT_CONFIG_VERSION = 3

export function isTauriRuntime() {
  return Boolean(window.__TAURI_INTERNALS__ || window.__TAURI__)
}

export async function installTauriBridge() {
  if (!isTauriRuntime() || window.CodeMasterBridge?.invoke) {
    return
  }

  const { invoke } = await import('@tauri-apps/api/core')

  let fileConfigResult = null
  try {
    fileConfigResult = await invoke('load_client_config')
  } catch (error) {
    console.error('Failed to load CodeMaster client config file:', error)
  }

  const fileConfig = fileConfigResult?.config || {}
  const storedConfig = readStoredConfig()
  const mergedConfig = {
    ...storedConfig,
    ...fileConfig,
    configVersion: CLIENT_CONFIG_VERSION
  }
  localStorage.setItem(CLIENT_CONFIG_KEY, JSON.stringify(mergedConfig))

  window.CodeMasterBridge = {
    invoke(channel, payload) {
      if (channel !== 'codegenExecution') {
        return Promise.reject(new Error(`Unsupported CodeMaster bridge channel: ${channel}`))
      }

      return invoke('codegen_execution', { request: payload })
    }
  }

  window.CodeMasterClientConfig = {
    save: async (config = {}) => {
      const result = await invoke('save_client_config', {
        config: {
          serverBaseUrl: config.serverBaseUrl || ''
        }
      })
      const savedConfig = {
        ...readStoredConfig(),
        ...(result?.config || {}),
        configVersion: CLIENT_CONFIG_VERSION
      }
      localStorage.setItem(CLIENT_CONFIG_KEY, JSON.stringify(savedConfig))
      window.__CODEMASTER_CLIENT__ = {
        ...(window.__CODEMASTER_CLIENT__ || {}),
        ...(result?.config || {}),
        localExecution: true
      }
      if (result?.filePath) {
        window.__CODEMASTER_CLIENT_CONFIG_FILE__ = result.filePath
      }
      return result
    }
  }

  window.__CODEMASTER_CLIENT__ = {
    ...(window.__CODEMASTER_CLIENT__ || {}),
    ...fileConfig,
    localExecution: true
  }
  if (fileConfigResult?.filePath) {
    window.__CODEMASTER_CLIENT_CONFIG_FILE__ = fileConfigResult.filePath
  }
}

function readStoredConfig() {
  try {
    return JSON.parse(localStorage.getItem(CLIENT_CONFIG_KEY) || '{}') || {}
  } catch (error) {
    console.error('Failed to parse stored CodeMaster client config:', error)
    return {}
  }
}
