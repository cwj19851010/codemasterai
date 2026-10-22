import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

const read = async relativePath => readFile(new URL(relativePath, import.meta.url), 'utf8')

test('project module forms reserve system monitor and reports menu orders', async () => {
  const addPage = await read('../src/views/codegen/projectModule/add.vue')
  const editPage = await read('../src/views/codegen/projectModule/edit.vue')

  for (const source of [addPage, editPage]) {
    assert.match(source, /:min="isReportsModule \? 9999 : 3"/)
    assert.match(source, /:max="isReportsModule \? 9999 : 9998"/)
    assert.match(source, /Reporting\/报表管理固定使用 9999/)
    assert.match(source, /name === 'reporting'/)
    assert.match(source, /form\.orderNum = value \? 9999/)
  }

  assert.match(addPage, /orderNum:\s*3/)
})
