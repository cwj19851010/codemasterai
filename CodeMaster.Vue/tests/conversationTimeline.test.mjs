import test from 'node:test'
import assert from 'node:assert/strict'
import { buildConversationTimeline } from '../src/components/AgentAssistant/conversationTimeline.js'
import { PROJECT_MATERIALIZATION_ACTIONS } from '../src/components/AgentAssistant/projectMaterialization.js'

test('conversation timeline places approvals after the completed reply', () => {
  const timeline = buildConversationTimeline({
    messages: [
      { id: '100', role: 'user', createTime: '2026-08-23T01:00:00.000Z' },
      { id: '300', role: 'assistant', createTime: '2026-08-23T01:00:02.000Z' }
    ],
    pendingApprovals: [
      { id: '200', status: 'PendingApproval', createTime: '2026-08-23T01:00:01.000Z' }
    ],
    currentPlanApprovalIds: new Set(['200'])
  })

  assert.deepEqual(timeline.map(item => item.key), [
    'message-100',
    'message-300',
    'approval-200'
  ])
  assert.equal(timeline[2].isCurrentPlan, true)
})

test('approval cards remain oldest first even when the API array is newest first', () => {
  const timeline = buildConversationTimeline({
    pendingApprovals: [
      { id: '300', createTime: '2026-08-23T01:00:03.000Z' },
      { id: '100', createTime: '2026-08-23T01:00:01.000Z' },
      { id: '200', createTime: '2026-08-23T01:00:02.000Z' }
    ]
  })

  assert.deepEqual(timeline.map(item => item.key), [
    'approval-100',
    'approval-200',
    'approval-300'
  ])
})

test('snowflake ids provide a stable order when timestamps are identical', () => {
  const createTime = '2026-08-23T01:00:01.000Z'
  const timeline = buildConversationTimeline({
    messages: [{ id: '300', createTime }],
    pendingApprovals: [{ id: '200', createTime }],
    failedExecutions: [{ id: '400', createTime }]
  })

  assert.deepEqual(timeline.map(item => item.key), [
    'message-300',
    'failed-400',
    'approval-200'
  ])
})

test('work plan confirmation is rendered in the bottom approval section', () => {
  const timeline = buildConversationTimeline({
    messages: [{ id: '100', role: 'assistant', createTime: '2026-08-23T01:00:02.000Z' }],
    workPlanApproval: { id: '900', structureVersion: '4' }
  })

  assert.deepEqual(timeline.map(item => item.key), [
    'message-100',
    'work-plan-approval-900-4'
  ])
})

test('project materialization generates entities, applies views, then generates pages before build and tests', () => {
  assert.deepEqual([...PROJECT_MATERIALIZATION_ACTIONS], [
    'generateProjectSmartCode',
    'migrateDatabase',
    'generateComplexPages',
    'buildProject',
    'testProject'
  ])
})
