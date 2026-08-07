<template>
  <div class="login-container">
    <div class="ambient-glow" aria-hidden="true"></div>
    <div class="ambient-grid" aria-hidden="true"></div>
    <theme-picker class="login-theme" />

    <div class="login-card">
      <div class="brand">
        <div class="brand-mark">
          <el-icon :size="24"><Platform /></el-icon>
        </div>
        <div>
          <h1>{{ t('app_title') }}</h1>
          <p>以数据为驱动，生成全场景代码</p>
        </div>
      </div>

      <div class="access-line">
        <span>SECURE ACCESS</span>
        <i />
      </div>

      <div class="card-header">
        <h2>{{ t('login_title') }}</h2>
        <p>{{ isClientLogin ? '客户端授权登录' : '服务器控制台登录' }}</p>
      </div>

      <el-form ref="loginFormRef" :model="loginForm" :rules="loginRules" class="login-form">
        <el-form-item prop="username">
          <el-input
            ref="username"
            v-model="loginForm.username"
            :placeholder="$t2('please_input', 'username')"
            size="large"
            :prefix-icon="User"
            clearable
          />
        </el-form-item>

        <el-form-item prop="password">
          <el-input
            ref="password"
            v-model="loginForm.password"
            :type="passwordType"
            :placeholder="$t2('please_input', 'password')"
            size="large"
            :prefix-icon="Lock"
            @keyup.enter="handleLogin"
          >
            <template #suffix>
              <el-icon class="password-icon" @click="showPwd">
                <View v-if="passwordType === 'password'" />
                <Hide v-else />
              </el-icon>
            </template>
          </el-input>
        </el-form-item>

        <el-button
          :loading="loading"
          type="primary"
          size="large"
          class="login-button"
          @click.prevent="handleLogin"
        >
          <span v-if="!loading">{{ t('login') }}</span>
          <span v-else>{{ t('login_loading') }}</span>
        </el-button>
      </el-form>

      <div class="auth-links">
        <el-button text @click="openRegister">注册账号</el-button>
        <el-button text @click="openGithubLogin">GitHub 登录</el-button>
      </div>

      <div class="card-footer">
        <p>{{ t('copyright') }}</p>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { User, Lock, View, Hide, Platform } from '@element-plus/icons-vue'
import { useUserStore } from '@/stores/user'
import { getToken } from '@/utils/auth'
import { useI18n } from 'vue-i18n'
import { t2 } from '@/i18n'
import ThemePicker from '@/layout/components/ThemePicker.vue'
import {
  DEFAULT_CODEMASTER_SERVER_BASE_URL,
  getCodeMasterClientConfig,
  isCodeMasterClient,
  normalizeCodeMasterServerBaseUrl,
  saveCodeMasterClientConfig
} from '@/utils/codegenExecution'

const router = useRouter()
const route = useRoute()
const userStore = useUserStore()
const loginFormRef = ref(null)
const { t } = useI18n()
const clientConfig = getCodeMasterClientConfig()
const isClientLogin = isCodeMasterClient()
const defaultServerBaseUrl = normalizeCodeMasterServerBaseUrl(
  isClientLogin
    ? clientConfig.serverBaseUrl ||
      window.__CODEMASTER_CLIENT__?.serverBaseUrl ||
      DEFAULT_CODEMASTER_SERVER_BASE_URL ||
      (/^https?:$/i.test(window.location.protocol) ? window.location.origin : '')
    : window.location.origin
)

const loginForm = reactive({
  username: 'admin',
  password: 'admin123'
})

const loginRules = {
  username: [{ required: true, trigger: 'blur', message: t2('please_input', 'username') }],
  password: [{ required: true, trigger: 'blur', message: t2('please_input', 'password') }]
}

const loading = ref(false)
const passwordType = ref('password')

const showPwd = () => {
  passwordType.value = passwordType.value === 'password' ? 'text' : 'password'
}

function getPublicUrl(path) {
  return new URL(path, defaultServerBaseUrl || window.location.origin).toString()
}

function openRegister() {
  window.location.href = getPublicUrl('/register')
}

function openGithubLogin() {
  window.location.href = getPublicUrl('/api/account/github/login')
}

async function completeExternalLogin() {
  if (!route.query.github && !route.query.registered) {
    return
  }

  const token = getToken() || localStorage.getItem('access_token') || localStorage.getItem('token')
  if (!token) {
    return
  }

  try {
    userStore.token = token
    await userStore.getUserInfo()
    ElMessage.success(route.query.github ? 'GitHub 登录成功' : '注册成功')
    router.replace('/')
  } catch (error) {
    ElMessage.error(error.message || t('login_failed'))
  }
}

const handleLogin = () => {
  loginFormRef.value.validate(async (valid) => {
    if (valid) {
      loading.value = true
      try {
        if (isClientLogin && defaultServerBaseUrl) {
          await saveCodeMasterClientConfig({
            ...getCodeMasterClientConfig(),
            serverBaseUrl: defaultServerBaseUrl
          })
        }

        await userStore.login({
          userName: loginForm.username,
          password: loginForm.password
        })

        ElMessage.success(t('login_success'))
        router.push('/')
      } catch (error) {
        ElMessage.error(error.message || t('login_failed'))
      } finally {
        loading.value = false
      }
    }
  })
}

