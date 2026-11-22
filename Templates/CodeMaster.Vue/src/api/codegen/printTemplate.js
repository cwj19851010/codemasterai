import request from '@/utils/request'

// 打印模板运行时 API（DynamicApi: api/codegen/printrender/*）
// 生成项目经共享源码获得 PrintRenderService，接口路径与平台一致。

/** 查询模板列表（多模板选择用，status=0 为启用） */
export function getList(params = {}) {
  return request({ url: '/codegen/printtemplate/getlist', method: 'get', params })
}

/**
 * 取数并渲染整册 HTML（运行时打印/预览）
 * @param {{ templateId: number|string, parameters: object }} data
 */
export function renderHtml(data) {
  return request({ url: '/codegen/printrender/renderhtml', method: 'post', data })
}

/** 取数并导出 PDF（base64） */
export function exportPdf(data) {
  return request({ url: '/codegen/printrender/exportpdf', method: 'post', data })
}

/** 取数并导出 Excel（base64） */
export function exportExcel(data) {
  return request({ url: '/codegen/printrender/exportexcel', method: 'post', data })
}

/** 取数并导出 Word（base64） */
export function exportWord(data) {
  return request({ url: '/codegen/printrender/exportword', method: 'post', data })
}

/** 按模板内容渲染整册 HTML（不取业务数据，详情页查看样式用） */
export function renderPreview(data) {
  return request({ url: '/codegen/printrender/renderpreview', method: 'post', data })
}

/** 按模板 ID 获取模板（名称/布局/参数声明） */
export function getById(id) {
  return request({ url: `/codegen/printtemplate/getbyid/${id}`, method: 'get' })
}
