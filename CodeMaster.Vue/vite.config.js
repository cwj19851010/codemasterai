import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'path'

const apiProxyTarget = process.env.CODEMASTER_API_PROXY_TARGET || 'http://localhost:5000'

export default defineConfig(({ mode }) => ({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
      '@view-modules': resolve(
        __dirname,
        mode === 'public'
          ? 'src/router/viewModules.public.js'
          : 'src/router/viewModules.full.js'
      ),
      vue: 'vue/dist/vue.esm-bundler.js'
    }
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: apiProxyTarget,
        changeOrigin: true,
        secure: false
      },
      '/hubs': {
        target: apiProxyTarget,
        changeOrigin: true,
        ws: true
      }
    }
  },
  build: {
    chunkSizeWarningLimit: 1200,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) {
            return undefined
          }

          if (id.includes('element-plus') || id.includes('@element-plus')) {
            return 'vendor-element-plus'
          }

          if (id.includes('@wangeditor') || id.includes('@vueup/vue-quill') || id.includes('@tiptap')) {
            return 'vendor-editor'
          }

          if (id.includes('vue') || id.includes('pinia') || id.includes('vue-router') || id.includes('vue-i18n')) {
            return 'vendor-vue'
          }

          if (id.includes('axios') || id.includes('@microsoft/signalr') || id.includes('@tauri-apps')) {
            return 'vendor-runtime'
          }

          return 'vendor'
        }
      }
    }
  }
}))
