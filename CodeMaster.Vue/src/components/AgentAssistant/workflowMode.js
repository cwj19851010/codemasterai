export const AGENT_WORKFLOW_MODE = Object.freeze({
  DEFAULT: 'Default',
  STANDARD: 'Standard',
  MINIMAL: 'Minimal'
})

export const DEFAULT_AGENT_WORKFLOW_MODE = AGENT_WORKFLOW_MODE.DEFAULT

export const normalizeWorkflowMode = value => {
  if (String(value || '').toLowerCase() === AGENT_WORKFLOW_MODE.STANDARD.toLowerCase()) {
    return AGENT_WORKFLOW_MODE.STANDARD
  }
  if (String(value || '').toLowerCase() === AGENT_WORKFLOW_MODE.MINIMAL.toLowerCase()) {
    return AGENT_WORKFLOW_MODE.MINIMAL
  }
  return AGENT_WORKFLOW_MODE.DEFAULT
}

// Default/Minimal are the two-turn autonomous modes. Standard is the only
// mode that intentionally waits for an explicit user confirmation.
export const shouldAutoProcessApprovals = (workflowMode, autoApproveApprovals) =>
  normalizeWorkflowMode(workflowMode) !== AGENT_WORKFLOW_MODE.STANDARD || Boolean(autoApproveApprovals)

export const shouldMaterializeAfterAutomaticApproval = () => true

export const selectCurrentWorkTask = workPlan => {
  const tasks = (workPlan?.stages || []).flatMap(stage => stage.tasks || [])
  return tasks.find(task => ['Applying', 'Generating', 'Migrating', 'Building', 'Verifying', 'WaitingApproval'].includes(task.status))
    || tasks.find(task => task.status === 'Ready')
    || null
}

export const executionProgressKey = execution => {
  if (!execution?.id) return ''
  return `${execution.id}:${execution.status || ''}`
}
