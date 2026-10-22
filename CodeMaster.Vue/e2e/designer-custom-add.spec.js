import { expect, test } from '@playwright/test'

// 验证实体设计器属性面板的自定义新增能力（属性/指令/事件）。
// 覆盖：新增 → 面板出现自定义行（可编辑名 + 删除按钮）→ 删除恢复。
// 实体通过环境变量注入，默认用 HDTCargoERP/ShippingCompany（开发库 CodeMasterPrivateDB，已生成 .tree.json）。
const username = process.env.CODEMASTER_E2E_USERNAME || 'admin'
const password = process.env.CODEMASTER_E2E_PASSWORD || 'admin123'
const entityId = process.env.CODEMASTER_E2E_ENTITY_ID || '2084349524914081792'
const entityName = process.env.CODEMASTER_E2E_ENTITY_NAME || 'ShippingCompany'

async function login(page) {
  await page.goto('/#/login')
  const loginForm = page.locator('.login-form')
  await expect(loginForm).toBeVisible()
  await loginForm.locator('input[type="text"]').fill(username)
  await loginForm.locator('input[type="password"]').fill(password)
  await loginForm.locator('button.login-button').click()
  await page.waitForURL(/#\/(dashboard|home)/)
}

async function openDesigner(page) {
  await page.goto(
    `/#/codegen/entityDesigner?entityId=${entityId}&pageType=add&entityName=${entityName}`
  )
  await expect(page.getByText(`${entityName} — 新增页`, { exact: true })).toBeVisible({
    timeout: 20000
  })
}

test('属性面板支持自定义新增属性/指令/事件并可删除', async ({ page }) => {
  await login(page)
  await openDesigner(page)

  // 选中画布上第一个输入框控件（el-input 有元数据 → 智能面板）
  const firstInput = page.locator('.canvas-area .el-input__inner').first()
  await expect(firstInput).toBeVisible({ timeout: 15000 })
  await firstInput.click()

  const panel = page.locator('.property-sidebar')
  await expect(panel).toBeVisible()
  await expect(panel.locator('.el-tabs__item').filter({ hasText: '属性' }).first()).toBeVisible()

  // ---- 自定义属性 ----
  await panel.locator('.el-tabs__item').filter({ hasText: '属性' }).first().click()
  const propInput = panel.locator('input[placeholder^="自定义属性名"]')
  await expect(propInput).toBeVisible()
  await propInput.fill('data-test-id')
  await panel.getByRole('button', { name: '+ 属性', exact: true }).click()
  // 自定义行的名称是 input 的 value（非文本），定位含 .prop-name-input 的行，新增项在最后
  const customPropRows = panel.locator('.prop-row').filter({ has: panel.locator('.prop-name-input input') })
  const propRow = customPropRows.last()
  await expect(propRow.locator('.prop-name-input input')).toHaveValue('data-test-id')
  await expect(propRow.getByTitle('删除自定义属性')).toBeVisible()

  // ---- 自定义指令 ----
  await panel.locator('.el-tabs__item').filter({ hasText: '指令' }).first().click()
  const dirInput = panel.locator('input[placeholder^="自定义指令名"]')
  await expect(dirInput).toBeVisible()
  await dirInput.fill('v-my-test')
  await panel.getByRole('button', { name: '+ 指令', exact: true }).click()
  const dirRow = panel.locator('.prop-row').filter({ has: panel.locator('.prop-name-input input') }).last()
  await expect(dirRow.locator('.prop-name-input input')).toHaveValue('v-my-test')
  await expect(dirRow.getByTitle('删除自定义指令')).toBeVisible()

  // ---- 自定义事件 ----
  await panel.locator('.el-tabs__item').filter({ hasText: /^事件/ }).first().click()
  const evtInput = panel.locator('input[placeholder^="自定义事件名"]')
  await expect(evtInput).toBeVisible()
  await evtInput.fill('row-test')
  await panel.getByRole('button', { name: '+ 事件', exact: true }).click()
  const evtRow = panel.locator('.prop-row').filter({ has: panel.locator('.prop-name-input input') }).last()
  await expect(evtRow.locator('.prop-name-input input')).toHaveValue('row-test')
  await expect(evtRow.getByTitle('删除自定义事件')).toBeVisible()

  // ---- 清理：删除三个自定义条目，恢复原状 ----
  await evtRow.getByTitle('删除自定义事件').click()
  await expect(panel.locator('.prop-row').filter({ hasText: 'row-test' })).toHaveCount(0)

  await panel.locator('.el-tabs__item').filter({ hasText: '指令' }).first().click()
  await dirRow.getByTitle('删除自定义指令').click()
  await expect(panel.locator('.prop-row').filter({ hasText: 'v-my-test' })).toHaveCount(0)

  await panel.locator('.el-tabs__item').filter({ hasText: '属性' }).first().click()
  await propRow.getByTitle('删除自定义属性').click()
  await expect(panel.locator('.prop-row').filter({ hasText: 'data-test-id' })).toHaveCount(0)
})
