<template>
  <el-drawer
    v-model="visible"
    size="560px"
    direction="rtl"
    append-to-body
    class="codemaster-agent-drawer"
    :show-close="false"
  >
    <template #header>
      <div class="drawer-header">
        <div>
          <div class="drawer-title">CodeMaster Agent</div>
          <div class="drawer-subtitle">项目建模与代码生成助手</div>
        </div>
        <div class="drawer-actions">
          <el-tooltip v-if="tauriClient" content="打开 Agent 日志" placement="bottom">
            <el-button text circle aria-label="打开 Agent 日志" :icon="FolderOpened" @click="openDiagnosticLogs" />
          </el-tooltip>
          <el-tooltip content="新对话" placement="bottom">
            <el-button text circle aria-label="新对话" :icon="EditPen" @click="startNewConversation" />
          </el-tooltip>
          <el-tooltip content="模型配置" placement="bottom">
            <el-button text circle aria-label="模型配置" :icon="Setting" @click="providerDialogVisible = true" />
          </el-tooltip>
          <el-button text circle aria-label="关闭 AI 助手" :icon="Close" @click="visible = false" />
        </div>
      </div>
    </template>

    <div class="agent-mode-bar">
      <el-radio-group
        v-model="agentMode"
        size="small"
        :disabled="sending || switchingMode"
        @change="handleAgentModeChange"
      >
        <el-radio-button :value="AGENT_MODE.REQUIREMENTS">需求设计</el-radio-button>
        <el-radio-button :value="AGENT_MODE.CODE">生成代码</el-radio-button>
      </el-radio-group>
      <span class="agent-mode-hint" :class="{ 'is-code': isCodeModeActive }">
        {{ agentModeCopyText.hint }}
      </span>
    </div>

    <div class="agent-layout">
      <div class="context-panel">
        <div class="context-field project-field">
          <span class="context-label">所属项目</span>
          <el-select
            v-model="selectedProjectId"
            filterable
            placeholder="请选择项目"
            style="width: 100%"
            @change="handleProjectChange"
          >
            <el-option
              v-for="project in projects"
              :key="project.id"
              :label="project.displayName || project.projectName"
              :value="project.id"
            >
              <div class="project-option">
                <span>{{ project.displayName || project.projectName }}</span>
                <small>{{ project.projectName }}</small>
              </div>
            </el-option>
          </el-select>
        </div>

        <div class="context-field provider-field">
          <span class="context-label">模型</span>
          <el-select
            v-if="enabledProviders.length"
            v-model="selectedProviderId"
            style="width: 100%"
            :disabled="sending || confirmingWorkPlan || applyingWorkPlanMetadata"
            @change="handleProviderChange"
          >
            <el-option
              v-for="provider in enabledProviders"
              :key="provider.id"
              :label="provider.name"
              :value="provider.id"
            >
              <div class="provider-option">
                <span>{{ provider.name }}</span>
                <small>{{ provider.modelName }}</small>
              </div>
            </el-option>
          </el-select>
          <el-button v-else type="primary" plain style="width: 100%" @click="providerDialogVisible = true">
            请先配置模型
          </el-button>
        </div>

        <div v-if="selectedProjectId" class="context-field workflow-field">
          <span class="context-label">自动执行</span>
          <div class="workflow-auto-toggle">
            <span>确认设计后自动执行后续任务</span>
            <el-switch
              v-model="autoApproveApprovals"
              size="small"
              :disabled="sending || confirmingWorkPlan || applyingWorkPlanMetadata"
              @change="handleWorkflowPreferencesChange"
            />
          </div>
          <small v-if="autoApproveApprovals">
            第一轮给出设计，确认后自动调用 upsert_project_work_plan 并推进所有就绪任务。
          </small>
          <small v-else>
            第一轮给出设计并等待确认；确认后自动调用 upsert_project_work_plan 并推进所有就绪任务。
          </small>
        </div>

        <div v-if="selectedProjectId" class="conversation-field">
          <el-select
            v-model="activeConversationId"
            class="conversation-select"
            placeholder="新对话"
            clearable
            style="width: 100%"
            @change="handleConversationChange"
          >
            <el-option
              v-for="conversation in conversations"
              :key="conversation.id"
              :label="conversation.title"
              :value="conversation.id"
            />
          </el-select>
          <el-tooltip v-if="activeConversationId" content="删除当前对话" placement="bottom">
            <el-button
              class="conversation-delete"
              text
              circle
              aria-label="删除当前对话"
              :icon="Delete"
              @click="archiveCurrentConversation"
            />
          </el-tooltip>
        </div>

        <div v-if="selectedProjectId" class="agent-scope-hint">
          <span>元数据由 Agent 准备；源代码请在项目页面手动生成</span>
        </div>
      </div>

      <div ref="messagePanelRef" class="message-panel">
        <div v-if="!selectedProjectId" class="empty-state">
          <el-icon><FolderOpened /></el-icon>
          <span>输入需求，自动创建新项目开始对话；或先选择已有项目</span>
          <el-button type="primary" @click="openCreateWizard">创建项目</el-button>
        </div>
        <div v-else-if="enabledProviders.length === 0" class="empty-state">
          <el-icon><Connection /></el-icon>
          <span>请先配置模型</span>
          <el-button type="primary" @click="providerDialogVisible = true">配置模型</el-button>
        </div>
        <template v-else>
          <div v-if="messages.length === 0" class="empty-state compact">
            <el-icon><ChatDotRound /></el-icon>
            <span>可以开始描述需求了</span>
          </div>

          <section v-if="workPlan" class="work-plan-panel" aria-label="项目任务列表">
            <div class="work-plan-header">
              <div>
                <strong>{{ workPlan.title }}</strong>
                <span>{{ workPlan.completedTasks }}/{{ workPlan.totalTasks }} 个任务完成</span>
              </div>
              <div class="work-plan-tags">
                <el-tag
                  size="small"
                  :type="agentPhaseType(currentAgentPhase)"
                  :title="agentPhaseLabel(currentAgentPhase)"
                  data-testid="agent-phase-tag"
                >{{ agentPhaseLabel(currentAgentPhase) }}</el-tag>
                <el-tag size="small" :type="workPlanStatusType(workPlan.status)">{{ workPlanStatusLabel(workPlan.status) }}</el-tag>
              </div>
            </div>
            <el-progress
              :percentage="workPlanProgress"
              :show-text="false"
              :status="workPlan.status === 'Failed' ? 'exception' : undefined"
            />
            <el-button
              v-if="workPlanCanResume"
              class="work-plan-resume"
              type="primary"
              plain
              :loading="applyingWorkPlanMetadata"
              @click="applyConfirmedWorkPlanMetadata"
            >继续执行任务</el-button>
            <div v-if="currentWorkTask" class="work-plan-current-task" aria-live="polite">
              <span class="work-plan-current-task-label">当前执行任务</span>
              <strong>{{ currentWorkTask.title }}</strong>
              <span>{{ taskStatusLabel(currentWorkTask.status) }}</span>
            </div>
            <div v-if="workPlan.repairPolicy?.canInspectCodeMasterSource" class="work-repair-policy">
              <el-tag size="small" type="warning">测试源码修复已授权</el-tag>
              <span>仅允许结构化补丁和服务器验证配置</span>
            </div>
            <div v-for="stage in workPlan.stages" :key="stage.id || stage.stageKey" class="work-stage">
              <div class="work-stage-header">
                <span class="work-stage-title">{{ stage.title }}</span>
                <span class="work-stage-count">{{ stage.completedTasks }}/{{ stage.totalTasks }}</span>
              </div>
              <div v-for="task in stage.tasks" :key="task.id || task.taskKey" class="work-task">
                <div class="work-task-row">
                  <span class="work-task-state" :class="`state-${String(task.status).toLowerCase()}`">
                    {{ taskStatusLabel(task.status, autoApprovalEnabled) }}
                  </span>
                  <span class="work-task-title">{{ task.title }}</span>
                  <span v-if="task.unmetDependencyTaskKeys?.length" class="work-task-lock">
                    等待 {{ task.unmetDependencyTaskKeys.join('、') }}
                  </span>
                  <span
                    v-else-if="task.lastDiagnosis && !(autoApprovalEnabled && task.status === 'WaitingApproval')"
                    class="work-task-diagnosis"
                  >{{ task.lastDiagnosis }}</span>
                </div>
                <details v-if="task.lastAttempt?.evidence" class="work-task-evidence">
                  <summary>
                    查看失败证据
                    <el-tag size="small" type="danger">
                      {{ failureCategoryLabel(task.lastAttempt.evidence.category) }}
                    </el-tag>
                  </summary>
                  <p>{{ task.lastAttempt.evidence.summary }}</p>
                  <ul v-if="task.lastAttempt.evidence.supportingEvidence?.length">
                    <li v-for="item in task.lastAttempt.evidence.supportingEvidence" :key="item">{{ item }}</li>
                  </ul>
                  <pre v-if="task.lastAttempt.evidence.diagnosticOutput">{{ task.lastAttempt.evidence.diagnosticOutput }}</pre>
                  <el-button
                    v-if="task.status === 'Failed' && task.lastAttempt.failureSignature"
                    size="small"
                    :loading="retryingTaskId === task.id"
                    @click="retryFailedTask(task)"
                  >重新排队</el-button>
                </details>
                <details v-if="task.lastAttempt?.repair" class="work-task-repair">
                  <summary>
                    源码修复记录
                    <el-tag size="small" :type="task.lastAttempt.repair.validationPassed ? 'success' : 'danger'">
                      {{ task.lastAttempt.repair.validationPassed ? '验证通过' : '验证未通过' }}
                    </el-tag>
                  </summary>
                  <p v-if="task.lastAttempt.repair.rationale">{{ task.lastAttempt.repair.rationale }}</p>
                  <p>Profile: {{ task.lastAttempt.repair.validationProfile }}</p>
                  <p v-if="task.lastAttempt.repair.diffSummary">{{ task.lastAttempt.repair.diffSummary }}</p>
                  <pre v-if="task.lastAttempt.repair.validationOutput">{{ task.lastAttempt.repair.validationOutput }}</pre>
                </details>
              </div>
            </div>
          </section>

          <template v-for="item in conversationTimeline" :key="item.key">
            <div
              v-if="item.kind === 'message'"
              class="message-row"
              :class="item.value.role"
            >
              <div class="message-role">{{ item.value.role === 'user' ? '你' : isRequestFailure(item.value) ? '执行错误' : 'Agent' }}</div>
              <div class="message-bubble" :class="{ streaming: item.value.isStreaming }">
                <span v-if="item.value.content">{{ item.value.content }}</span>
                <span v-if="item.value.isStreaming && item.value.content" class="stream-cursor"></span>
                <div v-if="item.value.streamStatus" class="stream-status">
                  <span class="stream-status-dot"></span>
                  {{ item.value.streamStatus }}
                </div>
                <div v-if="item.value.compileProgress?.active" class="compile-progress-panel">
                  <div class="compile-progress-header">
                    <span>业务计划编译</span>
                    <span>{{ item.value.compileProgress.percent }}%</span>
                  </div>
                  <el-progress
                    :percentage="item.value.compileProgress.percent"
                    :show-text="false"
                    :stroke-width="8"
                  />
                  <div class="compile-progress-message">
                    {{ item.value.compileProgress.message || item.value.compileProgress.stage }}
                  </div>
                  <div v-if="item.value.compileProgress.totalTasks" class="compile-progress-meta">
                    预计任务数：{{ item.value.compileProgress.totalTasks }}
                    <span v-if="item.value.compileProgress.addedEntities"> · 已处理实体 {{ item.value.compileProgress.addedEntities }}</span>
                    <span v-if="item.value.compileProgress.addedViews"> · 已处理视图 {{ item.value.compileProgress.addedViews }}</span>
                  </div>
                </div>
                <div v-if="item.value.stageProgress?.length" class="stage-progress-panel">
                  <div class="stage-progress-title">阶段进度</div>
                  <ul class="stage-progress-list">
                    <li
                      v-for="stage in item.value.stageProgress"
                      :key="stage.key"
                      class="stage-progress-item"
                      :class="`stage-${stage.status}`"
                    >
                      <span class="stage-progress-dot" />
                      <span class="stage-progress-name">{{ stage.title }}</span>
                      <span class="stage-progress-status">{{ stageStatusLabel(stage.status) }}</span>
                    </li>
                  </ul>
                </div>
                <div v-if="item.value.taskProgress?.active" class="task-progress-panel">
                  <div class="task-progress-header">
                    <span>任务执行</span>
                    <span>{{ item.value.taskProgress.index || 1 }}/{{ item.value.taskProgress.total || '?' }}</span>
                  </div>
                  <div class="task-progress-title">{{ item.value.taskProgress.title || item.value.taskProgress.taskKey }}</div>
                  <el-progress
                    v-if="item.value.taskProgress.total"
                    :percentage="Math.round((item.value.taskProgress.index / item.value.taskProgress.total) * 100)"
                    :show-text="false"
                    :stroke-width="6"
                  />
                  <div v-if="item.value.taskProgress.errorMessage" class="task-progress-error">
                    {{ item.value.taskProgress.errorMessage }}
                  </div>
                </div>
                <div v-if="item.value.toolError" class="stream-tool-error">
                  <div class="tool-error-summary">
                    <el-icon class="tool-error-icon"><WarningFilled /></el-icon>
                    <span class="tool-error-title">
                      <template v-if="item.value.toolErrorDetail?.stageLabel">
                        {{ item.value.toolErrorDetail.stageLabel }}阶段失败
                      </template>
                      <template v-else>工具调用失败</template>
                    </span>
                    <el-tag v-if="item.value.toolErrorDetail?.kindLabel" :type="item.value.toolErrorDetail.kindTone" size="small">
                      {{ item.value.toolErrorDetail.kindLabel }}
                    </el-tag>
                  </div>
                  <div class="tool-error-message">{{ item.value.toolError }}</div>
                  <div v-if="item.value.toolErrorDetail?.exceptionType" class="tool-error-meta">
                    <span><strong>异常:</strong> {{ item.value.toolErrorDetail.exceptionType }}</span>
                    <span v-if="item.value.toolErrorDetail.exceptionSource"><strong>位置:</strong> {{ item.value.toolErrorDetail.exceptionSource }}</span>
                    <span v-if="item.value.toolErrorDetail.attempt > 1"><strong>第 {{ item.value.toolErrorDetail.attempt }} 次重试</strong></span>
                  </div>
                  <div v-if="item.value.toolErrorDetail?.nextAction" class="tool-error-action">
                    <el-icon><InfoFilled /></el-icon>
                    <span>{{ item.value.toolErrorDetail.nextAction }}</span>
                  </div>
                  <div v-if="item.value.toolErrorDetail?.stackSnippet" class="tool-error-stack">
                    <el-button size="small" link @click="toggleStack(item.value)">
                      {{ item.value.toolErrorDetail.stackOpen ? '收起堆栈' : '查看堆栈' }}
                    </el-button>
                    <pre v-if="item.value.toolErrorDetail.stackOpen">{{ item.value.toolErrorDetail.stackSnippet }}</pre>
                  </div>
                  <div class="tool-error-actions">
                    <el-button size="small" link @click="copyFailureDiagnostic(item.value)">
                      <el-icon><DocumentCopy /></el-icon>
                      复制诊断
                    </el-button>
                    <el-button size="small" link @click="clearToolError(item.value)">关闭</el-button>
                  </div>
                </div>
                <div v-if="isRequestFailure(item.value)" class="request-failure-actions">
                  <el-tag :type="requestFailureStatus(item.value) === 'Resolved' ? 'success' : 'danger'" size="small">
                    {{ requestFailureStatus(item.value) === 'Resolved' ? '重试后已恢复' : '本次请求未完成' }}
                  </el-tag>
                  <el-button v-if="requestFailureStatus(item.value) !== 'Resolved'" size="small" :disabled="sending || !canSend" @click="retryRequestFailure(item.value)">重试本次请求</el-button>
                </div>
              </div>
              <time>{{ formatTime(item.value.createTime) }}</time>
            </div>

            <div v-else-if="item.kind === 'work-plan-approval'" class="approval-card work-plan-confirmation">
              <div class="work-plan-confirmation-header">
                <div>
                  <strong>{{ autoApprovalEnabled ? '正在按任务顺序执行' : '完整方案已准备' }}</strong>
                  <span>{{ autoApprovalEnabled ? '系统会自动完成可执行任务' : '确认后将自动按顺序执行' }}</span>
                </div>
                <div class="work-plan-change-summary">
                  <el-tag type="success" size="small">新增 {{ workPlan.changeSummary?.added || 0 }}</el-tag>
                  <el-tag type="primary" size="small">修改 {{ workPlan.changeSummary?.updated || 0 }}</el-tag>
                  <el-tag type="danger" size="small">删除 {{ workPlan.changeSummary?.deleted || 0 }}</el-tag>
                </div>
              </div>
              <div v-if="workPlanChanges.length" class="work-plan-change-list">
                <div v-for="change in workPlanChanges" :key="change.changeId" class="work-plan-change-item">
                  <div class="work-plan-change-main">
                    <el-tag :type="changeOperationType(change.operation)" size="small">
                      {{ changeOperationLabel(change.operation) }}
                    </el-tag>
                    <span class="work-plan-change-resource">{{ changeResourceLabel(change.resourceType) }}</span>
                    <strong>{{ changeDisplayTitle(change) }}</strong>
                  </div>
                  <div class="work-plan-change-fields">
                    <span v-if="changeTechnicalName(change)">字段名：{{ changeTechnicalName(change) }}</span>
                    <span v-if="changeDisplayName(change)">显示名称：{{ changeDisplayName(change) }}</span>
                    <span v-if="changeBusinessDescription(change)">业务说明：{{ changeBusinessDescription(change) }}</span>
                    <span v-if="changeDataType(change)">类型：{{ changeDataType(change) }}</span>
                    <span v-if="changeControlType(change)">控件：{{ changeControlType(change) }}</span>
                  </div>
                  <details v-if="change.differences || change.impacts" class="work-plan-change-details">
                    <summary>查看修改前后与影响</summary>
                    <pre>{{ formatJsonValue({ differences: change.differences, impacts: change.impacts }) }}</pre>
                  </details>
                </div>
              </div>
              <details class="work-plan-standard-json">
                <summary>查看将被确认的完整 JSON</summary>
                <pre>{{ formatJsonValue(workPlan.standardJson) }}</pre>
              </details>
              <div class="approval-actions">
                <span>{{ autoApprovalEnabled ? '当前模式会自动执行，无需再次确认。' : '确认一次后，后续任务会按依赖自动执行。' }}</span>
                <el-button
                  v-if="!autoApprovalEnabled"
                  type="primary"
                  :loading="confirmingWorkPlan"
                  :disabled="!workPlan.contentHash"
                  @click="confirmCurrentWorkPlan"
                >确认并开始执行</el-button>
              </div>
            </div>

            <div v-else-if="item.kind === 'approval'" class="approval-card">
              <div class="approval-header">
                <div>
                  <strong>{{ toolTitle(item.value.toolName) }}</strong>
                  <span>{{ approvalStatusLabel(item.value.status, autoApprovalEnabled) }}</span>
                </div>
                <div class="approval-status-tags">
                  <el-tag v-if="!item.isCurrentPlan" size="small" type="info">历史变更</el-tag>
                  <el-tag size="small" type="warning">
                    {{ approvalStatusLabel(item.value.status, autoApprovalEnabled) }}
                  </el-tag>
                </div>
              </div>
              <div v-if="item.isCurrentPlan && item.value.toolName === 'project_change_set'" class="change-set-overview">
                <strong>{{ changeSetOverview(item.value.inputJson).summary || '项目建模变更' }}</strong>
                <div class="change-set-counts">
                  <el-tag size="small">模块 {{ changeSetOverview(item.value.inputJson).modules }}</el-tag>
                  <el-tag size="small">实体 {{ changeSetOverview(item.value.inputJson).entities }}</el-tag>
                  <el-tag size="small">字段 {{ changeSetOverview(item.value.inputJson).fields }}</el-tag>
                  <el-tag size="small">关系 {{ changeSetOverview(item.value.inputJson).relations }}</el-tag>
                  <el-tag v-if="changeSetOverview(item.value.inputJson).dictionaries" size="small" type="info">
                    字典 {{ changeSetOverview(item.value.inputJson).dictionaries }}
                  </el-tag>
                  <el-tag v-if="changeSetOverview(item.value.inputJson).updates" size="small" type="warning">
                    更新 {{ changeSetOverview(item.value.inputJson).updates }}
                  </el-tag>
                  <el-tag v-if="changeSetOverview(item.value.inputJson).deletes" size="small" type="danger">
                    删除 {{ changeSetOverview(item.value.inputJson).deletes }}
                  </el-tag>
                  <el-tag v-if="changeSetOverview(item.value.inputJson).generationMode !== 'None'" size="small" type="success">
                    {{ changeSetOverview(item.value.inputJson).generationMode === 'Full' ? '全量生成' : '增量生成' }}
                  </el-tag>
                </div>
              </div>
              <div v-else-if="item.isCurrentPlan && item.value.toolName === 'project_work_batch'" class="change-set-overview">
                <strong>{{ workPlan?.activeBatch?.title || '当前阶段批量生成' }}</strong>
                <div class="change-set-counts">
                  <el-tag size="small">任务 {{ workPlan?.activeBatch?.taskIds?.length || 0 }}</el-tag>
                  <el-tag size="small" type="info">{{ workPlan?.activeBatch?.executionLocation || 'Client' }}</el-tag>
                  <el-tag size="small" type="info">{{ approvalStatusLabel(item.value.status, autoApprovalEnabled) }}</el-tag>
                </div>
              </div>
              <div v-else-if="item.isCurrentPlan && isUiCompositionApproval(item.value.toolName)" class="change-set-overview">
                <strong>{{ compositionOverview(item.value.toolName, item.value.inputJson).summary }}</strong>
                <div class="change-set-counts">
                  <el-tag
                    v-for="tag in compositionOverview(item.value.toolName, item.value.inputJson).tags"
                    :key="tag"
                    size="small"
                  >{{ tag }}</el-tag>
                </div>
              </div>
              <details class="approval-details">
                <summary>查看完整变更</summary>
                <pre>{{ formatToolInput(item.value.inputJson) }}</pre>
              </details>
              <div class="approval-actions">
                <span>{{ approvalActionHint(item.value.status, autoApprovalEnabled) }}</span>
                <el-button
                  v-if="!autoApprovalEnabled && item.value.status === 'PendingApproval'"
                  :loading="approvalLoadingId === item.value.id"
                  @click="rejectApproval(item.value)"
                >拒绝</el-button>
                <el-button
                  v-if="!autoApprovalEnabled && (item.value.status === 'PendingApproval' || item.value.status === 'Approved')"
                  type="primary"
                  :loading="approvalLoadingId === item.value.id || isMetadataApplicationRunning(item.value.id)"
                  :disabled="item.value.status === 'Executing'"
                  @click="approveApproval(item.value)"
                >
                  {{ item.value.status === 'PendingApproval'
                    ? '确认并开始执行'
                    : (item.value.status === 'Approved' && !isMetadataApplicationRunning(item.value.id)
                        ? '继续执行'
                        : '正在执行') }}
                </el-button>
              </div>
            </div>

            <div v-else class="execution-error-card">
              <div class="approval-header">
                <div>
                  <strong>{{ toolTitle(item.value.toolName) }}</strong>
                  <span>{{ item.value.errorMessage || '执行失败' }}</span>
                </div>
                <el-tag size="small" type="danger">执行失败</el-tag>
              </div>
              <details v-if="item.value.outputJson" class="approval-details">
                <summary>查看错误详情</summary>
                <pre>{{ formatToolInput(item.value.outputJson) }}</pre>
              </details>
            </div>
          </template>

          <div v-if="sending" class="typing-row">
            <template v-if="!streamingMessageVisible">
              <span></span><span></span><span></span>
            </template>
            <el-button
              v-if="canStopAgentRequest"
              class="stop-agent-button"
              text
              type="danger"
              @click="stopCurrentAgentRequest"
            >
              停止
            </el-button>
          </div>

          <div ref="messageEndRef" class="message-end-anchor" aria-hidden="true"></div>
        </template>
      </div>

      <div class="composer">
        <el-input
          v-model="draft"
          type="textarea"
          :rows="3"
          resize="none"
          maxlength="4000"
          :placeholder="agentModeCopyText.placeholder"
          :disabled="!canSend"
          @keydown.ctrl.enter.prevent="handleSend"
        />
        <div class="composer-footer">
          <span>{{ isCodeModeActive ? 'Ctrl + Enter' : agentModeCopyText.emptyTip }}</span>
          <el-button
            type="primary"
            :icon="Promotion"
            :loading="sending"
            :disabled="!canSend || !draft.trim()"
            @click="handleSend"
          >
            发送
          </el-button>
        </div>
      </div>

      <div v-if="!isCodeModeActive" class="generate-action">
        <el-button
          type="primary"
          size="large"
          :icon="MagicStick"
          :loading="switchingMode"
          :disabled="!canSwitchToCodeMode || sending"
          @click="handleStartGeneration"
        >
          生成代码
        </el-button>
        <span class="generate-action-tip">{{ generateActionTip }}</span>
      </div>
    </div>

    <provider-settings-dialog
      v-model="providerDialogVisible"
      :providers="providers"
      @refresh="loadProviders"
    />
    <project-create-wizard
      v-model="wizardVisible"
      @initialized="handleWizardInitialized"
    />
  </el-drawer>
