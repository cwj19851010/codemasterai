import { defineStore } from 'pinia'

// 新增/编辑/详情页每次进入都要按 query 里的 id 重新加载数据，不能进 keep-alive：
// 缓存按组件名命中，同路由不同 query 的多个标签会共用同一个实例，切回来仍是第一次的数据
const RECORD_PATH_PATTERN = /\/(add|edit|detail)\/?$/i

const normalizeQuery = (query = {}) => {
  const result = {}
  Object.keys(query || {}).sort().forEach(key => {
    const value = query[key]
    if (value !== undefined) {
      result[key] = value
    }
  })
  return result
}

const getViewKey = (view = {}) => view.path || ''

const toTagView = (view) => ({
  name: view.name,
  path: view.path,
  title: view.meta?.title || view.title || 'no-name',
  query: normalizeQuery(view.query),
  params: view.params || {},
  fullPath: view.fullPath || view.path,
  meta: { ...(view.meta || {}) }
})

export const useTagsViewStore = defineStore('tagsView', {
  state: () => ({
    visitedViews: [],
    cachedViews: []
  }),

  persist: {
    paths: ['visitedViews', 'cachedViews']
  },

  getters: {
    getVisitedViews: (state) => state.visitedViews,
    getCachedViews: (state) => state.cachedViews
  },

  actions: {
    addView(view) {
      this.addVisitedView(view)
      this.addCachedView(view)
    },

    addVisitedView(view) {
      if (!view?.path || view.meta?.hideTab) return

      const tagView = toTagView(view)
      const index = this.visitedViews.findIndex(item => getViewKey(item) === getViewKey(tagView))

      // 同一路由只保留一个标签，参数（如 id）变化时更新为最近一次访问
      if (index > -1) {
        this.visitedViews[index] = { ...this.visitedViews[index], ...tagView }
        return
      }

      this.visitedViews.push(tagView)
    },

    addCachedView(view) {
      if (!view?.name) return
      if (view?.meta?.noCache || RECORD_PATH_PATTERN.test(view.path || '')) {
        this.delCachedView(view)
        return
      }
      if (!this.cachedViews.includes(view.name)) {
        this.cachedViews.push(view.name)
      }
    },

    delView(view) {
      this.delVisitedView(view)
      this.delCachedView(view)
      return Promise.resolve(this.getSnapshot())
    },

    delVisitedView(view) {
      const targetKey = getViewKey(view)
      this.visitedViews = this.visitedViews.filter(item => getViewKey(item) !== targetKey)
    },

    delCachedView(view) {
      if (!view?.name) return Promise.resolve(this.getSnapshot())

      const index = this.cachedViews.indexOf(view.name)
      if (index > -1) {
        this.cachedViews.splice(index, 1)
      }

      return Promise.resolve(this.getSnapshot())
    },

    delOthersViews(view) {
      this.delOthersVisitedViews(view)
      this.delOthersCachedViews(view)
      return Promise.resolve(this.getSnapshot())
    },

    delOthersVisitedViews(view) {
      const targetKey = getViewKey(view)
      this.visitedViews = this.visitedViews.filter(item => item.meta?.affix || getViewKey(item) === targetKey)
    },

    delOthersCachedViews(view) {
      this.cachedViews = view?.name && this.cachedViews.includes(view.name) ? [view.name] : []
    },

    delAllViews() {
      this.delAllVisitedViews()
      this.delAllCachedViews()
      return Promise.resolve(this.getSnapshot())
    },

    delAllVisitedViews() {
      this.visitedViews = this.visitedViews.filter(item => item.meta?.affix)
    },

    delAllCachedViews() {
      this.cachedViews = []
    },

    updateVisitedView(view) {
      const targetKey = getViewKey(view)
      const index = this.visitedViews.findIndex(item => getViewKey(item) === targetKey)
      if (index > -1) {
        this.visitedViews[index] = { ...this.visitedViews[index], ...toTagView(view) }
      }
    },

    // 清理历史持久化里的重复标签（同一路由只保留最后访问的一次）
    dedupeVisitedViews() {
      const lastIndex = new Map()
      this.visitedViews.forEach((view, index) => lastIndex.set(getViewKey(view), index))
      this.visitedViews = this.visitedViews.filter((view, index) => lastIndex.get(getViewKey(view)) === index)
    },

    getSnapshot() {
      return {
        visitedViews: [...this.visitedViews],
        cachedViews: [...this.cachedViews]
      }
    }
  }
})