onMounted(async () => {
  if (isClientLogin && defaultServerBaseUrl) {
    try {
      await saveCodeMasterClientConfig({
        ...getCodeMasterClientConfig(),
        serverBaseUrl: defaultServerBaseUrl
      })
    } catch (error) {
      console.error('Failed to persist CodeMaster client config:', error)
    }
  }
  completeExternalLogin()
})
</script>

<style lang="scss" scoped>
.login-container {
  position: relative;
  min-height: 100vh;
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px;
  overflow: hidden;
  background: var(--app-bg, #f4f7fb);
}

.ambient-glow {
  position: absolute;
  top: -220px;
  left: 50%;
  transform: translateX(-50%);
  width: 720px;
  height: 460px;
  pointer-events: none;
  background:
    radial-gradient(closest-side, rgba(var(--app-primary-rgb, 79, 70, 229), 0.16), transparent),
    radial-gradient(closest-side, rgba(5, 150, 105, 0.08), transparent 70%);
  filter: blur(6px);
}

.ambient-grid {
  position: absolute;
  inset: 0;
  background:
    linear-gradient(90deg, rgba(var(--app-primary-rgb, 79, 70, 229), 0.07) 1px, transparent 1px),
    linear-gradient(0deg, rgba(var(--app-primary-rgb, 79, 70, 229), 0.07) 1px, transparent 1px);
  background-size: 44px 44px;
  mask-image: radial-gradient(ellipse 70% 60% at 50% 42%, rgba(0, 0, 0, 0.9), transparent);
  pointer-events: none;
}

.login-theme {
  position: fixed;
  top: 18px;
  right: 18px;
  z-index: 2;
  display: flex;
  align-items: center;
  padding: 4px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 8px;
  background: var(--app-surface, #fff);
  box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));
}

.login-card {
  position: relative;
  width: min(420px, 100%);
  padding: 40px 38px 26px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 14px;
  background: var(--app-surface, #fff);
  box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));
}

.brand {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-bottom: 26px;

  .brand-mark {
    display: inline-flex;
    width: 46px;
    height: 46px;
    flex-shrink: 0;
    align-items: center;
    justify-content: center;
    border-radius: 12px;
    color: #fff;
    background: linear-gradient(135deg, var(--app-primary, #4f46e5), var(--app-accent, #059669));
    box-shadow: 0 10px 22px rgba(var(--app-primary-rgb, 79, 70, 229), 0.3);
  }

  h1 {
    margin: 0;
    color: var(--app-text, #1f2937);
    font-size: 22px;
    font-weight: 800;
    line-height: 1.2;
  }

  p {
    margin: 5px 0 0;
    color: var(--app-text-muted, #64748b);
    font-size: 12px;
  }
}

.access-line {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 24px;

  span {
    color: var(--app-text-muted, #64748b);
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
  }

  i {
    height: 1px;
    flex: 1;
    background: linear-gradient(90deg, var(--app-border, #dbe4ef), transparent);
  }
}

.card-header {
  margin-bottom: 26px;

  h2 {
    margin: 0 0 6px;
    color: var(--app-text, #1f2937);
    font-size: 26px;
    font-weight: 800;
  }

  p {
    margin: 0;
    color: var(--app-text-muted, #64748b);
    font-size: 13px;
  }
}

.login-form {
  :deep(.el-form-item) {
    margin-bottom: 16px;
  }

  :deep(.el-input__wrapper) {
    padding: 9px 12px;
    border-radius: 8px;
    background: var(--app-surface-soft, #f8fafc);
    box-shadow: 0 0 0 1px var(--app-border, #dbe4ef) inset;
    transition: all 0.25s;

    &:hover {
      box-shadow: 0 0 0 1px rgba(var(--app-primary-rgb, 79, 70, 229), 0.45) inset;
    }

    &.is-focus {
      background: var(--app-surface, #fff);
      box-shadow: 0 0 0 1px var(--app-primary, #4f46e5) inset, 0 0 0 3px rgba(var(--app-primary-rgb, 79, 70, 229), 0.12);
    }
  }

  .password-icon {
    cursor: pointer;
    color: var(--app-text-muted, #64748b);
    transition: color 0.25s;

    &:hover {
      color: var(--app-primary, #4f46e5);
    }
  }

  .login-button {
    width: 100%;
    height: 46px;
    margin-top: 8px;
    font-size: 15px;
    font-weight: 700;
    border-radius: 8px;
    background: linear-gradient(135deg, var(--app-primary, #4f46e5) 0%, rgba(var(--app-primary-rgb, 79, 70, 229), 0.72) 100%);
    border: none;
    transition: all 0.25s;

    &:hover {
      transform: translateY(-1px);
      box-shadow: 0 12px 24px rgba(var(--app-primary-rgb, 79, 70, 229), 0.3);
    }

    &:active {
      transform: translateY(0);
    }
  }
}

.auth-links {
  display: flex;
  justify-content: center;
  gap: 14px;
  margin-top: 18px;

  :deep(.el-button) {
    color: var(--app-primary, #4f46e5);
    font-weight: 600;
  }
}

.card-footer {
  margin-top: 26px;
  padding-top: 16px;
  border-top: 1px solid var(--app-border, #dbe4ef);
  text-align: center;

  p {
    margin: 0;
    font-size: 12px;
    color: var(--app-text-muted, #64748b);
  }
}

@media (max-width: 640px) {
  .login-container {
    padding: 18px;
    align-items: flex-start;
    padding-top: 10vh;
  }

  .login-card {
    padding: 30px 24px 22px;
  }
}
</style>
