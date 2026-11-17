import { defineStore } from 'pinia'

// 新增/编辑/详情页每次进入都要按 query 里的 id 重新加载数据，不能进 keep-alive：
// 缓存按组件名命中，同路由不同 query 的多个标签会共用同一个实例，切回来仍是第一次的数据
const RECORD_PATH_PATTERN = /\/(add|edit|detail)\/?$/i

export const useTagsViewStore = defineStore('tagsView', {
  state: () => ({
    visitedViews: [], // 访问过的视图
    cachedViews: [] // 需要缓存的视图
  }),

  persist: {
    paths: ['visitedViews', 'cachedViews']
  },

  getters: {
    // 获取所有访问过的视图
    getVisitedViews: (state) => state.visitedViews,
    // 获取所有缓存的视图
    getCachedViews: (state) => state.cachedViews
  },

  actions: {
    // 添加视图
    addView(view) {
      this.addVisitedView(view)
      this.addCachedView(view)
    },

    // 添加访问过的视图
    addVisitedView(view) {
      // 同一路由只保留一个标签，参数（如 id）变化时更新为最近一次访问
      const exists = this.visitedViews.find(v => v.path === view.path)

      if (exists) {
        Object.assign(exists, {
          name: view.name,
          title: view.meta?.title || exists.title || 'no-name',
          query: view.query,
          params: view.params,
          fullPath: view.fullPath,
          meta: { ...view.meta }
        })
        return
      }

      this.visitedViews.push({
        name: view.name,
        path: view.path,
        title: view.meta?.title || 'no-name',
        query: view.query,
        params: view.params,
        fullPath: view.fullPath,
        meta: { ...view.meta }
      })
    },

    // 添加缓存视图
    addCachedView(view) {
      if (!view?.name) return

      // 如果路由配置了不缓存，则不添加
      if (view.meta?.noCache || RECORD_PATH_PATTERN.test(view.path || '')) {
        this.delCachedView(view)
        return
      }

      // 如果已经缓存，则不重复添加
      if (this.cachedViews.includes(view.name)) return

      this.cachedViews.push(view.name)
    },

    // 删除视图
    delView(view) {
      return new Promise((resolve) => {
        this.delVisitedView(view)
        this.delCachedView(view)
        resolve({
          visitedViews: [...this.visitedViews],
          cachedViews: [...this.cachedViews]
        })
      })
    },

    // 删除访问过的视图
    delVisitedView(view) {
      this.visitedViews = this.visitedViews.filter(v => v.path !== view.path)
    },

    // 删除缓存视图
    delCachedView(view) {
      const index = this.cachedViews.indexOf(view.name)
      if (index > -1) {
        this.cachedViews.splice(index, 1)
      }
    },

    // 删除其他视图
    delOthersViews(view) {
      return new Promise((resolve) => {
        this.delOthersVisitedViews(view)
        this.delOthersCachedViews(view)
        resolve({
          visitedViews: [...this.visitedViews],
          cachedViews: [...this.cachedViews]
        })
      })
    },

    // 删除其他访问过的视图
    delOthersVisitedViews(view) {
      this.visitedViews = this.visitedViews.filter(v => v.meta?.affix || v.path === view.path)
    },

    // 删除其他缓存视图
    delOthersCachedViews(view) {
      const index = this.cachedViews.indexOf(view.name)
      if (index > -1) {
        this.cachedViews = this.cachedViews.slice(index, index + 1)
      } else {
        this.cachedViews = []
      }
    },

    // 删除所有视图
    delAllViews() {
      return new Promise((resolve) => {
        this.delAllVisitedViews()
        this.delAllCachedViews()
        resolve({
          visitedViews: [...this.visitedViews],
          cachedViews: [...this.cachedViews]
        })
      })
    },

    // 删除所有访问过的视图
    delAllVisitedViews() {
      // 保留固定的标签
      this.visitedViews = this.visitedViews.filter(tag => tag.meta?.affix)
    },

    // 删除所有缓存视图
    delAllCachedViews() {
      this.cachedViews = []
    },

    // 更新访问过的视图
    updateVisitedView(view) {
      const target = this.visitedViews.find(v => v.path === view.path)
      if (target) {
        Object.assign(target, view.meta ? { ...view, meta: { ...view.meta } } : view)
      }
    },

    // 清理历史持久化里的重复标签（同一路由只保留最后访问的一次）
    dedupeVisitedViews() {
      const lastIndex = new Map()
      this.visitedViews.forEach((view, index) => lastIndex.set(view.path, index))
      this.visitedViews = this.visitedViews.filter((view, index) => lastIndex.get(view.path) === index)
    }
  }
})
