import { defineStore } from 'pinia'

export const appThemes = [
  {
    name: 'tech-blue',
    label: '靛蓝科技',
    labelKey: 'theme_tech_blue',
    icon: 'Sunny',
    variables: {
      '--sidebar-bg': '#101c33',
      '--sidebar-border': 'rgba(148, 163, 184, 0.16)',
      '--sidebar-text-color': '#c6d3e6',
      '--sidebar-active-text-color': '#ffffff',
      '--sidebar-active-bg': 'rgba(79, 70, 229, 0.28)',
      '--sidebar-hover-bg': 'rgba(129, 140, 248, 0.14)',
      '--app-bg': '#f4f7fd',
      '--app-surface': '#ffffff',
      '--app-surface-soft': '#eef3fc',
      '--app-border': '#d9e2f0',
      '--app-text': '#1f2937',
      '--app-text-muted': '#64748b',
      '--app-primary': '#4f46e5',
      '--app-primary-rgb': '79, 70, 229',
      '--app-accent': '#059669',
      '--app-accent-rgb': '5, 150, 105',
      '--app-warning': '#b7791f',
      '--app-header-bg': 'rgba(255, 255, 255, 0.94)',
      '--app-card-shadow': '0 10px 30px rgba(31, 41, 55, 0.08)',
      '--app-header-shadow': '0 1px 8px rgba(15, 23, 42, 0.05)',
      '--el-color-primary': '#4f46e5',
      '--el-color-primary-light-3': '#847eed',
      '--el-color-primary-light-5': '#a7a2f2',
      '--el-color-primary-light-7': '#cac8f7',
      '--el-color-primary-light-8': '#dcdafa',
      '--el-color-primary-light-9': '#edecfc',
      '--el-color-primary-dark-2': '#3f38b7'
    }
  },
  {
    name: 'classic-blue',
    label: '经典蓝',
    labelKey: 'theme_classic_blue',
    icon: 'Sunny',
    variables: {
      '--sidebar-bg': '#152238',
      '--sidebar-border': 'rgba(148, 163, 184, 0.16)',
      '--sidebar-text-color': '#c6d3e6',
      '--sidebar-active-text-color': '#ffffff',
      '--sidebar-active-bg': 'rgba(59, 130, 246, 0.22)',
      '--sidebar-hover-bg': 'rgba(96, 165, 250, 0.14)',
      '--app-bg': '#f4f7fb',
      '--app-surface': '#ffffff',
      '--app-surface-soft': '#f8fafc',
      '--app-border': '#dbe4ef',
      '--app-text': '#1f2937',
      '--app-text-muted': '#64748b',
      '--app-primary': '#2563eb',
      '--app-primary-rgb': '37, 99, 235',
      '--app-accent': '#0f766e',
      '--app-accent-rgb': '15, 118, 110',
      '--app-warning': '#b7791f',
      '--app-header-bg': 'rgba(255, 255, 255, 0.94)',
      '--app-card-shadow': '0 10px 30px rgba(31, 41, 55, 0.08)',
      '--app-header-shadow': '0 1px 8px rgba(15, 23, 42, 0.05)',
      '--el-color-primary': '#2563eb',
      '--el-color-primary-light-3': '#4f83f1',
      '--el-color-primary-light-5': '#8fb3fa',
      '--el-color-primary-light-7': '#bfd3fd',
      '--el-color-primary-light-8': '#d5e3fe',
      '--el-color-primary-light-9': '#eaf1ff',
      '--el-color-primary-dark-2': '#1d4ed8'
    }
  },
  {
    name: 'code-green',
    label: '生成绿',
    labelKey: 'theme_code_green',
    icon: 'Sunny',
    variables: {
      '--sidebar-bg': '#0b2e23',
      '--sidebar-border': 'rgba(148, 163, 184, 0.16)',
      '--sidebar-text-color': '#c8dcd3',
      '--sidebar-active-text-color': '#ffffff',
      '--sidebar-active-bg': 'rgba(5, 150, 105, 0.26)',
      '--sidebar-hover-bg': 'rgba(16, 185, 129, 0.14)',
      '--app-bg': '#f2faf6',
      '--app-surface': '#ffffff',
      '--app-surface-soft': '#e9f7f0',
      '--app-border': '#cde5da',
      '--app-text': '#1f2b27',
      '--app-text-muted': '#5d7169',
      '--app-primary': '#059669',
      '--app-primary-rgb': '5, 150, 105',
      '--app-accent': '#0d9488',
      '--app-accent-rgb': '13, 148, 136',
      '--app-warning': '#b45309',
      '--app-header-bg': 'rgba(255, 255, 255, 0.94)',
      '--app-card-shadow': '0 10px 30px rgba(6, 46, 36, 0.08)',
      '--app-header-shadow': '0 1px 8px rgba(6, 46, 36, 0.05)',
      '--el-color-primary': '#059669',
      '--el-color-primary-light-3': '#50b696',
      '--el-color-primary-light-5': '#82cab4',
      '--el-color-primary-light-7': '#b4e0d2',
      '--el-color-primary-light-8': '#cdeae1',
      '--el-color-primary-light-9': '#e6f4f0',
      '--el-color-primary-dark-2': '#047854'
    }
  },
  {
    name: 'ocean',
    label: '海青',
    labelKey: 'theme_ocean',
    icon: 'Sunny',
    variables: {
      '--sidebar-bg': '#0b3a43',
      '--sidebar-border': 'rgba(148, 163, 184, 0.16)',
      '--sidebar-text-color': '#c5dde3',
      '--sidebar-active-text-color': '#ffffff',
      '--sidebar-active-bg': 'rgba(13, 148, 136, 0.28)',
      '--sidebar-hover-bg': 'rgba(45, 212, 191, 0.14)',
      '--app-bg': '#f1f9fb',
      '--app-surface': '#ffffff',
      '--app-surface-soft': '#e8f4f8',
      '--app-border': '#cfe3ea',
      '--app-text': '#1c2a2e',
      '--app-text-muted': '#5c717a',
      '--app-primary': '#0d9488',
      '--app-primary-rgb': '13, 148, 136',
      '--app-accent': '#059669',
      '--app-accent-rgb': '5, 150, 105',
      '--app-warning': '#b7791f',
      '--app-header-bg': 'rgba(255, 255, 255, 0.94)',
      '--app-card-shadow': '0 10px 30px rgba(11, 58, 67, 0.08)',
      '--app-header-shadow': '0 1px 8px rgba(11, 58, 67, 0.05)',
      '--el-color-primary': '#0d9488',
      '--el-color-primary-light-3': '#56b4ac',
      '--el-color-primary-light-5': '#86cac4',
      '--el-color-primary-light-7': '#b6dfdb',
      '--el-color-primary-light-8': '#cfeae7',
      '--el-color-primary-light-9': '#e7f4f3',
      '--el-color-primary-dark-2': '#0a766d'
    }
  },
  {
    name: 'dark',
    label: '暗黑模式',
    labelKey: 'theme_dark',
    icon: 'Moon',
    dark: true,
    variables: {
      '--sidebar-bg': '#0d1420',
      '--sidebar-border': 'rgba(148, 163, 184, 0.14)',
      '--sidebar-text-color': '#a8b3c5',
      '--sidebar-active-text-color': '#e0f2fe',
      '--sidebar-active-bg': 'rgba(56, 189, 248, 0.16)',
      '--sidebar-hover-bg': 'rgba(56, 189, 248, 0.12)',
      '--app-bg': '#0f141b',
      '--app-surface': '#171d25',
      '--app-surface-soft': '#1f2732',
      '--app-border': '#2f3a48',
      '--app-text': '#e5e7eb',
      '--app-text-muted': '#94a3b8',
      '--app-primary': '#38bdf8',
      '--app-primary-rgb': '56, 189, 248',
      '--app-accent': '#34d399',
      '--app-accent-rgb': '52, 211, 153',
      '--app-warning': '#f59e0b',
      '--app-header-bg': 'rgba(23, 29, 37, 0.94)',
      '--app-card-shadow': '0 16px 34px rgba(0, 0, 0, 0.26)',
      '--app-header-shadow': '0 1px 10px rgba(0, 0, 0, 0.24)',
      '--el-color-primary': '#38bdf8',
      '--el-color-primary-light-3': '#67cdfa',
      '--el-color-primary-light-5': '#9be1fc',
      '--el-color-primary-light-7': '#c3effe',
      '--el-color-primary-light-8': '#d7f5fe',
      '--el-color-primary-light-9': '#ecfbff',
      '--el-color-primary-dark-2': '#0ea5e9'
    }
  },
  {
    name: 'china-red',
    label: '中国红',
    labelKey: 'theme_china_red',
    icon: 'Sunny',
    variables: {
      '--sidebar-bg': '#ffffff',
      '--sidebar-border': '#f0dddd',
      '--sidebar-text-color': '#52525b',
      '--sidebar-active-text-color': '#dc2626',
      '--sidebar-active-bg': 'rgba(220, 38, 38, 0.08)',
      '--sidebar-hover-bg': 'rgba(220, 38, 38, 0.05)',
      '--app-bg': '#fdf8f8',
      '--app-surface': '#ffffff',
      '--app-surface-soft': '#fdf1f1',
      '--app-border': '#f0dcdc',
      '--app-text': '#1f2937',
      '--app-text-muted': '#78716c',
      '--app-primary': '#dc2626',
      '--app-primary-rgb': '220, 38, 38',
      '--app-accent': '#b91c1c',
      '--app-accent-rgb': '185, 28, 28',
      '--app-warning': '#d97706',
      '--app-header-bg': 'rgba(255, 255, 255, 0.94)',
      '--app-card-shadow': '0 10px 30px rgba(120, 53, 53, 0.08)',
      '--app-header-shadow': '0 1px 8px rgba(120, 53, 53, 0.05)',
      '--el-color-primary': '#dc2626',
      '--el-color-primary-light-3': '#e66767',
      '--el-color-primary-light-5': '#ee9292',
      '--el-color-primary-light-7': '#f4bebe',
      '--el-color-primary-light-8': '#f8d4d4',
      '--el-color-primary-light-9': '#fce9e9',
      '--el-color-primary-dark-2': '#b01e1e'
    }
  },
  {
    name: 'business-gray',
    label: '商务灰',
    labelKey: 'theme_business_gray',
    icon: 'Cloudy',
    variables: {
      '--sidebar-bg': '#303a45',
      '--sidebar-border': 'rgba(203, 213, 225, 0.18)',
      '--sidebar-text-color': '#d1d9e0',
      '--sidebar-active-text-color': '#ffffff',
      '--sidebar-active-bg': 'rgba(22, 163, 74, 0.2)',
      '--sidebar-hover-bg': 'rgba(34, 197, 94, 0.12)',
      '--app-bg': '#f3f5f7',
      '--app-surface': '#ffffff',
      '--app-surface-soft': '#f7f9fb',
      '--app-border': '#d8dee6',
      '--app-text': '#24303b',
      '--app-text-muted': '#667281',
      '--app-primary': '#2563eb',
      '--app-primary-rgb': '37, 99, 235',
      '--app-accent': '#16a34a',
      '--app-accent-rgb': '22, 163, 74',
      '--app-warning': '#b7791f',
      '--app-header-bg': 'rgba(255, 255, 255, 0.94)',
      '--app-card-shadow': '0 10px 26px rgba(36, 48, 59, 0.08)',
      '--app-header-shadow': '0 1px 8px rgba(36, 48, 59, 0.05)',
      '--el-color-primary': '#2563eb',
      '--el-color-primary-light-3': '#4f83f1',
      '--el-color-primary-light-5': '#8fb3fa',
      '--el-color-primary-light-7': '#bfd3fd',
      '--el-color-primary-light-8': '#d5e3fe',
      '--el-color-primary-light-9': '#eaf1ff',
      '--el-color-primary-dark-2': '#1d4ed8'
    }
  }
]