</template>

<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  ChatDotRound,
  Close,
  Connection,
  Delete,
  DocumentCopy,
  EditPen,
  FolderOpened,
  InfoFilled,
  MagicStick,
  Promotion,
  Setting,
  WarningFilled
} from '@element-plus/icons-vue'
import { buildProject, testProject, enhanceUiPage, getList as getProjectList, migrateDatabase } from '@/api/codegen/project'
import { takePendingDraft } from './wizardDraft'
import { buildConversationTimeline } from './conversationTimeline'
import {
  AGENT_MODE,
  DEFAULT_AGENT_MODE,
  agentModeCopy,
  isCodeMode,
  normalizeAgentMode
} from './agentMode'
import {
  DEFAULT_AGENT_WORKFLOW_MODE,
  executionProgressKey,
  normalizeWorkflowMode,
  shouldAutoProcessApprovals,
  selectCurrentWorkTask
} from './workflowMode'
import request from '@/utils/request'
import {
  generateCode,
  generateIncrementalCode,
  generateSmartCode,
  generateProjectCode,
  generateProjectIncrementalCode,
  generateProjectSmartCode
} from '@/api/codegen/moduleEntity'
import {
  approveTool,
  cancelAgentRequest,
  completeToolClientActions,
  confirmWorkPlan,
  createConversation,
  deleteConversation,
  executeApprovedTool,
  getConversations,
  getActiveWorkPlan,
  getMessages,
  getProviders,
  getToolExecutions,
  rejectTool,
  resumeWorkPlan,
  retryWorkPlanTask,
  streamMessage,
  switchAgentMode,
  updateConversationPreferences,
  updateConversationProvider
} from '@/api/agent'
import ProviderSettingsDialog from './ProviderSettingsDialog.vue'
import ProjectCreateWizard from './ProjectCreateWizard.vue'
import { isRequestFailure, requestFailureStatus, requestOutcome, keepRequestFailure, resolveRequestFailure } from './requestFailure'
import {
  cancelLocalCodegenExecution,
  executeCodegenAction,
  invalidateLocalAgentContext,
  isCodeMasterClient,
  openClientDiagnosticLogDirectory,
  writeClientDiagnostic
} from '@/utils/codegenExecution'
import { isTauriRuntime } from '@/utils/tauriBridge'

