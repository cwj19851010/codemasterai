<template>
  <el-popover placement="bottom-end" :width="296" trigger="click" popper-class="theme-gallery-popper">
    <template #reference>
      <button class="theme-trigger" type="button" :aria-label="t('theme_switch')" :title="t('theme_switch')">
        <el-icon :size="18">
          <component :is="activeIcon" />
        </el-icon>
      </button>
    </template>

    <div class="theme-gallery">
      <div class="gallery-title">{{ t('theme_switch') }}</div>
      <div class="gallery-grid">
        <button
          v-for="theme in themeOptions"
          :key="theme.name"
          type="button"
          class="gallery-item"
          :class="{ active: currentTheme === theme.name }"
          @click="handleThemeChange(theme.name)"
        >
          <span class="gallery-swatch" :style="{ background: getSwatch(theme.name) }">
            <el-icon v-if="currentTheme === theme.name" class="gallery-check"><Check /></el-icon>
          </span>
          <span class="gallery-label">{{ themeLabel(theme) }}</span>
        </button>
      </div>
    </div>
  </el-popover>
</template>

<script setup>
import { computed } from 'vue'
import { Check, Cloudy, Moon, Sunny } from '@element-plus/icons-vue'
import { useSettingsStore } from '@/stores/settings'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'

const iconMap = {
  Sunny,
  Moon,
  Cloudy
}

const swatchMap = {
  'tech-blue': 'linear-gradient(135deg, #4f46e5 0%, #059669 100%)',
  'classic-blue': 'linear-gradient(135deg, #2563eb 0%, #0f766e 100%)',
  'code-green': 'linear-gradient(135deg, #059669 0%, #0d9488 100%)',
  ocean: 'linear-gradient(135deg, #0d9488 0%, #0ea5e9 100%)',
  'china-red': 'linear-gradient(135deg, #dc2626 0%, #ffffff 70%)',
  dark: 'linear-gradient(135deg, #111827 0%, #38bdf8 100%)',
  'business-gray': 'linear-gradient(135deg, #3f4c57 0%, #16a34a 100%)'
}

const settingsStore = useSettingsStore()
const { t, locale } = useI18n()

const currentTheme = computed(() => settingsStore.currentTheme)
const themeOptions = computed(() => settingsStore.themeOptions)
const activeTheme = computed(() => themeOptions.value.find(item => item.name === currentTheme.value) || themeOptions.value[0])
const activeIcon = computed(() => iconMap[activeTheme.value?.icon] || Sunny)

const getSwatch = (theme) => swatchMap[theme] || swatchMap['tech-blue']

const themeLabel = (theme) => {
  if (locale.value === 'en-US') {
    const v = t(theme.labelKey)
    if (v && v !== theme.labelKey) return v
    return theme.label
  }
  return theme.label || t(theme.labelKey)
}

const handleThemeChange = (theme) => {
  if (theme === currentTheme.value) return

  settingsStore.changeTheme(theme)
  const nextTheme = themeOptions.value.find(item => item.name === theme)
  ElMessage.success(t('theme_switch_success', { theme: themeLabel(nextTheme || {}) }))
}
</script>

<style scoped lang="scss">
.theme-trigger {
  width: 34px;
  height: 34px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: var(--app-text-muted, #64748b);
  background: transparent;
  border: 1px solid transparent;
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.2s ease;

  &:hover {
    color: var(--app-primary, #2563eb);
    background: rgba(var(--app-primary-rgb, 37, 99, 235), 0.08);
    border-color: rgba(var(--app-primary-rgb, 37, 99, 235), 0.16);
  }

  &:focus-visible {
    outline: 2px solid rgba(var(--app-primary-rgb, 37, 99, 235), 0.36);
    outline-offset: 2px;
  }
}

.theme-gallery {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.gallery-title {
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
  font-weight: 600;
}

.gallery-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.gallery-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  cursor: pointer;
  text-align: left;
  transition: all 0.2s ease;

  &:hover {
    background: rgba(var(--app-primary-rgb, 37, 99, 235), 0.06);
  }

  &:focus-visible {
    outline: 2px solid rgba(var(--app-primary-rgb, 37, 99, 235), 0.36);
    outline-offset: 2px;
  }

  &.active {
    border-color: rgba(var(--app-primary-rgb, 37, 99, 235), 0.4);
    background: rgba(var(--app-primary-rgb, 37, 99, 235), 0.08);
  }
}

.gallery-swatch {
  display: inline-flex;
  width: 26px;
  height: 26px;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  border-radius: 50%;
  box-shadow: 0 0 0 1px rgba(148, 163, 184, 0.35) inset;
}

.gallery-check {
  color: #ffffff;
  filter: drop-shadow(0 1px 2px rgba(0, 0, 0, 0.5));
}

.gallery-label {
  overflow: hidden;
  color: var(--app-text, #1f2937);
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
