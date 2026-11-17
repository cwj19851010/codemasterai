<template>
  <div class="login-page">
    <div class="login-theme">
      <theme-picker />
    </div>

    <section class="showcase">
      <div class="showcase-glow" aria-hidden="true"></div>
      <div class="showcase-grid" aria-hidden="true"></div>
      <div class="showcase-ring ring-a" aria-hidden="true"></div>
      <div class="showcase-ring ring-b" aria-hidden="true"></div>

      <div class="showcase-inner">
        <div class="brand">
          <div class="brand-mark">
            <el-icon :size="24"><Platform /></el-icon>
          </div>
          <span class="brand-name">{{ t('app_title') }}</span>
        </div>

        <div class="showcase-copy">
          <p class="eyebrow">CODE MASTER PLATFORM</p>
          <h1>
            以数据为驱动<br />
            <span>生成全场景代码</span>
          </h1>
          <p class="lead">可视化建模、模板化生成、开箱即用的企业级应用底座。</p>
        </div>

        <ul class="feature-list">
          <li>
            <span class="feature-icon"><el-icon :size="18"><Grid /></el-icon></span>
            <div>
              <strong>可视化建模</strong>
              <em>拖拽搭建页面，配置即刻生效</em>
            </div>
          </li>
          <li>
            <span class="feature-icon"><el-icon :size="18"><MagicStick /></el-icon></span>
            <div>
              <strong>一键生成</strong>
              <em>前后端代码同步产出，保持可维护</em>
            </div>
          </li>
          <li>
            <span class="feature-icon"><el-icon :size="18"><Connection /></el-icon></span>
            <div>
              <strong>多库适配</strong>
              <em>SQL Server / MySQL / PostgreSQL / SQLite</em>
            </div>
          </li>
        </ul>

        <div class="showcase-foot">
          <span>SECURE ACCESS</span>
          <i />
          <span>© 2026 CodeMaster</span>
        </div>
      </div>
    </section>

    <section class="panel">
      <div class="panel-inner">
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
            <span v-if="!loading">登 录</span>
            <span v-else>登录中…</span>
          </el-button>
        </el-form>

        <div class="auth-links">
          <el-button text @click="openRegister">注册账号</el-button>
          <em />
          <el-button text @click="openGithubLogin">GitHub 登录</el-button>
        </div>
      </div>
    </section>
  </div>
</template>

<script setup>
import { ref, reactive, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { User, Lock, View, Hide, Platform, Grid, MagicStick, Connection } from '@element-plus/icons-vue'
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
.login-page {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1.06fr) minmax(0, 1fr);
  min-height: 100vh;
  background: var(--app-bg, #f4f7fb);
}

.login-theme {
  position: fixed;
  top: 20px;
  right: 20px;
  z-index: 5;
  display: flex;
  align-items: center;
  padding: 4px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 8px;
  background: var(--app-surface, #fff);
  box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));
}