const props = defineProps({
  modelValue: { type: Boolean, default: false }
})

const emit = defineEmits(['update:modelValue'])

const visible = ref(props.modelValue)
const projects = ref([])
const providers = ref([])
const conversations = ref([])
const messages = ref([])
const toolExecutions = ref([])
const workPlan = ref(null)
const selectedProjectId = ref('')
const selectedProviderId = ref('')
const activeConversationId = ref('')
const workflowMode = ref(DEFAULT_AGENT_WORKFLOW_MODE)
const autoApproveApprovals = ref(false)
// The mode the user is working in. It is persisted on the conversation by the server, so
// reloading the drawer or switching machines keeps the same page. Requirements is the safe
// default: nothing generates code until the user presses the generate action.
const agentMode = ref(DEFAULT_AGENT_MODE)
const switchingMode = ref(false)
const draft = ref('')
const sending = ref(false)
const retryRequestId = ref('')
const retryContent = ref('')
let workPlanPollTimer = null
let workPlanPollBusy = false
const providerDialogVisible = ref(false)
const wizardVisible = ref(false)
const pendingDraft = ref('')
const approvalLoadingId = ref('')
const metadataApplicationIds = ref(new Set())
const retryingTaskId = ref('')
const confirmingWorkPlan = ref(false)
const applyingWorkPlanMetadata = ref(false)
const automaticApprovalRunning = ref(false)
const automaticClientRecoveryRunning = ref(false)
const automaticPlanRepairKeys = ref(new Set())
const cancelRequested = ref(false)
const serverAbortController = ref(null)
const activeServerRequest = ref(null)
const messagePanelRef = ref()
const messageEndRef = ref()
const clientMode = isCodeMasterClient()
const tauriClient = isTauriRuntime()
const executionMode = clientMode ? 'Local' : 'Server'
const enabledProviders = computed(() => providers.value.filter(item =>
  item.isEnabled && (item.executionMode !== 'Local' || clientMode)
))
const selectedProvider = computed(() => providers.value.find(item => item.id === selectedProviderId.value))
const localAgentRequest = computed(() => tauriClient && selectedProvider.value?.executionMode === 'Local')
const canStopAgentRequest = computed(() => localAgentRequest.value || Boolean(activeServerRequest.value))
const pendingApprovals = computed(() => toolExecutions.value.filter(item =>
  item.status === 'PendingApproval' || item.status === 'Approved' ||
  item.status === 'Executing' || item.status === 'AwaitingClientExecution'
))
const currentPlanApprovals = computed(() => {
  if (!workPlan.value) return []
  return pendingApprovals.value.filter(item =>
    String(item.workPlanId || '') === String(workPlan.value.id) &&
    (!workPlan.value.activeBatchId || String(item.workBatchId || '') === String(workPlan.value.activeBatchId))
  )
})
const workPlanProgress = computed(() => {
  const total = Number(workPlan.value?.totalTasks || 0)
  return total ? Math.round(Number(workPlan.value.completedTasks || 0) * 100 / total) : 0
})
const workPlanChanges = computed(() => (workPlan.value?.stages || []).flatMap(stage =>
  (stage.tasks || []).flatMap(task => (task.changes || []).map(change => ({
    ...change,
    stageTitle: stage.title,
    taskTitle: task.title
  })))
))
const workPlanCanConfirm = computed(() => Boolean(workPlan.value?.requiresConfirmation))
const autoApprovalEnabled = computed(() => shouldAutoProcessApprovals(workflowMode.value, autoApproveApprovals.value))
// Doc phase 6: distinct agent phase tag (WaitingModel / WaitingUser / Blocked / Completed).
const currentAgentPhase = computed(() => deriveAgentPhase(
  workPlan.value,
  sending,
  pendingApprovals.value.length
))
const workPlanCanResume = computed(() => Boolean(
  workPlan.value?.status === 'Active' &&
  Number(workPlan.value?.completedTasks || 0) < Number(workPlan.value?.totalTasks || 0) &&
  (!workPlan.value?.activeBatchId || !workPlan.value?.activeBatch?.toolExecutionId)
))
const currentWorkTask = computed(() => selectCurrentWorkTask(workPlan.value))
const failedExecutions = computed(() => toolExecutions.value
  .filter(item => item.status === 'Failed')
)
const conversationTimeline = computed(() => buildConversationTimeline({
  messages: messages.value,
  pendingApprovals: pendingApprovals.value,
  failedExecutions: failedExecutions.value,
  currentPlanApprovalIds: new Set(currentPlanApprovals.value.map(item => String(item.id))),
  workPlanApproval: workPlanCanConfirm.value ? workPlan.value : null
}))
const approvalScrollSignature = computed(() => [
  ...conversationTimeline.value.map(item => `${item.key}:${item.value.status || ''}`)
].join('|'))
const streamingMessageVisible = computed(() => messages.value.some(message => message.isStreaming))
const canSend = computed(() => Boolean(selectedProviderId.value && !sending.value))
const isCodeModeActive = computed(() => isCodeMode(agentMode.value))
const agentModeCopyText = computed(() => agentModeCopy(agentMode.value))
// Generation only makes sense once the user has actually described something. Without this
// the button would happily start a run built from an empty conversation.
const hasRequirementContent = computed(() => messages.value.some(message =>
  String(message.role || '').toLowerCase() === 'user' && String(message.content || '').trim()
))
const canSwitchToCodeMode = computed(() => Boolean(
  selectedProviderId.value && !switchingMode.value && hasRequirementContent.value
))
const activeConversation = computed(() => conversations.value.find(
  item => String(item.id) === String(activeConversationId.value)
))
// True when the last successful generation is older than every requirement message. A re-run
// in that state would rebuild the same metadata and generate nothing, so the server refuses
// the switch until the client acknowledges it.
const hasNewRequirementSinceGeneration = computed(() => {
  const lastGeneratedAt = activeConversation.value?.lastGeneratedAt
  if (!lastGeneratedAt) return true
  const since = new Date(lastGeneratedAt).getTime()
  if (!Number.isFinite(since)) return true
  return messages.value.some(message => {
    if (String(message.role || '').toLowerCase() !== 'user') return false
    if (!String(message.content || '').trim()) return false
    const created = new Date(message.createTime || 0).getTime()
    return Number.isFinite(created) && created > since
  })
})
const lastGeneratedAtLabel = computed(() => {
  const value = activeConversation.value?.lastGeneratedAt
  if (!value) return ''
  const parsed = new Date(value)
  return Number.isFinite(parsed.getTime()) ? parsed.toLocaleString() : ''
})
// The button states what it will do instead of just saying "generate", because this is the
// one place the user knowingly authorizes writes.
const generateActionTip = computed(() => {
  if (!selectedProviderId.value) return '请先选择模型'
  if (!hasRequirementContent.value) return '请先描述需求，描述后即可生成代码'
  if (!hasNewRequirementSinceGeneration.value) {
    return `上次生成后没有新需求（${lastGeneratedAtLabel.value}），重新生成不会有变化`
  }
  return '将把当前对话中确认的需求写入项目元数据，然后生成代码'
})

const openDiagnosticLogs = async () => {
  try {
    const path = await openClientDiagnosticLogDirectory()
    if (path) ElMessage.success(`日志目录：${path}`)
  } catch (error) {
    ElMessage.error(error?.message || '无法打开 Agent 日志目录')
  }
}

watch(() => props.modelValue, value => { visible.value = value })
watch(approvalScrollSignature, () => scheduleScrollToBottom(), { flush: 'post' })
watch(visible, async value => {
  emit('update:modelValue', value)
  if (!value) stopWorkPlanPolling()
  if (value) {
    await loadInitialData()
  }
})

const loadInitialData = async () => {
  const [projectData] = await Promise.all([getProjectList({}), loadProviders()])
  projects.value = Array.isArray(projectData) ? projectData : []
}

const loadProviders = async () => {
  providers.value = await getProviders(executionMode)
  const selectedExists = enabledProviders.value.some(item => item.id === selectedProviderId.value)
  if (!selectedExists) {
    const preferred = enabledProviders.value.find(item => item.isDefault) || enabledProviders.value[0]
    selectedProviderId.value = preferred?.id || ''
  }
}

const loadWorkPlan = async () => {
  if (!selectedProjectId.value) {
    workPlan.value = null
    return
  }
  try {
    workPlan.value = await getActiveWorkPlan(selectedProjectId.value)
  } catch {
    workPlan.value = null
  }
}

const workPlanProgressStatus = plan => {
  if (!plan) return '正在等待任务计划…'
  const completed = Number(plan.completedTasks || 0)
  const total = Number(plan.totalTasks || 0)
  const task = selectCurrentWorkTask(plan)
  if (task?.title) {
    return `已完成 ${completed}/${total}，正在处理：${task.title}`
  }
  if (plan.status === 'Completed') return `全部 ${total} 个任务已完成`
  if (plan.status === 'Blocked' || plan.status === 'Failed') return `已完成 ${completed}/${total}，存在阻塞任务`
  return `已完成 ${completed}/${total}，正在准备下一项任务`
}

const stopWorkPlanPolling = () => {
  if (workPlanPollTimer) {
    clearInterval(workPlanPollTimer)
    workPlanPollTimer = null
  }
  workPlanPollBusy = false
}

const startWorkPlanPolling = streamingMessage => {
  stopWorkPlanPolling()
  if (!selectedProjectId.value || !streamingMessage) return

  const poll = async () => {
    if (workPlanPollBusy || !selectedProjectId.value || !sending.value) return
    workPlanPollBusy = true
    try {
      const latest = await getActiveWorkPlan(selectedProjectId.value)
      if (latest) {
        workPlan.value = latest
        streamingMessage.streamStatus = workPlanProgressStatus(latest)
      }
    } catch {
      // A transient status-poll failure must not interrupt the model request.
    } finally {
      workPlanPollBusy = false
    }
  }

  void poll()
  workPlanPollTimer = setInterval(() => { void poll() }, 1500)
}

const retryFailedTask = async task => {
  if (!workPlan.value || !task?.lastAttempt?.failureSignature) return
  retryingTaskId.value = task.id
  try {
    workPlan.value = await retryWorkPlanTask(workPlan.value.id, task.id, task.lastAttempt.failureSignature)
    ElMessage.success('失败任务已重新排队，稍后继续执行')
  } catch (error) {
    ElMessage.error(error?.message || '任务重新排队失败')
    await loadWorkPlan()
  } finally {
    retryingTaskId.value = ''
  }
}

const confirmCurrentWorkPlan = async () => {
  if (!workPlan.value?.id || !workPlan.value?.contentHash) return
  confirmingWorkPlan.value = true
  try {
    const confirmedResult = await confirmWorkPlan(workPlan.value.id, {
      expectedStructureVersion: workPlan.value.structureVersion,
      contentHash: workPlan.value.contentHash
    })
    const confirmed = confirmedResult?.plan || confirmedResult
    workPlan.value = confirmed
    ElMessage.success('方案已确认，正在按任务顺序执行')
    await applyConfirmedWorkPlanMetadata(confirmedResult?.execution || null)
  } catch (error) {
    ElMessage.error(error?.message || '确认工作计划失败')
    await loadWorkPlan()
  } finally {
    confirmingWorkPlan.value = false
  }
}

const applyConfirmedWorkPlanMetadata = async initialExecution => {
  if (!workPlan.value?.id || applyingWorkPlanMetadata.value) return
  applyingWorkPlanMetadata.value = true
  try {
    await advanceConfirmedWorkPlan(initialExecution)
    await loadWorkPlan()
    if (workPlan.value?.status !== 'Completed') {
      if (hasBlockedPlanTask(workPlan.value)) {
        scheduleAutomaticPlanRepair()
        return
      }
      if (workPlan.value?.status === 'Blocked' || workPlan.value?.status === 'Failed') {
        ElMessage.warning('当前任务未完成，已保留在任务列表中；修复后会继续执行')
        scheduleAutomaticPlanRepair()
      }
      return
    }
    ElMessage.success('元数据已全部应用；源代码请在项目页面手动生成')
  } catch (error) {
    ElMessage.error(error?.message || '应用工作计划元数据失败')
    await loadWorkPlan()
  } finally {
    applyingWorkPlanMetadata.value = false
  }
}

const advanceConfirmedWorkPlan = async initialExecution => {
  let execution = initialExecution || null
  const processedExecutions = new Set()
  while (workPlan.value?.id) {
    if (execution) {
      const progressKey = executionProgressKey(execution)
      if (progressKey && processedExecutions.has(progressKey)) {
        await loadWorkPlan()
        return
      }
      if (progressKey) processedExecutions.add(progressKey)
      updateExecution(execution)
      if (execution.status === 'Failed')
        throw new Error(execution.errorMessage || '工作计划执行失败')
      if (execution.status === 'AwaitingClientExecution') {
        execution = await executeClientActions(execution)
        continue
      }
    }

    await loadWorkPlan()
    if (!workPlanCanResume.value) return
    const resumed = await resumeWorkPlan(workPlan.value.id, activeConversationId.value)
    if (resumed?.plan) workPlan.value = resumed.plan
    execution = resumed?.execution || null
    if (!execution) {
      await loadWorkPlan()
      return
    }
  }
}

const handleProjectChange = async () => {
  activeConversationId.value = ''
  messages.value = []
  toolExecutions.value = []
  workPlan.value = null
  conversations.value = selectedProjectId.value
    ? await getConversations(selectedProjectId.value, executionMode)
    : []

  if (conversations.value.length > 0) {
    activeConversationId.value = conversations.value[0].id
    await loadConversation()
  } else {
    resetWorkflowPreferences()
    await loadWorkPlan()
  }
}

