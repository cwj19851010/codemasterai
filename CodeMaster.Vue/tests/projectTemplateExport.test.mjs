import assert from 'node:assert/strict'
import test from 'node:test'
import { readFile } from 'node:fs/promises'

test('project template export allows enough time to package printing runtime', async () => {
  const source = await readFile(new URL('../src/api/codegen/project.js', import.meta.url), 'utf8')
  const exportTemplate = source.match(/export function exportTemplate[\s\S]*?\n}\n/)

  assert.ok(exportTemplate, 'exportTemplate API definition was not found')
  assert.match(exportTemplate[0], /timeout:\s*300000/)
})
