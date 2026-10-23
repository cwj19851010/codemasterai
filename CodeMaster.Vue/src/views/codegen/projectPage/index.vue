<template>
  <div class="app-container complex-page-manager">
    <el-card shadow="never" class="filter-card">
      <el-form :inline="true" :model="query" @submit.prevent>
        <el-form-item label="项目">
          <el-select v-model="query.projectId" clearable filterable style="width: 240px" @change="loadModules">
            <el-option v-for="item in projects" :key="item.id" :label="item.displayName || item.projectName" :value="item.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="PageKey">
          <el-input v-model="query.pageKey" clearable placeholder="输入页面 PageKey" @keyup.enter="loadPages" />
        </el-form-item>
        <el-button type="primary" @click="loadPages">查询</el-button>
        <el-button @click="resetQuery">重置</el-button>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <div class="toolbar">
        <div>
          <el-button type="primary" @click="openCreate">创建复杂页面</el-button>
          <el-button :disabled="!query.projectId" @click="loadPages">刷新状态</el-button>
          <el-button :disabled="!query.projectId" :loading="synchronizing" @click="handleProjectSynchronize">同步项目待处理页面</el-button>
        </div>
        <span class="hint">页面结构、实体服务、DTO 基数和组件绑定统一保存在复杂页定义中。</span>
      </div>

      <el-table v-loading="loading" :data="pages" border>
        <el-table-column prop="pageName" label="页面名称" min-width="180" />
        <el-table-column prop="pageKey" label="PageKey" min-width="170" />
        <el-table-column prop="routePath" label="路由" min-width="210" />
        <el-table-column prop="syncStatus" label="同步状态" width="110">
          <template #default="{ row }">
            <el-tag :type="statusType(row.syncStatus)">{{ statusText(row.syncStatus) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="数据源 / 绑定" width="130" align="center">
          <template #default="{ row }">{{ row.dataSources?.length || 0 }} / {{ row.bindings?.length || 0 }}</template>
        </el-table-column>
        <el-table-column label="版本" width="85" align="center">
          <template #default="{ row }">v{{ row.definitionVersion || 1 }}</template>
        </el-table-column>
        <el-table-column label="操作" width="340" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">设计</el-button>
            <el-button link type="success" :loading="generatingPageId === row.id" @click="handleGenerate(row)">生成</el-button>
            <el-button v-if="row.syncStatus === 3" link type="warning" @click="openSyncPreview(row)">同步</el-button>
            <el-dropdown trigger="click" @command="command => handleMore(command, row)">
              <el-button link type="primary">更多<el-icon class="el-icon--right"><ArrowDown /></el-icon></el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="copy">复制页面</el-dropdown-item>
                  <el-dropdown-item command="sync">检查实体变更</el-dropdown-item>
                  <el-dropdown-item command="external">检查 Vue 外部改动</el-dropdown-item>
                  <el-dropdown-item command="delete" divided>删除页面</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-dialog
      v-model="designerVisible"
      :title="editingId ? `设计复杂页面：${form.pageName}` : '创建复杂页面'"
      fullscreen
      destroy-on-close
      class="designer-dialog"
    >
      <el-steps :active="activeStep" finish-status="success" align-center class="designer-steps">
        <el-step title="基础信息" description="项目、路由和布局预设" />
        <el-step title="页面数据源" description="实体、视图和 API 服务契约" />
        <el-step title="结构与绑定" description="布局树、组件和字段绑定" />
        <el-step title="检查并保存" description="统一定义与生成影响" />
      </el-steps>

      <div class="designer-body">
        <el-form v-show="activeStep === 0" ref="formRef" :model="form" :rules="rules" label-width="110px" class="basic-form">
          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="项目" prop="projectId">
                <el-select v-model="form.projectId" filterable style="width: 100%" :disabled="!!editingId" @change="loadModulesForForm">
                  <el-option v-for="item in projects" :key="item.id" :label="item.displayName || item.projectName" :value="item.id" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="模块" prop="moduleId">
                <el-select v-model="form.moduleId" filterable style="width: 100%">
                  <el-option v-for="item in formModules" :key="item.id" :label="item.moduleDescription || item.moduleName" :value="item.id" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="12"><el-form-item label="页面名称" prop="pageName"><el-input v-model="form.pageName" /></el-form-item></el-col>
            <el-col :span="12"><el-form-item label="PageKey" prop="pageKey"><el-input v-model="form.pageKey" /></el-form-item></el-col>
            <el-col :span="12"><el-form-item label="路由" prop="routePath"><el-input v-model="form.routePath" placeholder="/sales/order-workbench" /></el-form-item></el-col>
            <el-col :span="12"><el-form-item label="菜单图标"><el-input v-model="form.icon" placeholder="Grid" /></el-form-item></el-col>
            <el-col :span="12">
              <el-form-item label="页面类型">
                <el-select v-model="form.pageType" style="width: 100%">
                  <el-option v-for="item in pageTypes" :key="item.value" :label="item.label" :value="item.value" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="布局预设">
                <el-select v-model="form.layoutPresetId" clearable style="width: 100%" @change="loadPresetPreview">
                  <el-option v-for="item in presets" :key="item.id" :label="item.presetName" :value="item.id">
                    <span>{{ item.presetName }}</span><span class="option-description">{{ item.description }}</span>
                  </el-option>
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="同步策略">
                <el-radio-group v-model="form.syncMode">
                  <el-radio-button :value="1">自动跟随实体</el-radio-button>
                  <el-radio-button :value="2">手动确认</el-radio-button>
                </el-radio-group>
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="同步菜单">
                <el-switch v-model="form.isMenuSynced" />
                <span class="inline-tip">保存页面是否需要同步菜单的配置标记</span>
              </el-form-item>
            </el-col>
            <el-col :span="24"><el-form-item label="说明"><el-input v-model="form.description" type="textarea" :rows="3" /></el-form-item></el-col>
          </el-row>
          <el-alert title="布局预设只负责初始化结构，后续在结构设计器中的修改会作为页面自己的树形 JSON 保存。" type="info" :closable="false" />
        </el-form>

        <div v-show="activeStep === 1" class="source-step">
          <div class="section-toolbar">
            <div>
              <h3>页面数据源</h3>
              <p>实体直接选择能力目录；视图注册为只读实体；API 仅允许选择已注册的 GET 服务方法。</p>
            </div>
            <div>
              <el-dropdown split-button type="primary" plain @click="addDataSource(1)" @command="handleAddDataSourceCommand">
                添加数据源
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item command="entity">实体数据源</el-dropdown-item>
                    <el-dropdown-item command="view">注册数据库视图</el-dropdown-item>
                    <el-dropdown-item command="api">API 数据源</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </div>
          <el-table :data="form.dataSources" border row-key="sourceKey">
            <el-table-column label="类型" width="130">
              <template #default="{ row }">
                <el-select v-model="row.sourceType" @change="onSourceTypeChange(row)">
                  <el-option v-for="item in editableSourceTypes" :key="item.value" :label="item.label" :value="item.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="SourceKey" min-width="150">
              <template #default="{ row }"><el-input v-model="row.sourceKey" placeholder="orderList" /></template>
            </el-table-column>
            <el-table-column label="实体 / API 服务" min-width="230">
              <template #default="{ row }">
                <el-select v-if="row.sourceType === 1" v-model="row.entityId" filterable style="width: 100%" @change="onSourceEntityChange(row)">
                  <el-option v-for="item in entitySourceCapabilities" :key="item.entityId" :label="item.entityName" :value="item.entityId" />
                </el-select>
                <template v-else>
                  <el-select :model-value="apiSelectionKey(row)" filterable style="width: 100%" @change="value => onApiSelectionChange(row, value)">
                    <el-option-group v-if="projectApiSources.length" label="OpenAPI 导入">
                      <el-option v-for="item in projectApiSources" :key="`openapi:${item.id}`" :label="item.displayName || item.methodName" :value="`openapi:${item.id}`">
                        <span>{{ item.displayName || item.methodName }}</span><span class="option-hint">{{ item.httpMethod }} {{ item.routePath }}</span>
                      </el-option>
                    </el-option-group>
                    <el-option-group label="系统已注册 API">
                      <el-option v-for="item in apiServices" :key="`service:${item.serviceName}`" :label="item.displayName || item.serviceName" :value="`service:${item.serviceName}`">
                        <span>{{ item.displayName || item.serviceName }}</span><span class="option-hint">{{ item.serviceName }}</span>
                      </el-option>
                    </el-option-group>
                  </el-select>
                  <div class="source-meta">{{ row.entityId ? `字段模型：${sourceEntityText(row)}` : '无实体字段模型：仅支持容器级绑定' }}</div>
                </template>
              </template>
            </el-table-column>
            <el-table-column label="数据基数" min-width="140">
              <template #default="{ row }">
                <el-select v-model="row.dataShape" style="width: 100%" :disabled="row.sourceType === 4" @change="onSourceShapeChange(row)">
                  <el-option v-for="item in dataShapes" :key="item.value" :label="item.label" :value="item.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="服务方法" min-width="230">
              <template #default="{ row }">
                <el-input v-if="row.projectDataSourceId" v-model="row.methodName" disabled />
                <el-select v-else v-model="row.methodName" style="width: 100%" @change="onSourceMethodChange(row)">
                  <el-option v-for="item in methodsForSource(row)" :key="item.methodName" :label="methodOptionLabel(item)" :value="item.methodName" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="DTO" min-width="180">
              <template #default="{ row }"><el-input v-model="row.dtoName" disabled /></template>
            </el-table-column>
            <el-table-column label="同步" width="100">
              <template #default="{ row }"><el-switch v-model="row.syncMode" :active-value="1" :inactive-value="2" /></template>
            </el-table-column>
            <el-table-column label="参数 JSON" min-width="250">
              <template #default="{ row }"><el-input v-model="row.paramsJson" placeholder='{"id":{"$ref":"$route.id"}}' /></template>
            </el-table-column>
            <el-table-column label="操作" width="70" align="center">
              <template #default="{ $index }"><el-button link type="danger" @click="removeDataSource($index)">删除</el-button></template>
            </el-table-column>
          </el-table>
          <el-empty v-if="!form.dataSources.length" description="还没有数据源；可添加实体、注册视图或选择 API。" />
        </div>

        <div v-show="activeStep === 2" class="layout-designer">
          <aside class="palette-panel">
            <div class="panel-heading">
              <div>
                <h3>组件与字段</h3>
                <p class="panel-tip">拖入画布，或点击添加到当前选中容器。</p>
              </div>
            </div>
            <el-collapse v-model="paletteSections" class="palette-collapse">
              <el-collapse-item title="布局组件" name="layout">
                <div class="palette-grid">
                  <button
                    v-for="item in layoutPalette"
                    :key="item.type"
                    type="button"
                    class="palette-item"
                    draggable="true"
                    @dragstart="event => onPaletteDragStart(event, item.type)"
                    @click="addLayoutNode(item.type)"
                  >
                    <span class="palette-icon">{{ item.icon }}</span>
                    <span>{{ item.label }}</span>
                  </button>
                </div>
              </el-collapse-item>
              <el-collapse-item title="业务组件" name="business">
                <div class="palette-grid">
                  <button
                    v-for="item in businessPalette"
                    :key="item.type"
                    type="button"
                    class="palette-item"
                    draggable="true"
                    @dragstart="event => onPaletteDragStart(event, item.type)"
                    @click="addLayoutNode(item.type)"
                  >
                    <span class="palette-icon">{{ item.icon }}</span>
                    <span>{{ item.label }}</span>
                  </button>
                </div>
              </el-collapse-item>
              <el-collapse-item title="数据源字段" name="fields">
                <div v-if="form.dataSources.length" class="source-field-list">
                  <div v-for="source in form.dataSources" :key="source.sourceKey" class="source-field-group">
                    <div class="source-field-title">
                      <strong>{{ source.sourceKey }}</strong>
                      <span>{{ sourceEntityText(source) }}</span>
                    </div>
                    <button
                      v-for="field in fieldsForSource(source)"
                      :key="`${source.sourceKey}:${field.fieldId}`"
                      type="button"
                      class="field-palette-item"
                      draggable="true"
                      @dragstart="event => onFieldDragStart(event, source, field)"
                      @click="addFieldToSelected(source, field)"
                    >
                      <span>{{ field.name }}</span>
                      <small>{{ field.dataType }}</small>
                    </button>
                    <div v-if="!fieldsForSource(source).length" class="source-empty">该数据源没有可拖拽字段模型</div>
                  </div>
                </div>
                <el-empty v-else :image-size="54" description="请先在上一步配置数据源" />
              </el-collapse-item>
            </el-collapse>
          </aside>

          <section class="visual-panel">
            <div class="visual-toolbar">
              <div class="visual-toolbar-group">
                <el-radio-group v-model="designerMode" size="small">
                  <el-radio-button value="canvas">可视化画布</el-radio-button>
                  <el-radio-button value="tree">组件树</el-radio-button>
                </el-radio-group>
                <el-switch v-model="previewMode" active-text="预览" inactive-text="编辑" size="small" @change="resetPreviewState" />
              </div>
              <div class="visual-toolbar-group">
                <el-radio-group v-model="canvasViewport" size="small" :disabled="designerMode !== 'canvas'">
                  <el-radio-button value="desktop">桌面</el-radio-button>
                  <el-radio-button value="tablet">平板</el-radio-button>
                  <el-radio-button value="mobile">手机</el-radio-button>
                </el-radio-group>
                <el-button size="small" :disabled="!selectedNode || selectedNode.type === 'page'" @click="moveSelected(-1)">上移</el-button>
                <el-button size="small" :disabled="!selectedNode || selectedNode.type === 'page'" @click="moveSelected(1)">下移</el-button>
                <el-button size="small" type="danger" plain :disabled="!selectedNode || selectedNode.type === 'page'" @click="removeSelectedNode">删除</el-button>
              </div>
            </div>
            <div v-if="designerMode === 'canvas'" class="canvas-panel">
              <ComplexPageCanvas
                :node="layoutRoot"
                :selected-id="selectedNode?.componentId || ''"
                :preview-mode="previewMode"
                :viewport="canvasViewport"
                :bindings="form.bindings"
                :data-sources="form.dataSources"
                :capabilities="capabilities"
                :preview-state="previewState"
                @select="selectNode"
                @drop-item="handleCanvasDrop"
                @trigger-event="previewNodeEvent"
              />
            </div>
            <div v-else class="tree-panel">
              <div class="tree-toolbar"><strong>页面结构树</strong><span class="panel-tip">右键业务组件可重新绑定数据源</span></div>
              <el-tree
                :data="layoutTreeData"
                node-key="componentId"
                default-expand-all
                highlight-current
                draggable
                :expand-on-click-node="false"
                :allow-drop="allowTreeDrop"
                @node-click="selectNode"
                @node-contextmenu="openBindingMenu"
                @node-drop="handleTreeDrop"
              >
                <template #default="{ data }">
                  <div class="tree-node">
                    <span><el-tag size="small" effect="plain">{{ nodeTypeText(data.type) }}</el-tag> {{ data.componentId }}</span>
                    <span v-if="data.bindingId" class="binding-badge">{{ bindingSummary(data) }}</span>
                  </div>
                </template>
              </el-tree>
            </div>
          </section>

          <aside class="property-panel">
            <template v-if="selectedNode">
              <div class="property-heading">
                <div><h3>节点属性</h3><span>{{ nodeTypeText(selectedNode.type) }}</span></div>
                <el-tag size="small">{{ originText(selectedNode.origin) }}</el-tag>
              </div>
              <el-tabs v-model="propertyTab" class="property-tabs">
                <el-tab-pane label="布局" name="layout">
                  <el-form label-position="top" size="small">
                    <el-form-item label="ComponentId"><el-input v-model="selectedNode.componentId" disabled /></el-form-item>
                    <el-form-item v-if="supportsTitle(selectedNode)" :label="nodeTitleLabel(selectedNode)">
                      <el-input :model-value="nodeProp(selectedNode, nodeTitleProp(selectedNode))" placeholder="可选文本" @input="value => setNodeProp(selectedNode, nodeTitleProp(selectedNode), value)" />
                    </el-form-item>
                    <el-form-item v-if="selectedNode.type === 'row'" label="列间距">
                      <el-slider :model-value="Number(nodeProp(selectedNode, 'gutter') || 16)" :min="0" :max="48" show-input @change="value => setNodeProp(selectedNode, 'gutter', String(value))" />
                    </el-form-item>
                    <template v-if="selectedNode.type === 'column'">
                      <el-form-item label="桌面宽度（24 栅格）">
                        <el-slider v-model="selectedNode.span" :min="1" :max="columnMaxSpan(selectedNode)" show-input @change="syncColumnGrid" />
                      </el-form-item>
                      <el-form-item label="响应式宽度">
                        <div class="responsive-grid-editor">
                          <label>手机<el-input-number v-model="selectedNode.grid.xs" :min="1" :max="24" controls-position="right" @change="syncLayoutJson" /></label>
                          <label>平板<el-input-number v-model="selectedNode.grid.sm" :min="1" :max="24" controls-position="right" @change="syncLayoutJson" /></label>
                          <label>中屏<el-input-number v-model="selectedNode.grid.md" :min="1" :max="24" controls-position="right" @change="syncLayoutJson" /></label>
                          <label>大屏<el-input-number v-model="selectedNode.grid.lg" :min="1" :max="24" controls-position="right" @change="syncLayoutJson" /></label>
                        </div>
                      </el-form-item>
                      <el-form-item>
                        <el-button plain style="width: 100%" :disabled="(selectedNode.span || 24) < 2" @click="splitSelectedColumn">拆分当前列</el-button>
                      </el-form-item>
                    </template>
                    <el-form-item label="CSS 类名"><el-input :model-value="nodeProp(selectedNode, 'class')" @input="value => setNodeProp(selectedNode, 'class', value)" /></el-form-item>
                  </el-form>
                </el-tab-pane>
                <el-tab-pane label="数据" name="data">
                  <el-button type="primary" plain style="width: 100%; margin-bottom: 12px" @click="openBindingDialog(selectedNode)">选择或重新绑定</el-button>
                  <el-descriptions v-if="bindingForNode(selectedNode)" :column="1" border size="small">
                    <el-descriptions-item label="数据源">{{ bindingSourceText(bindingForNode(selectedNode)) }}</el-descriptions-item>
                    <el-descriptions-item label="字段">{{ bindingFieldText(bindingForNode(selectedNode)) }}</el-descriptions-item>
                    <el-descriptions-item label="状态">{{ bindingStatusText(bindingForNode(selectedNode).bindingStatus) }}</el-descriptions-item>
                    <el-descriptions-item label="控件">{{ bindingForNode(selectedNode).controlType || inferSemanticControl(fieldForNode(selectedNode), bindingForNode(selectedNode).bindingRole) }}</el-descriptions-item>
                  </el-descriptions>
                  <el-empty v-else :image-size="54" description="当前节点尚未绑定数据" />
                </el-tab-pane>
                <el-tab-pane label="事件" name="events">
                  <div class="event-toolbar">
                    <span class="panel-tip">一个事件可以按顺序执行多个安全动作。</span>
                    <el-button size="small" type="primary" plain :disabled="selectedNode.type === 'page'" @click="addNodeEvent">添加事件</el-button>
                  </div>
                  <div v-if="eventDefinitions.length" class="event-list">
                    <div v-for="(eventItem, eventIndex) in eventDefinitions" :key="eventItem.localId" class="event-card">
                      <div class="event-card-header">
                        <el-select v-model="eventItem.eventName" size="small" filterable allow-create @change="syncSelectedEvents">
                          <el-option v-for="option in eventOptionsForNode(selectedNode)" :key="option.value" :label="option.label" :value="option.value" />
                        </el-select>
                        <el-button link type="danger" @click="removeNodeEvent(eventIndex)">删除事件</el-button>
                      </div>
                      <div class="action-list">
                        <div v-for="(action, actionIndex) in eventItem.actions" :key="action.localId" class="action-card">
                          <div class="action-card-header">
                            <span>动作 {{ actionIndex + 1 }}</span>
                            <div>
                              <el-button link :disabled="actionIndex === 0" @click="moveEventAction(eventItem, actionIndex, -1)">上移</el-button>
                              <el-button link :disabled="actionIndex === eventItem.actions.length - 1" @click="moveEventAction(eventItem, actionIndex, 1)">下移</el-button>
                              <el-button link type="danger" @click="removeEventAction(eventItem, actionIndex)">删除</el-button>
                            </div>
                          </div>
                          <el-select v-model="action.type" size="small" style="width: 100%" @change="() => { resetActionFields(action); syncSelectedEvents() }">
                            <el-option v-for="option in actionTypes" :key="option.value" :label="option.label" :value="option.value" />
                          </el-select>
                          <el-select v-if="action.type === 'refreshDataSource'" v-model="action.sourceKey" size="small" placeholder="选择数据源" style="width: 100%; margin-top: 8px" @change="syncSelectedEvents">
                            <el-option v-for="source in form.dataSources" :key="source.sourceKey" :label="`${source.sourceKey} · ${sourceEntityText(source)}`" :value="source.sourceKey" />
                          </el-select>
                          <el-input v-else-if="action.type === 'navigate'" v-model="action.path" size="small" placeholder="例如 /sales/orders 或 $event.path" style="margin-top: 8px" @input="syncSelectedEvents" />
                          <template v-else-if="action.type === 'showMessage'">
                            <el-input v-model="action.message" size="small" placeholder="提示内容" style="margin-top: 8px" @input="syncSelectedEvents" />
                            <el-select v-model="action.messageType" size="small" style="width: 100%; margin-top: 8px" @change="syncSelectedEvents">
                              <el-option label="成功" value="success" /><el-option label="提示" value="info" /><el-option label="警告" value="warning" /><el-option label="错误" value="error" />
                            </el-select>
                          </template>
                          <template v-else-if="['setPageParam', 'setQueryParam'].includes(action.type)">
                            <el-input v-model="action.key" size="small" placeholder="参数名" style="margin-top: 8px" @input="syncSelectedEvents" />
                            <el-input v-model="action.value" size="small" placeholder="固定值或 $event.xxx / $source.xxx" style="margin-top: 8px" @input="syncSelectedEvents" />
                          </template>
                          <el-select v-else-if="['openDialog', 'closeDialog', 'toggleComponent'].includes(action.type)" v-model="action.targetComponentId" size="small" filterable placeholder="选择目标组件" style="width: 100%; margin-top: 8px" @change="syncSelectedEvents">
                            <el-option v-for="target in actionTargetNodes(action.type)" :key="target.componentId" :label="`${nodeTypeText(target.type)} · ${target.componentId}`" :value="target.componentId" />
                          </el-select>
                        </div>
                        <el-button size="small" plain style="width: 100%" @click="addEventAction(eventItem)">+ 添加动作</el-button>
                      </div>
                    </div>
                  </div>
                  <el-empty v-else :image-size="54" description="当前组件还没有事件" />
                </el-tab-pane>
              </el-tabs>
            </template>
            <el-empty v-else description="选择一个结构节点" />
          </aside>
        </div>

        <div v-show="activeStep === 3" class="review-step">
          <el-row :gutter="16">
            <el-col :span="8">
              <el-card shadow="never" header="定义摘要">
                <el-descriptions :column="1" border>
                  <el-descriptions-item label="页面">{{ form.pageName }}（{{ form.pageKey }}）</el-descriptions-item>
                  <el-descriptions-item label="数据源">{{ form.dataSources.length }} 个</el-descriptions-item>
                  <el-descriptions-item label="组件绑定">{{ form.bindings.length }} 个</el-descriptions-item>
                  <el-descriptions-item label="结构节点">{{ flattenNodes(layoutRoot).length }} 个</el-descriptions-item>
                  <el-descriptions-item label="定义版本">v{{ form.definitionVersion || 1 }}</el-descriptions-item>
                </el-descriptions>
              </el-card>
            </el-col>
            <el-col :span="16">
              <el-alert v-if="definitionWarnings.length" title="保存前需要处理" type="warning" :closable="false">
                <ul><li v-for="item in definitionWarnings" :key="item">{{ item }}</li></ul>
              </el-alert>
              <el-alert v-else title="定义检查通过，可以保存并生成页面。" type="success" :closable="false" />
              <el-collapse class="json-review">
                <el-collapse-item title="查看布局树 JSON" name="layout">
                  <el-input v-model="form.layoutJson" type="textarea" :rows="14" spellcheck="false" @change="parseLayoutJson" />
                </el-collapse-item>
                <el-collapse-item title="查看数据源和绑定 JSON" name="bindings">
                  <pre>{{ JSON.stringify({ dataSources: form.dataSources, bindings: form.bindings }, null, 2) }}</pre>
                </el-collapse-item>
              </el-collapse>
            </el-col>
          </el-row>
        </div>
      </div>

      <template #footer>
        <div class="dialog-footer">
          <el-button @click="designerVisible = false">取消</el-button>
          <div>
            <el-button v-if="activeStep > 0" @click="activeStep--">上一步</el-button>
            <el-button v-if="activeStep < 3" type="primary" @click="nextStep">下一步</el-button>
            <template v-else>
              <el-button :loading="saving" :disabled="definitionWarnings.length > 0" @click="submitForm(false)">仅保存</el-button>
              <el-button type="primary" :loading="saving" :disabled="definitionWarnings.length > 0" @click="submitForm(true)">保存并生成</el-button>
            </template>
          </div>
        </div>
      </template>
    </el-dialog>

    <el-dialog v-model="bindingVisible" title="组件数据源绑定" width="620px" append-to-body>
      <el-alert :title="`当前组件：${bindingDraft.componentId || '-'}`" type="info" :closable="false" class="binding-alert" />
      <el-form label-width="105px">
        <el-form-item label="数据源">
          <el-select v-model="bindingDraft.dataSourceKey" style="width: 100%" @change="onBindingSourceChange">
            <el-option v-for="item in compatibleBindingSources" :key="item.sourceKey" :label="`${item.sourceKey} · ${sourceEntityText(item)}`" :value="item.sourceKey" />
          </el-select>
        </el-form-item>
        <el-form-item label="绑定角色">
          <el-select v-model="bindingDraft.bindingRole" style="width: 100%" @change="onBindingRoleChange">
            <el-option v-for="item in bindingRoles" :key="item.value" :label="item.label" :value="item.value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="bindingNeedsField(bindingDraft.bindingRole)" label="实体字段">
          <el-select v-model="bindingDraft.fieldId" filterable clearable style="width: 100%">
            <el-option v-for="item in fieldsForBinding" :key="item.fieldId" :label="`${item.name} · ${item.dataType}`" :value="item.fieldId" />
          </el-select>
        </el-form-item>
        <el-form-item label="显示标签"><el-input v-model="bindingDraft.labelOverride" placeholder="留空则使用实体字段说明" /></el-form-item>
        <el-form-item v-if="bindingNeedsField(bindingDraft.bindingRole)" label="语义默认控件">
          <el-input :model-value="semanticDefaultControl" disabled />
        </el-form-item>
        <el-form-item label="控件覆盖">
          <el-select v-model="bindingDraft.controlType" filterable allow-create clearable style="width: 100%" placeholder="留空使用语义默认控件">
            <el-option v-for="item in controlTypeOptions" :key="item" :label="item" :value="item" />
          </el-select>
        </el-form-item>
        <el-form-item label="显示模板">
          <el-select v-model="bindingDraft.displayTemplateId" clearable filterable style="width: 100%" placeholder="可选：挂载容器级或字段级模板">
            <el-option v-for="item in compatibleDisplayTemplates" :key="item.id" :label="item.templateName" :value="item.id">
              <span>{{ item.templateName }}</span><span class="option-hint">{{ item.templateKey }}</span>
            </el-option>
          </el-select>
        </el-form-item>
        <el-form-item v-if="[3, 5, 6].includes(bindingDraft.bindingRole)" label="自动新增字段">
          <el-switch v-model="bindingDraft.autoIncludeNewFields" />
          <span class="inline-tip">实体新增字段时自动追加子绑定</span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="bindingVisible = false">取消</el-button>
        <el-button type="primary" @click="applyBindingDraft(false)">应用到当前定义</el-button>
        <el-button v-if="editingId && selectedBindingSource?.id" type="success" plain @click="applyBindingDraft(true)">立即保存绑定</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="viewBuilderVisible" title="数据库视图构建器" width="1040px" append-to-body destroy-on-close>
      <el-tabs v-model="viewBuilderMode">
        <el-tab-pane label="创建视图" name="create">
          <el-row :gutter="16">
            <el-col :span="10">
              <el-card shadow="never" header="实体与字段">
                <el-tree
                  ref="viewFieldTreeRef"
                  :data="viewFieldTree"
                  node-key="key"
                  show-checkbox
                  default-expand-all
                  :props="{ label: 'label', children: 'children', disabled: 'disabled' }"
                  @check="syncViewSelections"
                />
              </el-card>
            </el-col>
            <el-col :span="14">
              <el-form label-width="100px">
                <el-form-item label="视图名"><el-input v-model="viewBuilderForm.viewName" placeholder="vw_order_summary" /></el-form-item>
                <el-form-item label="目标模块">
                  <el-select v-model="viewBuilderForm.moduleId" style="width: 100%">
                    <el-option v-for="item in formModules" :key="item.id" :label="item.moduleName" :value="item.id" />
                  </el-select>
                </el-form-item>
                <el-form-item label="实体名"><el-input v-model="viewBuilderForm.entityName" placeholder="OrderSummary" /></el-form-item>
                <el-form-item label="显示名"><el-input v-model="viewBuilderForm.displayName" placeholder="订单汇总" /></el-form-item>
                <el-form-item label="主键列"><el-input v-model="viewBuilderForm.keyColumn" placeholder="可选；默认首列，必须为整数列" /></el-form-item>
                <el-form-item label="关联关系">
                  <el-select v-model="viewBuilderForm.joinKeys" multiple clearable style="width: 100%" placeholder="单实体无需选择；多实体请选择关联">
                    <el-option v-for="item in viewJoinOptions" :key="item.key" :label="item.label" :value="item.key" />
                  </el-select>
                </el-form-item>
                <el-form-item label="SQL">
                  <el-input v-model="viewBuilderForm.sql" type="textarea" :rows="12" spellcheck="false" placeholder="先选择字段并预览，也可以直接编辑 SELECT / CTE" />
                </el-form-item>
                <el-form-item>
                  <el-button :loading="previewingView" @click="handlePreviewViewSql">生成 SQL 预览</el-button>
                  <el-button type="primary" :loading="creatingView" @click="handleCreateView">创建视图并注册只读实体</el-button>
                </el-form-item>
              </el-form>
            </el-col>
          </el-row>
        </el-tab-pane>
        <el-tab-pane label="导入已有视图" name="import">
          <el-alert title="导入只读取既有视图列，不修改视图定义。" type="info" :closable="false" />
          <el-form label-width="110px" class="view-import-form">
            <el-form-item label="已有视图">
              <el-select v-model="viewBuilderForm.viewName" filterable style="width: 100%" @change="onExistingViewChange">
                <el-option v-for="item in existingViews" :key="item.viewName" :label="item.viewName" :value="item.viewName" />
              </el-select>
            </el-form-item>
            <el-form-item label="目标模块"><el-select v-model="viewBuilderForm.moduleId" style="width: 100%"><el-option v-for="item in formModules" :key="item.id" :label="item.moduleName" :value="item.id" /></el-select></el-form-item>
            <el-form-item label="实体名"><el-input v-model="viewBuilderForm.entityName" placeholder="OrderSummary" /></el-form-item>
            <el-form-item label="显示名"><el-input v-model="viewBuilderForm.displayName" placeholder="订单汇总" /></el-form-item>
            <el-form-item label="主键列"><el-input v-model="viewBuilderForm.keyColumn" placeholder="可选；默认首列，必须为整数列" /></el-form-item>
            <el-form-item><el-button type="primary" :loading="creatingView" @click="handleImportExistingView">导入并注册</el-button></el-form-item>
          </el-form>
        </el-tab-pane>
      </el-tabs>
      <template #footer><el-button @click="viewBuilderVisible = false">关闭</el-button></template>
    </el-dialog>

    <el-dialog v-model="syncVisible" title="实体变更同步预览" width="760px">
      <el-alert
        :title="syncPreview.requiresSync ? `发现 ${syncPreview.changes.length} 项实体结构变化` : '页面依赖的实体结构没有变化'"
        :type="syncPreview.requiresSync ? 'warning' : 'success'"
        :closable="false"
      />
      <el-table :data="syncPreview.changes" border class="sync-table">
        <el-table-column prop="changeType" label="变化类型" width="170"><template #default="{ row }">{{ changeTypeText(row.changeType) }}</template></el-table-column>
        <el-table-column label="字段" min-width="180"><template #default="{ row }">{{ row.previousName || '-' }} → {{ row.currentName || '-' }}</template></el-table-column>
        <el-table-column label="受影响绑定" min-width="220"><template #default="{ row }">{{ row.bindingIds?.join(', ') || '自动包含容器' }}</template></el-table-column>
      </el-table>
      <template #footer>
        <el-button @click="syncVisible = false">关闭</el-button>
        <el-button v-if="syncPreview.requiresSync" type="primary" :loading="synchronizing" @click="handleSynchronize">应用同步</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="generationVisible" title="复杂页面生成结果" width="820px">
      <el-alert
        :title="generationResult.hasConflicts ? '页面已部分更新，存在需要处理的生成区域冲突' : generationResult.success ? '页面文件生成完成' : '页面生成失败'"
        :type="generationResult.hasConflicts ? 'warning' : generationResult.success ? 'success' : 'error'"
        :closable="false"
      />
      <el-descriptions v-if="generationResult.success || generationResult.hasConflicts" :column="1" border class="generation-details">
        <el-descriptions-item label="页面">{{ generationResult.pageKey || '-' }}</el-descriptions-item>
        <el-descriptions-item label="输出目录">{{ generationResult.outputDirectory || '-' }}</el-descriptions-item>
        <el-descriptions-item label="Vue 文件">{{ generationResult.vuePath || '-' }}</el-descriptions-item>
        <el-descriptions-item label="组件树">{{ generationResult.treePath || '-' }}</el-descriptions-item>
        <el-descriptions-item label="Vue Hash">{{ generationResult.vueHash || '-' }}</el-descriptions-item>
      </el-descriptions>
      <el-table v-if="generationResult.files.length" :data="generationResult.files" border max-height="240" class="generation-files">
        <el-table-column label="生成文件"><template #default="{ row }">{{ row }}</template></el-table-column>
      </el-table>
      <el-table v-if="generationResult.conflicts.length" :data="generationResult.conflicts" border max-height="220" class="generation-files">
        <el-table-column prop="genId" label="冲突区域" min-width="220" />
        <el-table-column prop="reason" label="原因" min-width="360" />
      </el-table>
      <el-alert v-if="generationResult.error" :title="generationResult.error" type="error" :closable="false" show-icon />
      <pre v-if="generationResult.diagnosticOutput" class="generation-diagnostic">{{ generationResult.diagnosticOutput }}</pre>
      <div v-if="generationResult.buildMessage || generationResult.buildOutput" class="build-result">
        <el-alert
          :title="generationResult.buildMessage || '构建结果'"
          :type="generationResult.buildSuccess ? 'success' : 'error'"
          :closable="false"
        />
        <pre v-if="generationResult.buildOutput">{{ generationResult.buildOutput }}</pre>
      </div>
      <template #footer>
        <el-button @click="generationVisible = false">关闭</el-button>
        <el-button v-if="generationResult.hasConflicts" @click="handleManualMergeConflict">手动合并</el-button>
        <el-button v-if="generationResult.hasConflicts" type="warning" @click="handleReimportConflicts">重新导入</el-button>
        <el-button v-if="generationResult.hasConflicts" type="danger" @click="handleDiscardConflicts">放弃修改并重新生成</el-button>
        <el-button
          v-if="generationResult.success && !generationResult.hasConflicts"
          type="primary"
          :loading="building"
          @click="handleBuildGeneratedProject"
        >构建目标项目</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { ArrowDown } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useRoute } from 'vue-router'
import ComplexPageCanvas from './ComplexPageCanvas.vue'
import { buildProject, getList as getProjects } from '@/api/codegen/project'
import { getByProjectId } from '@/api/codegen/projectModule'
import {
  create,
  createProjectView,
  copy,
  deleteById,
  generate,
  getById,
  getDataSourceCapabilityCatalog,
  getDisplayTemplates,
  getEntityServiceCapabilities,
  getExistingViews,
  getLayoutPresets,
  getLayoutTree,
  getList,
  getProjectDataSources,
  getViewBuilderCatalog,
  importExistingView,
  importExternalTemplate,
  inspectExternalChange,
  previewSynchronization,
  previewViewSql,
  rebindComponent,
  registerReadOnlyViewEntity,
  saveDefinition,
  synchronize,
  synchronizeProject
} from '@/api/codegen/projectPage'

const route = useRoute()
const loading = ref(false)
const saving = ref(false)
const building = ref(false)
const synchronizing = ref(false)
const designerVisible = ref(false)
const bindingVisible = ref(false)
const viewBuilderVisible = ref(false)
const syncVisible = ref(false)
const generationVisible = ref(false)
const editingId = ref(0)
const generatingPageId = ref(0)
const generationPageId = ref(0)
const generationProjectId = ref(0)
const activeStep = ref(0)
const formRef = ref()
const viewFieldTreeRef = ref()
const projects = ref([])
const formModules = ref([])
const presets = ref([])
const capabilities = ref([])
const dataSourceCatalog = ref({ entities: [], apiServices: [], views: [] })
const projectApiSources = ref([])
const displayTemplates = ref([])
const viewBuilderCatalog = ref({ entities: [], joinCandidates: [] })
const existingViews = ref([])
const viewBuilderMode = ref('create')
const previewingView = ref(false)
const creatingView = ref(false)
const pages = ref([])
const selectedNode = ref(null)
const layoutRoot = ref(createBlankRoot())
const designerMode = ref('canvas')
const previewMode = ref(false)
const canvasViewport = ref('desktop')
const paletteSections = ref(['layout', 'business', 'fields'])
const propertyTab = ref('layout')
const eventDefinitions = ref([])
const previewState = reactive({})
const syncPageId = ref(0)
const syncPreview = reactive({ requiresSync: false, changes: [] })
const generationResult = reactive(defaultGenerationResult())
const query = reactive({ projectId: Number(route.query.projectId) || undefined, pageKey: '' })
const form = reactive(defaultForm())
const bindingDraft = reactive(defaultBindingDraft())
const viewBuilderForm = reactive(defaultViewBuilderForm())

const pageTypes = [
  { value: 1, label: '自定义页面' }, { value: 2, label: '业务工作台' }, { value: 3, label: '仪表盘' },
  { value: 4, label: '复杂表单' }, { value: 5, label: '复合列表' }, { value: 6, label: '详情页面' }
]
const dataShapes = [
  { value: 1, label: '单体 DTO' }, { value: 2, label: '集合 DTO[]' }, { value: 3, label: '分页 Paged' },
  { value: 4, label: '选项集合' }, { value: 5, label: '聚合结果' }
]
const editableSourceTypes = [
  { value: 1, label: '实体' },
  { value: 4, label: 'API' }
]
const bindingRoles = [
  { value: 1, label: '容器' }, { value: 2, label: '表单字段' }, { value: 3, label: '表格' },
  { value: 4, label: '表格列' }, { value: 5, label: '表单' }, { value: 6, label: '详情' }, { value: 7, label: '选项' }
]
const layoutPalette = [
  { type: 'row', label: '行', icon: '▤' },
  { type: 'column', label: '列', icon: '▥' },
  { type: 'card', label: '卡片', icon: '▢' }
]
const businessPalette = [
  { type: 'search', label: '查询区', icon: '⌕' },
  { type: 'table', label: '表格', icon: '▦' },
  { type: 'table-column', label: '表格列', icon: '┃' },
  { type: 'form', label: '表单', icon: '▧' },
  { type: 'form-field', label: '表单字段', icon: '⌨' },
  { type: 'detail', label: '详情', icon: '☷' },
  { type: 'detail-field', label: '详情字段', icon: '≡' },
  { type: 'pagination', label: '分页', icon: '•••' },
  { type: 'button', label: '按钮', icon: '▣' },
  { type: 'dialog', label: '对话框', icon: '▱' },
  { type: 'text', label: '文本', icon: 'T' }
]
const actionTypes = [
  { value: 'refreshDataSource', label: '刷新数据源' },
  { value: 'navigate', label: '跳转页面' },
  { value: 'showMessage', label: '显示消息' },
  { value: 'setPageParam', label: '设置页面参数' },
  { value: 'setQueryParam', label: '设置查询参数' },
  { value: 'resetQuery', label: '重置查询并刷新' },
  { value: 'openDialog', label: '打开对话框' },
  { value: 'closeDialog', label: '关闭对话框' },
  { value: 'toggleComponent', label: '切换组件显示' },
  { value: 'stop', label: '停止后续动作' }
]
const rules = {
  projectId: [{ required: true, message: '请选择项目', trigger: 'change' }],
  moduleId: [{ required: true, message: '请选择模块', trigger: 'change' }],
  pageName: [{ required: true, message: '请输入页面名称', trigger: 'blur' }],
  pageKey: [{ required: true, pattern: /^[A-Za-z][A-Za-z0-9_-]*$/, message: 'PageKey 必须以字母开头，只能包含字母、数字、下划线或短横线', trigger: 'blur' }],
  routePath: [{ required: true, pattern: /^\/[A-Za-z0-9_\-/]*$/, message: '请输入项目内的绝对路由', trigger: 'blur' }]
}

const layoutTreeData = computed(() => [layoutRoot.value])
const apiServices = computed(() => (dataSourceCatalog.value.apiServices || []).filter(service =>
  (service.methods || []).some(isSupportedApiMethod)
))
const entitySourceCapabilities = computed(() => capabilities.value.filter(item => (item.generationMode || 0) === 0))
const selectedBindingSource = computed(() => form.dataSources.find(item => item.sourceKey === bindingDraft.dataSourceKey))
const compatibleBindingSources = computed(() => form.dataSources.filter(source =>
  compatibleShapes(bindingDraft.bindingRole).includes(source.dataShape) &&
  (!bindingNeedsField(bindingDraft.bindingRole) || Boolean(capabilityForSource(source)))
))
const fieldsForBinding = computed(() => capabilityForSource(selectedBindingSource.value)?.fields || [])
const selectedBindingField = computed(() => fieldsForBinding.value.find(item => item.fieldId === bindingDraft.fieldId))
const semanticDefaultControl = computed(() => inferSemanticControl(selectedBindingField.value, bindingDraft.bindingRole))
const controlTypeOptions = computed(() => controlOptionsForRole(bindingDraft.bindingRole))
const compatibleDisplayTemplates = computed(() => {
  const scope = bindingNeedsField(bindingDraft.bindingRole) ? 2 : 1
  const nodeType = selectedNode.value?.type
  return displayTemplates.value.filter(item => item.scope === scope && (!item.targetType || !nodeType || item.targetType === nodeType))
})
const viewFieldTree = computed(() => (viewBuilderCatalog.value.entities || []).map(entity => ({
  key: `entity:${entity.entityId}`,
  label: `${entity.displayName || entity.entityName} · ${entity.tableName}`,
  children: (entity.fields || []).filter(field => !field.isSystemField).map(field => ({
    key: `field:${field.fieldId}`,
    label: `${field.displayName || field.name} · ${field.dataType}`,
    entityId: entity.entityId,
    fieldId: field.fieldId
  }))
})))
const viewJoinOptions = computed(() => (viewBuilderCatalog.value.joinCandidates || []).map((item, index) => ({
  ...item,
  key: joinCandidateKey(item, index),
  label: `${entityCatalogName(item.sourceEntityId)}.${item.sourceField} → ${entityCatalogName(item.targetEntityId)}.${item.targetField}`
})))
const definitionWarnings = computed(() => {
  const warnings = []
  const keys = form.dataSources.map(item => item.sourceKey?.trim()).filter(Boolean)
  if (keys.length !== new Set(keys.map(item => item.toLowerCase())).size) warnings.push('数据源 SourceKey 不能重复。')
  form.dataSources.forEach((source, index) => {
    const key = source.sourceKey || index + 1
    if (!source.sourceKey || !source.serviceName || !source.methodName || !source.dtoName) warnings.push(`第 ${index + 1} 个数据源尚未配置完整。`)
    if (source.sourceType === 1 && !source.entityId) warnings.push(`实体数据源 ${key} 尚未选择实体。`)
    if (![1, 4].includes(source.sourceType)) warnings.push(`数据源 ${key} 使用了当前未启用的类型。`)
    try {
      const params = JSON.parse(source.paramsJson || '{}')
      if (source.sourceType === 4) {
        const missingRouteParams = (apiMethodForSource(source)?.parameters || [])
          .filter(item => item.isRouteParameter && !item.isOptional && !(item.name in params))
        if (missingRouteParams.length) warnings.push(`API 数据源 ${key} 缺少路由参数：${missingRouteParams.map(item => item.name).join(', ')}。`)
      }
    } catch { warnings.push(`数据源 ${key} 的参数 JSON 无效。`) }
  })
  const nodeIds = new Set(flattenNodes(layoutRoot.value).map(item => item.componentId))
  form.bindings.forEach(binding => {
    if (!nodeIds.has(binding.componentId)) warnings.push(`绑定 ${binding.bindingId || binding.componentId} 指向不存在的组件。`)
    if (!keys.includes(binding.dataSourceKey)) warnings.push(`组件 ${binding.componentId} 的数据源不存在。`)
    if (bindingNeedsField(binding.bindingRole) && !binding.fieldId) warnings.push(`组件 ${binding.componentId} 尚未选择实体字段。`)
  })
  flattenNodes(layoutRoot.value).forEach(node => {
    let events = []
    try { events = JSON.parse(node.props?._events || '[]') } catch { warnings.push(`组件 ${node.componentId} 的事件配置 JSON 无效。`) }
    const names = events.map(item => item.eventName).filter(Boolean)
    if (names.length !== new Set(names).size) warnings.push(`组件 ${node.componentId} 存在重复事件。`)
    events.forEach(eventItem => {
      if (!eventItem.eventName || !eventItem.actions?.length) warnings.push(`组件 ${node.componentId} 的事件配置不完整。`)
      ;(eventItem.actions || []).forEach(action => {
        if (action.type === 'refreshDataSource' && !keys.includes(action.sourceKey)) warnings.push(`组件 ${node.componentId} 的事件引用了不存在的数据源。`)
        if (action.type === 'navigate' && !action.path) warnings.push(`组件 ${node.componentId} 的跳转动作缺少路径。`)
        if (action.type === 'showMessage' && !action.message) warnings.push(`组件 ${node.componentId} 的消息动作缺少内容。`)
        if (['setPageParam', 'setQueryParam'].includes(action.type) && !action.key) warnings.push(`组件 ${node.componentId} 的参数动作缺少参数名。`)
        if (['openDialog', 'closeDialog', 'toggleComponent'].includes(action.type) && !nodeIds.has(action.targetComponentId)) warnings.push(`组件 ${node.componentId} 的事件目标已经不存在。`)
      })
    })
  })
  return [...new Set(warnings)]
})

function createBlankRoot() {
  return { type: 'page', componentId: 'page_root', origin: 2, children: [] }
}

function defaultForm() {
  return {
    projectId: query?.projectId,
    moduleId: undefined,
    pageName: '',
    pageKey: '',
    description: '',
    routePath: '',
    icon: '',
    layoutPresetId: undefined,
    layoutJson: JSON.stringify({ version: 1, root: createBlankRoot() }, null, 2),
    pageType: 1,
    syncMode: 1,
    isMenuSynced: false,
    definitionVersion: undefined,
    dataSources: [],
    bindings: []
  }
}

function defaultBindingDraft() {
  return { componentId: '', bindingId: '', dataSourceKey: '', fieldId: undefined, bindingRole: 1, syncMode: 1, autoIncludeNewFields: false, controlType: '', labelOverride: '', propsJson: '{}', displayTemplateId: undefined }
}

function defaultViewBuilderForm() {
  return {
    projectId: undefined,
    moduleId: undefined,
    viewName: '',
    entityName: '',
    displayName: '',
    keyColumn: '',
    sql: '',
    expectedColumns: [],
    selections: [],
    joinKeys: []
  }
}

function defaultGenerationResult() {
  return {
    success: false,
    hasConflicts: false,
    pageKey: '',
    outputDirectory: '',
    vuePath: '',
    treePath: '',
    vueHash: '',
    files: [],
    conflicts: [],
    error: '',
    diagnosticOutput: '',
    buildSuccess: false,
    buildMessage: '',
    buildOutput: ''
  }
}

async function loadProjects() {
  projects.value = await getProjects({}) || []
  if (!query.projectId && projects.value.length) query.projectId = projects.value[0].id
  await loadModules()
}

async function loadModules() {
  await loadPages()
}

async function loadPages() {
  loading.value = true
  try { pages.value = await getList(query) || [] } finally { loading.value = false }
}

async function loadModulesForForm() {
  formModules.value = form.projectId ? (await getByProjectId(form.projectId) || []) : []
  if (!formModules.value.some(item => item.id === form.moduleId)) form.moduleId = formModules.value[0]?.id
  if (form.projectId) {
    const [entityCapabilities, sourceCatalog, apiSourcePage] = await Promise.all([
      getEntityServiceCapabilities(form.projectId),
      getDataSourceCapabilityCatalog(form.projectId),
      getProjectDataSources({ projectId: form.projectId, sourceType: 4, pageNum: 1, pageSize: 200 })
    ])
    capabilities.value = entityCapabilities || []
    dataSourceCatalog.value = sourceCatalog || { entities: [], apiServices: [], views: [] }
    projectApiSources.value = (apiSourcePage?.items || []).filter(isSupportedImportedApiSource)
  } else {
    capabilities.value = []
    dataSourceCatalog.value = { entities: [], apiServices: [], views: [] }
    projectApiSources.value = []
  }
  displayTemplates.value = await getDisplayTemplates({ includeDisabled: false }) || []
}

async function loadPresetPreview() {
  if (!form.layoutPresetId) return
  const preset = presets.value.find(item => item.id === form.layoutPresetId)
  if (!preset) return
  form.layoutJson = normalizeLayoutJson(await getLayoutTree(preset.presetKey))
  parseLayoutJson()
}

function resetQuery() {
  query.pageKey = ''
  loadPages()
}

async function openCreate() {
  editingId.value = 0
  activeStep.value = 0
  Object.assign(form, defaultForm())
  form.projectId = query.projectId
  await loadModulesForForm()
  layoutRoot.value = createBlankRoot()
  selectedNode.value = layoutRoot.value
  designerMode.value = 'canvas'
  previewMode.value = false
  canvasViewport.value = 'desktop'
  loadSelectedEvents()
  resetPreviewState()
  syncLayoutJson()
  designerVisible.value = true
}

async function openEdit(row) {
  const detail = await getById(row.id)
  editingId.value = row.id
  activeStep.value = 0
  Object.assign(form, defaultForm(), detail)
  form.dataSources = (detail.dataSources || []).map(normalizeSource)
  form.bindings = (detail.bindings || []).map(item => ({
    ...item,
    dataSourceKey: sourceKeyById(detail.dataSources, item.dataSourceId),
    displayTemplateId: displayTemplateIdFromProps(item.propsJson)
  }))
  await loadModulesForForm()
  parseLayoutJson()
  selectedNode.value = layoutRoot.value
  designerMode.value = 'canvas'
  previewMode.value = false
  canvasViewport.value = 'desktop'
  designerVisible.value = true
}

function normalizeSource(source) {
  return { sourceType: 1, syncMode: 1, paramsJson: '{}', isEnabled: true, ...source }
}

function sourceKeyById(sources, id) {
  return (sources || []).find(item => item.id === id)?.sourceKey || ''
}

async function nextStep() {
  if (activeStep.value === 0) await formRef.value.validate()
  if (activeStep.value === 1 && definitionWarnings.value.some(item => item.includes('数据源'))) {
    ElMessage.warning('请先补全数据源定义。')
    return
  }
  syncLayoutJson()
  activeStep.value++
}

async function submitForm(generateAfterSave) {
  await formRef.value.validate()
  syncLayoutJson()
  saving.value = true
  try {
    const payload = buildPayload()
    let pageId = editingId.value
    if (editingId.value) await saveDefinition(editingId.value, payload)
    else pageId = await create(payload)
    ElMessage.success('复杂页定义已保存')
    designerVisible.value = false
    await loadPages()
    if (generateAfterSave) await runGeneration(pageId, form.projectId)
  } finally { saving.value = false }
}

function buildPayload() {
  return {
    ...form,
    layoutJson: form.layoutJson,
    dataSources: form.dataSources.map((source, index) => ({ ...source, orderNum: index, sourceType: source.sourceType || 1, paramsJson: source.paramsJson || '{}' })),
    bindings: form.bindings.map(binding => ({
      ...binding,
      bindingStatus: binding.bindingStatus || 1,
      syncMode: binding.syncMode || 1,
      propsJson: propsJsonWithDisplayTemplate(binding.propsJson, binding.displayTemplateId)
    }))
  }
}

function handleAddDataSourceCommand(command) {
  if (command === 'view') return openViewBuilder()
  addDataSource(command === 'api' ? 4 : 1)
}

function addDataSource(sourceType = 1) {
  const number = form.dataSources.length + 1
  const source = normalizeSource({ sourceKey: `source${number}`, sourceType, dataShape: 3, serviceName: '', methodName: '', dtoName: '' })
  form.dataSources.push(source)
  onSourceTypeChange(source)
}

function removeDataSource(index) {
  const [removed] = form.dataSources.splice(index, 1)
  form.bindings = form.bindings.filter(item => item.dataSourceKey !== removed.sourceKey)
  flattenNodes(layoutRoot.value).forEach(node => {
    if (node.dataSourceId && node.dataSourceId === removed.id) clearNodeBinding(node)
  })
  syncLayoutJson()
}

function capabilityForSource(source) {
  return source ? capabilities.value.find(item => item.entityId === source.entityId) : null
}

function apiServiceForSource(source) {
  return source ? apiServices.value.find(item => item.serviceName === source.serviceName) : null
}

function projectApiSourceFor(source) {
  return source?.projectDataSourceId
    ? projectApiSources.value.find(item => String(item.id) === String(source.projectDataSourceId))
    : null
}

function apiMethodForSource(source) {
  const imported = projectApiSourceFor(source)
  if (imported) return importedApiMethod(imported)
  return (apiServiceForSource(source)?.methods || []).find(item => item.methodName === source?.methodName)
}

function importedApiMethod(source) {
  let parameters = []
  try {
    const contract = JSON.parse(source.operationContractJson || '{}')
    parameters = (contract.parameters || []).map(item => ({
      name: item.name,
      isOptional: !item.isRequired,
      isRouteParameter: String(item.location || '').toLowerCase() === 'path'
    }))
  } catch { parameters = [] }
  const knownRouteNames = new Set(parameters.filter(item => item.isRouteParameter).map(item => item.name))
  for (const match of String(source.routePath || '').matchAll(/\{([^}/]+)\}/g)) {
    if (!knownRouteNames.has(match[1])) parameters.push({ name: match[1], isOptional: false, isRouteParameter: true })
  }
  return {
    methodName: source.methodName,
    dtoName: source.dtoName,
    dataShape: source.dataShape,
    httpMethod: source.httpMethod,
    routePath: source.routePath,
    parameters
  }
}

