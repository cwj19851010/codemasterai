import assert from 'node:assert/strict'
import test from 'node:test'

import {
  AGENT_WORKFLOW_MODES,
  DEFAULT_AGENT_WORKFLOW_MODE,
  executionProgressKey,
  normalizeWorkflowMode,
  shouldAutoProcessApprovals,
  shouldMaterializeAfterAutomaticApproval,
  selectCurrentWorkTask
} from '../src/components/AgentAssistant/workflowMode.js'

test('new conversations default to minimal automatic workflow', () => {
  assert.equal(DEFAULT_AGENT_WORKFLOW_MODE, AGENT_WORKFLOW_MODES.MINIMAL)
  assert.equal(shouldAutoProcessApprovals(DEFAULT_AGENT_WORKFLOW_MODE, false), true)
})

test('standard mode keeps approval manual by default', () => {
  assert.equal(shouldAutoProcessApprovals(AGENT_WORKFLOW_MODES.STANDARD, false), false)
  assert.equal(shouldMaterializeAfterAutomaticApproval(AGENT_WORKFLOW_MODES.STANDARD), false)
})

test('standard mode can explicitly enable automatic approval', () => {
  assert.equal(shouldAutoProcessApprovals(AGENT_WORKFLOW_MODES.STANDARD, true), true)
})

test('minimal mode always handles approval and final materialization automatically', () => {
  assert.equal(shouldAutoProcessApprovals(AGENT_WORKFLOW_MODES.MINIMAL, false), true)
  assert.equal(shouldMaterializeAfterAutomaticApproval(AGENT_WORKFLOW_MODES.MINIMAL), true)
})

test('unknown modes safely fall back to standard mode', () => {
  assert.equal(normalizeWorkflowMode('Unknown'), AGENT_WORKFLOW_MODES.STANDARD)
})

test('current task favors executing work before the next ready task', () => {
  const current = selectCurrentWorkTask({
    stages: [{
      tasks: [
        { title: '创建项目实体', status: 'Ready' },
        { title: '配置字段控件', status: 'Applying' }
      ]
    }]
  })

  assert.equal(current.title, '配置字段控件')
})

test('execution progress keys prevent repeated work without limiting total tasks', () => {
  assert.equal(executionProgressKey({ id: '101', status: 'Completed' }), '101:Completed')
  assert.equal(executionProgressKey({ id: '102', status: 'Completed' }), '102:Completed')
  assert.equal(executionProgressKey(null), '')
})