/* ---------- 左侧品牌展示区 ---------- */
.showcase {
  position: relative;
  display: flex;
  overflow: hidden;
  padding: 56px 64px;
  color: #e6edf7;
  background:
    radial-gradient(120% 90% at 8% 0%, rgba(var(--app-primary-rgb, 37, 99, 235), 0.5), transparent 58%),
    radial-gradient(90% 70% at 92% 100%, rgba(var(--app-accent-rgb, 5, 150, 105), 0.28), transparent 62%),
    linear-gradient(158deg, #0a1424 0%, #0d1a2c 46%, #08111d 100%);
}

.showcase-glow {
  position: absolute;
  top: -160px;
  left: -120px;
  width: 520px;
  height: 520px;
  border-radius: 50%;
  background: radial-gradient(closest-side, rgba(var(--app-primary-rgb, 37, 99, 235), 0.42), transparent 72%);
  filter: blur(4px);
  pointer-events: none;
}

.showcase-grid {
  position: absolute;
  inset: 0;
  background:
    linear-gradient(90deg, rgba(148, 197, 255, 0.1) 1px, transparent 1px),
    linear-gradient(0deg, rgba(148, 197, 255, 0.1) 1px, transparent 1px);
  background-size: 52px 52px;
  mask-image: radial-gradient(ellipse 90% 80% at 30% 20%, rgba(0, 0, 0, 0.85), transparent 78%);
  pointer-events: none;
}

.showcase-ring {
  position: absolute;
  border: 1px solid rgba(148, 197, 255, 0.14);
  border-radius: 50%;
  pointer-events: none;

  &.ring-a {
    right: -180px;
    bottom: -220px;
    width: 620px;
    height: 620px;
  }

  &.ring-b {
    right: -90px;
    bottom: -130px;
    width: 440px;
    height: 440px;
    border-color: rgba(148, 197, 255, 0.2);
  }
}

.showcase-inner {
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: space-between;
  gap: 44px;
  max-width: 560px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 14px;

  .brand-mark {
    display: inline-flex;
    width: 46px;
    height: 46px;
    align-items: center;
    justify-content: center;
    border: 1px solid rgba(255, 255, 255, 0.22);
    border-radius: 12px;
    color: #fff;
    background: linear-gradient(135deg, rgba(var(--app-primary-rgb, 37, 99, 235), 0.95), rgba(var(--app-accent-rgb, 5, 150, 105), 0.9));
    box-shadow: 0 14px 30px rgba(2, 8, 20, 0.45);
  }

  .brand-name {
    color: #f4f8ff;
    font-size: 20px;
    font-weight: 700;
    letter-spacing: 0.01em;
  }
}

.showcase-copy {
  .eyebrow {
    margin: 0 0 18px;
    color: rgba(186, 214, 255, 0.66);
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.3em;
  }

  h1 {
    margin: 0;
    color: #f8fbff;
    font-size: clamp(34px, 3vw, 48px);
    font-weight: 800;
    line-height: 1.22;
    letter-spacing: -0.01em;

    span {
      background: linear-gradient(96deg, #8ec5ff 0%, #b3b9ff 48%, #f5b8ff 100%);
      background-clip: text;
      -webkit-background-clip: text;
      color: transparent;
    }
  }

  .lead {
    margin: 20px 0 0;
    max-width: 420px;
    color: rgba(203, 218, 238, 0.72);
    font-size: 15px;
    line-height: 1.75;
  }
}

.feature-list {
  display: grid;
  gap: 14px;
  margin: 0;
  padding: 0;
  list-style: none;

  li {
    display: flex;
    align-items: flex-start;
    gap: 14px;
    padding: 14px 16px;
    border: 1px solid rgba(148, 197, 255, 0.14);
    border-radius: 12px;
    background: rgba(255, 255, 255, 0.04);
    backdrop-filter: blur(6px);
    transition: border-color 0.25s, background-color 0.25s, transform 0.25s;

    &:hover {
      border-color: rgba(148, 197, 255, 0.32);
      background: rgba(255, 255, 255, 0.07);
      transform: translateX(4px);
    }
  }

  .feature-icon {
    display: inline-flex;
    width: 34px;
    height: 34px;
    flex-shrink: 0;
    align-items: center;
    justify-content: center;
    border-radius: 9px;
    color: #9ec8ff;
    background: rgba(var(--app-primary-rgb, 37, 99, 235), 0.22);
  }

  strong {
    display: block;
    color: #eaf2fd;
    font-size: 14px;
    font-weight: 600;
  }

  em {
    display: block;
    margin-top: 4px;
    color: rgba(190, 210, 235, 0.62);
    font-size: 12px;
    font-style: normal;
  }
}

.showcase-foot {
  display: flex;
  align-items: center;
  gap: 12px;
  color: rgba(170, 195, 224, 0.5);
  font-size: 11px;
  letter-spacing: 0.14em;

  i {
    height: 1px;
    flex: 1;
    background: linear-gradient(90deg, rgba(148, 197, 255, 0.28), transparent);
  }
}

/* ---------- 右侧登录表单 ---------- */
.panel {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 48px 40px;
  background:
    radial-gradient(80% 60% at 100% 0%, rgba(var(--app-primary-rgb, 37, 99, 235), 0.07), transparent 62%),
    var(--app-surface, #fff);
  border-left: 1px solid var(--app-border, #dbe4ef);
}

.panel-inner {
  width: min(372px, 100%);
}

.card-header {
  margin-bottom: 30px;

  h2 {
    margin: 0 0 8px;
    color: var(--app-text, #1f2937);
    font-size: 28px;
    font-weight: 800;
    letter-spacing: -0.01em;
  }

  p {
    margin: 0;
    color: var(--app-text-muted, #64748b);
    font-size: 13px;
  }
}

.login-form {
  :deep(.el-form-item) {
    margin-bottom: 18px;
  }

  :deep(.el-input__wrapper) {
    padding: 10px 12px;
    border-radius: 8px;
    background: var(--app-surface-soft, #f8fafc);
    box-shadow: 0 0 0 1px var(--app-border, #dbe4ef) inset;
    transition: all 0.25s;

    &:hover {
      box-shadow: 0 0 0 1px rgba(var(--app-primary-rgb, 37, 99, 235), 0.45) inset;
    }

    &.is-focus {
      background: var(--app-surface, #fff);
      box-shadow: 0 0 0 1px var(--app-primary, #2563eb) inset, 0 0 0 3px rgba(var(--app-primary-rgb, 37, 99, 235), 0.12);
    }
  }

  .password-icon {
    cursor: pointer;
    color: var(--app-text-muted, #64748b);
    transition: color 0.25s;

    &:hover {
      color: var(--app-primary, #2563eb);
    }
  }

  .login-button {
    width: 100%;
    height: 48px;
    margin-top: 10px;
    font-size: 15px;
    font-weight: 700;
    letter-spacing: 0.06em;
    border: none;
    border-radius: 8px;
    background: linear-gradient(135deg, var(--app-primary, #2563eb) 0%, rgba(var(--app-primary-rgb, 37, 99, 235), 0.76) 100%);
    box-shadow: 0 10px 22px rgba(var(--app-primary-rgb, 37, 99, 235), 0.24);
    transition: all 0.25s;

    &:hover {
      transform: translateY(-1px);
      box-shadow: 0 14px 28px rgba(var(--app-primary-rgb, 37, 99, 235), 0.32);
    }

    &:active {
      transform: translateY(0);
    }
  }
}

.auth-links {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
  margin-top: 22px;

  :deep(.el-button) {
    color: var(--app-primary, #2563eb);
    font-weight: 600;
  }

  em {
    width: 1px;
    height: 12px;
    background: var(--app-border, #dbe4ef);
  }
}

@media (max-width: 960px) {
  .login-page {
    grid-template-columns: minmax(0, 1fr);
  }

  .showcase {
    padding: 40px 26px 36px;
  }

  .showcase-inner {
    gap: 28px;
  }

  .showcase-copy .lead {
    margin-top: 14px;
    font-size: 14px;
  }

  .feature-list,
  .showcase-foot,
  .showcase-ring {
    display: none;
  }

  .panel {
    padding: 36px 24px 48px;
    border-left: none;
    border-top: 1px solid var(--app-border, #dbe4ef);
  }
}
</style>