function isSupportedApiMethod(method) {
  const routeParameters = (method?.parameters || []).filter(item => item.isRouteParameter)
  if (String(method?.httpMethod || 'GET').toUpperCase() !== 'GET' || routeParameters.length > 1) return false
  if (!routeParameters.length) return true
  return String(method?.routePath || '').toLowerCase().endsWith(`/{${routeParameters[0].name}}`.toLowerCase())
}

function isSupportedImportedApiSource(source) {
  return source?.openApiDocumentId && source?.isEnabled && [1, 7].includes(Number(source?.openApiSyncState)) &&
    isSupportedApiMethod(importedApiMethod(source))
}

function methodsForSource(source) {
  if (source?.sourceType === 4) {
    const imported = projectApiSourceFor(source)
    return imported ? [importedApiMethod(imported)] : (apiServiceForSource(source)?.methods || []).filter(isSupportedApiMethod)
  }
  return (capabilityForSource(source)?.methods || []).filter(item => item.supportedShapes?.includes(source.dataShape))
}

function methodOptionLabel(method) {
  const shape = dataShapes.find(item => item.value === method.dataShape)?.label
  return shape ? `${method.methodName} · ${shape}` : method.methodName
}

function onSourceTypeChange(source) {
  source.entityId = undefined
  source.projectDataSourceId = undefined
  source.serviceName = ''
  source.methodName = ''
  source.dtoName = ''
  source.dataShape = source.sourceType === 4 ? 1 : 3
  if (source.sourceType === 4 && projectApiSources.value.length) {
    onApiSelectionChange(source, `openapi:${projectApiSources.value[0].id}`)
  } else if (source.sourceType === 4 && apiServices.value.length) {
    onApiSelectionChange(source, `service:${apiServices.value[0].serviceName}`)
  }
}