const handleProviderChange = async providerId => {
  if (!activeConversationId.value) return
  const conversation = conversations.value.find(item => item.id === activeConversationId.value)
  if (!conversation || String(conversation.providerId) === String(providerId)) return

  const previousProviderId = conversation.providerId
  try {
    const updated = await updateConversationProvider(activeConversationId.value, providerId)
    conversations.value = conversations.value.map(item =>
      String(item.id) === String(activeConversationId.value)
        ? { ...item, ...updated }
        : item
    )
    selectedProviderId.value = updated.providerId
    await loadConversation()
    ElMessage.success(`当前对话已切换到 ${selectedProvider.value?.name || '新模型'}`)
  } catch (error) {
    selectedProviderId.value = previousProviderId
    ElMessage.error(error?.message || '切换当前对话模型失败')
  }
}

const handleConversationChange = async () => {
  if (!activeConversationId.value) {
    messages.value = []
    toolExecutions.value = []
    resetWorkflowPreferences()
    await loadWorkPlan()
    return
  }

  const conversation = conversations.value.find(item => item.id === activeConversationId.value)
  if (conversation) {
    selectedProviderId.value = conversation.providerId
  }
  await loadConversation()
}

const loadConversation = async () => {
  if (!activeConversationId.value) return
  const [messageData, executionData] = await Promise.all([
    getMessages(activeConversationId.value),
    getToolExecutions(activeConversationId.value),
    loadWorkPlan()
  ])
  messages.value = messageData || []
  toolExecutions.value = executionData || []
  const conversation = conversations.value.find(item => String(item.id) === String(activeConversationId.value))
  if (conversation) {
    selectedProviderId.value = conversation.providerId
  }
  workflowMode.value = normalizeWorkflowMode(conversation?.workflowMode)
  agentMode.value = normalizeAgentMode(conversation?.agentMode)
  autoApproveApprovals.value = Boolean(conversation?.autoApproveApprovals)
  await scrollToBottom()
  if (autoApprovalEnabled.value) scheduleAutomaticApprovalProcessing()
  scheduleAutomaticClientActionRecovery()
}

const resetWorkflowPreferences = () => {
  workflowMode.value = DEFAULT_AGENT_WORKFLOW_MODE
  agentMode.value = DEFAULT_AGENT_MODE
  autoApproveApprovals.value = false
}

const handleWorkflowPreferencesChange = async () => {
  workflowMode.value = normalizeWorkflowMode(workflowMode.value)
  if (!activeConversationId.value) return
  try {
    const conversation = await updateConversationPreferences(activeConversationId.value, {
      workflowMode: workflowMode.value,
      autoApproveApprovals: autoApproveApprovals.value
    })
    workflowMode.value = normalizeWorkflowMode(conversation?.workflowMode || workflowMode.value)
    autoApproveApprovals.value = Boolean(conversation?.autoApproveApprovals)
    conversations.value = conversations.value.map(item => String(item.id) === String(activeConversationId.value)
      ? { ...item, ...conversation }
      : item)
    ElMessage.success('Agent 工作模式已更新')
    if (autoApprovalEnabled.value) scheduleAutomaticApprovalProcessing()
  } catch (error) {
    ElMessage.error(error?.message || '更新 Agent 工作模式失败')
    await loadConversation()
  }
}

const ensureConversation = async () => {
  if (activeConversationId.value) return activeConversationId.value
  const conversation = await createConversation({
    projectId: selectedProjectId.value,
    providerId: selectedProviderId.value,
    workflowMode: workflowMode.value,
    agentMode: agentMode.value,
    autoApproveApprovals: autoApproveApprovals.value
  })
  activeConversationId.value = conversation.id
  workflowMode.value = normalizeWorkflowMode(conversation.workflowMode || workflowMode.value)
  agentMode.value = normalizeAgentMode(conversation.agentMode || agentMode.value)
  autoApproveApprovals.value = Boolean(conversation.autoApproveApprovals)
  conversations.value.unshift(conversation)
  return conversation.id
}

const openCreateWizard = () => {
  wizardVisible.value = true
}

const handleWizardInitialized = async (projectId) => {
  const queuedDraft = takePendingDraft(pendingDraft)
  wizardVisible.value = false
  projects.value = await getProjectList({}) || []
  selectedProjectId.value = String(projectId)
  await handleProjectChange()
  if (queuedDraft) {
    draft.value = queuedDraft
    await handleSend()
  }
}

watch(wizardVisible, value => {
  if (!value && pendingDraft.value) {
    draft.value = pendingDraft.value
    pendingDraft.value = ''
  }
})

const applyAgentResult = async (result, requestId) => {
  if (isRequestFailure(result?.message)) {
    const error = new Error(result.message.content)
    error.agentTerminal = true
    throw error
  }
  messages.value = resolveRequestFailure(messages.value, requestId)
  const assistantIndex = messages.value.findIndex(message =>
    message.role === 'assistant' && message.requestId === requestId
  )
  if (assistantIndex >= 0) {
    messages.value[assistantIndex] = result.message
  } else {
    messages.value.push(result.message)
  }
  if (Array.isArray(result.pendingApprovals)) {
    const otherExecutions = toolExecutions.value.filter(item =>
      item.status !== 'PendingApproval' && item.status !== 'AwaitingClientExecution'
    )
    toolExecutions.value = [...otherExecutions, ...result.pendingApprovals]
  }
  await loadWorkPlan()
  try {
    conversations.value = await getConversations(selectedProjectId.value, executionMode)
  } catch {
    // The saved assistant message is authoritative even when refreshing the list fails.
  }
  retryRequestId.value = ''
  retryContent.value = ''
  await scrollToBottom()
  if (autoApprovalEnabled.value) scheduleAutomaticApprovalProcessing()
}

const waitForServerCompletion = async (conversationId, requestId, streamingMessage) => {
  const deadline = Date.now() + 10 * 60 * 1000
  if (streamingMessage) {
    streamingMessage.streamStatus = '连接已中断，后台 Agent 仍在继续处理…'
  }

  while (Date.now() < deadline) {
    if (cancelRequested.value) return null
    try {
      const serverMessages = await getMessages(conversationId)
      const assistant = requestOutcome(serverMessages || [], requestId)
      if (assistant) {
        const executions = await getToolExecutions(conversationId)
        return {
          message: assistant,
          pendingApprovals: (executions || []).filter(item =>
            item.status === 'PendingApproval' || item.status === 'AwaitingClientExecution'
          )
        }
      }
    } catch {
      // A transient poll failure should not restart the model request.
    }
    await new Promise(resolve => setTimeout(resolve, 1500))
  }
  return null
}

const persistAgentMode = async (target, force = false) => {
  // A conversation is only created on the first message, so a mode chosen before that is
  // carried into createConversation instead of needing its own request.
  if (!activeConversationId.value) {
    agentMode.value = target
    return true
  }
  const conversation = await switchAgentMode(activeConversationId.value, target, force)
  agentMode.value = normalizeAgentMode(conversation?.agentMode || target)
  conversations.value = conversations.value.map(item =>
    String(item.id) === String(activeConversationId.value)
      ? { ...item, agentMode: agentMode.value, lastGeneratedAt: conversation?.lastGeneratedAt }
      : item
  )
  return true
}

const handleAgentModeChange = async value => {
  const target = normalizeAgentMode(value)
  const previous = agentMode.value
  if (target === previous) return
  try {
    switchingMode.value = true
    await persistAgentMode(target)
  } catch (error) {
    agentMode.value = previous
    ElMessage.error(error?.message || '切换 Agent 页面失败')
  } finally {
    switchingMode.value = false
  }
}

/**
 * The single authorization action in the workflow. Everything before it is read-only, so
 * this confirmation is the one place the user knowingly hands over write access. The server
 * independently refuses the switch when the conversation holds no requirement.
 */
const handleStartGeneration = async () => {
  if (!canSwitchToCodeMode.value || sending.value) return

  // Ask once more when nothing new arrived, because a re-run would not change anything.
  let force = false
  if (!hasNewRequirementSinceGeneration.value) {
    try {
      await ElMessageBox.confirm(
        `上次生成时间是 ${lastGeneratedAtLabel.value}，此后没有新的需求内容，强制重新生成不会带来变化。确定继续吗？`,
        '没有检测到新需求',
        { type: 'warning', confirmButtonText: '仍然生成', cancelButtonText: '返回需求设计' }
      )
    } catch {
      return
    }
    force = true
  }

  try {
    await ElMessageBox.confirm(
      '接下来会依据当前对话中已确认的需求写入项目元数据，然后生成代码。这一步会真正修改项目，确定继续吗？',
      '确认生成代码',
      { type: 'warning', confirmButtonText: '开始生成', cancelButtonText: '再想想' }
    )
  } catch {
    return
  }

  try {
    switchingMode.value = true
    await persistAgentMode(AGENT_MODE.CODE, force)
  } catch (error) {
    ElMessage.error(error?.message || '切换到生成代码页失败')
    return
  } finally {
    switchingMode.value = false
  }

  // Code mode is the authorization itself, so this message starts the real work without
  // asking the user to confirm again.
  await handleSend('确认，按已讨论的需求开始生成代码')
}

const handleSend = async (contentOverride = null, requestIdOverride = '') => {
  const hasContentOverride = typeof contentOverride === 'string'
  const content = String(hasContentOverride ? contentOverride : draft.value).trim()
  if (!content || !canSend.value) return false

  if (!selectedProjectId.value) {
    pendingDraft.value = content
    if (!hasContentOverride) draft.value = ''
    openCreateWizard()
    return false
  }

  const requestId = requestIdOverride || (retryRequestId.value && retryContent.value === content
    ? retryRequestId.value
    : createRequestId())

  sending.value = true
  cancelRequested.value = false
  if (!hasContentOverride) draft.value = ''
  let streamingMessage
  try {
    const conversationId = await ensureConversation()
    const existingUserMessage = messages.value.some(message =>
      message.role === 'user' && message.requestId === requestId
    )
    if (!existingUserMessage) {
      messages.value.push({
        id: `local-${Date.now()}`,
        requestId,
        role: 'user',
        content,
        createTime: new Date().toISOString()
      })
    }
    await scrollToBottom()

    const payload = { conversationId, requestId, content }
    streamingMessage = {
      id: `stream-${requestId}`,
      requestId,
      role: 'assistant',
      content: '',
      createTime: new Date().toISOString(),
      isStreaming: true,
      streamStatus: selectedProvider.value?.executionMode === 'Local'
        ? '正在连接本地 Agent…'
        : '正在连接服务器 Agent…'
    }
    const existingAssistantIndex = messages.value.findIndex(message =>
      message.role === 'assistant' && message.requestId === requestId
    )
    if (existingAssistantIndex >= 0) {
      messages.value[existingAssistantIndex] = streamingMessage
      streamingMessage = messages.value[existingAssistantIndex]
    } else {
      messages.value.push(streamingMessage)
      streamingMessage = messages.value[messages.value.length - 1]
    }
    await scrollToBottom()
    startWorkPlanPolling(streamingMessage)

    if (selectedProvider.value?.executionMode !== 'Local') {
      serverAbortController.value = new AbortController()
      activeServerRequest.value = { conversationId, requestId }
    }
    const result = selectedProvider.value?.executionMode === 'Local'
      ? await executeCodegenAction(
        'agentChat',
        payload,
        () => Promise.reject(new Error('本机模型只能在 CodeMaster 客户端中使用')),
        { onProgress: progress => updateStreamingMessage(streamingMessage, progress) }
      )
      : await streamMessage(payload, {
        signal: serverAbortController.value.signal,
        onProgress: progress => updateStreamingMessage(streamingMessage, progress)
      })
    await applyAgentResult(result, requestId)
    return true
  } catch (error) {
    if (cancelRequested.value) {
      removeStreamingMessage(streamingMessage)
      retryRequestId.value = requestId
      retryContent.value = content
      draft.value = content
      ElMessage.info('已停止本次 Agent 请求')
      return false
    }
    if (!error?.agentTerminal && selectedProvider.value?.executionMode !== 'Local') {
      const recovered = await waitForServerCompletion(activeServerRequest.value?.conversationId || activeConversationId.value, requestId, streamingMessage)
      if (cancelRequested.value) {
        removeStreamingMessage(streamingMessage)
        retryRequestId.value = requestId
        retryContent.value = content
        draft.value = content
        ElMessage.info('已停止本次 Agent 请求')
        return false
      }
      if (recovered) {
        if (isRequestFailure(recovered.message)) {
          error = new Error(recovered.message.content)
          error.agentTerminal = true
        } else {
          await applyAgentResult(recovered, requestId)
          return true
        }
      }
    }
    try {
      await loadConversation()
      const completed = messages.value.some(message =>
        message.role === 'assistant' && message.requestId === requestId
      )
      if (completed) {
        retryRequestId.value = ''
        retryContent.value = ''
        return true
      }
    } catch {
      // Keep the original request id so retrying the same text remains idempotent.
    }
    retryRequestId.value = requestId
    retryContent.value = content
    draft.value = content
    const diagnostic = error?.message || 'AI 请求失败，请检查客户端模型配置和本地 Agent 状态'
    messages.value = keepRequestFailure(messages.value, requestId, diagnostic)
    await scrollToBottom()
    ElMessage.error('本次请求失败，具体原因已保留在对话中')
    return false
  } finally {
    stopWorkPlanPolling()
    activeServerRequest.value = null
    serverAbortController.value = null
    sending.value = false
  }
}

const retryRequestFailure = async message => {
  const original = messages.value.find(item => item.role === 'user' && item.requestId === message.requestId)
  if (!original || sending.value) return
  await handleSend(original.content, message.requestId)
}

const streamPhaseTitle = phase => ({
  connected: '已连接 Agent，正在准备上下文…',
  loading_context: '正在读取对话和项目上下文…',
  generating: '正在生成回复…',
  completing_answer: '正在完成正文…',
  saving: '正在保存回复…',
  stream_fallback: '流式连接不稳定，正在切换兼容模式…',
  completed: ''
}[phase] ?? 'Agent 正在处理…')