const defaultTheme = 'tech-blue'
const findTheme = (theme) => appThemes.find(item => item.name === theme) || appThemes[0]

export const useSettingsStore = defineStore('settings', {
  state: () => ({
    theme: defaultTheme,
    sidebarOpened: true,
    language: 'zh-CN'
  }),

  persist: {
    paths: ['theme', 'sidebarOpened', 'language']
  },

  getters: {
    currentTheme: (state) => state.theme,
    themeOptions: () => appThemes,
    isSidebarOpened: (state) => state.sidebarOpened,
    currentLanguage: (state) => state.language
  },

  actions: {
    changeTheme(theme) {
      const nextTheme = findTheme(theme).name
      this.theme = nextTheme
      this.applyTheme(nextTheme)
    },

    applyTheme(theme) {
      const themeConfig = findTheme(theme)
      const root = document.documentElement

      Object.entries(themeConfig.variables).forEach(([key, value]) => {
        root.style.setProperty(key, value)
      })

      root.dataset.theme = themeConfig.name
      root.classList.toggle('dark', Boolean(themeConfig.dark))
    },

    toggleSidebar() {
      this.sidebarOpened = !this.sidebarOpened
    },

    changeLanguage(language) {
      this.language = language
    },

    initTheme() {
      this.applyTheme(this.theme)
    }
  }
})