function onSourceEntityChange(source) {
  const capability = capabilityForSource(source)
  source.serviceName = capability?.serviceName || ''
  onSourceShapeChange(source)
}

function onApiServiceChange(source) {
  source.projectDataSourceId = undefined
  source.entityId = undefined
  const method = methodsForSource(source)[0]
  source.methodName = method?.methodName || ''
  applyApiMethod(source, method)
}

function apiSelectionKey(source) {
  if (source?.projectDataSourceId) return `openapi:${source.projectDataSourceId}`
  return source?.serviceName ? `service:${source.serviceName}` : ''
}

function onApiSelectionChange(source, selection) {
  if (String(selection).startsWith('openapi:')) {
    const id = String(selection).slice('openapi:'.length)
    const imported = projectApiSources.value.find(item => String(item.id) === id)
    if (!imported) return
    source.projectDataSourceId = imported.id
    source.entityId = imported.responseEntityId || imported.entityId
    source.serviceName = imported.serviceName
    source.methodName = imported.methodName
    source.dtoName = imported.dtoName
    source.dataShape = imported.dataShape
    applyApiMethod(source, importedApiMethod(imported), true)
    return
  }

  source.projectDataSourceId = undefined
  source.serviceName = String(selection).replace(/^service:/, '')
  onApiServiceChange(source)
}

