export const AGENT_MODE = Object.freeze({
  REQUIREMENTS: 'Requirements',
  CODE: 'Code'
})

export const DEFAULT_AGENT_MODE = AGENT_MODE.REQUIREMENTS

export const normalizeAgentMode = value => {
  if (String(value || '').toLowerCase() === AGENT_MODE.CODE.toLowerCase()) {
    return AGENT_MODE.CODE
  }
  return AGENT_MODE.REQUIREMENTS
}

export const isCodeMode = value => normalizeAgentMode(value) === AGENT_MODE.CODE

export const agentModeLabel = value =>
  normalizeAgentMode(value) === AGENT_MODE.CODE ? '生成代码' : '需求设计'

// The two pages differ in far more than a title, so keep the copy in one place.
export const agentModeCopy = value => {
  if (normalizeAgentMode(value) === AGENT_MODE.CODE) {
    return {
      tab: '生成代码',
      hint: '已授权生成：对话会依据已确认的需求改动元数据并生成代码。',
      placeholder: '补充生成要求，或说明要调整的地方',
      emptyTip: '需求已确认，可以开始生成代码'
    }
  }
  return {
    tab: '需求设计',
    hint: '此页只讨论需求，不会修改任何元数据，也不会生成代码。',
    placeholder: '描述业务需求，例如：工程项目、材料入库、付款对账等',
    emptyTip: '先把需求聊清楚，确认后再生成代码'
  }
}

// Generation is only meaningful once the user has told us where the project lives.
export const canStartGeneration = (conversationId, providerId) =>
  Boolean(conversationId && providerId)
