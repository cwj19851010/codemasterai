import { h, ref } from 'vue'
import { ElMessage, ElMessageBox, ElOption, ElSelect } from 'element-plus'
import router from '@/router'
import { getList as getPrintTemplateList } from '@/api/codegen/printTemplate'

/**
 * 通用打印报表调用封装（OpenSpec add-print-template-designer 任务 5.2 / 5.5）
 *
 * 业务页面统一通过本封装打开报表，不感知路由细节：
 *   import { openPrintReport } from '@/utils/printReport'
 *   openPrintReport(1001, { orderId: row.id })                 // 新标签页打开
 *   openPrintReport(1001, { orderId: row.id }, { self: true }) // 当前路由跳转
 *
 * 按实体/模块打开（多模板选择 → 单模板直达，任务 5.2）：
 *   openEntityPrintReport({ projectId, moduleId }, { orderId: row.id })
 *
 * 对话框/iframe 场景使用 printReportUrl 生成地址：
 *   <iframe :src="printReportUrl(1001, { orderId: row.id })" />
 */

/** 生成报表运行页地址（hash 路由，含参数 query） */
export function printReportUrl(templateId, params = {}) {
  const query = {}
  Object.entries(params || {}).forEach(([k, v]) => {
    if (v !== undefined && v !== null && v !== '') query[k] = String(v)
  })
  return router.resolve({ path: `/print-preview/${templateId}`, query }).href
}

/**
 * 打开通用报表运行页
 * @param {string|number} templateId 打印模板 ID
 * @param {object} params 运行时参数（如 { orderId: 123 }），与模板 dataSources.params 同名
 * @param {object} options { self: 当前页跳转而非新标签页 }
 */
export function openPrintReport(templateId, params = {}, options = {}) {
  const href = printReportUrl(templateId, params)
  if (options.self) return router.push(href)
  const win = window.open(href, '_blank')
  if (!win) {
    // 弹出窗口被拦截时回退为当前路由跳转
    ElMessage.warning('新标签页被浏览器拦截，已在当前页打开')
    return router.push(href)
  }
  return win
}

/**
 * 按项目/模块打开打印报表（任务 5.2 多模板选择）：
 * - 查询该范围启用状态的打印模板
 * - 无模板 → 提示未配置；单模板 → 直接进入预览页；多模板 → 弹出选择框
 * @param {{ projectId?: number|string, moduleId?: number|string }} scope 模板归属范围
 * @param {object} params 运行时参数
 * @param {object} options 透传 openPrintReport
 * @returns {Promise<void|object>} 选中的模板 ID（取消时为 null）
 */
export async function openEntityPrintReport(scope = {}, params = {}, options = {}) {
  const query = { status: 0 }
  if (scope.projectId !== undefined && scope.projectId !== null) query.projectId = scope.projectId
  if (scope.moduleId !== undefined && scope.moduleId !== null) query.moduleId = scope.moduleId

  let templates = []
  try {
    templates = (await getPrintTemplateList(query)) || []
  } catch (error) {
    ElMessage.error(error?.response?.data?.message || error?.message || '打印模板查询失败')
    return null
  }

  if (!templates.length) {
    ElMessage.warning('该范围尚未配置启用状态的打印模板，请先在设计器中创建')
    return null
  }

  let templateId = templates[0].id
  if (templates.length > 1) {
    templateId = await chooseTemplate(templates)
    if (templateId === null) return null // 用户取消
  }

  openPrintReport(templateId, params, options)
  return templateId
}

/** 多模板选择框：返回选中的模板 ID，取消返回 null */
function chooseTemplate(templates) {
  return new Promise(resolve => {
    const selected = ref(templates[0].id)
    ElMessageBox({
      title: '选择打印模板',
      message: () => h('div', { style: 'padding: 4px 0' }, [
        h(ElSelect, {
          modelValue: selected.value,
          'onUpdate:modelValue': v => { selected.value = v },
          style: 'width: 100%',
          placeholder: '请选择打印模板'
        }, () => templates.map(t => h(ElOption, {
          key: t.id,
          label: `${t.templateName}${t.layoutMode === 'absolute' ? '（套打）' : ''}`,
          value: t.id
        })))
      ]),
      confirmButtonText: '打开预览',
      cancelButtonText: '取消',
      type: 'info'
    })
      .then(() => resolve(selected.value))
      .catch(() => resolve(null))
  })
}