function onSourceShapeChange(source) {
  if (source.sourceType === 4) return
  const method = methodsForSource(source)[0]
  source.methodName = method?.methodName || ''
  source.dtoName = method?.dtoName || ''
}

function onSourceMethodChange(source) {
  const method = methodsForSource(source).find(item => item.methodName === source.methodName)
  if (source.sourceType === 4) return applyApiMethod(source, method)
  source.dtoName = method?.dtoName || source.dtoName
}

function applyApiMethod(source, method, preserveEntity = false) {
  source.dtoName = method?.dtoName || ''
  source.dataShape = method?.dataShape || 1
  if (!preserveEntity) {
    source.entityId = capabilities.value.find(capability =>
      (capability.methods || []).some(item => item.dtoName === source.dtoName)
    )?.entityId
  }
  const params = {}
  ;(method?.parameters || []).filter(item => item.isRouteParameter).forEach(item => {
    params[item.name] = { $ref: `$route.${item.name}` }
  })
  source.paramsJson = JSON.stringify(params)
}

function sourceEntityText(source) {
  if (source?.sourceType === 4) {
    const entity = capabilityForSource(source)?.entityName
    return entity || `${source.serviceName || 'API'}.${source.methodName || ''}`
  }
  return capabilityForSource(source)?.entityName || source?.serviceName || '未选择实体'
}

