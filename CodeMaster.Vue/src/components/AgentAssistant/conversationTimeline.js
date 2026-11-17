const toTimestamp = value => {
  const timestamp = Date.parse(value?.createTime || '')
  return Number.isFinite(timestamp) ? timestamp : Number.MAX_SAFE_INTEGER
}

const toNumericId = value => {
  const text = String(value?.id || '')
  return /^\d+$/.test(text) ? BigInt(text) : null
}

const compareStableIds = (left, right) => {
  const leftId = toNumericId(left.value)
  const rightId = toNumericId(right.value)
  if (leftId !== null && rightId !== null && leftId !== rightId) {
    return leftId < rightId ? -1 : 1
  }
  return left.sourceOrder - right.sourceOrder
}

export const TIME_ORDER_THRESHOLD_MS = 3000

export const buildConversationTimeline = ({
  messages = [],
  pendingApprovals = [],
  failedExecutions = [],
  currentPlanApprovalIds = new Set(),
  workPlanApproval = null
} = {}) => {
  let sourceOrder = 0
  const conversationItems = [
    ...messages.map(value => ({
      key: `message-${value.id}`,
      kind: 'message',
      value,
      sourceOrder: sourceOrder++
    })),
    ...failedExecutions.map(value => ({
      key: `failed-${value.id}`,
      kind: 'failure',
      value,
      sourceOrder: sourceOrder++
    }))
  ]

  const sortedConversationItems = conversationItems
    .sort((left, right) => {
      const timeDifference = toTimestamp(left.value) - toTimestamp(right.value)
      // For items created within a short window, preserve the original array
      // order to prevent server/client clock skew from reordering messages.
      if (Math.abs(timeDifference) <= TIME_ORDER_THRESHOLD_MS) {
        return left.sourceOrder - right.sourceOrder
      }
      return timeDifference || left.sourceOrder - right.sourceOrder
    })
    .map(({ sourceOrder: _, ...item }) => item)

  const approvalItems = pendingApprovals
    .map(value => ({
      key: `approval-${value.id}`,
      kind: 'approval',
      value,
      isCurrentPlan: currentPlanApprovalIds.has(String(value.id)),
      sourceOrder: sourceOrder++
    }))
    .sort((left, right) => {
      const timeDifference = toTimestamp(left.value) - toTimestamp(right.value)
      if (Math.abs(timeDifference) <= TIME_ORDER_THRESHOLD_MS) {
        return left.sourceOrder - right.sourceOrder
      }
      return timeDifference || left.sourceOrder - right.sourceOrder
    })
    .map(({ sourceOrder: _, ...item }) => item)

  if (workPlanApproval) {
    approvalItems.unshift({
      key: `work-plan-approval-${workPlanApproval.id}-${workPlanApproval.structureVersion}`,
      kind: 'work-plan-approval',
      value: workPlanApproval
    })
  }

  return [...sortedConversationItems, ...approvalItems]
}
