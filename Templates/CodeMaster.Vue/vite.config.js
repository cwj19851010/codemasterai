import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'path'

const apiProxyTarget = process.env.APP_API_PROXY_TARGET || 'http://127.0.0.1:5170'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
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
    chunkSizeWarningLimit: 1200
  }
})