function fieldsForSource(source) {
  return capabilityForSource(source)?.fields || []
}

function onPaletteDragStart(event, type) {
  event.dataTransfer.effectAllowed = 'copy'
  event.dataTransfer.setData('application/x-codemaster-layout-type', type)
}

function onFieldDragStart(event, source, field) {
  event.dataTransfer.effectAllowed = 'copy'
  event.dataTransfer.setData('application/x-codemaster-layout-field', JSON.stringify({
    sourceKey: source.sourceKey,
    fieldId: field.fieldId
  }))
}

function addFieldToSelected(source, field) {
  const target = resolveFieldDropTarget(selectedNode.value)
  if (!target) return ElMessage.warning('请先选择查询区、表格、表单或详情组件')
  handleCanvasDrop({
    kind: 'field',
    value: { sourceKey: source.sourceKey, fieldId: field.fieldId },
    targetId: target.componentId,
    index: null
  })
}

function handleCanvasDrop(payload) {
  const target = flattenNodes(layoutRoot.value).find(item => item.componentId === payload.targetId)
  if (!target) return
  if (payload.kind === 'component') return addNodeToTarget(payload.value, target, payload.index)
  if (payload.kind === 'move') return moveNodeToTarget(payload.value, target, payload.index)
  if (payload.kind === 'field') return addFieldBindingToTarget(payload.value, target, payload.index)
}

function addNodeToTarget(type, target, index = null) {
  if (!canAcceptChild(target, type)) {
    return ElMessage.warning(`${nodeTypeText(target.type)}中不能放置${nodeTypeText(type)}`)
  }
  const node = { type, componentId: nextNodeId(type), origin: 2, props: {}, children: [] }
  if (type === 'column') {
    node.span = 24
    node.grid = { span: 24, xs: 24, sm: 24, md: 24, lg: 24 }
  }
  target.children ||= []
  const insertIndex = index == null ? target.children.length : Math.max(0, Math.min(index, target.children.length))
  target.children.splice(insertIndex, 0, node)
  previewState[node.componentId] = type !== 'dialog'
  if (target.type === 'row') rebalanceRowColumns(target)
  selectedNode.value = node
  syncLayoutJson()
}

function moveNodeToTarget(componentId, target, index = null) {
  const node = flattenNodes(layoutRoot.value).find(item => item.componentId === componentId)
  const oldParent = findParent(layoutRoot.value, componentId)
  if (!node || !oldParent || node === target || flattenNodes(node).some(item => item === target)) return
  if (!canAcceptChild(target, node.type)) {
    return ElMessage.warning(`${nodeTypeText(target.type)}中不能放置${nodeTypeText(node.type)}`)
  }
  const oldIndex = oldParent.children.findIndex(item => item.componentId === componentId)
  oldParent.children.splice(oldIndex, 1)
  target.children ||= []
  let insertIndex = index == null ? target.children.length : Math.max(0, Math.min(index, target.children.length))
  if (oldParent === target && insertIndex > oldIndex) insertIndex--
  target.children.splice(insertIndex, 0, node)
  if (oldParent.type === 'row') rebalanceRowColumns(oldParent)
  if (target.type === 'row') rebalanceRowColumns(target)
  selectedNode.value = node
  syncLayoutJson()
}

function addFieldBindingToTarget(payload, target, index = null) {
  const source = form.dataSources.find(item => item.sourceKey === payload.sourceKey)
  const field = fieldsForSource(source).find(item => String(item.fieldId) === String(payload.fieldId))
  if (!source || !field) return ElMessage.warning('拖拽字段已经失效，请刷新数据源')
  const resolvedTarget = resolveFieldDropTarget(target)
  if (!resolvedTarget) return ElMessage.warning('字段只能拖入查询区、表格、表单或详情组件')
  const descriptor = fieldNodeDescriptor(resolvedTarget.type)
  if (!descriptor) return
  if (!compatibleShapesForTarget(resolvedTarget.type).includes(Number(source.dataShape))) {
    return ElMessage.warning(`${source.sourceKey} 的数据形态不适用于${nodeTypeText(resolvedTarget.type)}`)
  }

  const node = {
    type: descriptor.type,
    componentId: nextNodeId(descriptor.type),
    origin: 2,
    children: []
  }
  resolvedTarget.children ||= []
  const insertIndex = index == null ? resolvedTarget.children.length : Math.max(0, Math.min(index, resolvedTarget.children.length))
  resolvedTarget.children.splice(insertIndex, 0, node)
  const bindingId = nextBindingId(node.componentId)
  form.bindings.push({
    componentId: node.componentId,
    bindingId,
    dataSourceKey: source.sourceKey,
    dataSourceId: source.id,
    fieldId: field.fieldId,
    bindingRole: descriptor.role,
    bindingStatus: 1,
    syncMode: 1,
    autoIncludeNewFields: false,
    controlType: '',
    labelOverride: field.name,
    propsJson: '{}',
    origin: 2,
    generatedGenId: `gen_binding_${bindingId}`
  })
  if (source.id) {
    node.bindingId = bindingId
    node.dataSourceId = source.id
    node.fieldId = field.fieldId
  }
  selectedNode.value = node
  syncLayoutJson()
}

function resolveFieldDropTarget(node) {
  let current = node
  while (current) {
    if (['table', 'form', 'search', 'query-form', 'detail'].includes(current.type)) return current
    current = findParent(layoutRoot.value, current.componentId)
  }
  return null
}

function fieldNodeDescriptor(targetType) {
  if (targetType === 'table') return { type: 'table-column', role: 4 }
  if (targetType === 'detail') return { type: 'detail-field', role: 6 }
  if (['form', 'search', 'query-form'].includes(targetType)) return { type: 'form-field', role: 2 }
  return null
}

function compatibleShapesForTarget(targetType) {
  if (targetType === 'table') return [2, 3]
  if (['search', 'query-form'].includes(targetType)) return [1, 2, 3]
  return [1]
}

function canAcceptChild(parent, childType) {
  if (!parent) return false
  if (parent.type === 'row') return childType === 'column'
  if (parent.type === 'table') return childType === 'table-column'
  if (['form', 'search', 'query-form'].includes(parent.type)) return childType === 'form-field'
  if (parent.type === 'detail') return childType === 'detail-field'
  if (['pagination', 'table-column', 'form-field', 'detail-field', 'button', 'text'].includes(parent.type)) return false
  if (childType === 'column') return false
  if (['table-column', 'form-field', 'detail-field'].includes(childType)) return false
  return true
}

function nextBindingId(componentId) {
  const used = new Set(form.bindings.map(item => String(item.bindingId || '').toLowerCase()))
  let candidate = `binding_${componentId}`
  let suffix = 2
  while (used.has(candidate.toLowerCase())) candidate = `binding_${componentId}_${suffix++}`
  return candidate
}

function allowTreeDrop(draggingNode, dropNode, type) {
  if (draggingNode.data.type === 'page') return false
  const parent = type === 'inner' ? dropNode.data : findParent(layoutRoot.value, dropNode.data.componentId)
  return Boolean(parent && canAcceptChild(parent, draggingNode.data.type))
}

function handleTreeDrop() {
  flattenNodes(layoutRoot.value).filter(item => item.type === 'row').forEach(rebalanceRowColumns)
  syncLayoutJson()
}

function parseLayoutJson() {
  try {
    const parsed = typeof form.layoutJson === 'string'
      ? JSON.parse(form.layoutJson || '{}')
      : (form.layoutJson || {})
    form.layoutJson = normalizeLayoutJson(parsed)
    layoutRoot.value = parsed.root || createBlankRoot()
    ensureNodeDefaults(layoutRoot.value, true)
    selectedNode.value = layoutRoot.value
    loadSelectedEvents()
    resetPreviewState()
  } catch {
    ElMessage.error('布局树 JSON 无效')
  }
}

function normalizeLayoutJson(value) {
  if (typeof value === 'string') return value
  return JSON.stringify(value || {}, null, 2)
}