const streamToolTitle = name => ({
  get_project_summary: '正在读取项目摘要…',
  get_project_structure: '正在读取项目结构…',
  get_codegen_catalog: '正在读取控件与代码生成规则…',
  get_entity_blueprint: '正在读取实体字段…',
  get_ui_page_blueprint: '正在读取页面设计…',
  get_project_dictionaries: '正在读取项目字典…',
  get_ui_composition_context: '正在读取数据源与复杂页面…',
  get_view_builder_context: '正在读取视图构建上下文…',
  preview_openapi_import: '正在预览 OpenAPI 变更…',
  propose_project_change_set: '正在整理项目变更方案…',
  propose_dictionary_change: '正在整理字典变更方案…',
  propose_ui_page_enhancement: '正在整理页面变更方案…',
  propose_view_data_source_plan: '正在校验视图数据源方案…',
  propose_openapi_import: '正在校验 OpenAPI 导入方案…',
  get_complex_page_context: '正在读取复杂页面上下文…',
  propose_complex_page_plan: '正在校验复杂页面方案…'
}[name] || `正在调用 ${name}…`)

const updateStreamingMessage = (message, progress) => {
  if (!message || !progress) return

  if (progress.event === 'text_delta' && progress.text) {
    message.content += progress.text
    message.streamStatus = '正在生成回复…'
  } else if (progress.event === 'text_replace') {
    message.content = progress.text || ''
    message.streamStatus = '正在保存回复…'
  } else if (progress.event === 'tool_started') {
    message.streamStatus = streamToolTitle(progress.toolName)
  } else if (progress.event === 'tool_completed') {
    message.streamStatus = '工具返回完成，正在继续分析…'
  } else if (progress.event === 'tool_failed') {
    message.toolError = progress.message || '工具调用失败，服务端未返回具体诊断。'
    message.toolErrorDetail = buildToolErrorDetail(progress)
    message.streamStatus = '工具调用失败，正在处理错误…'
  } else if (progress.event === 'status') {
    message.streamStatus = streamPhaseTitle(progress.phase)
  } else if (progress.event === 'compile_progress') {
    applyCompileProgress(message, progress)
  } else if (progress.event === 'stage_started') {
    applyStageEvent(message, progress, 'started')
  } else if (progress.event === 'stage_completed') {
    applyStageEvent(message, progress, 'completed')
  } else if (progress.event === 'task_started') {
    applyTaskEvent(message, progress, 'started')
  } else if (progress.event === 'task_completed') {
    applyTaskEvent(message, progress, 'completed')
  }

  scrollToBottom()
}

const ensureProgressState = message => {
  if (!message.compileProgress) {
    message.compileProgress = {
      active: false,
      stage: '',
      percent: 0,
      message: '',
      addedEntities: 0,
      addedViews: 0,
      totalTasks: 0
    }
  }
  if (!message.stageProgress) {
    message.stageProgress = []
  }
  if (!message.taskProgress) {
    message.taskProgress = {
      active: false,
      planId: '',
      taskKey: '',
      title: '',
      index: 0,
      total: 0,
      success: false,
      errorMessage: ''
    }
  }
  return message
}

const stageStatusLabel = status => ({
  pending: '待开始',
  in_progress: '进行中',
  completed: '已完成',
  failed: '失败'
}[status] || status)

const applyCompileProgress = (message, progress) => {
  ensureProgressState(message)
  const cp = message.compileProgress
  cp.active = true
  cp.stage = progress.stage || cp.stage
  cp.percent = Number(progress.percent || cp.percent || 0)
  cp.message = progress.message || cp.message
  if (progress.addedEntities != null) cp.addedEntities = Number(progress.addedEntities)
  if (progress.addedViews != null) cp.addedViews = Number(progress.addedViews)
  if (progress.totalTasks != null) cp.totalTasks = Number(progress.totalTasks)
  message.streamStatus = cp.message || cp.stage || '正在编译业务计划…'
}

const applyStageEvent = (message, progress, kind) => {
  ensureProgressState(message)
  const stages = message.stageProgress
  const key = progress.stageKey || progress.stage || progress.toolStage
  let entry = stages.find(item => item.key === key)
  if (!entry) {
    entry = {
      key,
      title: progress.stage || progress.message || key,
      status: kind === 'started' ? 'in_progress' : 'pending',
      startedAt: null,
      completedAt: null
    }
    stages.push(entry)
  }
  if (kind === 'started') {
    entry.status = 'in_progress'
    entry.startedAt = entry.startedAt || new Date().toISOString()
  } else if (kind === 'completed') {
    entry.status = 'completed'
    entry.completedAt = new Date().toISOString()
  }
  message.streamStatus = `${entry.title}: ${kind === 'started' ? '开始' : '完成'}`
}

const applyTaskEvent = (message, progress, kind) => {
  ensureProgressState(message)
  const tp = message.taskProgress
  tp.active = true
  tp.planId = progress.planId || tp.planId
  if (kind === 'started') {
    tp.taskKey = progress.taskKey || tp.taskKey
    tp.title = progress.title || tp.title
    tp.index = Number(progress.taskIndex || tp.index)
    tp.total = Number(progress.total || tp.total)
    tp.success = false
    tp.errorMessage = ''
  } else if (kind === 'completed') {
    tp.success = progress.taskSuccess !== false && !progress.errorMessage
    tp.errorMessage = progress.errorMessage || ''
  }
  message.streamStatus = kind === 'started'
    ? `正在执行 ${tp.index || 1}/${tp.total || '?'}：${tp.title || tp.taskKey}`
    : (tp.success
      ? `任务完成：${tp.title || tp.taskKey}`
      : `任务失败：${tp.taskKey}（${tp.errorMessage || ''}）`)
}

const removeStreamingMessage = message => {
  if (!message) return
  messages.value = messages.value.filter(item => item !== message)
}

const clearToolError = message => {
  if (!message) return
  message.toolError = ''
  message.toolErrorDetail = null
}

const toggleStack = message => {
  if (!message?.toolErrorDetail) return
  message.toolErrorDetail.stackOpen = !message.toolErrorDetail.stackOpen
}

const copyFailureDiagnostic = async message => {
  if (!message?.toolErrorDetail) return
  const detail = message.toolErrorDetail
  const lines = [
    `工具: ${detail.toolName || '(未知)'}`,
    `阶段: ${detail.stageLabel || detail.toolStage || '(未知)'}`,
    `异常类型: ${detail.exceptionType || '(未知)'}`,
    `出错位置: ${detail.exceptionSource || '(未知)'}`,
    `分类: ${detail.kindLabel || '(未知)'}`,
    `第 ${detail.attempt} 次重试`,
    `建议: ${detail.nextAction || '(无)'}`,
    '',
    '消息:',
    detail.message || '(无)',
    '',
    detail.stackSnippet ? '堆栈:\n' + detail.stackSnippet : ''
  ].filter(Boolean)
  const text = lines.join('\n')
  try {
    if (navigator?.clipboard?.writeText) {
      await navigator.clipboard.writeText(text)
    } else {
      const textarea = document.createElement('textarea')
      textarea.value = text
      document.body.appendChild(textarea)
      textarea.select()
      document.execCommand('copy')
      document.body.removeChild(textarea)
    }
    ElMessage.success('诊断已复制到剪贴板')
  } catch (error) {
    ElMessage.error('复制失败:' + (error?.message || error))
  }
}

const buildToolErrorDetail = progress => {
  const stage = progress?.toolStage || null
  const kind = progress?.failureKind || 'Unknown'
  const detail = {
    toolName: progress?.toolName || null,
    toolStage: stage,
    stageLabel: stage ? stageLabel(stage) : null,
    exceptionType: progress?.exceptionType || null,
    exceptionSource: progress?.exceptionSource || null,
    failureKind: kind,
    kindLabel: kindLabel(kind),
    kindTone: kindTone(kind),
    nextAction: progress?.nextAction || null,
    attempt: Math.max(1, Number(progress?.attempt) || 1),
    correlationId: progress?.correlationId || null,
    message: progress?.message || null,
    stackSnippet: progress?.stackSnippet || null,
    stackOpen: false
  }
  return detail
}

const stageLabels = {
  EntityFields: '实体与字段',
  Views: '视图',
  Relations: '关系',
  FieldControls: '字段控件',
  ComplexPages: '复杂页面',
  ConsistencyCheck: '一致性校验',
  GenerateCode: '代码生成',
  plan: '任务计划',
  Workflow: '工作流'
}
const stageLabel = key => stageLabels[key] || key || null

const kindLabels = {
  Infrastructure: '通道失败',
  Authorization: '权限不足',
  NotFound: '目标不存在',
  Validation: '元数据校验失败',
  ToolExecution: '工具异常',
  Unknown: '未知错误'
}
const kindLabel = key => kindLabels[key] || key || '未知错误'

const kindTone = key => ({
  Infrastructure: 'warning',
  Authorization: 'warning',
  NotFound: 'danger',
  Validation: 'warning',
  ToolExecution: 'danger',
  Unknown: 'info'
}[key] || 'danger')

const stopCurrentAgentRequest = async () => {
  if (!sending.value || !canStopAgentRequest.value || cancelRequested.value) return
  cancelRequested.value = true
  if (activeServerRequest.value) {
    try {
      const cancelled = await cancelAgentRequest(activeServerRequest.value)
      if (!cancelled) {
        cancelRequested.value = false
        ElMessage.info('本次 Agent 请求已结束，正在同步结果')
      }
      serverAbortController.value?.abort()
    } catch (error) {
      cancelRequested.value = false
      ElMessage.error(error?.message || '停止服务器 Agent 请求失败')
    }
    return
  }
  try {
    const cancelled = await cancelLocalCodegenExecution()
    if (!cancelled) {
      cancelRequested.value = false
      ElMessage.warning('当前没有可停止的本地 Agent 请求')
    }
  } catch (error) {
    cancelRequested.value = false
    ElMessage.error(error?.message || '停止 Agent 请求失败')
  }
}

