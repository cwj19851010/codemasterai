import { expect, test } from '@playwright/test'

const username = process.env.CODEMASTER_E2E_USERNAME || 'admin'
const password = process.env.CODEMASTER_E2E_PASSWORD || 'admin123'
const projectName = process.env.CODEMASTER_E2E_PROJECT || 'ControlShowcase'

const expectedFields = [
  ['TextInput', 'string', 'input'],
  ['LongText', 'string', 'textarea'],
  ['NumberValue', 'decimal', 'number'],
  ['CategoryCode', 'string', 'select'],
  ['StatusCode', 'string', 'select'],
  ['TableSelectedUserId', 'long', 'select-table'],
  ['DateValue', 'DateOnly', 'date'],
  ['DateTimeValue', 'DateTime', 'datetime'],
  ['Enabled', 'bool', 'switch'],
  ['Confirmed', 'bool', 'checkbox'],
  ['Priority', 'string', 'radio-group'],
  ['Tags', 'string', 'checkbox-group'],
  ['AttachmentUrl', 'string', 'file'],
  ['ImageUrl', 'string', 'image'],
  ['RichContent', 'string', 'editor'],
  ['DeptId', 'long', 'cascader'],
  ['ListOnlyValue', 'string', 'table-column']
]

const expectedDesignerTags = [
  'el-input',
  'el-input-number',
  'el-select',
  'el-date-picker',
  'el-switch',
  'el-checkbox',
  'el-radio-group',
  'el-checkbox-group',
  'el-upload',
  'el-cascader',
  'el-table',
  'el-dialog'
]

async function login(page) {
  await page.goto('/#/login')

  const loginForm = page.locator('.login-form')
  await expect(loginForm).toBeVisible()
  await loginForm.locator('input[type="text"]').fill(username)
  await loginForm.locator('input[type="password"]').fill(password)
  await loginForm.locator('button.login-button').click()

  await page.waitForURL(/#\/dashboard$/)
}

test('ControlShowcase 主子表、全部控件和设计器保持可用', async ({ page }) => {
  await login(page)
  await page.goto('/#/codegen/moduleentity')

  const projectSelect = page.getByRole('combobox', { name: '所属项目', exact: true })
  await expect(projectSelect).toBeVisible()
  const projectSelectWrapper = page
    .locator('.query-form .el-form-item')
    .filter({ has: projectSelect })
    .locator('.el-select__wrapper')
  await expect(projectSelectWrapper).toHaveCount(1)
  await projectSelectWrapper.click()
  await page.getByRole('option', { name: projectName, exact: true }).click()
  await page.getByRole('button', { name: '查询', exact: true }).click()

  const masterRow = page.getByRole('row').filter({ hasText: 'ControlMasterDemo' })
  const detailRow = page.getByRole('row').filter({ hasText: 'ControlDetailDemo' })
  await expect(masterRow).toHaveCount(1)
  await expect(detailRow).toHaveCount(1)
  await expect(page.getByText('共 2 条', { exact: true })).toBeVisible()

  await masterRow.getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.getByText('编辑模块实体', { exact: true })).toBeVisible()
  await expect(page.getByRole('textbox', { name: '* 实体名称', exact: true })).toHaveValue(
    'ControlMasterDemo'
  )

  for (const [fieldName, dataType, controlType] of expectedFields) {
    const fieldRow = page.getByRole('row').filter({ hasText: fieldName })
    await expect(fieldRow, `缺少字段 ${fieldName}`).toHaveCount(1)
    await expect(fieldRow).toContainText(dataType)
    await expect(fieldRow).toContainText(controlType)
  }

  const relationRow = page
    .getByRole('row')
    .filter({ hasText: 'ControlDetailDemo' })
    .filter({ hasText: 'ControlMasterDemoId' })
  await expect(relationRow).toHaveCount(1)
  await expect(relationRow).toContainText('Id')

  const idMatch = page.url().match(/[?&]id=(\d+)/)
  expect(idMatch, '编辑页 URL 中应包含实体 ID').not.toBeNull()

  await page.goto(
    `/#/codegen/entitydesigner?entityId=${idMatch[1]}&pageType=add&entityName=ControlMasterDemo`
  )
  await expect(page.getByText('ControlMasterDemo — 新增页', { exact: true })).toBeVisible()
  await expect(page.getByText('child_ControlDetailDemo', { exact: true })).toBeVisible()

  const designerTags = await page.locator('.tree-panel .tree-tag').allTextContents()
  for (const tag of expectedDesignerTags) {
    expect(designerTags, `设计器组件树缺少 ${tag}`).toContain(tag)
  }
})