function ensureNodeDefaults(node, isRoot = false) {
  node.type ||= isRoot ? 'page' : 'component'
  node.componentId ||= isRoot ? 'page_root' : nextNodeId(node.type)
  node.origin ||= 2
  node.props ||= {}
  node.children ||= []
  if (node.type === 'column') {
    node.span ||= node.grid?.span || 24
    node.grid ||= { span: node.span, xs: 24, sm: node.span, md: node.span, lg: node.span }
    node.grid.span = node.span
  }
  node.children.forEach(child => ensureNodeDefaults(child))
}

function syncLayoutJson() {
  form.layoutJson = JSON.stringify({ version: 1, root: layoutRoot.value }, null, 2)
}

function selectNode(data) {
  selectedNode.value = data
  ensureNodeDefaults(data, data.type === 'page')
  loadSelectedEvents()
}

function loadSelectedEvents() {
  try {
    const events = JSON.parse(selectedNode.value?.props?._events || '[]')
    eventDefinitions.value = (Array.isArray(events) ? events : []).map(normalizeEventDefinition)
  } catch {
    eventDefinitions.value = []
  }
}

function normalizeEventDefinition(item = {}) {
  return {
    localId: item.localId || createLocalId('event'),
    eventName: item.eventName || eventOptionsForNode(selectedNode.value)[0]?.value || 'click',
    actions: (item.actions || []).map(normalizeEventAction)
  }
}

function normalizeEventAction(item = {}) {
  return {
    localId: item.localId || createLocalId('action'),
    type: item.type || 'showMessage',
    sourceKey: item.sourceKey || '',
    targetComponentId: item.targetComponentId || '',
    path: item.path || '',
    key: item.key || '',
    value: item.value || '',
    message: item.message || '',
    messageType: item.messageType || 'success'
  }
}

function createLocalId(prefix) {
  return `${prefix}_${Date.now()}_${Math.random().toString(16).slice(2)}`
}

function addNodeEvent() {
  const eventItem = normalizeEventDefinition({ actions: [normalizeEventAction()] })
  eventDefinitions.value.push(eventItem)
  syncSelectedEvents()
}

function removeNodeEvent(index) {
  eventDefinitions.value.splice(index, 1)
  syncSelectedEvents()
}

function addEventAction(eventItem) {
  eventItem.actions.push(normalizeEventAction())
  syncSelectedEvents()
}

function removeEventAction(eventItem, index) {
  eventItem.actions.splice(index, 1)
  if (!eventItem.actions.length) eventItem.actions.push(normalizeEventAction())
  syncSelectedEvents()
}

function moveEventAction(eventItem, index, offset) {
  const target = index + offset
  if (target < 0 || target >= eventItem.actions.length) return
  const [action] = eventItem.actions.splice(index, 1)
  eventItem.actions.splice(target, 0, action)
  syncSelectedEvents()
}

function resetActionFields(action) {
  const type = action.type
  Object.assign(action, normalizeEventAction({ type, localId: action.localId }))
  if (type === 'refreshDataSource') action.sourceKey = form.dataSources[0]?.sourceKey || ''
  if (['openDialog', 'closeDialog', 'toggleComponent'].includes(type)) action.targetComponentId = actionTargetNodes(type)[0]?.componentId || ''
}

function syncSelectedEvents() {
  if (!selectedNode.value) return
  selectedNode.value.props ||= {}
  const serializable = eventDefinitions.value.map(item => ({
    eventName: item.eventName,
    actions: item.actions.map(action => {
      const result = { type: action.type }
      ;['sourceKey', 'targetComponentId', 'path', 'key', 'value', 'message', 'messageType'].forEach(key => {
        if (action[key] !== '' && action[key] != null) result[key] = action[key]
      })
      return result
    })
  }))
  if (serializable.length) selectedNode.value.props._events = JSON.stringify(serializable)
  else delete selectedNode.value.props._events
  syncLayoutJson()
}

function eventOptionsForNode(node) {
  if (!node) return []
  if (node.type === 'table') return [{ value: 'row-click', label: '行点击' }, { value: 'selection-change', label: '选择变化' }]
  if (['form', 'search', 'query-form'].includes(node.type)) return [{ value: 'submit.prevent', label: '提交' }, { value: 'reset', label: '重置' }]
  if (['form-field', 'detail-field'].includes(node.type)) return [{ value: 'change', label: '值变化' }, { value: 'blur', label: '失去焦点' }]
  if (node.type === 'dialog') return [{ value: 'close', label: '关闭' }, { value: 'open', label: '打开' }]
  return [{ value: 'click', label: '点击' }, { value: 'dblclick', label: '双击' }]
}

function actionTargetNodes(actionType) {
  const nodes = flattenNodes(layoutRoot.value).filter(item => item.type !== 'page')
  return ['openDialog', 'closeDialog'].includes(actionType)
    ? nodes.filter(item => item.type === 'dialog')
    : nodes
}

function previewNodeEvent({ node, eventName, event }) {
  let definitions = []
  try { definitions = JSON.parse(node.props?._events || '[]') } catch { definitions = [] }
  const definition = definitions.find(item => item.eventName === eventName) || definitions[0]
  if (!definition) return
  for (const action of definition.actions || []) {
    if (action.type === 'stop') break
    if (action.type === 'showMessage') (ElMessage[action.messageType || 'success'] || ElMessage.info)(action.message || '预览消息')
    else if (action.type === 'openDialog') previewState[action.targetComponentId] = true
    else if (action.type === 'closeDialog') previewState[action.targetComponentId] = false
    else if (action.type === 'toggleComponent') previewState[action.targetComponentId] = !(previewState[action.targetComponentId] !== false)
    else if (action.type === 'refreshDataSource') ElMessage.info(`预览：刷新数据源 ${action.sourceKey}`)
    else if (action.type === 'navigate') ElMessage.info(`预览：跳转到 ${action.path}`)
    else if (action.type === 'resetQuery') ElMessage.info('预览：重置查询并刷新')
    else if (['setPageParam', 'setQueryParam'].includes(action.type)) ElMessage.info(`预览：设置 ${action.key} = ${resolvePreviewValue(action.value, event)}`)
  }
}

function resolvePreviewValue(value, event) {
  if (typeof value !== 'string' || !value.startsWith('$event.')) return value
  return value.slice('$event.'.length).split('.').reduce((current, key) => current?.[key], event)
}

function resetPreviewState() {
  Object.keys(previewState).forEach(key => delete previewState[key])
  flattenNodes(layoutRoot.value).forEach(node => {
    previewState[node.componentId] = node.type !== 'dialog'
  })
}

function openBindingMenu(event, data) {
  event.preventDefault()
  selectedNode.value = data
  if (data.type !== 'page' && data.type !== 'row' && data.type !== 'column') openBindingDialog(data)
}

function openBindingDialog(node) {
  selectedNode.value = node
  const existing = bindingForNode(node)
  Object.assign(bindingDraft, defaultBindingDraft(), existing || {}, {
    componentId: node.componentId,
    bindingId: existing?.bindingId || node.bindingId || `binding_${node.componentId}`,
    dataSourceKey: existing?.dataSourceKey || sourceKeyById(form.dataSources, node.dataSourceId) || form.dataSources[0]?.sourceKey || '',
    bindingRole: existing?.bindingRole || defaultRoleForNode(node.type),
    displayTemplateId: existing?.displayTemplateId || displayTemplateIdFromProps(existing?.propsJson)
  })
  onBindingRoleChange()
  bindingVisible.value = true
}

function onBindingSourceChange() {
  if (bindingDraft.fieldId && !fieldsForBinding.value.some(item => item.fieldId === bindingDraft.fieldId)) bindingDraft.fieldId = undefined
}

function onBindingRoleChange() {
  if (!compatibleBindingSources.value.some(item => item.sourceKey === bindingDraft.dataSourceKey)) {
    bindingDraft.dataSourceKey = compatibleBindingSources.value[0]?.sourceKey || ''
    bindingDraft.fieldId = undefined
  }
  onBindingSourceChange()
}

async function applyBindingDraft(saveImmediately) {
  if (!bindingDraft.dataSourceKey) return ElMessage.warning('请选择数据源')
  if (bindingNeedsField(bindingDraft.bindingRole) && !bindingDraft.fieldId) return ElMessage.warning('请选择实体字段')
  const source = selectedBindingSource.value
  const binding = {
    ...bindingDraft,
    propsJson: propsJsonWithDisplayTemplate(bindingDraft.propsJson, bindingDraft.displayTemplateId),
    bindingStatus: 1,
    dataSourceId: source?.id,
    origin: selectedNode.value?.origin || 2,
    generatedGenId: `gen_binding_${bindingDraft.bindingId}`
  }
  const index = form.bindings.findIndex(item => item.componentId === binding.componentId)
  if (index >= 0) form.bindings.splice(index, 1, binding)
  else form.bindings.push(binding)
  selectedNode.value.bindingId = binding.bindingId
  if (source?.id) selectedNode.value.dataSourceId = source.id
  else delete selectedNode.value.dataSourceId
  if (binding.fieldId) selectedNode.value.fieldId = binding.fieldId
  else delete selectedNode.value.fieldId
  syncLayoutJson()

  if (saveImmediately) {
    const detail = await rebindComponent(editingId.value, {
      componentId: binding.componentId,
      bindingId: binding.bindingId,
      dataSourceId: source.id,
      fieldId: binding.fieldId,
      bindingRole: binding.bindingRole,
      syncMode: binding.syncMode,
      autoIncludeNewFields: binding.autoIncludeNewFields,
      controlType: binding.controlType,
      propsJson: propsJsonWithDisplayTemplate(binding.propsJson, binding.displayTemplateId),
      labelOverride: binding.labelOverride
    })
    form.definitionVersion = detail.definitionVersion
    form.bindings = detail.bindings.map(item => ({
      ...item,
      dataSourceKey: sourceKeyById(detail.dataSources, item.dataSourceId),
      displayTemplateId: displayTemplateIdFromProps(item.propsJson)
    }))
    form.layoutJson = detail.layoutJson
    parseLayoutJson()
    ElMessage.success('组件绑定已立即保存')
  }
  bindingVisible.value = false
}

function addLayoutNode(type) {
  const parent = resolveParentForType(type)
  if (!parent) return ElMessage.warning(`请选择可容纳“${nodeTypeText(type)}”的父节点`)
  const node = { type, componentId: nextNodeId(type), origin: 2, props: {}, children: [] }
  if (type === 'column') node.grid = { span: 24, xs: 24, sm: 24, md: 24, lg: 24 }
  parent.children ||= []
  parent.children.push(node)
  previewState[node.componentId] = type !== 'dialog'
  if (type === 'column') rebalanceRowColumns(parent)
  selectedNode.value = node
  syncLayoutJson()
  nextTick(() => ElMessage.success(`已添加${nodeTypeText(type)}`))
}

function resolveParentForType(type) {
  const selected = selectedNode.value || layoutRoot.value
  if (type === 'row') return selected.type === 'page' ? selected : layoutRoot.value
  if (type === 'column') return selected.type === 'row' ? selected : findParent(layoutRoot.value, selected.componentId)?.type === 'row' ? findParent(layoutRoot.value, selected.componentId) : null
  if (type === 'table-column') return selected.type === 'table' ? selected : findAncestorByType(selected.componentId, 'table')
  if (type === 'form-field') return selected.type === 'form' ? selected : findAncestorByType(selected.componentId, 'form')
  if (type === 'detail-field') return selected.type === 'detail' ? selected : findAncestorByType(selected.componentId, 'detail')
  return isContainer(selected) ? selected : findParent(layoutRoot.value, selected.componentId)
}

function isContainer(node) {
  return node && !['pagination', 'table-column', 'form-field', 'detail-field', 'button', 'text'].includes(node.type)
}

function rebalanceRowColumns(row) {
  const columns = (row.children || []).filter(item => item.type === 'column')
  if (!columns.length) return
  const base = Math.floor(24 / columns.length)
  let remainder = 24 - base * columns.length
  columns.forEach(column => {
    column.span = base + (remainder > 0 ? 1 : 0)
    column.grid ||= { span: column.span, xs: 24, sm: column.span, md: column.span, lg: column.span }
    column.grid.span = column.span
    if (remainder > 0) remainder--
  })
}

function columnMaxSpan(column) {
  const parent = findParent(layoutRoot.value, column.componentId)
  if (!parent) return 24
  const siblingTotal = (parent.children || [])
    .filter(item => item.type === 'column' && item.componentId !== column.componentId)
    .reduce((sum, item) => sum + (item.grid?.span || item.span || 24), 0)
  return Math.max(1, 24 - siblingTotal)
}

function splitSelectedColumn() {
  const column = selectedNode.value
  const parent = findParent(layoutRoot.value, column.componentId)
  if (!parent || parent.type !== 'row') return
  const oldSpan = column.span || 24
  if (oldSpan < 2) return
  const firstSpan = Math.floor(oldSpan / 2)
  const siblingSpan = oldSpan - firstSpan
  const sibling = {
    type: 'column', componentId: nextNodeId('column'), span: siblingSpan,
    grid: { span: siblingSpan, xs: 24, sm: siblingSpan, md: siblingSpan, lg: siblingSpan },
    origin: 2, props: {}, children: []
  }
  column.span = firstSpan
  column.grid ||= { span: firstSpan, xs: 24, sm: firstSpan, md: firstSpan, lg: firstSpan }
  column.grid.span = firstSpan
  const index = parent.children.findIndex(item => item.componentId === column.componentId)
  parent.children.splice(index + 1, 0, sibling)
  selectedNode.value = sibling
  syncLayoutJson()
}

