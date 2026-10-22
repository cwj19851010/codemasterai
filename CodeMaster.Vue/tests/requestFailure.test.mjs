import test from 'node:test'
import assert from 'node:assert/strict'
import { keepRequestFailure, requestOutcome, requestFailureStatus, resolveRequestFailure } from '../src/components/AgentAssistant/requestFailure.js'
import { buildConversationTimeline } from '../src/components/AgentAssistant/conversationTimeline.js'

const user = { id: '1', role: 'user', requestId: 'retry-1', content: '开始开发吧', createTime: '2026-09-19T12:00:00Z' }
const failure = { id: '2', role: 'error', requestId: 'retry-1', content: '模型连接中断', metadataJson: '{"status":"Failed"}', createTime: '2026-09-19T12:00:01Z' }

test('terminal error keeps a visible diagnostic when streaming bubble is removed', () => {
  const messages = keepRequestFailure([user, { id: 'stream', role: 'assistant', requestId: user.requestId, isStreaming: true }], user.requestId, '模型请求超时')
  assert.equal(messages.length, 2)
  assert.equal(messages[1].role, 'error')
  assert.equal(messages[1].content, '模型请求超时')
  assert.equal(requestOutcome(messages, user.requestId), messages[1])
})

test('persisted failure survives reload and is not duplicated', () => {
  const messages = JSON.parse(JSON.stringify([user, failure]))
  assert.equal(requestFailureStatus(messages[1]), 'Failed')
  const next = keepRequestFailure(messages, user.requestId, 'network error')
  assert.equal(next.length, 2)
  assert.equal(next[1].content, failure.content)
  assert.equal(buildConversationTimeline({ messages: next }).filter(item => item.kind === 'message').length, 2)
})

test('successful retry wins over its previous error and unrelated failures', () => {
  const success = { role: 'assistant', requestId: user.requestId, content: '已完成' }
  const other = { ...failure, requestId: 'other' }
  assert.equal(requestOutcome([user, failure, success, other], user.requestId), success)
  const resolved = resolveRequestFailure([user, failure, other], user.requestId)
  assert.equal(requestFailureStatus(resolved[1]), 'Resolved')
  assert.equal(requestFailureStatus(resolved[2]), 'Failed')
  assert.equal(keepRequestFailure([user, failure, success], user.requestId, 'late error').length, 3)
  assert.equal(requestOutcome([user, { ...failure, metadataJson: '{"status":"Resolved"}' }], user.requestId), undefined)
})

test('fallback errors do not reveal credentials', () => {
  const message = keepRequestFailure([user], user.requestId, 'Bearer abc.def token=very-secret password=hidden')[1]
  assert.doesNotMatch(message.content, /abc.def|very-secret|hidden/)
  assert.match(message.content, /REDACTED/)
})

test('a previous failure marked retrying must not end completion polling early', () => {
  const retrying = { ...failure, metadataJson: '{"status":"Retrying"}' }
  assert.equal(requestOutcome([user, retrying], user.requestId), undefined)
  const messages = keepRequestFailure([user, retrying], user.requestId, '新的网络错误')
  assert.equal(messages.length, 2)
  assert.equal(messages[1].id, failure.id)
  assert.equal(requestFailureStatus(messages[1]), 'Failed')
  assert.equal(messages[1].content, '新的网络错误')
})
