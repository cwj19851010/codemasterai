import request from '@/utils/request'

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

export function deleteConversation(conversationId) {
  return request({ url: `/agent/conversations/${conversationId}`, method: 'delete' })
}

export function getMessages(conversationId) {
  return request({ url: `/agent/conversations/${conversationId}/messages`, method: 'get' })
}

export function getToolExecutions(conversationId) {
  return request({ url: `/agent/conversations/${conversationId}/tools`, method: 'get' })
}

export function sendMessage(data) {
  return request({ url: '/agent/chat', method: 'post', data, timeout: 180000 })
}

export function approveTool(id) {
  return request({ url: `/agent/tools/${id}/approve`, method: 'post', timeout: 60000 })
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