function nextNodeId(type) {
  const prefix = type.replace(/[^a-z0-9]/gi, '_').toLowerCase()
  const used = new Set(flattenNodes(layoutRoot.value).map(item => item.componentId))
  let index = 1
  while (used.has(`${prefix}_${index}`)) index++
  return `${prefix}_${index}`
}

function removeSelectedNode() {
  const node = selectedNode.value
  const parent = findParent(layoutRoot.value, node.componentId)
  if (!parent) return
  parent.children = parent.children.filter(item => item.componentId !== node.componentId)
  const removedIds = new Set(flattenNodes(node).map(item => item.componentId))
  form.bindings = form.bindings.filter(item => !removedIds.has(item.componentId))
  selectedNode.value = parent
  syncLayoutJson()
}

function moveSelected(offset) {
  const node = selectedNode.value
  const parent = findParent(layoutRoot.value, node.componentId)
  if (!parent) return
  const index = parent.children.findIndex(item => item.componentId === node.componentId)
  const target = index + offset
  if (target < 0 || target >= parent.children.length) return
  parent.children.splice(index, 1)
  parent.children.splice(target, 0, node)
  syncLayoutJson()
}

function findParent(root, componentId) {
  for (const child of root.children || []) {
    if (child.componentId === componentId) return root
    const parent = findParent(child, componentId)
    if (parent) return parent
  }
  return null
}

function findAncestorByType(componentId, type) {
  let parent = findParent(layoutRoot.value, componentId)
  while (parent) {
    if (parent.type === type) return parent
    parent = findParent(layoutRoot.value, parent.componentId)
  }
  return null
}

function flattenNodes(root) {
  if (!root) return []
  return [root, ...(root.children || []).flatMap(flattenNodes)]
}

function clearNodeBinding(node) {
  delete node.bindingId
  delete node.dataSourceId
  delete node.fieldId
}

function bindingForNode(node) {
  return form.bindings.find(item => item.componentId === node?.componentId && Number(item.bindingRole) !== 8)
}

function fieldForNode(node) {
  const binding = bindingForNode(node)
  const source = form.dataSources.find(item => item.sourceKey === binding?.dataSourceKey) || form.dataSources.find(item => item.id === binding?.dataSourceId)
  return capabilityForSource(source)?.fields?.find(item => String(item.fieldId) === String(binding?.fieldId))
}

function nodeProp(node, key) { return node?.props?.[key] ?? '' }

function setNodeProp(node, key, value) {
  node.props ||= {}
  if (value === '' || value == null) delete node.props[key]
  else node.props[key] = String(value)
  syncLayoutJson()
}

function supportsTitle(node) { return ['card', 'button', 'dialog', 'text'].includes(node?.type) }

function nodeTitleProp(node) {
  if (node?.type === 'card') return 'header'
  if (node?.type === 'dialog') return 'title'
  return 'text'
}

function nodeTitleLabel(node) {
  return ({ card: '卡片标题', dialog: '对话框标题', button: '按钮文字', text: '文本内容' })[node?.type] || '标题'
}

function syncColumnGrid() {
  if (selectedNode.value?.type !== 'column') return
  const span = selectedNode.value.span || 24
  selectedNode.value.grid ||= { span, xs: 24, sm: span, md: span, lg: span }
  selectedNode.value.grid.span = span
  syncLayoutJson()
}

function bindingSummary(node) {
  const binding = bindingForNode(node)
  if (!binding) return node.bindingId
  return `${binding.dataSourceKey}${binding.fieldId ? ` · ${bindingFieldText(binding)}` : ''}`
}

function bindingSourceText(binding) {
  return binding?.dataSourceKey || sourceKeyById(form.dataSources, binding?.dataSourceId) || '-'
}

function bindingFieldText(binding) {
  const source = form.dataSources.find(item => item.sourceKey === binding?.dataSourceKey) || form.dataSources.find(item => item.id === binding?.dataSourceId)
  return capabilityForSource(source)?.fields?.find(item => item.fieldId === binding?.fieldId)?.name || (binding?.fieldId ? `字段 ${binding.fieldId}` : '-')
}

function bindingNeedsField(role) {
  return [2, 4, 6, 7].includes(role)
}

function compatibleShapes(role) {
  if ([3, 4].includes(role)) return [2, 3]
  if ([2, 5, 6].includes(role)) return [1]
  if (role === 7) return [2, 4]
  return [1, 2, 3, 4, 5]
}

function defaultRoleForNode(type) {
  return ({ table: 3, 'table-column': 4, form: 5, 'form-field': 2, detail: 6, 'detail-field': 6 })[type] || 1
}

function controlOptionsForRole(role) {
  if (role === 4) return ['text', 'tag', 'link', 'date', 'datetime', 'money', 'percent', 'image', 'switch']
  if ([2, 5].includes(role)) return ['input', 'textarea', 'number', 'select', 'date', 'datetime', 'switch', 'upload', 'editor']
  if (role === 6) return ['text', 'tag', 'link', 'date', 'datetime', 'money', 'percent', 'image']
  return ['table', 'form', 'description', 'query-form', 'pagination']
}

function inferSemanticControl(field, role) {
  if (!field) return '-'
  const name = String(field.name || '').toLowerCase()
  const type = String(field.dataType || '').toLowerCase()
  const displayRole = [4, 6].includes(role)
  if (/image|avatar|photo|logo|cover/.test(name)) return displayRole ? 'image' : 'upload'
  if (/url|link|website/.test(name)) return displayRole ? 'link' : 'input'
  if (/status|state|type|category/.test(name)) return displayRole ? 'tag' : 'select'
  if (/bool|boolean/.test(type) || /enabled|active|flag/.test(name)) return 'switch'
  if (/date|time/.test(type) || /date|time|at$/.test(name)) return /time/.test(type + name) ? 'datetime' : 'date'
  if (/decimal|double|float|money/.test(type) || /amount|price|cost|total/.test(name)) return displayRole ? 'money' : 'number'
  if (/int|long|short/.test(type)) return displayRole ? 'text' : 'number'
  if (/remark|description|content|note/.test(name)) return displayRole ? 'text' : 'textarea'
  return displayRole ? 'text' : 'input'
}

function displayTemplateIdFromProps(propsJson) {
  try {
    const props = JSON.parse(propsJson || '{}')
    return props.displayTemplateId || props.templateId || undefined
  } catch { return undefined }
}

function propsJsonWithDisplayTemplate(propsJson, displayTemplateId) {
  let props = {}
  try { props = JSON.parse(propsJson || '{}') } catch { props = {} }
  delete props.templateId
  if (displayTemplateId) props.displayTemplateId = displayTemplateId
  else delete props.displayTemplateId
  return JSON.stringify(props)
}

async function openViewBuilder() {
  if (!form.projectId) return ElMessage.warning('请先选择项目')
  if (!formModules.value.length) await loadModulesForForm()
  Object.assign(viewBuilderForm, defaultViewBuilderForm(), {
    projectId: form.projectId,
    moduleId: form.moduleId || formModules.value[0]?.id
  })
  viewBuilderMode.value = 'create'
  const [catalog, views] = await Promise.all([
    getViewBuilderCatalog(form.projectId),
    getExistingViews(form.projectId)
  ])
  viewBuilderCatalog.value = catalog || { entities: [], joinCandidates: [] }
  existingViews.value = views || []
  viewBuilderVisible.value = true
  await nextTick()
  viewFieldTreeRef.value?.setCheckedKeys([])
}

function syncViewSelections(_node, state) {
  const grouped = new Map()
  ;(state?.checkedNodes || []).filter(item => item.fieldId).forEach(item => {
    if (!grouped.has(item.entityId)) grouped.set(item.entityId, [])
    grouped.get(item.entityId).push(item.fieldId)
  })
  viewBuilderForm.selections = [...grouped.entries()].map(([entityId, fieldIds]) => ({ entityId, fieldIds }))
  const selectedEntityIds = new Set(viewBuilderForm.selections.map(item => item.entityId))
  viewBuilderForm.joinKeys = viewBuilderForm.joinKeys.filter(key => {
    const join = viewJoinOptions.value.find(item => item.key === key)
    return join && selectedEntityIds.has(join.sourceEntityId) && selectedEntityIds.has(join.targetEntityId)
  })
}

function joinCandidateKey(item, index) {
  return item.relationId
    ? `relation:${item.relationId}`
    : `join:${item.sourceEntityId}:${item.sourceField}:${item.targetEntityId}:${item.targetField}:${index}`
}

function entityCatalogName(entityId) {
  const entity = (viewBuilderCatalog.value.entities || []).find(item => item.entityId === entityId)
  return entity?.displayName || entity?.entityName || entityId
}

function selectedViewJoins() {
  return viewBuilderForm.joinKeys.map(key => viewJoinOptions.value.find(item => item.key === key)).filter(Boolean).map(item => ({
    sourceEntityId: item.sourceEntityId,
    targetEntityId: item.targetEntityId,
    sourceField: item.sourceField,
    targetField: item.targetField,
    joinType: item.defaultJoinType || 'Left',
    relationName: item.relationName || null,
    isInferred: Boolean(item.isInferred)
  }))
}

async function handlePreviewViewSql() {
  if (!viewBuilderForm.selections.length) return ElMessage.warning('请至少勾选一个实体字段')
  previewingView.value = true
  try {
    const result = await previewViewSql({
      projectId: viewBuilderForm.projectId,
      entities: viewBuilderForm.selections,
      joins: selectedViewJoins(),
      includeInferredJoins: viewBuilderForm.joinKeys.length === 0
    })
    viewBuilderForm.sql = result?.sql || ''
    viewBuilderForm.expectedColumns = (result?.selectedFields || []).map(item => item.alias)
    ElMessage.success('SQL 初稿已生成，可继续自由编辑')
    return result
  } finally {
    previewingView.value = false
  }
}

async function handleCreateView() {
  if (!validateViewRegistration()) return
  if (!viewBuilderForm.sql.trim()) {
    const preview = await handlePreviewViewSql()
    if (!preview?.sql) return
  }
  creatingView.value = true
  try {
    const created = await createProjectView({
      projectId: viewBuilderForm.projectId,
      viewName: viewBuilderForm.viewName.trim(),
      sql: viewBuilderForm.sql.trim(),
      expectedColumns: viewBuilderForm.expectedColumns || []
    })
    if (created?.requiresColumnConfirmation) {
      const differences = [
        created.missingColumns?.length ? `缺少列：${created.missingColumns.join(', ')}` : '',
        created.extraColumns?.length ? `新增列：${created.extraColumns.join(', ')}` : ''
      ].filter(Boolean).join('；')
      await ElMessageBox.confirm(
        `数据库实际列与字段选择结果不一致（${differences}）。视图已经创建，是否按数据库实际列继续注册只读实体？`,
        '确认视图列',
        { type: 'warning' }
      )
    }
    const registered = await registerReadOnlyViewEntity(buildViewRegistrationPayload())
    await completeViewRegistration(registered)
  } finally {
    creatingView.value = false
  }
}

async function handleImportExistingView() {
  if (!validateViewRegistration()) return
  creatingView.value = true
  try {
    const registered = await importExistingView(buildViewRegistrationPayload())
    await completeViewRegistration(registered)
  } finally {
    creatingView.value = false
  }
}

function validateViewRegistration() {
  if (!viewBuilderForm.projectId || !viewBuilderForm.moduleId) {
    ElMessage.warning('请选择目标项目和模块')
    return false
  }
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(viewBuilderForm.viewName.trim())) {
    ElMessage.warning('视图名只能包含字母、数字和下划线，且不能以数字开头')
    return false
  }
  if (!/^[A-Za-z][A-Za-z0-9]*$/.test(viewBuilderForm.entityName.trim())) {
    ElMessage.warning('实体名必须是字母开头的 PascalCase 标识符')
    return false
  }
  if (!viewBuilderForm.displayName.trim()) {
    ElMessage.warning('请输入实体显示名')
    return false
  }
  return true
}

function buildViewRegistrationPayload() {
  return {
    projectId: viewBuilderForm.projectId,
    moduleId: viewBuilderForm.moduleId,
    viewName: viewBuilderForm.viewName.trim(),
    entityName: viewBuilderForm.entityName.trim(),
    displayName: viewBuilderForm.displayName.trim(),
    keyColumn: viewBuilderForm.keyColumn.trim() || null,
    generateFrontend: true,
    dataSourceType: 2
  }
}

async function completeViewRegistration(registered) {
  await loadModulesForForm()
  appendRegisteredViewSource(registered)
  viewBuilderVisible.value = false
  ElMessage.success(`只读实体 ${registered?.entityName || viewBuilderForm.entityName} 已注册，并加入当前页面数据源；保存页面后生效`)
}

function appendRegisteredViewSource(registered) {
  if (!registered?.entityId || form.dataSources.some(item => item.entityId === registered.entityId)) return
  const capability = capabilities.value.find(item => item.entityId === registered.entityId)
  const method = (capability?.methods || []).find(item => item.supportedShapes?.includes(3))
  if (!capability || !method) return
  const baseKey = `${lowerCamel(registered.entityName)}List`
  let sourceKey = baseKey
  let suffix = 2
  const usedKeys = new Set(form.dataSources.map(item => String(item.sourceKey).toLowerCase()))
  while (usedKeys.has(sourceKey.toLowerCase())) sourceKey = `${baseKey}${suffix++}`
  form.dataSources.push(normalizeSource({
    sourceKey,
    sourceType: 1,
    entityId: registered.entityId,
    serviceName: capability.serviceName,
    methodName: method.methodName,
    dtoName: method.dtoName,
    dataShape: 3,
    paramsJson: '{}'
  }))
}

