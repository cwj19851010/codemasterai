export const isRequestFailure = message => message?.role === 'error'

export function requestFailureStatus(message) {
  try { return JSON.parse(message?.metadataJson || '{}').status || 'Failed' }
  catch { return 'Failed' }
}

export function requestOutcome(messages, requestId) {
  // A saved assistant result wins over an earlier failure from the same request.
  return messages.find(message => message.role === 'assistant' && message.requestId === requestId) ||
    messages.find(message => isRequestFailure(message) && message.requestId === requestId && requestFailureStatus(message) === 'Failed')
}

export function resolveRequestFailure(messages, requestId) {
  return messages.map(message => isRequestFailure(message) && message.requestId === requestId
    ? { ...message, metadataJson: JSON.stringify({ kind: 'request_failure', status: 'Resolved' }) }
    : message)
}

export function keepRequestFailure(messages, requestId, diagnostic) {
  const next = messages.filter(message => !(message.isStreaming && message.requestId === requestId))
  if (requestOutcome(next, requestId)) return next
  const content = String(diagnostic || 'Agent 请求失败，未返回具体诊断。')
    .replace(/Bearer\s+[A-Za-z0-9._~+/=-]+/gi, 'Bearer [REDACTED]')
    .replace(/((?:api[_-]?key|password|pwd|token|secret)\s*[:=]\s*)[^\s,;]+/gi, '$1[REDACTED]')
    .slice(0, 1800)
  const existing = next.findIndex(message => isRequestFailure(message) && message.requestId === requestId)
  if (existing >= 0) {
    next[existing] = { ...next[existing], content, metadataJson: JSON.stringify({ kind: 'request_failure', status: 'Failed', localFallback: true }) }
    return next
  }
  return [...next, {
    id: `error-${requestId}`, role: 'error', requestId, content,
    createTime: new Date().toISOString(),
    metadataJson: JSON.stringify({ kind: 'request_failure', status: 'Failed', localFallback: true })
  }]
}
