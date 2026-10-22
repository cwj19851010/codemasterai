import request from '@/utils/request'
import { getToken } from '@/utils/auth'

export function getProviders(executionMode) {
  return request({ url: '/agent/providers', method: 'get', params: executionMode ? { executionMode } : {} })
}

export function createProvider(data) {
  return request({ url: '/agent/providers', method: 'post', data })
}

export function updateProvider(id, data) {
  return request({ url: `/agent/providers/${id}`, method: 'put', data })
}

export function deleteProvider(id) {
  return request({ url: `/agent/providers/${id}`, method: 'delete' })
}

export function testProvider(id) {
  return request({ url: `/agent/providers/${id}/test`, method: 'post', timeout: 120000 })
}

export function getConversations(projectId, executionMode) {
  return request({
    url: '/agent/conversations',
    method: 'get',
    params: { projectId, ...(executionMode ? { executionMode } : {}) }
  })
}

export function createConversation(data) {
  return request({ url: '/agent/conversations', method: 'post', data })
}

export function updateConversationPreferences(conversationId, data) {
  return request({
    url: `/agent/conversations/${conversationId}/preferences`,
    method: 'put',
    data
  })
}

/**
 * The single explicit authorization point. Requirements mode can only read; code mode
 * unlocks generation. The server enforces the same rule, so this call is what actually
 * changes what the Agent is allowed to do.
 *
 * `force` is only set after the client has warned that no new requirement arrived since the
 * last generation, so the server-side guard cannot be skipped by accident.
 */
export function switchAgentMode(conversationId, agentMode, force = false) {
  return request({
    url: `/agent/conversations/${conversationId}/mode`,
    method: 'post',
    data: { conversationId, agentMode, force }
  })
}

export function updateConversationProvider(conversationId, providerId) {
  return request({
    url: `/agent/conversations/${conversationId}/provider`,
    method: 'put',
    data: { providerId }
  })
}

export function deleteConversation(conversationId) {
  return request({ url: `/agent/conversations/${conversationId}`, method: 'delete' })
}

export function getMessages(conversationId) {
  return request({ url: `/agent/conversations/${conversationId}/messages`, method: 'get' })
}

export function getToolExecutions(conversationId) {
  return request({ url: `/agent/conversations/${conversationId}/tools`, method: 'get' })
}

export function getActiveWorkPlan(projectId) {
  return request({ url: '/agent/work-plans/active', method: 'get', params: { projectId } })
}

export function getWorkPlan(planId) {
  return request({ url: `/agent/work-plans/${planId}`, method: 'get' })
}

export function confirmWorkPlan(planId, data) {
  return request({ url: `/agent/work-plans/${planId}/confirm`, method: 'post', data, timeout: 180000 })
}

export function resumeWorkPlan(planId, conversationId) {
  return request({
    url: `/agent/work-plans/${planId}/resume`,
    method: 'post',
    params: conversationId ? { conversationId } : {},
    timeout: 180000
  })
}

export function cancelWorkPlan(planId, superseded = false) {
  return request({ url: `/agent/work-plans/${planId}/cancel`, method: 'post', params: { superseded } })
}

export function retryWorkPlanTask(planId, taskId, failureSignature) {
  return request({
    url: `/agent/work-plans/${planId}/tasks/${taskId}/retry`,
    method: 'post',
    data: { failureSignature }
  })
}

export function sendMessage(data) {
  return request({ url: '/agent/chat', method: 'post', data, timeout: 180000 })
}

export function cancelAgentRequest(data) {
  return request({ url: '/agent/chat/cancel', method: 'post', data, timeout: 30000 })
}

export async function streamMessage(data, { onProgress, signal } = {}) {
  const headers = {
    Accept: 'text/event-stream',
    'Content-Type': 'application/json'
  }
  const userStore = localStorage.getItem('user')
  if (userStore) {
    try {
      const user = JSON.parse(userStore)
      if (user.token) headers.Authorization = `Bearer ${user.token}`
    } catch {
      // Fall back to the shared auth token below.
    }
  }
  if (!headers.Authorization) {
    const token = getToken()
    if (token) headers.Authorization = `Bearer ${token}`
  }

  const response = await fetch('/api/agent/chat/stream', {
    method: 'POST',
    headers,
    body: JSON.stringify(data),
    signal,
    credentials: 'same-origin'
  })
  if (!response.ok) {
    let message = `Agent 流式请求失败 (${response.status})`
    try {
      const error = await response.json()
      message = error?.message || error?.data?.message || message
    } catch {
      // Keep the HTTP status message when the response is not JSON.
    }
    const error = new Error(message)
    error.agentTerminal = true
    throw error
  }
  if (!response.body) throw new Error('当前浏览器不支持读取 Agent 流式响应')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let finalResult

  const consumeBlock = block => {
    const dataLines = block
      .split('\n')
      .filter(line => line.startsWith('data:'))
      .map(line => line.slice(5).trimStart())
    if (dataLines.length === 0) return

    const event = JSON.parse(dataLines.join('\n'))
    onProgress?.(event)
    if (event.event === 'error') {
      const error = new Error(event.message || 'Agent 执行失败')
      error.agentTerminal = true
      throw error
    }
    if (event.event === 'final') finalResult = event.result
  }

  while (true) {
    const { done, value } = await reader.read()
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done }).replace(/\r\n/g, '\n')
    let boundary = buffer.indexOf('\n\n')
    while (boundary >= 0) {
      consumeBlock(buffer.slice(0, boundary))
      buffer = buffer.slice(boundary + 2)
      boundary = buffer.indexOf('\n\n')
    }
    if (done) break
  }
  if (buffer.trim()) consumeBlock(buffer.trim())
  if (!finalResult) throw new Error('Agent 流式响应已结束，但没有收到最终结果')
  return finalResult
}

export function approveTool(id) {
  return request({ url: `/agent/tools/${id}/approve`, method: 'post' })
}

export function executeApprovedTool(id) {
  return request({ url: `/agent/tools/${id}/execute`, method: 'post', timeout: 180000 })
}

export function rejectTool(id) {
  return request({ url: `/agent/tools/${id}/reject`, method: 'post' })
}

export function completeToolClientActions(id, data) {
  return request({
    url: `/agent/tools/${id}/complete-client-actions`,
    method: 'post',
    data,
    timeout: 180000
  })
}