function onExistingViewChange(viewName) {
  const entityName = toPascalIdentifier(String(viewName || '').replace(/^vw_/i, ''))
  if (!viewBuilderForm.entityName) viewBuilderForm.entityName = entityName
  if (!viewBuilderForm.displayName) viewBuilderForm.displayName = entityName
}

function toPascalIdentifier(value) {
  return String(value || '').split(/[^A-Za-z0-9]+/).filter(Boolean)
    .map(item => item.charAt(0).toUpperCase() + item.slice(1)).join('')
}

function lowerCamel(value) {
  const normalized = toPascalIdentifier(value)
  return normalized ? normalized.charAt(0).toLowerCase() + normalized.slice(1) : 'view'
}

async function handleGenerate(row) {
  await runGeneration(row.id, row.projectId)
}

async function runGeneration(pageId, projectId, incremental = true) {
  generatingPageId.value = pageId
  generationPageId.value = pageId
  generationProjectId.value = projectId
  Object.assign(generationResult, defaultGenerationResult())
  try {
    const result = await generate(pageId, projectId, incremental)
    Object.assign(generationResult, defaultGenerationResult(), result || {})
    generationVisible.value = true
    if (result?.hasConflicts) ElMessage.warning('页面已更新，但部分生成区域保留了外部修改，请处理冲突')
    else if (result?.success) ElMessage.success('复杂页面已生成')
    await loadPages()
  } catch (error) {
    generationResult.error = error?.message || '生成失败'
    generationResult.diagnosticOutput = error?.diagnosticOutput || ''
    generationVisible.value = true
  } finally {
    generatingPageId.value = 0
  }
}

async function handleDiscardConflicts() {
  await ElMessageBox.confirm(
    '这会放弃冲突区域中的外部修改，并使用当前页面定义完整重建 Vue 文件。是否继续？',
    '放弃外部修改',
    { type: 'warning' }
  )
  generationVisible.value = false
  await runGeneration(generationPageId.value, generationProjectId.value, false)
}

async function handleManualMergeConflict() {
  const path = generationResult.vuePath || ''
  try {
    if (path && navigator.clipboard) await navigator.clipboard.writeText(path)
    ElMessage.info(path ? `Vue 路径已复制，请手动合并后再次生成：${path}` : '请在生成目录中手动合并冲突区域后再次生成')
  } catch {
    ElMessage.info(`请手动合并后再次生成：${path || generationResult.outputDirectory}`)
  }
}

async function handleReimportConflicts() {
  const inspection = await inspectExternalChange(generationPageId.value)
  if (!inspection?.changed) return ElMessage.info('当前 Vue 文件没有可重新导入的外部差异')
  if (!inspection.canImport) return ElMessage.warning(inspection.error || '当前冲突无法安全重新导入')
  await ElMessageBox.confirm(
    '重新导入会把当前 Vue 模板差异写回组件树定义，并保留备份。导入后请打开设计器检查。',
    '重新导入冲突区域',
    { type: 'warning' }
  )
  const result = await importExternalTemplate(generationPageId.value, true)
  if (result?.error) return ElMessage.error(result.error)
  generationVisible.value = false
  ElMessage.success('冲突模板已重新导入，请检查设计器定义后再次生成')
  await loadPages()
}

async function handleBuildGeneratedProject() {
  building.value = true
  generationResult.buildMessage = ''
  generationResult.buildOutput = ''
  try {
    const result = await buildProject(generationProjectId.value)
    generationResult.buildSuccess = result?.success !== false
    generationResult.buildMessage = result?.message || '目标项目构建完成'
    generationResult.buildOutput = result?.output || ''
  } catch (error) {
    generationResult.buildSuccess = false
    generationResult.buildMessage = error?.message || '目标项目构建失败'
    generationResult.buildOutput = error?.diagnosticOutput || ''
  } finally {
    building.value = false
  }
}

async function handleDelete(row) {
  await ElMessageBox.confirm(`确定删除页面“${row.pageName}”吗？`, '提示', { type: 'warning' })
  await deleteById(row.id)
  ElMessage.success('删除成功')
  await loadPages()
}

function handleMore(command, row) {
  if (command === 'copy') handleCopy(row)
  else if (command === 'sync') openSyncPreview(row)
  else if (command === 'external') handleExternalChange(row)
  else if (command === 'delete') handleDelete(row)
}

async function handleCopy(row) {
  const { value: pageKey } = await ElMessageBox.prompt('请输入新页面的 PageKey', `复制“${row.pageName}”`, {
    inputValue: `${row.pageKey}-copy`,
    inputPattern: /^[A-Za-z][A-Za-z0-9_-]*$/,
    inputErrorMessage: 'PageKey 格式无效'
  })
  await copy(row.id, pageKey, `${row.pageName} - 副本`)
  ElMessage.success('页面已复制')
  await loadPages()
}

async function openSyncPreview(row) {
  syncPageId.value = row.id
  const preview = await previewSynchronization(row.id)
  Object.assign(syncPreview, preview || { requiresSync: false, changes: [] })
  syncVisible.value = true
}

async function handleSynchronize() {
  synchronizing.value = true
  try {
    const result = await synchronize(syncPageId.value)
    if (!result?.success) return ElMessage.error(result?.error || '同步失败')
    ElMessage.success(`同步完成：新增 ${result.addedBindings || 0} 个绑定，失效 ${result.invalidBindings || 0} 个绑定`)
    syncVisible.value = false
    await loadPages()
  } finally { synchronizing.value = false }
}

async function handleProjectSynchronize() {
  if (!query.projectId) return
  synchronizing.value = true
  try {
    const result = await synchronizeProject(query.projectId)
    const succeeded = result?.filter(item => item.success).length || 0
    const failed = result?.length - succeeded || 0
    if (failed > 0) ElMessage.warning(`项目同步完成：成功 ${succeeded}，失败 ${failed}`)
    else ElMessage.success(`项目同步完成：共处理 ${succeeded} 个页面`)
    await loadPages()
  } finally { synchronizing.value = false }
}

async function handleExternalChange(row) {
  const inspection = await inspectExternalChange(row.id)
  if (!inspection?.changed) return ElMessage.success('生成的 Vue 文件没有外部改动')
  if (!inspection.canImport) return ElMessage.warning(inspection.error || '当前外部改动无法安全导入')
  await ElMessageBox.confirm(
    `检测到 Vue 外部改动：匹配 ${inspection.matchedNodeCount || 0} 个节点，新增 ${inspection.externalNodeCount || 0} 个节点。导入前会自动备份，是否继续？`,
    '导入外部 Vue 改动',
    { type: 'warning' }
  )
  const result = await importExternalTemplate(row.id, true)
  if (result?.error) ElMessage.error(result.error)
  else {
    ElMessage.success('外部模板已导入树形定义，请打开设计器检查并重新生成')
    await loadPages()
  }
}

function nodeTypeText(type) {
  return ({ page: '页面', row: '行', column: '列', card: '卡片', search: '查询区', table: '表格', 'table-column': '表格列', form: '表单', 'form-field': '表单字段', detail: '详情', 'detail-field': '详情字段', pagination: '分页', button: '按钮', dialog: '对话框', text: '文本' })[type] || type
}
function originText(origin) { return ({ 1: '生成', 2: '用户', 3: '外部导入' })[origin] || '用户' }
function statusText(status) { return ({ 1: '草稿', 2: '已生成', 3: '待同步', 4: '冲突', 5: '生成失败' })[status] || '未知' }
function statusType(status) { return ({ 1: 'info', 2: 'success', 3: 'warning', 4: 'danger', 5: 'danger' })[status] || 'info' }
function bindingStatusText(status) { return ({ 1: '正常', 2: '失效', 3: '需确认' })[status] || '正常' }
function changeTypeText(type) { return ({ Added: '新增字段', Deleted: '删除字段', Renamed: '字段重命名', TypeOrControlChanged: '类型或控件变化', SchemaChanged: '实体结构变化' })[type] || type }

onMounted(async () => {
  presets.value = await getLayoutPresets() || []
  await loadProjects()
})
</script>

<style scoped>
.complex-page-manager { min-width: 1000px; }
.filter-card { margin-bottom: 16px; }
.toolbar, .section-toolbar, .tree-toolbar, .dialog-footer { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
.toolbar { margin-bottom: 16px; }
.hint, .panel-tip, .section-toolbar p, .inline-tip { color: #909399; font-size: 12px; }
.section-toolbar h3, .section-toolbar p { margin: 0; }
.section-toolbar p { margin-top: 6px; }
.designer-steps { margin: 0 24px 18px; }
.designer-body { min-height: 620px; height: calc(100vh - 190px); overflow: auto; padding: 0 4px; }
.basic-form { max-width: 1100px; margin: 0 auto; }
.source-step { padding: 0 8px; }
.section-toolbar { margin-bottom: 14px; }
.layout-designer { display: grid; grid-template-columns: 240px minmax(560px, 1fr) 320px; min-height: 620px; height: calc(100vh - 200px); border: 1px solid #dfe6ef; border-radius: 8px; overflow: hidden; background: #fff; }
.palette-panel, .property-panel { min-width: 0; padding: 14px; overflow-y: auto; background: #fafbfc; }
.palette-panel { border-right: 1px solid #ebeef5; }
.property-panel { border-left: 1px solid #ebeef5; }
.palette-panel h3, .property-panel h3 { margin: 0; }
.panel-heading, .property-heading { margin-bottom: 10px; }
.property-heading { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
.property-heading span { color: #909399; font-size: 12px; }
.palette-collapse { border-top: 0; }
.palette-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.palette-item { display: flex; min-width: 0; align-items: center; gap: 7px; padding: 9px; border: 1px solid #dcdfe6; border-radius: 5px; background: #fff; color: #606266; cursor: grab; font-size: 12px; text-align: left; }
.palette-item:hover { border-color: #409eff; color: #409eff; background: #ecf5ff; }
.palette-icon { width: 20px; color: #409eff; font-size: 15px; text-align: center; }
.source-field-list { display: grid; gap: 12px; }
.source-field-group { overflow: hidden; border: 1px solid #e4e7ed; border-radius: 6px; background: #fff; }
.source-field-title { display: flex; justify-content: space-between; gap: 8px; padding: 8px 9px; border-bottom: 1px solid #ebeef5; background: #f5f7fa; font-size: 11px; }
.source-field-title strong { color: #303133; }
.source-field-title span { min-width: 0; overflow: hidden; color: #909399; text-overflow: ellipsis; white-space: nowrap; }
.field-palette-item { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 8px; padding: 7px 9px; border: 0; border-bottom: 1px solid #f0f2f5; background: #fff; color: #606266; cursor: grab; font-size: 12px; text-align: left; }
.field-palette-item:last-child { border-bottom: 0; }
.field-palette-item:hover { color: #409eff; background: #f0f7ff; }
.field-palette-item small { color: #a8abb2; }
.source-empty { padding: 10px; color: #a8abb2; font-size: 11px; text-align: center; }
.visual-panel { min-width: 0; display: flex; flex-direction: column; overflow: hidden; background: #f3f6fa; }
.visual-toolbar { display: flex; min-height: 50px; align-items: center; justify-content: space-between; gap: 12px; padding: 8px 12px; border-bottom: 1px solid #dfe6ef; background: #fff; }
.visual-toolbar-group { display: flex; align-items: center; gap: 10px; }
.canvas-panel, .tree-panel { min-height: 0; flex: 1; overflow: auto; }
.tree-panel { padding: 16px; background: #fff; }
.tree-toolbar { margin-bottom: 12px; }
.tree-node { width: 100%; display: flex; align-items: center; justify-content: space-between; gap: 12px; padding-right: 8px; }
.binding-badge { max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #409eff; font-size: 12px; }
.property-tabs { margin-top: 8px; }
.responsive-grid-editor { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.responsive-grid-editor label { display: grid; gap: 4px; color: #909399; font-size: 11px; }
.responsive-grid-editor :deep(.el-input-number) { width: 100%; }
.event-toolbar, .event-card-header, .action-card-header { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.event-toolbar { margin-bottom: 10px; }
.event-list, .action-list { display: grid; gap: 10px; }
.event-card { padding: 10px; border: 1px solid #d9ecff; border-radius: 6px; background: #f5faff; }
.event-card-header :deep(.el-select) { min-width: 0; flex: 1; }
.action-card { padding: 9px; border: 1px solid #ebeef5; border-radius: 5px; background: #fff; }
.action-card-header { margin-bottom: 7px; color: #606266; font-size: 11px; }
.action-card-header .el-button { padding: 0 3px; }
.review-step { padding: 4px 8px; }
.json-review { margin-top: 16px; }
.json-review pre { max-height: 340px; overflow: auto; margin: 0; padding: 12px; background: #f5f7fa; font-size: 12px; }
.dialog-footer { width: 100%; }
.binding-alert { margin-bottom: 16px; }
.inline-tip { margin-left: 10px; }
.sync-table { margin-top: 16px; }
.generation-details, .generation-files, .build-result { margin-top: 16px; }
.generation-diagnostic, .build-result pre { max-height: 320px; overflow: auto; padding: 12px; border-radius: 4px; background: #1f2329; color: #e5eaf3; font-size: 12px; white-space: pre-wrap; }
.option-description { float: right; max-width: 260px; overflow: hidden; color: #8492a6; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.option-hint { float: right; margin-left: 20px; color: #8492a6; font-size: 12px; }
.source-meta { margin-top: 4px; color: #909399; font-size: 12px; line-height: 1.4; }
.view-import-form { max-width: 720px; margin: 18px auto 0; }
@media (max-width: 1280px) {
  .layout-designer { grid-template-columns: 200px minmax(440px, 1fr) 280px; }
  .palette-grid { grid-template-columns: 1fr; }
}
</style>
