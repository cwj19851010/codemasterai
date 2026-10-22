import test from 'node:test'
import assert from 'node:assert/strict'
import { takePendingDraft } from '../src/components/AgentAssistant/wizardDraft.js'

test('takePendingDraft clears the ref before the caller closes the wizard', () => {
  const pendingDraft = { value: 'create the order project' }

  const draft = takePendingDraft(pendingDraft)

  assert.equal(draft, 'create the order project')
  assert.equal(pendingDraft.value, '')
})

test('takePendingDraft is safe when no draft is queued', () => {
  const pendingDraft = { value: '' }

  assert.equal(takePendingDraft(pendingDraft), '')
  assert.equal(pendingDraft.value, '')
})