const createRequestId = () => {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID()
  return `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

const isMetadataApplicationRunning = executionId => metadataApplicationIds.value.has(String(executionId))

const setMetadataApplicationRunning = (executionId, running) => {
  const next = new Set(metadataApplicationIds.value)
  if (running) next.add(String(executionId))
  else next.delete(String(executionId))
  metadataApplicationIds.value = next
}

const approveApproval = async approval => {
  if (approval.status === 'Executing' || isMetadataApplicationRunning(approval.id)) return
  if (approval.status === 'Approved') {
    void applyApprovedMetadata(approval)
    return
  }
  if (approval.status !== 'PendingApproval') return

  approvalLoadingId.value = approval.id
  try {
    const approvalResponse = await approveTool(approval.id)
    const approved = approvalResponse?.execution || approvalResponse
    updateExecution(approved)
    ElMessage.success('变更已确认，正在应用到 CodeMaster')
    void applyApprovedMetadata(approved)
  } catch (error) {
    ElMessage.error(error?.message || '确认审批失败')
    await loadConversation()
  } finally {
    approvalLoadingId.value = ''
  }
}

const applyApprovedMetadata = async approval => {
  if (!approval?.id || isMetadataApplicationRunning(approval.id)) return false
  setMetadataApplicationRunning(approval.id, true)
  const startedAt = performance.now()
  await writeClientDiagnostic('agent.metadata_application.started', {
    executionId: approval.id,
    action: approval.toolName,
    stage: 'metadata-application'
  })
  try {
    const approvalResponse = await executeApprovedTool(approval.id)
    const result = approvalResponse?.execution || approvalResponse
    updateExecution(result)
    const automaticRepairScheduled = Boolean(
      approvalResponse?.repairMessage ||
      approvalResponse?.localRepairRequest ||
      (Array.isArray(approvalResponse?.pendingApprovals) && approvalResponse.pendingApprovals.length > 0)
    )
    if (approvalResponse?.localRepairRequest) {
      const repairResult = await executeCodegenAction(
        'agentChat',
        approvalResponse.localRepairRequest,
        () => Promise.reject(new Error('本地自动修复只能在 CodeMaster 客户端中执行'))
      )
      if (repairResult?.message) {
        await applyAgentResult(repairResult, approvalResponse.localRepairRequest.requestId)
      }
    } else if (Array.isArray(approvalResponse?.pendingApprovals)) {
      const otherExecutions = toolExecutions.value.filter(item =>
        item.status !== 'PendingApproval' && item.status !== 'AwaitingClientExecution'
      )
      toolExecutions.value = [...otherExecutions, ...approvalResponse.pendingApprovals]
    }
    if (result.status === 'Failed' && !automaticRepairScheduled) {
      throw new Error(result.errorMessage || '变更执行失败')
    }
    if (result.status === 'Failed') {
      await loadConversation()
      ElMessage.warning('变更执行失败，Agent 已收到诊断并开始自动修复')
      return false
    }
    if (result.status === 'Rejected') {
      ElMessage.warning('该变更已经被拒绝')
      return false
    }
    if (result.status === 'Executing') {
      ElMessage.info('元数据正在应用，请稍后刷新')
      return false
    }
    if (['project_change_set', 'propose_view_data_source_plan', 'propose_openapi_import'].includes(approval.toolName)
      && activeConversationId.value) {
      try {
        await invalidateLocalAgentContext(activeConversationId.value)
      } catch (error) {
        await writeClientDiagnostic('agent.context.invalidate.failed', {
          level: 'warning',
          executionId: approval.id,
          action: approval.toolName,
          stage: 'context-invalidation',
          message: error?.message || 'Local Agent context invalidation failed'
        })
      }
    }
    ElMessage.success('元数据已应用；代码生成和验证将在全局项目操作中统一执行')
    await writeClientDiagnostic('agent.metadata_application.completed', {
      executionId: approval.id,
      action: approval.toolName,
      stage: 'metadata-application',
      durationMs: performance.now() - startedAt
    })
    return true
  } catch (error) {
    console.error('Agent metadata application failed:', error)
    await writeClientDiagnostic('agent.metadata_application.failed', {
      level: 'error',
      executionId: approval.id,
      action: approval.toolName,
      stage: 'metadata-application',
      durationMs: performance.now() - startedAt,
      message: getClientActionDiagnostic(error)
    })
    try {
      await loadConversation()
    } catch {
      // The server or sidecar already owns the real execution result.
    }
    ElMessage.error(error?.message || '元数据应用失败，错误详情已保留在当前对话中')
    return false
  } finally {
    setMetadataApplicationRunning(approval.id, false)
    await loadWorkPlan()
  }
}

const scheduleAutomaticApprovalProcessing = () => {
  if (typeof requestAnimationFrame === 'function') {
    requestAnimationFrame(() => void processAutomaticApprovals())
  } else {
    setTimeout(() => void processAutomaticApprovals(), 0)
  }
}

const scheduleAutomaticClientActionRecovery = () => {
  if (typeof requestAnimationFrame === 'function') {
    requestAnimationFrame(() => void processAutomaticClientActionRecovery())
  } else {
    setTimeout(() => void processAutomaticClientActionRecovery(), 0)
  }
}

const processAutomaticClientActionRecovery = async () => {
  if (automaticClientRecoveryRunning.value || !activeConversationId.value) return
  const pending = toolExecutions.value.find(item => item.status === 'AwaitingClientExecution')
  if (!pending) return

  automaticClientRecoveryRunning.value = true
  try {
    await executeClientActions(pending)
  } catch (error) {
    ElMessage.error(error?.message || '客户端任务恢复失败，错误详情已保留在当前对话中')
    try {
      await loadConversation()
    } catch {
      // Keep the persisted execution state visible on the next refresh.
    }
  } finally {
    automaticClientRecoveryRunning.value = false
  }
}

const processAutomaticApprovals = async () => {
  if (!autoApprovalEnabled.value || automaticApprovalRunning.value || !activeConversationId.value) return
  automaticApprovalRunning.value = true
  let appliedStandaloneMetadata = false
  const processedApprovals = new Set()
  try {
    while (true) {
      await loadWorkPlan()
      if (hasBlockedPlanTask(workPlan.value) || workPlan.value?.status === 'Blocked' || workPlan.value?.status === 'Failed') {
        scheduleAutomaticPlanRepair()
        break
      }
      if (workPlanCanConfirm.value) {
        confirmingWorkPlan.value = true
        try {
          const confirmedResult = await confirmWorkPlan(workPlan.value.id, {
            expectedStructureVersion: workPlan.value.structureVersion,
            contentHash: workPlan.value.contentHash
          })
          workPlan.value = confirmedResult?.plan || confirmedResult
          ElMessage.info('已自动确认方案，正在按任务顺序执行')
          await applyConfirmedWorkPlanMetadata(confirmedResult?.execution || null)
          return
        } finally {
          confirmingWorkPlan.value = false
        }
      }
      if (workPlanCanResume.value) {
        await applyConfirmedWorkPlanMetadata()
        return
      }

      const executionData = await getToolExecutions(activeConversationId.value)
      toolExecutions.value = executionData || []
      const approval = toolExecutions.value.find(item =>
        item.status === 'PendingApproval' || item.status === 'Approved'
      )
      if (!approval) break
      const approvalKey = executionProgressKey(approval)
      if (approvalKey && processedApprovals.has(approvalKey)) break
      if (approvalKey) processedApprovals.add(approvalKey)

      let approved = approval
      if (approval.status === 'PendingApproval') {
        const approvalResponse = await approveTool(approval.id)
        approved = approvalResponse?.execution || approvalResponse
        updateExecution(approved)
      }
      const applied = await applyApprovedMetadata(approved)
      if (!applied) return
      appliedStandaloneMetadata = true
    }

    if (appliedStandaloneMetadata && autoApproveApprovals.value) {
      ElMessage.success('元数据已完成；源代码请在项目页面手动生成')
    }
  } catch (error) {
    ElMessage.error(error?.message || '自动应用变更失败，可手动继续')
    try {
      await loadConversation()
    } catch {
      // The saved execution state remains authoritative.
    }
  } finally {
    automaticApprovalRunning.value = false
  }
}

const hasBlockedPlanTask = plan => (plan?.stages || [])
  .flatMap(stage => stage.tasks || [])
  .some(task => task.status === 'Blocked' && task.lastDiagnosis)

const scheduleAutomaticPlanRepair = () => {
  if (!autoApprovalEnabled.value || !activeConversationId.value || !workPlan.value) return
  const blockedTask = (workPlan.value.stages || [])
    .flatMap(stage => stage.tasks || [])
    .find(task => task.status === 'Blocked' && task.lastDiagnosis)
  if (!blockedTask) return

  const repairKey = `${workPlan.value.id}:${workPlan.value.structureVersion}:${blockedTask.taskKey}`
  if (automaticPlanRepairKeys.value.has(repairKey)) return
  automaticPlanRepairKeys.value = new Set([...automaticPlanRepairKeys.value, repairKey])

  const prompt = `请自动修复当前任务计划。第一个阻塞任务是 ${blockedTask.taskKey}，原因是：${blockedTask.lastDiagnosis}。请读取该任务详情，补全最小必要的可执行定义，保留已完成任务和其他未指定任务，不要重新创建已完成的 Project 实体，然后继续执行后续任务，不要等待我确认。`
  ElMessage.info('发现后续任务定义不完整，Agent 正在自动修复并继续执行')
  setTimeout(() => {
    void handleSend(prompt)
  }, 0)
}

const executeClientActions = async execution => {
  const output = parseJson(execution.outputJson)
  const actions = Array.isArray(output?.clientActions) ? output.clientActions : []
  if (actions.length === 0) {
    throw new Error(execution.errorMessage || '审批结果缺少客户端执行动作，请重新提交本次变更')
  }
  const completedActions = []
  let currentAction = null
  try {
    for (const action of actions) {
      currentAction = action
      let actionResult
      const actionStartedAt = performance.now()
      await writeClientDiagnostic('agent.client_action.started', {
        executionId: execution.id,
        action: action.action,
        stage: 'client-action'
      })
      try {
        if (action.action === 'generateCode') {
          if (!action.entityId) throw new Error('全量生成任务缺少实体 ID')
          actionResult = await generateCode(action.entityId)
        } else if (action.action === 'generateIncrementalCode') {
          if (!action.entityId) throw new Error('增量生成任务缺少实体 ID')
          actionResult = await generateIncrementalCode(action.entityId)
        } else if (action.action === 'generateSmartCode') {
          if (!action.entityId) throw new Error('生成任务缺少实体 ID')
          actionResult = await generateSmartCode(action.entityId)
        } else if (action.action === 'generateProjectCode') {
          if (!action.projectId) throw new Error('全量生成任务缺少项目 ID')
          actionResult = await generateProjectCode(action.projectId, action.entityIds || [])
        } else if (action.action === 'generateProjectIncrementalCode') {
          if (!action.projectId) throw new Error('增量生成任务缺少项目 ID')
          actionResult = await generateProjectIncrementalCode(action.projectId, action.entityIds || [])
        } else if (action.action === 'generateProjectSmartCode') {
          if (!action.projectId) throw new Error('生成任务缺少项目 ID')
          actionResult = await generateProjectSmartCode(action.projectId, action.entityIds || [])
        } else if (action.action === 'buildProject') {
          actionResult = await buildProject(action.projectId)
        } else if (action.action === 'migrateDatabase') {
          actionResult = await migrateDatabase(action.projectId)
        } else if (action.action === 'testProject') {
          actionResult = await testProject(action.projectId)
        } else if (action.action === 'enhanceUiPage') {
          if (!action.projectId) throw new Error('界面优化任务缺少项目 ID')
          actionResult = await enhanceUiPage(action)
        } else if (action.action === 'generateComplexPage') {
          if (!action.projectId || !action.pageId) throw new Error('复杂页面生成任务缺少项目或页面 ID')
          actionResult = await executeCodegenAction(
            'generateComplexPage',
            { projectId: action.projectId, pageId: action.pageId, incremental: action.incremental !== false },
            () => request({
              url: `/codegen/complexpagegeneration/generate/${action.pageId}`,
              method: 'post',
              params: { incremental: action.incremental !== false },
              timeout: 180000
            })
          )
        } else {
          throw new Error(`不支持的客户端任务: ${action.action}`)
        }
        ensureClientActionSucceeded(action, actionResult)
        await writeClientDiagnostic('agent.client_action.completed', {
          executionId: execution.id,
          action: action.action,
          stage: 'client-action',
          durationMs: performance.now() - actionStartedAt
        })
      } catch (error) {
        error.failedAction ||= action.action
        await writeClientDiagnostic('agent.client_action.failed', {
          level: 'error',
          executionId: execution.id,
          action: action.action,
          stage: 'client-action',
          durationMs: performance.now() - actionStartedAt,
          message: getClientActionDiagnostic(error)
        })
        throw error
      }
      completedActions.push(action.entityId ? `${action.action}:${action.entityId}` : action.action)
    }

    const completion = await completeToolClientActions(execution.id, {
      success: true,
      output: completedActions.join(', ')
    })
    updateExecution(completion.execution || completion)
    if (completion.workPlan) workPlan.value = completion.workPlan
    if (completion.nextExecution) {
      updateExecution(completion.nextExecution)
      if (completion.nextExecution.status === 'AwaitingClientExecution') {
        await executeClientActions(completion.nextExecution)
      }
    }
    if (completion.workPlan && completion.workPlan.status === 'Active' && !completion.nextExecution) {
      const next = await resumeWorkPlan(completion.workPlan.id, activeConversationId.value)
      if (next?.plan) workPlan.value = next.plan
      if (next?.execution) {
        updateExecution(next.execution)
        if (next.execution.status === 'AwaitingClientExecution') await executeClientActions(next.execution)
      }
    }
    await loadConversation()
    return completion.nextExecution || completion.execution || completion
  } catch (error) {
    try {
      const completion = await completeToolClientActions(execution.id, {
        success: false,
        output: completedActions.join(', '),
        errorMessage: error?.message || '客户端生成任务执行失败',
        failedAction: error?.failedAction || currentAction?.action || '',
        diagnosticOutput: getClientActionDiagnostic(error)
      })
      updateExecution(completion.execution || completion)
      if (completion.localRepairRequest) {
        const repairResult = await executeCodegenAction(
          'agentChat',
          completion.localRepairRequest,
          () => Promise.reject(new Error('本地自动修复只能在 CodeMaster 客户端中执行'))
        )
        if (Array.isArray(repairResult?.pendingApprovals)) {
          const otherExecutions = toolExecutions.value.filter(item => item.status !== 'PendingApproval')
          toolExecutions.value = [...otherExecutions, ...repairResult.pendingApprovals]
        }
      }
      await loadConversation()
    } catch {
      // Keep the original generation error visible to the user.
    }
    throw error
  }
}

const ensureClientActionSucceeded = (action, result) => {
  if (!result || result.success !== false) return

  const error = new Error(result.message || `${action.action} 执行失败`)
  error.failedAction = action.action
  error.diagnosticOutput = result.output || result.message || ''
  throw error
}

const getClientActionDiagnostic = error => {
  const responsePayload = error?.response?.data?.data || error?.response?.data
  const diagnostic = error?.diagnosticOutput ||
    responsePayload?.output ||
    responsePayload?.message ||
    error?.stack ||
    error?.message ||
    '未返回详细错误信息'
  return summarizeBuildDiagnostic(diagnostic, error?.message)
}

const summarizeBuildDiagnostic = (diagnostic, failureMessage = '') => {
  const text = String(diagnostic || '')
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  const importantPattern = /(:\s*error\b|\berror\s+(?:CS|NU|NETSDK|MSB|TS|VITE)\d+|build failed|生成失败|error during build|failed to compile|exited with code|退出码)/i
  const important = []
  const metadataHints = []
  const appendDistinct = value => {
    const normalized = String(value || '').trimEnd()
    if (normalized && !important.includes(normalized)) important.push(normalized)
  }

  appendDistinct(failureMessage)
  lines.forEach((line, index) => {
    const missingMember = line.match(/(?:'|“)(?<type>[^'”]+)(?:'|”)(?:\s+does not contain a definition for\s+|未包含)(?:'|“)(?<member>[^'”]+)(?:'|”)/i)
    if (missingMember?.groups) {
      const hint = `缺少成员：${missingMember.groups.type}.${missingMember.groups.member}。请核对实体字段元数据和手写代码引用，再增量生成对应实体或项目。`
      if (!metadataHints.includes(hint)) metadataHints.push(hint)
    }
    if (/(?:error\s+CS0535|does not implement interface member|未实现接口成员)/i.test(line)) {
      const hint = '生成实体缺少接口成员，请检查实体能力开关及对应系统字段，再重新生成。'
      if (!metadataHints.includes(hint)) metadataHints.push(hint)
    }
    if (!importantPattern.test(line)) return
    const start = Math.max(0, index - 1)
    const end = Math.min(lines.length - 1, index + 2)
    for (let current = start; current <= end; current += 1) {
      appendDistinct(lines[current])
    }
  })

  const tail = lines.slice(-80).filter(line => line.trim())
  const sections = []
  if (metadataHints.length) sections.push(`=== CodeMaster 元数据检查 ===\n${metadataHints.join('\n')}`)
  if (important.length) sections.push(`=== Build errors ===\n${important.join('\n')}`)
  if (tail.length) sections.push(`=== Build log tail ===\n${tail.join('\n')}`)
  const summary = sections.join('\n') || failureMessage || text || '未返回详细错误信息'

  if (summary.length <= 12000) return summary
  const separator = '\n... diagnostic truncated ...\n'
  const headLength = 8000 - separator.length
  const tailLength = 12000 - headLength - separator.length
  return summary.slice(0, headLength) + separator + summary.slice(-tailLength)
}

const rejectApproval = async approval => {
  approvalLoadingId.value = approval.id
  try {
    const result = await rejectTool(approval.id)
    updateExecution(result)
    await loadWorkPlan()
    ElMessage.success('已拒绝')
  } finally {
    approvalLoadingId.value = ''
  }
}

const updateExecution = value => {
  const index = toolExecutions.value.findIndex(item => item.id === value.id)
  if (index >= 0) {
    toolExecutions.value.splice(index, 1, value)
  } else {
    toolExecutions.value.push(value)
  }
}

const startNewConversation = () => {
  activeConversationId.value = ''
  messages.value = []
  toolExecutions.value = []
  resetWorkflowPreferences()
  void loadWorkPlan()
}

const archiveCurrentConversation = async () => {
  if (!activeConversationId.value) return

  await ElMessageBox.confirm('确认删除当前对话及其待确认操作吗？', '删除对话', {
    type: 'warning'
  })
  await deleteConversation(activeConversationId.value)
  activeConversationId.value = ''
  messages.value = []
  toolExecutions.value = []
  workPlan.value = null
  conversations.value = await getConversations(selectedProjectId.value, executionMode)
  if (conversations.value.length > 0) {
    activeConversationId.value = conversations.value[0].id
    await loadConversation()
  }
  ElMessage.success('对话已删除')
}

const scrollToBottom = async () => {
  await nextTick()
  const panel = messagePanelRef.value
  if (!panel) return
  panel.scrollTop = panel.scrollHeight
  if (messageEndRef.value) messageEndRef.value.scrollIntoView({ block: 'end', behavior: 'auto' })
}

const scheduleScrollToBottom = () => {
  void scrollToBottom()
  requestAnimationFrame(() => {
    void scrollToBottom()
    requestAnimationFrame(() => void scrollToBottom())
  })
}

const toolTitle = name => ({
  project_change_set: '项目变更单',
  project_work_batch: '当前阶段批量任务',
  dictionary_change: '项目字典变更',
  ui_page_enhancement: '页面设计变更',
  propose_view_data_source_plan: '视图数据源方案',
  propose_openapi_import: 'OpenAPI 导入方案',
  propose_complex_page_plan: '复杂页面方案',
  create_module: '创建模块',
  create_entity: '创建实体'
}[name] || name)

const approvalStatusLabel = (status, automatic = false) => ({
  PendingApproval: automatic ? '系统正在处理' : '需要你确认',
  Approved: automatic ? '系统已接收，准备执行' : '已确认，准备执行',
  Executing: '正在执行',
  AwaitingClientExecution: '正在完成项目任务'
}[status] || status)

const approvalActionHint = (status, automatic) => {
  if (automatic && status === 'PendingApproval') return '这是自动流程中的中间状态，无需点击；系统会继续处理。'
  if (automatic) return '当前模式会自动执行，无需再次确认。'
  if (status === 'PendingApproval') return '请确认这项变更，确认后会自动按任务依赖继续执行。'
  return '确认已记录，系统正在继续执行。'
}

const workPlanStatusLabel = status => ({
  Draft: '草稿',
  AwaitingConfirmation: '待整体确认',
  Active: '进行中',
  Completed: '已完成',
  Failed: '待修复',
  Blocked: '已阻塞',
  Cancelled: '已取消',
  Superseded: '已替换'
}[status] || status)

const workPlanStatusType = status => ({
  AwaitingConfirmation: 'warning',
  Completed: 'success',
  Failed: 'danger',
  Blocked: 'warning',
  Cancelled: 'info',
  Superseded: 'info'
}[status] || 'primary')

// Doc §6 §13 step 7: explicit "agent phase" so users can distinguish
// waiting-on-model, waiting-on-user, blocked, and completed.
const AGENT_PHASES = {
  IDLE: 'idle',
  WAITING_MODEL: 'waiting_model',
  WAITING_TOOL: 'waiting_tool',
  EXECUTING_PLAN: 'executing_plan',
  WAITING_USER: 'waiting_user',
  COMPLETED: 'completed',
  FAILED: 'failed',
  BLOCKED: 'blocked',
  CANCELLED: 'cancelled'
}

const agentPhaseLabel = phase => ({
  [AGENT_PHASES.IDLE]: '空闲',
  [AGENT_PHASES.WAITING_MODEL]: '等待模型',
  [AGENT_PHASES.WAITING_TOOL]: '等待工具',
  [AGENT_PHASES.EXECUTING_PLAN]: '执行计划',
  [AGENT_PHASES.WAITING_USER]: '等待用户',
  [AGENT_PHASES.COMPLETED]: '已完成',
  [AGENT_PHASES.FAILED]: '执行失败',
  [AGENT_PHASES.BLOCKED]: '已阻塞',
  [AGENT_PHASES.CANCELLED]: '已取消'
}[phase] || phase)

const agentPhaseType = phase => ({
  [AGENT_PHASES.WAITING_MODEL]: 'info',
  [AGENT_PHASES.WAITING_TOOL]: 'info',
  [AGENT_PHASES.EXECUTING_PLAN]: 'primary',
  [AGENT_PHASES.WAITING_USER]: 'warning',
  [AGENT_PHASES.COMPLETED]: 'success',
  [AGENT_PHASES.FAILED]: 'danger',
  [AGENT_PHASES.BLOCKED]: 'warning',
  [AGENT_PHASES.CANCELLED]: 'info'
}[phase] || '')

const deriveAgentPhase = (plan, sending, pendingToolCount) => {
  if (!plan) return sending.value ? AGENT_PHASES.WAITING_MODEL : AGENT_PHASES.IDLE
  if (plan.status === 'Completed') return AGENT_PHASES.COMPLETED
  if (plan.status === 'Cancelled') return AGENT_PHASES.CANCELLED
  if (plan.status === 'Failed') return AGENT_PHASES.FAILED
  if (plan.requiresConfirmation) return AGENT_PHASES.WAITING_USER
  if (pendingToolCount > 0) return AGENT_PHASES.WAITING_USER
  if (hasBlockedPlanTask(plan)) return AGENT_PHASES.BLOCKED
  if (plan.activeBatchId) return AGENT_PHASES.EXECUTING_PLAN
  return sending.value ? AGENT_PHASES.WAITING_MODEL : AGENT_PHASES.IDLE
}

const taskStatusLabel = (status, automatic = false) => ({
  Pending: '等待',
  Ready: '可执行',
  WaitingApproval: automatic ? '系统处理中' : '需要确认',
  Applying: '应用中',
  Generating: '生成中',
  Migrating: '迁移中',
  Building: '构建中',
  Verifying: '验证中',
  Completed: '完成',
  Failed: '失败',
  Blocked: '阻塞',
  Rejected: '已拒绝',
  Cancelled: '已取消'
}[status] || status)

const failureCategoryLabel = category => ({
  PlanOrMetadata: '计划或元数据',
  GeneratorOrTemplate: '生成器或模板',
  GeneratedProjectIntegration: '生成项目集成',
  ExternalEnvironment: '外部环境',
  Unknown: '原因待确认'
}[category] || category)

const formatToolInput = value => {
  if (!value) return ''
  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

const changeSetOverview = value => {
  try {
    const data = parseJson(value) || {}
    const entities = Array.isArray(data.entities) ? data.entities : []
    const entityUpdates = Array.isArray(data.entityUpdates) ? data.entityUpdates : []
    return {
      summary: data.summary || '',
      modules: Array.isArray(data.modules) ? data.modules.length : 0,
      entities: entities.length,
      fields: entities.reduce((total, entity) => total + (Array.isArray(entity.fields) ? entity.fields.length : 0), 0)
        + entityUpdates.reduce((total, entity) => total
          + (Array.isArray(entity.newFields) ? entity.newFields.length : 0)
          + (Array.isArray(entity.updatedFields) ? entity.updatedFields.length : 0)
          + (Array.isArray(entity.deletedFieldIds) ? entity.deletedFieldIds.length : 0), 0),
      relations: entities.reduce((total, entity) => total + (Array.isArray(entity.relations) ? entity.relations.length : 0), 0)
        + entityUpdates.reduce((total, entity) => total
          + (Array.isArray(entity.newRelations) ? entity.newRelations.length : 0)
          + (Array.isArray(entity.updatedRelations) ? entity.updatedRelations.length : 0)
          + (Array.isArray(entity.deletedRelationIds) ? entity.deletedRelationIds.length : 0), 0),
      dictionaries: Array.isArray(data.dictionaries) ? data.dictionaries.length : 0,
      updates: (Array.isArray(data.moduleUpdates) ? data.moduleUpdates.length : 0) + entityUpdates.length,
      deletes: (Array.isArray(data.deleteModuleIds) ? data.deleteModuleIds.length : 0)
        + (Array.isArray(data.deleteEntityIds) ? data.deleteEntityIds.length : 0),
      generationMode: data.generationMode || 'None'
    }
  } catch {
    return {
      summary: '',
      modules: 0,
      entities: 0,
      fields: 0,
      relations: 0,
      dictionaries: 0,
      updates: 0,
      deletes: 0,
      generationMode: 'None'
    }
  }
}

const formatJsonValue = value => JSON.stringify(value ?? null, null, 2)

const changeOperationLabel = operation => ({
  add: '新增',
  update: '修改',
  delete: '删除',
  unchanged: '不变'
}[String(operation || '').toLowerCase()] || operation)

const changeOperationType = operation => ({
  add: 'success',
  update: 'primary',
  delete: 'danger',
  unchanged: 'info'
}[String(operation || '').toLowerCase()] || 'info')

const changeResourceLabel = resourceType => ({
  module: '模块',
  entity: '实体',
  field: '字段',
  control: '控件',
  relation: '关系',
  dictionary: '字典',
  view: '视图',
  viewField: '视图字段',
  complexPage: '复杂页面',
  generation: '生成与验证'
}[resourceType] || resourceType || '资源')

const changeValue = (change, ...keys) => {
  for (const source of [change?.values, change?.target]) {
    if (!source || typeof source !== 'object') continue
    for (const key of keys) {
      const value = source[key]
      if (value !== undefined && value !== null && value !== '') return value
    }
  }
  return ''
}

const changeTechnicalName = change => changeValue(change, 'fieldName', 'name')
const changeDisplayName = change => changeValue(change, 'displayName', 'description', 'dictName', 'moduleDescription')
const changeBusinessDescription = change => changeValue(change, 'businessDescription')
const changeDataType = change => changeValue(change, 'dataType')
const changeControlType = change => changeValue(change, 'controlType', 'formControlType')
const changeDisplayTitle = change => {
  const entityName = changeValue(change, 'entityName')
  const technicalName = changeTechnicalName(change)
  const resourceName = changeValue(change, 'relationName', 'moduleName', 'dictType', 'viewName', 'pageName')
  if (entityName && technicalName && technicalName !== entityName) return `${entityName}.${technicalName}`
  return resourceName || technicalName || change.changeId
}

const isUiCompositionApproval = name => [
  'propose_view_data_source_plan',
  'propose_openapi_import',
  'propose_complex_page_plan'
].includes(name)

const compositionOverview = (toolName, value) => {
  const data = parseJson(value) || {}
  if (toolName === 'propose_view_data_source_plan') {
    return {
      summary: data.summary || '创建视图数据源',
      tags: [
        `视图 ${data.viewName || '-'}`,
        `只读实体 ${data.entityName || '-'}`,
        `字段 ${Array.isArray(data.expectedColumns) ? data.expectedColumns.length : 0}`
      ]
    }
  }
  if (toolName === 'propose_openapi_import') {
    return {
      summary: data.summary || '导入 OpenAPI 数据源',
      tags: [
        `文档 ${data.name || '-'}`,
        data.documentId ? '增量同步' : '首次导入',
        data.applyRemovals ? '确认移除缺失项' : '保留缺失项'
      ]
    }
  }

  return {
    summary: data.summary || '创建复杂页面',
    tags: [
      `数据源 ${Array.isArray(data.definition?.dataSources) ? data.definition.dataSources.length : 0}`,
      `绑定 ${Array.isArray(data.definition?.bindings) ? data.definition.bindings.length : 0}`,
      data.existingPageId ? '更新页面' : '新建页面'
    ]
  }
}

const parseJson = value => {
  if (!value) return null
  try {
    return typeof value === 'string' ? JSON.parse(value) : value
  } catch {
    return null
  }
}

const formatTime = value => {
  if (!value) return ''
  return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
</script>

<style scoped lang="scss">
.drawer-header {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.drawer-title {
  color: var(--app-text, #111827);
  font-size: 17px;
  font-weight: 700;
}

.drawer-subtitle {
  margin-top: 2px;
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.drawer-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}

.agent-layout {
  flex: 1 1 auto;
  min-height: 0;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr) auto;
  background: var(--app-bg, #f4f7fb);
}

.context-panel {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 10px;
  padding: 14px 16px;
  background: var(--app-header-bg, #fff);
  border-bottom: 1px solid var(--app-border, #dbe4ef);
}

.context-field {
  min-width: 0;
}

.workflow-field {
  grid-column: 1 / -1;
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  align-items: center;
  gap: 8px 12px;
  padding: 10px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 10px;
  background: var(--app-surface-soft, #f8fafc);
}

.workflow-field .context-label {
  margin: 0;
}

.workflow-field small {
  grid-column: 1 / -1;
  color: var(--app-text-muted, #64748b);
  line-height: 1.5;
}

.workflow-auto-toggle {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.context-label {
  display: block;
  margin-bottom: 6px;
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.selected-provider {
  min-height: 32px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 5px 10px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 4px;
  background: var(--app-bg, #f8fafc);
}

.selected-provider strong,
.selected-provider small {
  display: block;
}

.selected-provider small {
  margin-top: 2px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.conversation-field {
  grid-column: 1 / -1;
  display: flex;
  align-items: center;
  gap: 6px;
}

.conversation-select {
  min-width: 0;
  flex: 1;
}

.conversation-delete {
  flex: 0 0 auto;
}

.agent-scope-hint {
  grid-column: 1 / -1;
  display: flex;
  align-items: center;
  gap: 10px;
}

.agent-scope-hint span {
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.project-option,
.provider-option {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.project-option small,
.provider-option small {
  color: var(--app-text-muted, #94a3b8);
}

.message-panel {
  min-height: 0;
  overflow-y: auto;
  padding: 18px 16px;
}

.message-end-anchor {
  width: 1px;
  height: 1px;
  margin: 0;
  padding: 0;
}

.work-plan-panel {
  margin-bottom: 18px;
  padding: 14px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 8px;
  background: var(--app-header-bg, #fff);
}

.work-plan-header,
.work-stage-header,
.work-task-row {
  display: flex;
  align-items: center;
}

.work-task-evidence {
  margin: 0 0 7px 56px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-task-evidence summary {
  display: flex;
  align-items: center;
  gap: 8px;
  width: fit-content;
  cursor: pointer;
}

.work-task-evidence p,
.work-task-evidence ul {
  margin: 7px 0;
  overflow-wrap: anywhere;
}

.work-task-evidence ul {
  padding-left: 18px;
}

.work-task-evidence pre {
  max-height: 180px;
  margin: 7px 0 0;
  padding: 8px;
  overflow: auto;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 4px;
  background: var(--app-bg, #f8fafc);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.work-task-repair {
  margin: 0 0 7px 56px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-task-repair summary {
  display: flex;
  align-items: center;
  gap: 8px;
  width: fit-content;
  cursor: pointer;
}

.work-task-repair p {
  margin: 7px 0;
  overflow-wrap: anywhere;
}

.work-task-repair pre {
  max-height: 180px;
  margin: 7px 0 0;
  padding: 8px;
  overflow: auto;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 4px;
  background: var(--app-bg, #f8fafc);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.work-plan-tags {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.work-plan-header {
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}

.work-plan-header strong,
.work-plan-header span {
  display: block;
}

.work-plan-current-task {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 10px 0;
  padding: 8px 10px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--el-color-primary-light-9) 72%, var(--app-header-bg, #fff));
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.work-plan-current-task-label {
  color: var(--el-color-primary);
  font-weight: 600;
}

.work-plan-current-task strong {
  min-width: 0;
  overflow: hidden;
  color: var(--app-text, #111827);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.work-plan-header strong {
  color: var(--app-text, #111827);
  font-size: 14px;
}

.work-plan-header span {
  margin-top: 2px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-plan-confirmation {
  display: grid;
  gap: 10px;
  margin-top: 12px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--el-color-warning) 55%, var(--app-border, #dbe4ef));
  border-radius: 7px;
  background: color-mix(in srgb, var(--el-color-warning-light-9) 72%, var(--app-header-bg, #fff));
}

.work-plan-confirmation-header,
.work-plan-change-main,
.work-plan-change-summary {
  display: flex;
  align-items: center;
  gap: 8px;
}

.work-plan-confirmation-header {
  justify-content: space-between;
  align-items: flex-start;
}

.work-plan-confirmation-header strong,
.work-plan-confirmation-header span {
  display: block;
}

.work-plan-confirmation-header span {
  margin-top: 3px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-plan-change-summary {
  flex-wrap: wrap;
  justify-content: flex-end;
}

.work-plan-change-list {
  display: grid;
  gap: 7px;
  max-height: 360px;
  overflow-y: auto;
}

.work-plan-change-item {
  padding: 9px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 6px;
  background: var(--app-header-bg, #fff);
}

.work-plan-change-main strong {
  min-width: 0;
  color: var(--app-text, #1f2937);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.work-plan-change-resource {
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-plan-change-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px 12px;
  margin-top: 7px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-plan-change-fields span {
  overflow-wrap: anywhere;
}

.work-plan-change-details,
.work-plan-standard-json {
  margin-top: 7px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-plan-change-details summary,
.work-plan-standard-json summary {
  cursor: pointer;
}

.work-plan-change-details pre,
.work-plan-standard-json pre {
  max-height: 260px;
  margin: 7px 0 0;
  padding: 8px;
  overflow: auto;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 4px;
  background: var(--app-bg, #f8fafc);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.work-stage {
  margin-top: 12px;
  border-top: 1px solid var(--app-border, #dbe4ef);
}

.work-repair-policy {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 9px;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-stage-header {
  justify-content: space-between;
  padding: 9px 0 5px;
}

.work-stage-title {
  color: var(--app-text, #1f2937);
  font-size: 13px;
  font-weight: 600;
}

.work-stage-count {
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.work-task-row {
  min-height: 32px;
  gap: 8px;
  padding: 5px 0;
  border-top: 1px dashed color-mix(in srgb, var(--app-border, #dbe4ef) 72%, transparent);
  font-size: 12px;
}

.work-task-state {
  flex: 0 0 48px;
  color: var(--app-text-muted, #64748b);
}

.work-task-state.state-completed { color: var(--el-color-success); }
.work-task-state.state-failed,
.work-task-state.state-rejected { color: var(--el-color-danger); }
.work-task-state.state-ready,
.work-task-state.state-waitingapproval { color: var(--el-color-warning); }

.work-task-title {
  min-width: 0;
  flex: 1;
  color: var(--app-text, #1f2937);
  overflow-wrap: anywhere;
}

.work-task-lock,
.work-task-diagnosis {
  max-width: 42%;
  color: var(--app-text-muted, #64748b);
  overflow-wrap: anywhere;
  text-align: right;
}

.work-task-diagnosis { color: var(--el-color-danger); }

.empty-state {
  height: 100%;
  min-height: 240px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: var(--app-text-muted, #64748b);
}

.empty-state.compact {
  min-height: 180px;
}

.empty-state .el-icon {
  font-size: 34px;
  color: var(--app-primary, #2563eb);
}

.message-row {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  margin-bottom: 18px;
}

.message-row.user {
  align-items: flex-end;
}

.message-role {
  margin: 0 4px 5px;
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.message-bubble {
  max-width: 88%;
  padding: 11px 13px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 8px;
  background: var(--app-header-bg, #fff);
  color: var(--app-text, #1f2937);
  line-height: 1.65;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.message-bubble.streaming {
  min-width: 140px;
}

.message-row.error .message-bubble {
  border-color: var(--el-color-danger-light-5);
}

.request-failure-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 12px;
}

.stream-cursor {
  display: inline-block;
  width: 2px;
  height: 1em;
  margin-left: 2px;
  vertical-align: -2px;
  background: var(--app-primary, #2563eb);
  animation: stream-cursor 0.9s infinite;
}

.stream-status {
  display: flex;
  align-items: center;
  gap: 7px;
  margin-top: 5px;
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.message-bubble.streaming > .stream-status:first-child {
  margin-top: 0;
}

.stream-status-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--app-primary, #2563eb);
  animation: typing 1s infinite ease-in-out;
}

.compile-progress-panel,
.stage-progress-panel,
.task-progress-panel {
  margin-top: 10px;
  padding: 10px 12px;
  border-radius: 6px;
  background: rgba(37, 99, 235, 0.05);
  border-left: 3px solid var(--app-primary, #2563eb);
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 12px;
  color: var(--app-text, #1f2937);
}

.compile-progress-header,
.task-progress-header {
  display: flex;
  justify-content: space-between;
  font-weight: 600;
}

.compile-progress-message,
.task-progress-title,
.task-progress-error {
  color: var(--app-text-muted, #475569);
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.compile-progress-meta {
  font-size: 11px;
  color: var(--app-text-muted, #64748b);
}

.stage-progress-title {
  font-weight: 600;
  margin-bottom: 4px;
}

.stage-progress-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.stage-progress-item {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
}

.stage-progress-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #cbd5f5;
  flex-shrink: 0;
}

.stage-progress-item.stage-in_progress .stage-progress-dot {
  background: var(--app-primary, #2563eb);
  animation: typing 1.2s infinite ease-in-out;
}

.stage-progress-item.stage-completed .stage-progress-dot {
  background: #16a34a;
}

.stage-progress-item.stage-failed .stage-progress-dot {
  background: var(--el-color-danger);
}

.stage-progress-name {
  flex: 1;
  color: var(--app-text, #1f2937);
}

.stage-progress-status {
  font-size: 11px;
  color: var(--app-text-muted, #64748b);
}

.task-progress-error {
  color: var(--el-color-danger);
  font-weight: 500;
}

.stream-tool-error {
  margin-top: 7px;
  padding: 10px 12px;
  border-left: 3px solid var(--el-color-danger);
  border-radius: 4px;
  background: rgba(245, 108, 108, 0.06);
  color: var(--el-color-danger);
  font-size: 12px;
  line-height: 1.5;
  overflow-wrap: anywhere;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.stream-tool-error .tool-error-summary {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  color: var(--el-color-danger);
}

.stream-tool-error .tool-error-icon {
  font-size: 14px;
}

.stream-tool-error .tool-error-title {
  flex: 1;
}

.stream-tool-error .tool-error-message {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  color: var(--el-text-color-regular);
  background: rgba(0, 0, 0, 0.04);
  padding: 4px 6px;
  border-radius: 3px;
}

.stream-tool-error .tool-error-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.stream-tool-error .tool-error-action {
  display: flex;
  align-items: center;
  gap: 4px;
  color: var(--el-color-warning);
  font-size: 12px;
}

.stream-tool-error .tool-error-stack pre {
  margin: 6px 0 0;
  padding: 8px;
  background: rgba(0, 0, 0, 0.04);
  border-radius: 3px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--el-text-color-regular);
}

.stream-tool-error .tool-error-actions {
  display: flex;
  gap: 8px;
  margin-top: 4px;
}

.message-row.user .message-bubble {
  border-color: var(--app-primary, #2563eb);
  background: var(--app-primary, #2563eb);
  color: #fff;
}

.message-row time {
  margin: 4px 4px 0;
  color: var(--app-text-muted, #94a3b8);
  font-size: 11px;
}

.approval-card {
  margin: 0 0 18px;
  padding: 14px;
  border: 1px solid rgba(245, 158, 11, 0.48);
  border-radius: 8px;
  background: color-mix(in srgb, var(--app-header-bg, #fff) 92%, #f59e0b 8%);
}

.execution-error-card {
  margin: 0 0 18px;
  padding: 14px;
  border: 1px solid rgba(239, 68, 68, 0.52);
  border-radius: 8px;
  background: color-mix(in srgb, var(--app-header-bg, #fff) 92%, #ef4444 8%);
}

.approval-header,
.approval-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.approval-actions > span {
  margin-right: auto;
  color: var(--app-text-muted, #64748b);
  font-size: 11px;
}

.approval-status-tags {
  display: flex;
  align-items: center;
  gap: 6px;
}

.approval-header strong {
  display: block;
  color: var(--app-text, #111827);
}

.approval-header span {
  display: block;
  margin-top: 3px;
  color: var(--app-text-muted, #64748b);
  font-size: 12px;
}

.approval-card pre,
.execution-error-card pre {
  max-height: 220px;
  margin: 12px 0;
  padding: 10px;
  overflow: auto;
  border: 1px solid var(--app-border, #dbe4ef);
  background: var(--app-bg, #f4f7fb);
  color: var(--app-text, #1f2937);
  font-family: Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
}

.change-set-overview {
  margin-top: 12px;
  padding: 10px;
  border: 1px solid var(--app-border, #dbe4ef);
  background: var(--app-bg, #f4f7fb);
}

.change-set-overview > strong {
  display: block;
  color: var(--app-text, #111827);
  line-height: 1.5;
}

.change-set-counts {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.approval-details {
  margin: 10px 0 12px;
}

.approval-details summary {
  color: var(--app-primary, #2563eb);
  cursor: pointer;
  font-size: 12px;
}

.approval-details pre {
  margin-bottom: 0;
}

.approval-actions {
  justify-content: flex-end;
  gap: 8px;
}

.typing-row {
  display: flex;
  gap: 5px;
  padding: 10px 4px 18px;
}

.typing-row span {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--app-primary, #2563eb);
  animation: typing 1s infinite ease-in-out;
}

.typing-row span:nth-child(2) { animation-delay: 0.15s; }
.typing-row span:nth-child(3) { animation-delay: 0.3s; }

.stop-agent-button {
  margin-left: 8px;
}

.composer {
  padding: 12px 16px 14px;
  background: var(--app-header-bg, #fff);
  border-top: 1px solid var(--app-border, #dbe4ef);
}

.composer-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 8px;
}

.composer-footer span {
  color: var(--app-text-muted, #94a3b8);
  font-size: 11px;
}

/* Mode switch: requirements is the read-only page, code is the authorized page. */
.agent-mode-bar {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--app-border, #dbe4ef);
  background: var(--app-header-bg, #fff);
}

.agent-mode-hint {
  color: var(--app-text-muted, #94a3b8);
  font-size: 11px;
  line-height: 1.4;
}

.agent-mode-hint.is-code {
  color: #b45309;
}

.generate-action {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 0 16px 14px;
  background: var(--app-header-bg, #fff);
}

.generate-action .el-button {
  width: 100%;
}

.generate-action-tip {
  color: var(--app-text-muted, #94a3b8);
  font-size: 11px;
  line-height: 1.4;
  text-align: center;
}

@keyframes typing {
  0%, 60%, 100% { opacity: 0.35; transform: translateY(0); }
  30% { opacity: 1; transform: translateY(-3px); }
}

@keyframes stream-cursor {
  0%, 45% { opacity: 1; }
  46%, 100% { opacity: 0; }
}

@media (max-width: 640px) {
  .context-panel {
    grid-template-columns: minmax(0, 1fr);
  }

  .conversation-field {
    grid-column: auto;
  }
}
</style>

<style lang="scss">
.codemaster-agent-drawer .el-drawer__header {
  margin-bottom: 0;
  padding: 14px 16px;
  border-bottom: 1px solid var(--app-border, #dbe4ef);
}

.codemaster-agent-drawer .el-drawer__body {
  padding: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

@media (max-width: 640px) {
  .codemaster-agent-drawer.el-drawer {
    width: 100% !important;
  }
}
</style>
