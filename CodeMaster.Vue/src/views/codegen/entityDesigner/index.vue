<template>
  <div ref="designerRootRef" class="designer-root" style="position:relative">
    <!-- 工具栏 -->
    <div class="toolbar">
      <el-button @click="handleBack" size="small">← 返回</el-button>
      <span style="font-weight:600">{{ entityName }} — {{ pageLabel }}</span>
      <el-switch v-model="previewMode" active-text="预览" inactive-text="编辑" size="small" />
      <el-button size="small" @click="showTagPalette=!showTagPalette">🧩 控件</el-button>
      <el-button size="small" @click="handleUndo" :disabled="undoStack.length===0">↩ 撤销</el-button>
      <el-button size="small" @click="handleRedo" :disabled="redoStack.length===0">↪ 重做</el-button>
      <el-button size="small" @click="showScriptDialog = true">📜 Script</el-button>
      <el-button size="small" @click="showSelectedScriptDialog = true">🎯 选中 Script</el-button>
      <el-button size="small" :type="translationEnabled ? 'primary' : ''" :loading="translationLoading" @click="toggleTranslationPreview">
        🌐 {{ translationEnabled ? '显示原文' : '翻译预览' }}
      </el-button>
      <span v-if="translationKeyCount" class="translation-status">
        已识别 {{ translationKeyCount }} 个翻译键<span v-if="missingTranslationKeys.length">，缺少 {{ missingTranslationKeys.length }} 个</span>
      </span>
      <el-button type="primary" size="small" @click="handleSave" :loading="saving">保存</el-button>
    </div>

    <div class="main-area" v-loading="loading">
      <div v-if="error" style="color:red">{{ error }}</div>
      <!-- 标签面板 -->
      <div class="tag-palette" v-if="showTagPalette">
        <div class="palette-header">
          <span>控件库</span>
          <el-input v-model="tagFilter" size="small" placeholder="搜索" clearable style="width:100%;margin-top:4px" />
        </div>
        <div class="palette-list">
          <div v-for="t in filteredTags" :key="t" class="palette-item" draggable="true"
            @dragstart="onPaletteDragStart($event, t)"
            @dragend="onPaletteDragEnd"
            @click="addTagToTree(t)">
            {{ t }}
          </div>
        </div>
      </div>
      <!-- 左侧树形结构 -->
      <div class="tree-panel">
        <div class="tree-header">
          <span>组件树</span>
          <el-button link size="small" @click="expandAll">展开</el-button>
          <el-button link size="small" @click="collapseAll">折叠</el-button>
        </div>
        <el-tree
          ref="treeRef"
          :data="treeData"
          node-key="id"
          :expand-on-click-node="false"
          :highlight-current="true"
          :current-node-key="selectedId"
          draggable
          :allow-drag="allowDrag"
          :allow-drop="allowDrop"
          @node-click="onTreeNodeClick"
          @node-drag-start="onTreeDragStart"
          @node-drag-end="onTreeDragEnd"
          @node-drop="onTreeNodeDrop"
          default-expand-all
        >
          <template #default="{ node, data }">
            <span class="tree-node" :class="{ 'is-text': data.tag==='text', 'is-slot': data.tag==='slot' }"
              @contextmenu.prevent.stop="onTreeContextMenu($event, data)">
                <span v-if="data.tag==='text'" class="tree-text-content"
                  @dblclick.stop="startEditText(data)"
                  :title="displayDesignText(data.content)">{{ displayDesignText(data.label || data.content) }}</span>
              <template v-else>
                <span class="tree-tag">{{ data.tag }}</span>
                <span v-if="data.genId" class="tree-genid">{{ data.genId.replace('gen_','') }}</span>
              </template>
            </span>
          </template>
        </el-tree>
      </div>

      <!-- 右键菜单遮罩 -->
      <teleport to="body">
        <div v-if="ctxMenu.visible" class="ctx-overlay" @click="hideCtxMenu" @contextmenu.prevent="hideCtxMenu"></div>
        <div v-if="ctxMenu.visible" class="ctx-menu" :style="{ left: ctxMenu.x+'px', top: ctxMenu.y+'px' }">
          <div class="ctx-item" @click.stop="ctxCopy"><span>📋</span> 复制</div>
          <div class="ctx-item" :class="{ disabled: !ctxClipboard }" @click.stop="ctxPaste"><span>📄</span> 粘贴</div>
          <div class="ctx-sep"></div>
          <div class="ctx-item" @click.stop="ctxInsertBefore"><span>⬆</span> 在此前插入</div>
          <div class="ctx-item" @click.stop="ctxInsertAfter"><span>⬇</span> 在此后插入</div>
          <div class="ctx-item" @click.stop="ctxMoveUp"><span>↑</span> 上移</div>
          <div class="ctx-item" @click.stop="ctxMoveDown"><span>↓</span> 下移</div>
          <div class="ctx-sep"></div>
          <div class="ctx-item danger" @click.stop="ctxDelete"><span>🗑</span> 删除</div>
        </div>
      </teleport>

      <!-- 画布 -->
      <div
        ref="canvasAreaRef"
        class="canvas-area"
        @scroll.passive="scheduleRefresh"
        @dragover.prevent="onCanvasDragOver"
        @drop.prevent.stop="onCanvasDrop"
      >
        <DesignCanvas v-if="tree.length" :list="tree" :selectedId="selectedId"
          :translation-map="translationMap" :translation-enabled="translationEnabled" @select="onSelect" />
      </div>
      <!-- 属性栏 -->
      <aside v-if="drawerVisible" class="property-sidebar">
        <div class="prop-panel" v-if="selectedNode" v-loading="panelLoading">
          <div class="panel-header">
            <el-select v-model="selectedNode.tag" size="small" filterable allow-create @change="onTagChange" style="width:140px">
              <el-option v-for="t in ALL_TAGS" :key="t" :label="t" :value="t" />
            </el-select>
            <span v-if="panelData" class="field-info">{{ panelData.componentName }}</span>
            <div class="panel-actions">
              <el-button size="small" type="danger" @click="handleDelete">删除</el-button>
              <el-button :icon="Close" size="small" circle @click="closePropertyPanel" />
            </div>
          </div>
          <!-- Dialog 显示开关 -->
          <div v-if="selectedNode.tag==='el-dialog'" style="padding:6px 0;display:flex;align-items:center;gap:8px">
            <el-switch v-model="dialogVisible" size="small" @change="toggleDialogVisible" />
            <span style="font-size:12px;color:#666">设计中显示</span>
          </div>

          <!-- 文本节点编辑 -->
          <template v-if="selectedNode && selectedNode.tag === 'text'">
            <div style="padding:12px">
              <div style="font-weight:600;margin-bottom:8px">文本内容</div>
              <el-input v-model="textEditContent" type="textarea" :rows="6" placeholder="输入文本内容" @change="onTextContentChange" />
            </div>
          </template>

          <!-- 智能面板（有 genId 的字段组件，显示所有可选属性+勾选） -->
          <template v-if="panelData">
            <el-tabs v-model="propTab" type="border-card">
              <el-tab-pane :label="`属性 ${panelData.properties.filter(p=>p.isActive).length}/${panelData.properties.length}`" name="props">
                <div class="tab-toolbar">
                  <el-checkbox v-model="showOnlyActive" size="small">仅启用</el-checkbox>
                  <el-checkbox v-model="showAdvanced" size="small">高级</el-checkbox>
                </div>
                <div class="prop-list">
                  <div v-for="prop in filteredProps" :key="prop.propName" class="prop-row" :class="{ inactive: !prop.isActive }">
                    <el-checkbox :model-value="prop.isActive" @change="v => { prop.isActive = v; applyPropToNode(prop); onTreeChange() }" size="small" />
                    <el-input v-if="prop.isCustom" v-model="prop.propName" size="small" class="prop-name-input" @change="onCustomPropRename(prop)" />
                    <span v-else class="prop-name" :title="prop.description">{{ prop.propName }}</span>
                    <template v-if="prop.isActive">
                      <el-select v-if="prop.valueType === 'boolean'" model-value="true" size="small" class="prop-value" disabled />
                      <el-select v-else-if="prop.enumValues" v-model="prop.currentValue" size="small" class="prop-value" @change="() => { applyPropToNode(prop); onTreeChange() }" clearable>
                        <el-option v-for="ev in enumOptions(prop)" :key="ev" :label="ev" :value="ev" />
                      </el-select>
                      <el-input v-else v-model="prop.currentValue" size="small" class="prop-value" @input="() => { applyPropToNode(prop); onTreeChange() }" :placeholder="prop.defaultValue" />
                      <el-dropdown trigger="click" size="small" @command="cmd => { prop.valueType = cmd; if(cmd==='boolean')prop.currentValue=null; applyPropToNode(prop); onTreeChange() }">
                          <el-button size="small" class="mode-btn">{{ modeIcon(prop.valueType) }}</el-button>
                        <template #dropdown>
                          <el-dropdown-menu>
                            <el-dropdown-item command="static">
                              <span style="font-size:12px">📝 static <span style="color:#999"> prop="val"</span></span>
                            </el-dropdown-item>
                            <el-dropdown-item command="bind">
                              <span style="font-size:12px">🔗 :bind <span style="color:#999"> :prop="expr"</span></span>
                            </el-dropdown-item>
                            <el-dropdown-item command="boolean">
                              <span style="font-size:12px">⚡ boolean <span style="color:#999"> prop</span></span>
                            </el-dropdown-item>
                          </el-dropdown-menu>
                        </template>
                      </el-dropdown>
                    </template>
                    <el-button v-if="prop.isCustom" :icon="Delete" size="small" circle title="删除自定义属性" @click="removeCustomProp(prop)" />
                  </div>
                </div>
                <div class="custom-add">
                  <el-input v-model="newPropName" size="small" placeholder="自定义属性名，如 data-role" @keyup.enter="addCustomProp" />
                  <el-button size="small" type="primary" @click="addCustomProp">+ 属性</el-button>
                </div>
              </el-tab-pane>
              <el-tab-pane :label="`指令 ${panelData.directives.filter(d=>d.isActive).length}/${panelData.directives.length}`" name="instructions">
                <div class="tab-toolbar">
                  <el-checkbox v-model="showOnlyActiveDir" size="small">仅启用</el-checkbox>
                </div>
                <div class="prop-list">
                  <div v-for="d in filteredDirectives" :key="d.directiveName" class="prop-row" :class="{ inactive: !d.isActive }">
                    <el-checkbox :model-value="d.isActive" @change="v => { d.isActive = v; applyDirectiveToNode(d); onTreeChange() }" size="small" />
                    <el-input v-if="d.isCustom" v-model="d.directiveName" size="small" class="prop-name-input" @change="onCustomDirectiveRename(d)" />
                    <span v-else class="prop-name" :title="d.description">{{ d.directiveName }}</span>
                    <template v-if="d.isActive && d.hasValue">
                      <el-input v-model="d.currentValue" size="small" class="prop-value" @input="() => { applyDirectiveToNode(d); onTreeChange() }" placeholder="expression" />
                    </template>
                    <el-button v-if="d.isCustom" :icon="Delete" size="small" circle title="删除自定义指令" @click="removeCustomDirective(d)" />
                  </div>
                </div>
                <div class="custom-add">
                  <el-input v-model="newDirName" size="small" placeholder="自定义指令名，如 v-my-dir" @keyup.enter="addCustomDirective" />
                  <el-button size="small" type="primary" @click="addCustomDirective">+ 指令</el-button>
                </div>
              </el-tab-pane>
              <el-tab-pane :label="`事件 ${panelData.events.filter(e=>e.isActive).length}/${panelData.events.length}`" name="events">
                <div class="tab-toolbar">
                  <el-checkbox v-model="showOnlyActiveEvt" size="small">仅启用</el-checkbox>
                </div>
                <div class="prop-list">
                  <div v-for="evt in filteredEvents" :key="evt.eventName" class="prop-row" :class="{ inactive: !evt.isActive }">
                    <el-checkbox :model-value="evt.isActive" @change="v => { evt.isActive = v; applyEventToNode(evt); onTreeChange() }" size="small" />
                    <el-input v-if="evt.isCustom" v-model="evt.eventName" size="small" class="prop-name-input" @change="onCustomEventRename(evt)" />
                    <span v-else class="prop-name" :title="evt.description">@{{ evt.eventName }}</span>
                    <template v-if="evt.isActive && !evt.isSingleActive">
                      <el-input v-model="evt.currentValue" size="small" class="prop-value" @input="() => { applyEventToNode(evt); onTreeChange() }" placeholder="handler" />
                    </template>
                    <template v-if="evt.isActive">
                      <el-button size="small" class="mode-btn" :type="evt.isSingleActive ? 'primary' : ''" @click="evt.isSingleActive = !evt.isSingleActive; applyEventToNode(evt); onTreeChange()">@</el-button>
                    </template>
                    <el-button v-if="evt.isCustom" :icon="Delete" size="small" circle title="删除自定义事件" @click="removeCustomEvent(evt)" />
                  </div>
                </div>
                <div class="custom-add">
                  <el-input v-model="newEvtName" size="small" placeholder="自定义事件名，如 row-click" @keyup.enter="addCustomEvent" />
                  <el-button size="small" type="primary" @click="addCustomEvent">+ 事件</el-button>
                </div>
              </el-tab-pane>
              <el-tab-pane :label="`插槽 ${panelData.slots.filter(s=>s.isActive).length}/${panelData.slots.length}`" name="slots">
                <div class="tab-toolbar">
                  <el-checkbox v-model="showOnlyActiveSlot" size="small">仅启用</el-checkbox>
                </div>
                <div class="prop-list">
                  <div v-for="s in filteredSlots" :key="s.slotName" class="prop-row" :class="{ inactive: !s.isActive }">
                    <el-checkbox :model-value="s.isActive" @change="v => setSlotActive(s, v)" size="small" />
                    <el-input v-if="s.isCustom" :model-value="s.slotName" size="small" class="prop-name-input" @change="v => onCustomSlotRename(s, v)" />
                    <span v-else class="prop-name" :title="s.description">#{{ s.slotName }}</span>
                    <el-input v-if="s.isActive" v-model="s.parameter" size="small" class="prop-value" placeholder="scope" @change="onSlotParamChange(s)" />
                    <el-button v-if="s.isCustom" :icon="Delete" size="small" circle title="删除自定义插槽" @click="removeCustomSlot(s)" />
                  </div>
                </div>
                <div class="custom-add">
                  <el-input v-model="newSlotName" size="small" placeholder="自定义插槽名，如 footer" @keyup.enter="addCustomSlot" />
                  <el-button size="small" type="primary" @click="addCustomSlot">+ 插槽</el-button>
                </div>
              </el-tab-pane>
            </el-tabs>
          </template>

          <!-- 降级面板（无 genId 的普通组件，原始编辑模式） -->
          <template v-else>
            <el-form size="small" label-width="50px">
              <el-form-item label="标签">
                <el-select v-model="selectedNode.tag" size="small" filterable allow-create @change="onTagChange" style="width:100%">
                  <el-option v-for="t in ALL_TAGS" :key="t" :label="t" :value="t" />
                </el-select>
              </el-form-item>
            </el-form>
            <el-tabs v-model="propTab" type="border-card">
              <el-tab-pane label="属性" name="props">
                <div v-for="(p,i) in (selectedNode.props||[])" :key="'p'+i" class="prop-row">
                  <el-input v-model="p.key" placeholder="key" size="small" style="width:70px" @change="onTreeChange" />
                  <el-input v-model="p.value" placeholder="value" size="small" style="flex:1" @change="onTreeChange" />
                  <el-checkbox v-model="p.isBind" size="small" @change="onTreeChange">:</el-checkbox>
                  <el-button :icon="Delete" size="small" circle @click="removeProp(i)" />
                </div>
                <el-button size="small" @click="addProp" style="width:100%">+ 属性</el-button>
              </el-tab-pane>
              <el-tab-pane label="事件" name="events">
                <div v-for="(e,i) in (selectedNode.events||[])" :key="'e'+i" class="prop-row">
                  <el-input v-model="e.name" placeholder="click" size="small" style="width:70px" @change="onTreeChange" />
                  <el-input v-model="e.body" placeholder="handler" size="small" style="flex:1" @change="onTreeChange" />
                  <el-button :icon="Delete" size="small" circle @click="removeEvent(i)" />
                </div>
                <el-button size="small" @click="addEvent" style="width:100%">+ 事件</el-button>
              </el-tab-pane>
              <el-tab-pane label="插槽" name="slots">
                <div v-for="(s,i) in (selectedNode.useSlots||[])" :key="'s'+i" class="prop-row">
                  <el-input v-model="s.name" placeholder="name" size="small" style="width:70px" @change="onTreeChange" />
                  <el-input v-model="s.parameter" placeholder="param" size="small" style="flex:1" @change="onTreeChange" />
                  <el-button :icon="Delete" size="small" circle @click="removeSlot(i)" />
                </div>
                <el-button size="small" @click="addSlot" style="width:100%">+ 插槽</el-button>
              </el-tab-pane>
              <el-tab-pane label="指令" name="instructions">
                <div v-for="(ins,i) in (selectedNode.instructions||[])" :key="'v'+i" class="prop-row">
                  <el-select v-model="ins.name" size="small" style="width:80px" filterable allow-create default-first-option @change="onTreeChange">
                    <el-option v-for="d in dirs" :key="d" :label="d" :value="d" />
                  </el-select>
                  <el-input v-model="ins.value" placeholder="expr" size="small" style="flex:1" @change="onTreeChange" />
                  <el-button :icon="Delete" size="small" circle @click="removeIns(i)" />
                </div>
                <el-button size="small" @click="addIns" style="width:100%">+ 指令</el-button>
              </el-tab-pane>
            </el-tabs>
          </template>
        </div>
      </aside>
    </div>
    <!-- 拖拽条 overlay（预览模式隐藏） -->
    <div class="drag-overlay" v-if="allHandles.length && !previewMode">
      <div v-if="dragOverId" class="drop-indicator" :style="indStyle">
        <span class="di di-before" @drop.prevent.stop="onDrop($event, dragOverId, 'before')" @dragover.prevent>前</span>
        <span class="di di-after" @drop.prevent.stop="onDrop($event, dragOverId, 'after')" @dragover.prevent>后</span>
        <span class="di di-inside" @drop.prevent.stop="onDrop($event, dragOverId, 'inside')" @dragover.prevent>内</span>
        <span class="di di-swap" @drop.prevent.stop="onDrop($event, dragOverId, 'swap')" @dragover.prevent>换</span>
      </div>

      <div v-for="h in allHandles" :key="h.id"
        class="drag-handle" :class="{ selected: h.id === selectedId, 'drag-over': dragOverId === h.id }"
        :style="{ left: h.x + 'px', top: h.y + 'px' }"
        draggable="true"
        @click.stop="onHandleClick(h.id)"
        @dragstart="onDragStart($event, h.id)"
        @dragover.prevent="onDragOver($event, h.id)"
        @dragleave="onDragLeave(h.id)"
        @drop.prevent.stop="onDrop($event, h.id, 'after')"
        @dragend="onDragEnd">{{ h.tag }}</div>
    </div>

    <!-- Script 查看/编辑对话框 -->
    <el-dialog v-model="showScriptDialog" title="页面 ScriptSection" width="800px" top="5vh">
      <div v-if="scriptLoading" v-loading="scriptLoading" style="min-height:200px"></div>
      <template v-else-if="pageScript">
        <!-- 主表/子表切换 -->
        <el-radio-group v-model="selectedScriptKey" size="small" style="margin-bottom:12px" @change="onScriptKeyChange">
          <el-radio-button value="">主表</el-radio-button>
          <el-radio-button v-for="(_, key) in childScripts" :key="key" :value="key">{{ key }}</el-radio-button>
        </el-radio-group>
        <el-collapse v-model="scriptActiveNames">
          <el-collapse-item title="Imports" name="imports">
            <div v-for="(imp, i) in editingPageScript.imports" :key="'imp'+i" class="script-item" style="display:flex;gap:4px;align-items:center">
              <el-select v-model="imp.mode" size="small" style="width:90px" @change="pageScriptDirty=true">
                <el-option label="named" value="named" /><el-option label="default" value="default" />
                <el-option label="namespace" value="namespace" /><el-option label="sideEffect" value="sideEffect" />
              </el-select>
              <el-input v-model="imp.names" size="small" placeholder="ref, reactive" style="flex:1" @change="pageScriptDirty=true" />
              <span style="font-size:11px;color:#999">from</span>
              <el-input v-model="imp.from" size="small" placeholder="vue" style="flex:1" @change="pageScriptDirty=true" />
              <el-button size="small" type="danger" :icon="Delete" circle @click="editingPageScript.imports.splice(i,1);pageScriptDirty=true" />
            </div>
            <div v-if="!editingPageScript.imports?.length" class="script-empty">无</div>
            <el-button size="small" @click="editingPageScript.imports.push({mode:'named',names:'',from:''});pageScriptDirty=true" style="width:100%">+ Import</el-button>
          </el-collapse-item>
          <el-collapse-item title="Refs" name="refs">
            <div v-for="(r, i) in editingPageScript.refs" :key="'ref'+i" class="script-item" style="display:flex;gap:4px;align-items:center">
              <span style="font-size:11px">const</span>
              <el-input v-model="r.name" size="small" placeholder="myRef" style="width:100px" @change="pageScriptDirty=true" />
              <span style="font-size:11px">= ref(</span>
              <el-input v-model="r.value" size="small" placeholder="null" style="flex:1" @change="pageScriptDirty=true" />
              <span style="font-size:11px">)</span>
              <el-button size="small" type="danger" :icon="Delete" circle @click="editingPageScript.refs.splice(i,1);pageScriptDirty=true" />
            </div>
            <div v-if="!editingPageScript.refs?.length" class="script-empty">无</div>
            <el-button size="small" @click="editingPageScript.refs.push({name:'',value:'null'});pageScriptDirty=true" style="width:100%">+ Ref</el-button>
          </el-collapse-item>
          <el-collapse-item title="Reactives" name="reactives">
            <div v-for="(r, i) in editingPageScript.reactives" :key="'re'+i" class="script-item">
              <code>const {{ r.name }} = reactive({ {{ Object.entries(r.fields||{}).map(([k,v])=>k+': '+v).join(', ') }} })</code>
            </div>
            <div v-if="!editingPageScript.reactives?.length" class="script-empty">无</div>
          </el-collapse-item>
          <el-collapse-item title="Functions" name="functions">
            <div v-for="(fn, i) in editingPageScript.functions" :key="'fn'+i" class="script-item">
              <div style="display:flex;gap:4px;align-items:center;margin-bottom:4px">
                <el-checkbox v-model="fn.async" size="small" @change="pageScriptDirty=true">async</el-checkbox>
                <el-input v-model="fn.name" size="small" placeholder="functionName" style="width:140px;font-weight:600" @change="pageScriptDirty=true" />
                <span>(</span><el-input v-model="fn.params" size="small" placeholder="params" style="width:100px" @change="pageScriptDirty=true" /><span>)</span>
                <el-button size="small" type="danger" :icon="Delete" circle @click="editingPageScript.functions.splice(i,1);pageScriptDirty=true" />
              </div>
              <el-input v-model="fn.body" type="textarea" :rows="6" style="font-family:monospace;font-size:12px" @change="pageScriptDirty=true" />
            </div>
            <div v-if="!editingPageScript.functions?.length" class="script-empty">无</div>
            <el-button size="small" @click="editingPageScript.functions.push({name:'newFunction',params:'',async:false,body:''});pageScriptDirty=true" style="width:100%">+ Function</el-button>
          </el-collapse-item>
          <el-collapse-item title="Hooks" name="hooks">
            <div v-for="(h, i) in editingPageScript.hooks" :key="'hk'+i" class="script-item">
              <div style="display:flex;gap:4px;align-items:center;margin-bottom:4px">
                <el-select v-model="h.name" size="small" style="width:140px" @change="pageScriptDirty=true">
                  <el-option label="onMounted" value="onMounted" /><el-option label="onUnmounted" value="onUnmounted" />
                  <el-option label="onBeforeMount" value="onBeforeMount" /><el-option label="onUpdated" value="onUpdated" />
                </el-select>
                <el-button size="small" type="danger" :icon="Delete" circle @click="editingPageScript.hooks.splice(i,1);pageScriptDirty=true" />
              </div>
              <el-input v-model="h.body" type="textarea" :rows="4" style="font-family:monospace;font-size:12px" @change="pageScriptDirty=true" />
            </div>
            <div v-if="!editingPageScript.hooks?.length" class="script-empty">无</div>
            <el-button size="small" @click="editingPageScript.hooks.push({name:'onMounted',body:''});pageScriptDirty=true" style="width:100%">+ Hook</el-button>
          </el-collapse-item>
          <el-collapse-item title="Computed" name="computed">
            <div v-for="(c, i) in editingPageScript.computed" :key="'cp'+i" class="script-item">
              <div style="font-weight:600">{{ c.name }}</div>
              <el-input v-model="c.body" type="textarea" :rows="3" style="font-family:monospace;font-size:12px" @change="pageScriptDirty=true" />
            </div>
            <div v-if="!editingPageScript.computed?.length" class="script-empty">无</div>
          </el-collapse-item>
          <el-collapse-item title="Watches" name="watches">
            <div v-for="(w, i) in editingPageScript.watches" :key="'w'+i" class="script-item">
              <div style="font-weight:600">watch({{ w.source }})</div>
              <el-input v-model="w.body" type="textarea" :rows="4" style="font-family:monospace;font-size:12px" @change="pageScriptDirty=true" />
            </div>
            <div v-if="!editingPageScript.watches?.length" class="script-empty">无</div>
          </el-collapse-item>
        </el-collapse>
        <el-button v-if="pageScriptDirty" type="primary" @click="savePageScriptNow" :loading="savingPageScript" style="width:100%;margin-top:12px">保存页面 Script</el-button>
      </template>
      <div v-else style="color:#999;text-align:center;padding:40px">暂无 Script 数据，请先生成代码</div>
    </el-dialog>

    <!-- 选中控件 Script 对话框（可编辑） -->
    <el-dialog v-model="showSelectedScriptDialog" :title="'控件 Script — ' + (selectedNode?.tag || '')" width="750px" top="5vh" @open="initEditingFieldScript">
      <div v-if="!selectedNode" style="color:#999;text-align:center;padding:40px">
        当前选中的控件无关联 ScriptSection<br/>
        <span style="font-size:11px">请先生成代码，或选中一个有 Script 的控件</span>
      </div>
      <div v-else-if="!editingFieldScript" style="color:#999;text-align:center;padding:20px">加载中...</div>
      <div v-else style="max-height:65vh;overflow-y:auto">
        <!-- Imports -->
        <div style="margin-bottom:12px">
          <div style="font-weight:600;color:#409eff;margin-bottom:4px;display:flex;justify-content:space-between">
            <span>Imports</span>
            <el-button size="small" @click="editingFieldScript.imports.push({mode:'named',names:'',from:''});onFieldScriptDirty=true">+</el-button>
          </div>
          <div v-for="(imp,i) in editingFieldScript.imports" :key="'si'+i" style="display:flex;gap:4px;align-items:center;margin-bottom:4px">
            <el-select v-model="imp.mode" size="small" style="width:80px" @change="onFieldScriptDirty=true">
              <el-option v-for="m in ['named','default','namespace','sideEffect']" :key="m" :label="m" :value="m" />
            </el-select>
            <el-input v-model="imp.names" size="small" placeholder="ref, reactive" style="flex:1" @change="onFieldScriptDirty=true" />
            <span style="font-size:11px">from</span>
            <el-input v-model="imp.from" size="small" placeholder="vue" style="flex:1" @change="onFieldScriptDirty=true" />
            <el-button size="small" type="danger" :icon="Delete" circle @click="editingFieldScript.imports.splice(i,1);onFieldScriptDirty=true" />
          </div>
        </div>
        <!-- Refs -->
        <div style="margin-bottom:12px">
          <div style="font-weight:600;color:#409eff;margin-bottom:4px;display:flex;justify-content:space-between">
            <span>Refs</span>
            <el-button size="small" @click="editingFieldScript.refs.push({name:'',value:'null'});onFieldScriptDirty=true">+</el-button>
          </div>
          <div v-for="(r,i) in editingFieldScript.refs" :key="'sr'+i" style="display:flex;gap:4px;align-items:center;margin-bottom:4px">
            <span style="font-size:11px">const</span>
            <el-input v-model="r.name" size="small" placeholder="myRef" style="width:100px" @change="onFieldScriptDirty=true" />
            <span style="font-size:11px">= ref(</span>
            <el-input v-model="r.value" size="small" placeholder="null" style="flex:1" @change="onFieldScriptDirty=true" />
            <span style="font-size:11px">)</span>
            <el-button size="small" type="danger" :icon="Delete" circle @click="editingFieldScript.refs.splice(i,1);onFieldScriptDirty=true" />
          </div>
        </div>
        <!-- Functions -->
        <div style="margin-bottom:12px">
          <div style="font-weight:600;color:#409eff;margin-bottom:4px;display:flex;justify-content:space-between">
            <span>Functions</span>
            <el-button size="small" @click="editingFieldScript.functions.push({name:'newFn',params:'',async:false,body:''});onFieldScriptDirty=true">+</el-button>
          </div>
          <div v-for="(fn,i) in editingFieldScript.functions" :key="'sf'+i" style="border:1px solid #eee;border-radius:4px;padding:6px;margin-bottom:6px">
            <div style="display:flex;gap:4px;align-items:center;margin-bottom:4px">
              <el-checkbox v-model="fn.async" size="small" @change="onFieldScriptDirty=true">async</el-checkbox>
              <el-input v-model="fn.name" size="small" placeholder="fnName" style="width:120px" @change="onFieldScriptDirty=true" />
              <span>(</span><el-input v-model="fn.params" size="small" placeholder="params" style="width:80px" @change="onFieldScriptDirty=true" /><span>)</span>
              <el-button size="small" type="danger" :icon="Delete" circle @click="editingFieldScript.functions.splice(i,1);onFieldScriptDirty=true" />
            </div>
            <el-input v-model="fn.body" type="textarea" :rows="4" style="font-family:monospace;font-size:11px" @change="onFieldScriptDirty=true" />
          </div>
        </div>
        <!-- Hooks -->
        <div style="margin-bottom:12px">
          <div style="font-weight:600;color:#409eff;margin-bottom:4px;display:flex;justify-content:space-between">
            <span>Hooks</span>
            <el-button size="small" @click="editingFieldScript.hooks.push({name:'onMounted',body:''});onFieldScriptDirty=true">+</el-button>
          </div>
          <div v-for="(h,i) in editingFieldScript.hooks" :key="'sh'+i" style="border:1px solid #eee;border-radius:4px;padding:6px;margin-bottom:6px">
            <div style="display:flex;gap:4px;align-items:center;margin-bottom:4px">
              <el-select v-model="h.name" size="small" style="width:130px" @change="onFieldScriptDirty=true">
                <el-option v-for="hn in ['onMounted','onUnmounted','onBeforeMount','onUpdated']" :key="hn" :label="hn" :value="hn" />
              </el-select>
              <el-button size="small" type="danger" :icon="Delete" circle @click="editingFieldScript.hooks.splice(i,1);onFieldScriptDirty=true" />
            </div>
            <el-input v-model="h.body" type="textarea" :rows="4" style="font-family:monospace;font-size:11px" @change="onFieldScriptDirty=true" />
          </div>
        </div>
        <el-button v-if="onFieldScriptDirty" type="primary" size="small" @click="handleSaveFieldScript" :loading="savingScript" style="width:100%;margin-top:8px">保存 Script</el-button>
      </div>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted, onBeforeUnmount, nextTick, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Close, Delete } from '@element-plus/icons-vue'
import { getPageContent, savePageContent, deleteChildRelation, deleteEntityRelation, getPageScript, savePageScript, getFieldScripts, saveFieldScripts, getProjectI18nMap } from '@/api/codegen/moduleEntity'
import { getFieldPropertyPanel } from '@/api/system/component'
import i18n from '@/i18n'
import DesignCanvas from './DesignCanvas.vue'
import { collectDesignTranslationKeys, resolveDesignTranslation } from './translationPreview'

const router = useRouter()
const route = useRoute()
const entityId = ref(route.query.entityId)
const entityName = ref(route.query.entityName||'')
const pageType = ref(route.query.pageType||'index')
const pageLabel = ref({index:'列表页',add:'新增页',edit:'编辑页',detail:'详情页'}[pageType.value]||'')

// 设计器预览翻译：只替换画布显示，不改写 tree.json/.vue 内容
const translationMap = ref({})
const translationEnabled = ref(true)
const translationLoading = ref(false)
const translationLanguage = computed(() => i18n.global.locale.value || 'zh-CN')
const translationKeys = computed(() => [...collectDesignTranslationKeys(tree.value)])
const translationKeyCount = computed(() => translationKeys.value.length)
const missingTranslationKeys = computed(() => translationKeys.value.filter(key => !translationMap.value[key]))
const displayDesignText = value => resolveDesignTranslation(value, translationMap.value, translationEnabled.value)

async function loadProjectTranslations() {
  if (!entityId.value) return
  translationLoading.value = true
  try {
    const result = await getProjectI18nMap(entityId.value, translationLanguage.value)
    translationMap.value = result && typeof result === 'object' ? result : {}
  } catch (e) {
    // 翻译仅影响设计器预览，目标项目未初始化时保持编辑功能可用
    translationMap.value = {}
  } finally {
    translationLoading.value = false
  }
}

async function toggleTranslationPreview() {
  translationEnabled.value = !translationEnabled.value
  if (translationEnabled.value && !Object.keys(translationMap.value).length) await loadProjectTranslations()
}

const tree = ref([])
const loading = ref(true), error = ref(''), saving = ref(false)
const selectedId = ref(null), selectedNode = ref(null)
const drawerVisible = ref(false)
const propTab = ref('props')
const previewMode = ref(false)
const dirs = ['v-if','v-else-if','v-else','v-show','v-for','v-model','v-loading','v-permission']

// 标签面板
const showTagPalette = ref(false)
const tagFilter = ref('')
const ALL_TAGS = [
  'text','div','span','template',
  'el-row','el-col','el-space','el-container','el-header','el-aside','el-main','el-footer',
  'el-card','el-divider','el-alert','el-result','el-empty','el-skeleton','el-skeleton-item',
  'el-form','el-form-item','el-input','el-input-number','el-input-tag','el-autocomplete','el-mention',
  'el-select','el-option','el-option-group','el-virtualized-select','el-cascader','el-cascader-panel',
  'el-checkbox','el-checkbox-group','el-checkbox-button','el-radio','el-radio-group','el-radio-button',
  'el-switch','el-slider','el-rate','el-color-picker','el-date-picker','el-time-picker','el-time-select',
  'el-upload','el-image','el-table','el-table-column','el-virtualized-table',
  'el-descriptions','el-descriptions-item','el-pagination','el-tabs','el-tab-pane',
  'el-dialog','el-drawer','el-tooltip','el-popover','el-popconfirm','el-dropdown','el-dropdown-menu','el-dropdown-item',
  'el-button','el-button-group','el-icon','el-tag','el-check-tag','el-badge','el-avatar','el-progress',
  'el-tree','el-tree-select','el-virtualized-tree','el-menu','el-menu-item','el-sub-menu','el-menu-item-group',
  'el-breadcrumb','el-breadcrumb-item','el-steps','el-step','el-timeline','el-timeline-item',
  'el-link','el-scrollbar','el-calendar','el-statistic','el-countdown','el-transfer'
]
const filteredTags = computed(() => {
  if (!tagFilter.value) return ALL_TAGS
  return ALL_TAGS.filter(t => t.includes(tagFilter.value.toLowerCase()))
})

let paletteDragTag = null
const CHILDLESS_TAGS = new Set([
  'text','input','img','br','hr',
  'el-input','el-input-number','el-input-tag','el-autocomplete','el-mention',
  'el-switch','el-slider','el-rate','el-color-picker','el-date-picker','el-time-picker','el-time-select',
  'el-cascader','el-cascader-panel','el-pagination','el-image','el-progress','el-avatar','el-skeleton-item'
])

function textNode(content = 'Text') {
  return { tag: 'text', content, props: [{ key: 'content', value: content, isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' }
}

const NODE_PRESETS = {
  text: textNode(),
  div: { tag: 'div', props: [], children: [textNode()], useSlots: [], events: [], instructions: [], genId: '' },
  span: { tag: 'span', props: [], children: [textNode()], useSlots: [], events: [], instructions: [], genId: '' },
  template: { tag: 'template', props: [], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-row': { tag: 'el-row', props: [{ key: 'gutter', value: '16', isBind: true, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-col': { tag: 'el-col', props: [{ key: 'span', value: '12', isBind: true, isSingle: false }], children: [textNode()], useSlots: [], events: [], instructions: [], genId: '' },
  'el-card': { tag: 'el-card', props: [], children: [textNode('Card')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-space': { tag: 'el-space', props: [], children: [textNode('Space')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-form': { tag: 'el-form', ref: 'formRef', props: [{ key: 'model', value: 'form', isBind: true, isSingle: false }, { key: 'label-width', value: '100px', isBind: false, isSingle: false }], children: [{ tag: 'el-form-item', props: [{ key: 'label', value: 'Label', isBind: false, isSingle: false }], children: [{ tag: 'el-input', props: [{ key: 'placeholder', value: 'Please input', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [{ name: 'v-model', value: 'form.name', isSingle: false }], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-form-item': { tag: 'el-form-item', props: [{ key: 'label', value: 'Label', isBind: false, isSingle: false }], children: [{ tag: 'el-input', props: [{ key: 'placeholder', value: 'Please input', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-input': { tag: 'el-input', props: [{ key: 'placeholder', value: 'Please input', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-input-number': { tag: 'el-input-number', props: [], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-select': { tag: 'el-select', props: [{ key: 'placeholder', value: 'Please select', isBind: false, isSingle: false }], children: [{ tag: 'el-option', props: [{ key: 'label', value: 'Option', isBind: false, isSingle: false }, { key: 'value', value: 'option', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-option': { tag: 'el-option', props: [{ key: 'label', value: 'Option', isBind: false, isSingle: false }, { key: 'value', value: 'option', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-checkbox': { tag: 'el-checkbox', props: [{ key: 'label', value: 'value', isBind: false, isSingle: false }], children: [textNode('Checkbox')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-radio': { tag: 'el-radio', props: [{ key: 'label', value: 'value', isBind: false, isSingle: false }], children: [textNode('Radio')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-switch': { tag: 'el-switch', props: [], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-date-picker': { tag: 'el-date-picker', props: [{ key: 'type', value: 'date', isBind: false, isSingle: false }, { key: 'placeholder', value: 'Select date', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-upload': { tag: 'el-upload', props: [{ key: 'action', value: '#', isBind: false, isSingle: false }], children: [{ tag: 'el-button', props: [{ key: 'type', value: 'primary', isBind: false, isSingle: false }], children: [textNode('Upload')], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-image': { tag: 'el-image', props: [{ key: 'style', value: 'width: 80px; height: 80px', isBind: false, isSingle: false }, { key: 'fit', value: 'cover', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-table': { tag: 'el-table', props: [{ key: 'data', value: 'tableData', isBind: true, isSingle: false }, { key: 'border', value: null, isBind: false, isSingle: true }], children: [{ tag: 'el-table-column', props: [{ key: 'prop', value: 'name', isBind: false, isSingle: false }, { key: 'label', value: 'Name', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-table-column': { tag: 'el-table-column', props: [{ key: 'prop', value: 'name', isBind: false, isSingle: false }, { key: 'label', value: 'Name', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-descriptions': { tag: 'el-descriptions', props: [{ key: 'border', value: null, isBind: false, isSingle: true }], children: [{ tag: 'el-descriptions-item', props: [{ key: 'label', value: 'Label', isBind: false, isSingle: false }], children: [textNode('Value')], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-descriptions-item': { tag: 'el-descriptions-item', props: [{ key: 'label', value: 'Label', isBind: false, isSingle: false }], children: [textNode('Value')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-pagination': { tag: 'el-pagination', props: [{ key: 'layout', value: 'prev, pager, next', isBind: false, isSingle: false }, { key: 'total', value: '0', isBind: true, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-tabs': { tag: 'el-tabs', props: [{ key: 'model-value', value: 'first', isBind: false, isSingle: false }], children: [{ tag: 'el-tab-pane', props: [{ key: 'label', value: 'Tab', isBind: false, isSingle: false }, { key: 'name', value: 'first', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' }], useSlots: [], events: [], instructions: [], genId: '' },
  'el-tab-pane': { tag: 'el-tab-pane', props: [{ key: 'label', value: 'Tab', isBind: false, isSingle: false }, { key: 'name', value: 'first', isBind: false, isSingle: false }], children: [], useSlots: [], events: [], instructions: [], genId: '' },
  'el-dialog': { tag: 'el-dialog', props: [{ key: 'modelValue', value: 'true', isBind: true, isSingle: false }, { key: 'title', value: 'Dialog', isBind: false, isSingle: false }], children: [textNode('Dialog content')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-drawer': { tag: 'el-drawer', props: [{ key: 'modelValue', value: 'true', isBind: true, isSingle: false }, { key: 'title', value: 'Drawer', isBind: false, isSingle: false }], children: [textNode('Drawer content')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-button': { tag: 'el-button', props: [{ key: 'type', value: 'primary', isBind: false, isSingle: false }], children: [textNode('Button')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-tag': { tag: 'el-tag', props: [{ key: 'type', value: 'primary', isBind: false, isSingle: false }], children: [textNode('Tag')], useSlots: [], events: [], instructions: [], genId: '' },
  'el-alert': { tag: 'el-alert', props: [{ key: 'title', value: 'Alert', isBind: false, isSingle: false }, { key: 'type', value: 'success', isBind: false, isSingle: false }, { key: 'show-icon', value: null, isBind: false, isSingle: true }], children: [], useSlots: [], events: [], instructions: [], genId: '' }
}

function cloneNode(node) { return JSON.parse(JSON.stringify(node)) }
function canHaveChildrenTag(tag) { return !CHILDLESS_TAGS.has(tag) }
function canHaveChildren(node) { return !!node && canHaveChildrenTag(node.tag) }
function createDesignerNode(tag) {
  const fallback = { tag, props: [], children: canHaveChildrenTag(tag) ? [] : [], useSlots: [], events: [], instructions: [], genId: '' }
  const node = cloneNode(NODE_PRESETS[tag] || fallback)
  node.props = node.props || []
  node.children = node.children || []
  node.useSlots = node.useSlots || []
  node.events = node.events || []
  node.instructions = node.instructions || []
  node.genId = node.genId || ''
  node.id = node.id || createStableNodeId(node.genId || tag)
  return node
}

function createStableNodeId(seed = 'node') {
  const suffix = globalThis.crypto?.randomUUID
    ? globalThis.crypto.randomUUID().replaceAll('-', '')
    : `${Date.now()}${Math.random().toString(16).slice(2)}`
  return `design_${String(seed).replace(/[^a-zA-Z0-9_-]/g, '_')}_${suffix}`
}

function refreshIds() {
  function assignIds(nodes) {
    for (const n of (nodes || [])) {
      n.id = n.id || createStableNodeId(n.genId || n.entityField || n.tag)
      if (n.children) assignIds(n.children)
      for (const s of (n.useSlots || [])) {
        if (s.components) assignIds(s.components)
      }
    }
  }
  assignIds(tree.value)
}

function onPaletteDragStart(e, tag) {
  paletteDragTag = tag
  e.dataTransfer.effectAllowed = 'copy'
  e.dataTransfer.setData('application/x-codemaster-component', tag)
  e.dataTransfer.setData('text/plain', `component:${tag}`)
}
function onPaletteDragEnd() { paletteDragTag = null }

// ====== 右键菜单 ======
const ctxMenu = reactive({ visible: false, x: 0, y: 0, data: null })
const ctxClipboard = ref(null) // 复制的节点

function onTreeContextMenu(e, data) {
  ctxMenu.visible = true
  ctxMenu.x = e.clientX
  ctxMenu.y = e.clientY
  ctxMenu.data = data
  onSelect(data.id)
}

function hideCtxMenu() { ctxMenu.visible = false }

function ctxCopy() {
  if (!ctxMenu.data) return
  ctxClipboard.value = JSON.parse(JSON.stringify(ctxMenu.data))
  hideCtxMenu()
}

function ctxPaste() {
  if (!ctxClipboard.value || !ctxMenu.data) return
  pushUndo()
  const clone = JSON.parse(JSON.stringify(ctxClipboard.value))
  // 给所有节点重新分配 ID
  let cid = Date.now()
  function reassignIds(node) { node.id = 'c' + (cid++); if (node.children) node.children.forEach(reassignIds) }
  reassignIds(clone)
  // 找到目标节点的父级，插入到目标之后
  const targetId = ctxMenu.data.id
  const parent = findParentNode(tree.value, targetId)
  if (parent && parent.children) {
    const idx = parent.children.findIndex(c => c.id === targetId)
    if (idx >= 0) parent.children.splice(idx + 1, 0, clone)
    else parent.children.push(clone)
  } else {
    const idx = tree.value.findIndex(c => c.id === targetId)
    if (idx >= 0) tree.value.splice(idx + 1, 0, clone)
    else tree.value.push(clone)
  }
  onTreeChange()
  hideCtxMenu()
}

function ctxInsertBefore() {
  if (!ctxMenu.data) return
  pushUndo()
  const newNode = { tag: 'div', props: [], children: [], useSlots: [], events: [], instructions: [], genId: '', id: 'n' + Date.now() }
  insertSibling(ctxMenu.data.id, newNode, 'before')
  onTreeChange()
  hideCtxMenu()
}

function ctxInsertAfter() {
  if (!ctxMenu.data) return
  pushUndo()
  const newNode = { tag: 'div', props: [], children: [], useSlots: [], events: [], instructions: [], genId: '', id: 'n' + Date.now() }
  insertSibling(ctxMenu.data.id, newNode, 'after')
  onTreeChange()
  hideCtxMenu()
}

function insertSibling(targetId, newNode, position) {
  const location = findNodeLocation(tree.value, targetId)
  if (!location) return false
  location.collection.splice(position === 'before' ? location.index : location.index + 1, 0, newNode)
  return true
}

function insertDesignerNode(targetId, newNode, position = 'after') {
  if (!targetId) {
    tree.value.push(newNode)
    return true
  }

  const targetNode = findNode(tree.value, targetId)
  if (!targetNode) {
    tree.value.push(newNode)
    return true
  }

  if (position === 'inside' && canHaveChildren(targetNode)) {
    targetNode.children = targetNode.children || []
    targetNode.children.push(newNode)
    return true
  }

  insertSibling(targetId, newNode, position === 'before' ? 'before' : 'after')
  return true
}

function selectDesignerNode(node) {
  refreshIds()
  selectedId.value = node.id
  selectedNode.value = node
  onTreeChange()
  openDrawer()
}

function ctxMoveUp() {
  if (!ctxMenu.data) return
  pushUndo()
  const parent = findParentNode(tree.value, ctxMenu.data.id)
  const arr = (parent && parent.children) ? parent.children : tree.value
  const idx = arr.findIndex(c => c.id === ctxMenu.data.id)
  if (idx > 0) { arr.splice(idx, 1); arr.splice(idx - 1, 0, ctxMenu.data); onTreeChange() }
  hideCtxMenu()
}

function ctxMoveDown() {
  if (!ctxMenu.data) return
  pushUndo()
  const parent = findParentNode(tree.value, ctxMenu.data.id)
  const arr = (parent && parent.children) ? parent.children : tree.value
  const idx = arr.findIndex(c => c.id === ctxMenu.data.id)
  if (idx >= 0 && idx < arr.length - 1) { arr.splice(idx, 1); arr.splice(idx + 1, 0, ctxMenu.data); onTreeChange() }
  hideCtxMenu()
}

function ctxDelete() {
  if (!ctxMenu.data) return
  pushUndo()
  handleDelete()
  hideCtxMenu()
}

// Dialog 显示开关
const dialogVisible = computed(() => {
  const node = selectedNode.value
  if (!node || node.tag !== 'el-dialog') return true
  const p = (node.props || []).find(p => p.key === 'modelValue')
  return p ? p.value !== false && p.value !== 'false' : true
})
function toggleDialogVisible(val) {
  const node = selectedNode.value
  if (!node) return
  node.props = node.props || []
  const p = node.props.find(p => p.key === 'modelValue')
  if (val) {
    if (p) node.props = node.props.filter(p => p.key !== 'modelValue') // 删除显式 false，让 buildProps 补 true
  } else {
    if (p) p.value = false
    else node.props.push({ key: 'modelValue', value: false, isBind: true })
  }
}



function addTagToTree(tag) {
  pushUndo()
  const newNode = createDesignerNode(tag)
  if (selectedNode.value && canHaveChildren(selectedNode.value)) {
    selectedNode.value.children = selectedNode.value.children || []
    selectedNode.value.children.push(newNode)
  } else if (selectedNode.value?.id) {
    insertDesignerNode(selectedNode.value.id, newNode, 'after')
  } else {
    tree.value.push(newNode)
  }
  selectDesignerNode(newNode)
}

function onTagChange(newTag) {
  if (!selectedNode.value) return
  pushUndo()
  selectedNode.value.tag = newTag
  // 切换为 el-table-column 时自动加 prop 属性
  if (newTag === 'el-table-column' && !(selectedNode.value.props||[]).some(p=>p.key==='prop')) {
    selectedNode.value.props = selectedNode.value.props || []
    selectedNode.value.props.unshift({key:'prop',value:'',isBind:false})
  }
  // 切换为 el-form-item 时自动加 label 属性
  if (newTag === 'el-form-item' && !(selectedNode.value.props||[]).some(p=>p.key==='label')) {
    selectedNode.value.props = selectedNode.value.props || []
    selectedNode.value.props.unshift({key:'label',value:'',isBind:true})
  }
  onTreeChange()
}

// Script 编辑状态
const editingFieldScript = ref(null)
const onFieldScriptDirty = ref(false)
const savingScript = ref(false)
const showSelectedScriptDialog = ref(false)

function initEditingFieldScript() {
  if (!selectedNode.value) return
  editingFieldScript.value = JSON.parse(JSON.stringify(selectedFieldScript.value || emptyNodeScript()))
  onFieldScriptDirty.value = false
}

async function handleSaveFieldScript() {
  const node = selectedNode.value
  if (!node || !editingFieldScript.value) return
  savingScript.value = true
  try {
    const script = normalizeNodeScript(editingFieldScript.value)
    // 写回 tree JSON 节点
    node.scriptSection = JSON.stringify(script)
    const genId = node.genId

    // 同时保存到 fields.json（持久化，下次生成时保留）
    if (genId) {
      const allScripts = JSON.parse(JSON.stringify(fieldScripts.value || {}))
      allScripts[genId] = JSON.stringify(script)
      fieldScripts.value = allScripts
      await saveFieldScripts(entityId.value, pageType.value, JSON.stringify(allScripts))
    }

    await savePageContent(entityId.value, pageType.value, JSON.stringify(tree.value))
    onFieldScriptDirty.value = false
    ElMessage.success('Script 已保存')
  } catch (e) {
    ElMessage.error('保存失败: ' + e.message)
  } finally { savingScript.value = false }
}

// Script 查看对话框
const showScriptDialog = ref(false)
const scriptLoading = ref(false)
const pageScript = ref(null)
const editingPageScript = ref(null)
const pageScriptDirty = ref(false)
const savingPageScript = ref(false)
const scriptActiveNames = ref(['imports', 'functions'])
const childScripts = ref({})       // 子表脚本 { 'orderItem': {...} }
const selectedScriptKey = ref('')  // 当前选中的脚本 key: '' = 主表, 'orderItem' = 子表
const activeScriptKey = ref('')    // editingPageScript 当前实际对应的 key
const scriptDirtyStates = ref({})

watch(showScriptDialog, async (val) => {
  if (val && entityId.value) {
    scriptLoading.value = true
    selectedScriptKey.value = ''
    activeScriptKey.value = ''
    scriptDirtyStates.value = {}
    try {
      const res = await getPageScript(entityId.value, pageType.value)
      const parsed = res ? (typeof res === 'string' ? JSON.parse(res) : res) : null
      pageScript.value = parsed
      editingPageScript.value = parsed ? JSON.parse(JSON.stringify(parsed)) : null
      mainPageScriptBackup.value = parsed ? JSON.parse(JSON.stringify(parsed)) : null
      pageScriptDirty.value = false

      // 加载子表脚本
      const fs = fieldScripts.value || {}
      const childMap = {}
      for (const [k, v] of Object.entries(fs)) {
        if (k.startsWith('child_')) {
          const childKey = k.replace('child_', '').replace(`.${pageType.value}`, '')
          childMap[childKey] = typeof v === 'string' ? JSON.parse(v) : v
        }
      }
      childScripts.value = childMap
    } catch (e) {
      pageScript.value = null
      editingPageScript.value = null
      childScripts.value = {}
    } finally { scriptLoading.value = false }
  }
})

// 主表原始脚本备份（切换子表后恢复用）
const mainPageScriptBackup = ref(null)

function cloneScript(script) {
  return script ? JSON.parse(JSON.stringify(script)) : null
}

function onScriptKeyChange(nextKey) {
  const previousKey = activeScriptKey.value

  // Element Plus 的 change 发生在 v-model 更新之后，因此必须用单独保存的
  // activeScriptKey 记录旧脚本，不能拿 selectedScriptKey 覆盖新目标。
  if (editingPageScript.value) {
    if (previousKey) childScripts.value[previousKey] = cloneScript(editingPageScript.value)
    else mainPageScriptBackup.value = cloneScript(editingPageScript.value)
  }
  scriptDirtyStates.value = {
    ...scriptDirtyStates.value,
    [previousKey]: pageScriptDirty.value
  }

  const targetKey = nextKey ?? selectedScriptKey.value ?? ''
  activeScriptKey.value = targetKey
  if (!targetKey) {
    editingPageScript.value = mainPageScriptBackup.value ? JSON.parse(JSON.stringify(mainPageScriptBackup.value)) : (pageScript.value ? JSON.parse(JSON.stringify(pageScript.value)) : null)
  } else {
    const s = childScripts.value[targetKey]
    editingPageScript.value = s ? JSON.parse(JSON.stringify(s)) : null
  }
  pageScriptDirty.value = !!scriptDirtyStates.value[targetKey]
}

async function savePageScriptNow() {
  const s = editingPageScript.value
  if (!s) return
  savingPageScript.value = true
  try {
    if (!selectedScriptKey.value) {
      // 主表
      await savePageScript(entityId.value, pageType.value, JSON.stringify(s))
      pageScript.value = JSON.parse(JSON.stringify(s))
      mainPageScriptBackup.value = JSON.parse(JSON.stringify(s))
    } else {
      await savePageScript(entityId.value, pageType.value, JSON.stringify(s), selectedScriptKey.value)
      childScripts.value[selectedScriptKey.value] = JSON.parse(JSON.stringify(s))
    }
    scriptDirtyStates.value = { ...scriptDirtyStates.value, [selectedScriptKey.value]: false }
    pageScriptDirty.value = false
    ElMessage.success('Script 已保存')
  } catch (e) {
    ElMessage.error('保存失败: ' + e.message)
  } finally { savingPageScript.value = false }
}

function renderImport(imp) {
  if (imp.mode === 'named') return `import { ${imp.names} } from '${imp.from}'`
  if (imp.mode === 'default') return `import ${imp.names} from '${imp.from}'`
  if (imp.mode === 'namespace') return `import * as ${imp.names} from '${imp.from}'`
  if (imp.mode === 'sideEffect') return `import '${imp.from}'`
  return imp.from
}

// 属性面板 API 数据（选中字段时加载）
const panelData = ref(null)

// 字段级 ScriptSection（key=gen_id）—— 保留用于向后兼容，新数据在 node.scriptSection 中
const fieldScripts = ref({})

// 当前选中节点的 ScriptSection（直接从 tree JSON 节点读取）
function parseMaybeDoubleEncoded(raw) {
  if (!raw) return null
  if (typeof raw !== 'string') return raw
  try {
    const first = JSON.parse(raw)
    // 双重编码：第一次 parse 得到 string，再 parse 一次得到 object
    return typeof first === 'string' ? JSON.parse(first) : first
  } catch { return raw }
}

function emptyNodeScript() {
  return {
    imports: [],
    consts: [],
    lets: [],
    refs: [],
    reactives: [],
    functions: [],
    hooks: [],
    computed: [],
    watches: [],
    dictRefs: []
  }
}

function normalizeNodeScript(raw) {
  const parsed = parseMaybeDoubleEncoded(raw)
  if (!parsed || typeof parsed !== 'object') return emptyNodeScript()
  if (isMarkerScript(parsed)) return markerToBuilderScript(parsed)

  return {
    ...emptyNodeScript(),
    imports: (parsed.imports || []).map(imp => ({
      mode: imp.mode || (imp.default ? 'default' : 'named'),
      names: imp.names || imp.default || imp.destructured || '',
      from: imp.from || imp.path || ''
    })),
    consts: parsed.consts || [],
    lets: parsed.lets || [],
    refs: (parsed.refs || []).map(r => ({
      name: r.name || '',
      value: r.value ?? r.initialValue ?? 'null'
    })),
    reactives: (parsed.reactives || []).map(r => ({
      name: r.name || '',
      fields: r.fields || {}
    })),
    functions: (parsed.functions || []).map(fn => ({
      name: fn.name || '',
      params: fn.params ?? fn.parameters ?? '',
      async: !!fn.async,
      body: Array.isArray(fn.body) ? fn.body.join('\n') : (fn.body || '')
    })),
    hooks: (parsed.hooks || []).map(h => ({
      name: h.name || 'onMounted',
      body: Array.isArray(h.body) ? h.body.join('\n') : (h.body || '')
    })),
    computed: (parsed.computed || []).map(c => ({
      name: c.name || '',
      body: Array.isArray(c.body) ? c.body.join('\n') : (c.body || '')
    })),
    watches: (parsed.watches || []).map(w => ({
      source: w.source || '',
      body: Array.isArray(w.body) ? w.body.join('\n') : (w.body || ''),
      deep: !!w.deep,
      immediate: !!w.immediate
    })),
    dictRefs: parsed.dictRefs || []
  }
}

function isMarkerScript(script) {
  return (script.imports || []).some(imp => imp.path || imp.destructured || imp.default) ||
    (script.refs || []).some(r => Object.prototype.hasOwnProperty.call(r, 'initialValue')) ||
    (script.functions || []).some(fn => Array.isArray(fn.body)) ||
    Object.prototype.hasOwnProperty.call(script, 'uses')
}

function markerToBuilderScript(script) {
  return {
    ...emptyNodeScript(),
    imports: (script.imports || []).map(imp => {
      if (imp.destructured) return { mode: 'named', names: imp.destructured, from: imp.path || '' }
      if (imp.default) return { mode: 'default', names: imp.default, from: imp.path || '' }
      return { mode: 'sideEffect', names: '', from: imp.path || '' }
    }),
    refs: (script.refs || []).map(r => ({ name: r.name || '', value: r.initialValue ?? 'null' })),
    reactives: (script.reactives || []).map(r => ({ name: r.name || '', fields: r.fields || {} })),
    functions: (script.functions || []).map(fn => ({
      name: fn.name || '',
      params: fn.params ?? fn.parameters ?? '',
      async: !!fn.async,
      body: Array.isArray(fn.body) ? fn.body.join('\n') : (fn.body || '')
    })),
    hooks: (script.hooks || []).map(h => ({
      name: h.name || 'onMounted',
      body: Array.isArray(h.body) ? h.body.join('\n') : (h.body || '')
    })),
    computed: (script.computed || []).map(c => ({
      name: c.name || '',
      body: Array.isArray(c.body) ? c.body.join('\n') : (c.body || '')
    })),
    watches: (script.watches || []).map(w => ({
      source: w.source || '',
      body: Array.isArray(w.body) ? w.body.join('\n') : (w.body || ''),
      deep: !!w.deep,
      immediate: !!w.immediate
    }))
  }
}

const selectedFieldScript = computed(() => {
  const node = selectedNode.value
  if (!node) return null
  if (node.scriptSection) return normalizeNodeScript(node.scriptSection)
  // fallback: 尝试从 fieldScripts 查找
  if (node.genId && fieldScripts.value[node.genId]) {
    const raw = fieldScripts.value[node.genId]
    const obj = parseMaybeDoubleEncoded(raw)
    if (obj?.script) return normalizeNodeScript(obj.script)
    return normalizeNodeScript(obj)
  }
  return emptyNodeScript()
})

// 当选中节点的 Script 变化时，复制一份用于编辑
watch(selectedFieldScript, (val) => {
  editingFieldScript.value = val ? JSON.parse(JSON.stringify(val)) : null
  onFieldScriptDirty.value = false
})

const panelLoading = ref(false)
let panelNode = null  // 实际用于属性合并的节点（可能是子节点）
let wasDragging = false

  async function loadPanelData() {
    if (!selectedNode.value) return
    panelLoading.value = true
    try {
      // 查属性：优先当前节点，0属性时钻入子节点
      const tryLoadPanel = async (node) => {
        if (!node) return null
        const data = await getFieldPropertyPanel(node.tag)
        if (data && data.properties && data.properties.length > 0) return { node, data }
        for (const c of (node.children || [])) {
          if (c.tag && c.tag !== 'text') {
            const result = await tryLoadPanel(c)
            if (result) return result
            break  // 只试第一个非文本子节点
          }
        }
        return null
      }
      const result = await tryLoadPanel(selectedNode.value)
      if (result) {
        panelNode = result.node
        panelData.value = result.data
      } else {
        panelData.value = await getFieldPropertyPanel(selectedNode.value.tag)
        panelNode = selectedNode.value
      }

      // 合并已解析的组件树：标记已有的属性/事件/插槽/指令
      const node = panelNode
      if (panelData.value && node) {
        panelData.value.properties = panelData.value.properties || []
        panelData.value.events = panelData.value.events || []
        panelData.value.slots = panelData.value.slots || []
        panelData.value.directives = panelData.value.directives || []
        // 属性
        const propMap = {}
        if (node.ref) {
          propMap.ref = { value: node.ref, isBind: false, isSingle: false }
        }
        ;(node.props || []).forEach(p => {
          if (!p.key || p.key === 'data-gen-id') return
          propMap[p.key] = { value: p.value, isBind: p.isBind, isSingle: p.isSingle, isCustom: p.isCustom }
        })
        Object.entries(propMap).forEach(([name, existing]) => {
          if (!panelData.value.properties.find(p => p.propName === name)) {
            panelData.value.properties.unshift({
              propName: name,
              propType: existing.isSingle ? 'boolean' : 'string',
              enumValues: null,
              defaultValue: null,
              description: 'Parsed from current node',
              isCommon: true,
              isAdvanced: false,
              isActive: false,
              currentValue: null,
              valueType: 'static',
              isCustom: existing.isCustom === true,
              _appliedName: name
            })
          }
        })
        panelData.value.properties.forEach(p => {
          const existing = propMap[p.propName]
          if (existing) {
            p.isActive = true
            p.currentValue = existing.value
            p.valueType = existing.isBind ? 'bind' : existing.isSingle ? 'boolean' : 'static'
            if (existing.isCustom) p.isCustom = true
          }
          p._appliedName = p.propName
        })
        // 事件
        const evtMap = {}
        ;(node.events || []).forEach(e => {
          evtMap[e.name] = { body: e.body, isSingle: e.isSingle }
        })
        panelData.value.events.forEach(e => {
          const existing = evtMap[e.eventName]
          if (existing) {
            e.isActive = true
            e.currentValue = existing.body
            e.isSingleActive = existing.isSingle
          }
          e._appliedName = e.eventName
        })
        // 补充节点中已有但 API 未返回的事件
        ;(node.events || []).forEach(e => {
          if (!panelData.value.events.find(x => x.eventName === e.name)) {
            panelData.value.events.push({ eventName: e.name, description: '', isCommon: false, isSingle: e.isSingle, isActive: true, currentValue: e.body, isSingleActive: e.isSingle, isCustom: e.isCustom === true, _appliedName: e.name })
          } else {
            const hit = panelData.value.events.find(x => x.eventName === e.name)
            if (e.isCustom) hit.isCustom = true
          }
        })
        // 插槽
        const slotSet = new Set((node.useSlots || []).map(s => s.name))
        panelData.value.slots.forEach(s => {
          if (slotSet.has(s.slotName)) s.isActive = true
          if (s.isCustom) s._appliedName = s._appliedName || s.slotName
          else s._appliedName = s.slotName
        })
        // 补充节点中已有但 API 未返回的插槽
        ;(node.useSlots || []).forEach(s => {
          if (!panelData.value.slots.find(x => x.slotName === s.name)) {
            panelData.value.slots.push({ slotName: s.name, description: '', isCommon: false, isActive: true, isCustom: s.isCustom === true, _appliedName: s.name })
          } else {
            const hit = panelData.value.slots.find(x => x.slotName === s.name)
            if (s.isCustom) hit.isCustom = true
          }
        })
        // 指令
        const instMap = {}
        ;(node.instructions || []).forEach(i => {
          instMap[i.name] = i.value
        })
        panelData.value.directives.forEach(d => {
          if (instMap.hasOwnProperty(d.directiveName)) {
            d.isActive = true
            d.currentValue = instMap[d.directiveName]
          }
          d._appliedName = d.directiveName
        })
        // Add directives that exist on the node but are missing from metadata.
        ;(node.instructions || []).forEach(i => {
          if (!panelData.value.directives.find(x => x.directiveName === i.name)) {
            panelData.value.directives.push({
              directiveName: i.name,
              description: '',
              hasValue: !i.isSingle,
              isCommon: false,
              isActive: true,
              currentValue: i.value,
              isCustom: i.isCustom === true,
              _appliedName: i.name
            })
          } else {
            const hit = panelData.value.directives.find(x => x.directiveName === i.name)
            if (i.isCustom) hit.isCustom = true
          }
        })
      }
    } catch (e) {
      console.error('加载属性面板失败', e)
    } finally { panelLoading.value = false }
  }

function openDrawer() {
  drawerVisible.value = !!selectedNode.value
  panelData.value = null
  panelNode = null
  textEditContent.value = ''
  if (selectedNode.value && selectedNode.value.tag === 'text') {
    textEditContent.value = selectedNode.value.content || ''
  }
  if (selectedNode.value) loadPanelData()
  nextTick(scheduleRefresh)
}

function closePropertyPanel() {
  drawerVisible.value = false
  nextTick(scheduleRefresh)
}

// 过滤器
const showOnlyActive = ref(false)
const showAdvanced = ref(false)
const showOnlyActiveDir = ref(false)
const showOnlyActiveEvt = ref(false)
const showOnlyActiveSlot = ref(false)
// 智能面板「自定义新增」输入框
const newPropName = ref('')
const newDirName = ref('')
const newEvtName = ref('')
const newSlotName = ref('')

const filteredProps = computed(() => {
  if (!panelData.value) return []
  let list = panelData.value.properties || []
  if (showOnlyActive.value) list = list.filter(p => p.isActive)
  if (!showAdvanced.value) list = list.filter(p => !p.isAdvanced)
  return list
})
const filteredDirectives = computed(() => {
  if (!panelData.value) return []
  let list = panelData.value.directives || []
  if (showOnlyActiveDir.value) list = list.filter(d => d.isActive)
  return list
})
const filteredEvents = computed(() => {
  if (!panelData.value) return []
  let list = panelData.value.events || []
  if (showOnlyActiveEvt.value) list = list.filter(e => e.isActive)
  return list
})
const filteredSlots = computed(() => {
  if (!panelData.value) return []
  let list = panelData.value.slots || []
  if (showOnlyActiveSlot.value) list = list.filter(s => s.isActive)
  // 合并实际节点上的 parameter 值
  const actualSlots = selectedNode.value?.useSlots || []
  return list.map(s => {
    const actual = actualSlots.find(a => a.name === s.slotName)
    return { ...s, parameter: actual?.parameter || '' }
  })
})

function enumOptions(prop) { return (prop.enumValues || '').split(',').map(s => s.trim()).filter(Boolean) }
function modeIcon(vt) { return vt === 'bind' ? '🔗' : vt === 'boolean' ? '⚡' : '📝' }

// handles
window.__DESIGNER_HANDLES__ = window.__DESIGNER_HANDLES__ || new Map()
const handleMap = window.__DESIGNER_HANDLES__
// 树形面板
const treeRef = ref(null)

const treeData = computed(() => buildTreeData(tree.value))

function buildTreeData(nodes) {
  if (!nodes) return []
  const result = []
  for (const n of nodes) {
    if (n.tag === 'text') {
      const txt = (n.content || '').trim()
      if (txt) {
        result.push({
          id: n.id, tag: 'text', genId: '', 
          label: txt.substring(0, 40) + (txt.length > 40 ? '...' : ''),
          content: txt,
          children: []
        })
      }
      continue
    }
    const item = {
      id: n.id, tag: n.tag,
      genId: n.genId || '',
      label: n.tag + (n.genId ? ' [' + n.genId.replace('gen_','') + ']' : ''),
      children: buildTreeData(n.children || [])
    }
    // Add slots as children
    if (n.useSlots && n.useSlots.length) {
      for (const s of n.useSlots) {
        item.children.push({
          id: n.id + '__slot__' + s.name,
          tag: 'slot',
          genId: '',
          label: '#' + s.name + (s.parameter ? '="' + s.parameter + '"' : ''),
          children: buildTreeData(s.components || [])
        })
      }
    }
    result.push(item)
  }
  return result
}

let editingTextNode = null
const textEditContent = ref('')

function onTextContentChange() {
  if (!selectedNode.value || selectedNode.value.tag !== 'text') return
  pushUndo()
  selectedNode.value.content = textEditContent.value
  onTreeChange()
}

function startEditText(data) {
  const newText = prompt('编辑文本内容', data.content || '')
  if (newText !== null) {
    pushUndo()
    const node = findNode(tree.value, data.id)
    if (node) {
      node.content = newText
      data.label = newText.substring(0, 40)
      data.content = newText
      onTreeChange()
    }
  }
}

function onTreeNodeClick(data) {
  onSelect(data.id)
}

function expandAll() {
  const nodes = treeRef.value?.store?.nodesMap || {}
  Object.values(nodes).forEach(n => n.expanded = true)
}

function collapseAll() {
  const nodes = treeRef.value?.store?.nodesMap || {}
  Object.values(nodes).forEach(n => n.expanded = false)
}

function allowDrag(node) { return node.data.tag !== 'slot' }
function allowDrop(draggingNode, dropNode, type) {
  if (dropNode.data.tag === 'slot') return false
  const sourceNode = findNode(tree.value, draggingNode.data.id)
  const targetNode = findNode(tree.value, dropNode.data.id)
  if (!sourceNode || !targetNode || containsNode(sourceNode, targetNode.id)) return false
  return type !== 'inner' || canHaveChildren(targetNode)
}

function onTreeDragStart(node, ev) {
  pushUndo()
  dragId.value = node.data.id
}

function onTreeDragEnd() {
  dragId.value = null
  dragOverId.value = null
}

function onTreeNodeDrop() {
  // el-tree has already updated the bound data before emitting node-drop.
  // Only refresh the designer; moving the node again causes duplicate moves.
  onTreeChange()
}


const allHandles = ref([])
const designerRootRef = ref(null)
const canvasAreaRef = ref(null)
let refreshFrame = null
function scheduleRefresh() {
  if (refreshFrame !== null) return
  refreshFrame = requestAnimationFrame(() => {
    refreshFrame = null
    const c = designerRootRef.value
    const cr = c ? c.getBoundingClientRect() : { left: 0, top: 0 }
    const canvasRect = canvasAreaRef.value?.getBoundingClientRect()
    const r = []
    handleMap.forEach((el, id) => {
      if (typeof id==='string' && id.startsWith('__tag__')) return
      if (!el || !document.contains(el)) return
      const b = el.getBoundingClientRect()
      if (canvasRect && (b.bottom < canvasRect.top || b.top > canvasRect.bottom || b.right < canvasRect.left || b.left > canvasRect.right)) return
      r.push({ id, tag: handleMap.get('__tag__'+id)||'?', x: b.left-cr.left, y: b.top-cr.top-20 })
    })
    allHandles.value = r
  })
}
window.__DESIGNER_REFRESH__ = scheduleRefresh

function findNode(nodes, id) {
  for (const n of nodes) {
    if (n.id===id) return n
    if (n.children) { const f = findNode(n.children, id); if (f) return f }
    for (const s of (n.useSlots||[])) { if (s.components) { const f = findNode(s.components, id); if (f) return f } }
  }
  return null
}
function findNodeLocation(nodes, id, parentNode = null, slot = null) {
  for (let index = 0; index < (nodes || []).length; index++) {
    const node = nodes[index]
    if (node.id === id) return { node, collection: nodes, index, parentNode, slot }
    const childLocation = findNodeLocation(node.children || [], id, node, null)
    if (childLocation) return childLocation
    for (const currentSlot of (node.useSlots || [])) {
      const slotLocation = findNodeLocation(currentSlot.components || [], id, node, currentSlot)
      if (slotLocation) return slotLocation
    }
  }
  return null
}
function containsNode(node, id) {
  if (!node || !id) return false
  if (node.id === id) return true
  if ((node.children || []).some(child => containsNode(child, id))) return true
  return (node.useSlots || []).some(slot => (slot.components || []).some(child => containsNode(child, id)))
}
function moveDesignerNode(sourceId, targetId, position) {
  const sourceLocation = findNodeLocation(tree.value, sourceId)
  const targetLocation = findNodeLocation(tree.value, targetId)
  if (!sourceLocation || !targetLocation || containsNode(sourceLocation.node, targetId)) return false
  if (position === 'inner' || position === 'inside') {
    if (!canHaveChildren(targetLocation.node)) return false
  }

  const sourceNode = sourceLocation.node
  sourceLocation.collection.splice(sourceLocation.index, 1)

  const currentTarget = findNodeLocation(tree.value, targetId)
  if (!currentTarget) {
    sourceLocation.collection.splice(sourceLocation.index, 0, sourceNode)
    return false
  }

  if (position === 'inner' || position === 'inside') {
    currentTarget.node.children = currentTarget.node.children || []
    currentTarget.node.children.push(sourceNode)
  } else {
    const insertIndex = position === 'before' ? currentTarget.index : currentTarget.index + 1
    currentTarget.collection.splice(insertIndex, 0, sourceNode)
  }
  return true
}
function swapDesignerNodes(sourceId, targetId) {
  const sourceLocation = findNodeLocation(tree.value, sourceId)
  const targetLocation = findNodeLocation(tree.value, targetId)
  if (!sourceLocation || !targetLocation) return false
  if (containsNode(sourceLocation.node, targetId) || containsNode(targetLocation.node, sourceId)) return false

  const sourceNode = sourceLocation.node
  const targetNode = targetLocation.node
  sourceLocation.collection[sourceLocation.index] = targetNode
  targetLocation.collection[targetLocation.index] = sourceNode
  return true
}
function findParentNode(nodes, childId) {
  return findNodeLocation(nodes, childId)?.parentNode || null
}

let selectionTimer = null
let lastDragEndedAt = 0

function onHandleClick(id) {
  if (Date.now() - lastDragEndedAt < 250) return
  onSelect(id)
}

async function onSelect(id) {
  if (wasDragging) return
  selectedId.value = id
  if (selectionTimer) clearTimeout(selectionTimer)
  selectionTimer = setTimeout(() => {
    selectionTimer = null
    if (wasDragging) return
    selectedNode.value = id ? findNode(tree.value, id) : null
    openDrawer()
  }, 0)

}
function onTreeChange() { tree.value = [...tree.value]; scheduleRefresh() }

// 属性面板操作（同步 panelData → tree node）
function applyPropToNode(prop) {
  const node = panelNode || selectedNode.value
  if (!node) return
  if (prop.propName === 'ref') {
    node.ref = prop.isActive ? (prop.currentValue || '') : null
    return
  }
  node.props = node.props || []
  const existing = node.props.find(p => p.key === prop.propName)
  if (prop.isActive) {
    if (prop.valueType === 'boolean') {
      if (!existing) node.props.push({ key: prop.propName, value: null, isBind: false, isSingle: true })
      else { existing.isBind = false; existing.isSingle = true; existing.value = null }
    } else if (prop.valueType === 'bind') {
      if (!existing) node.props.push({ key: prop.propName, value: prop.currentValue || '', isBind: true, isSingle: false })
      else { existing.isBind = true; existing.isSingle = false; existing.value = prop.currentValue || '' }
    } else {
      if (!existing) node.props.push({ key: prop.propName, value: prop.currentValue || '', isBind: false, isSingle: false })
      else { existing.isBind = false; existing.isSingle = false; existing.value = prop.currentValue || '' }
    }
  } else {
    node.props = node.props.filter(p => p.key !== prop.propName)
  }
  // 记录自定义标记与已应用名称，重新加载面板后仍可编辑/删除
  if (prop.isActive) {
    const applied = node.props.find(p => p.key === prop.propName)
    if (applied) applied.isCustom = prop.isCustom === true
  }
  prop._appliedName = prop.propName
}

function applyDirectiveToNode(d) {
  const node = panelNode || selectedNode.value
  if (!node) return
  node.instructions = node.instructions || []
  const existing = node.instructions.find(i => i.name === d.directiveName)
  if (d.isActive) {
    if (!existing) node.instructions.push({ name: d.directiveName, value: d.currentValue || '', isSingle: !d.hasValue })
    else { existing.value = d.currentValue || ''; existing.isSingle = !d.hasValue }
  } else {
    node.instructions = node.instructions.filter(i => i.name !== d.directiveName)
  }
  if (d.isActive) {
    const applied = node.instructions.find(i => i.name === d.directiveName)
    if (applied) applied.isCustom = d.isCustom === true
  }
  d._appliedName = d.directiveName
}

function applyEventToNode(evt) {
  const node = panelNode || selectedNode.value
  if (!node) return
  node.events = node.events || []
  const existing = node.events.find(e => e.name === evt.eventName)
  if (evt.isActive) {
    if (!existing) node.events.push({ name: evt.eventName, body: evt.currentValue || null, isSingle: evt.isSingleActive })
    else { existing.body = evt.currentValue || null; existing.isSingle = evt.isSingleActive }
  } else {
    node.events = node.events.filter(e => e.name !== evt.eventName)
  }
  if (evt.isActive) {
    const applied = node.events.find(e => e.name === evt.eventName)
    if (applied) applied.isCustom = evt.isCustom === true
  }
  evt._appliedName = evt.eventName
}

function applySlotToNode(s) {
  const node = panelNode || selectedNode.value
  if (!node) return
  node.useSlots = node.useSlots || []
  const existing = node.useSlots.find(sl => sl.name === s.slotName)
  if (s.isActive) {
    if (!existing) node.useSlots.push({ name: s.slotName, parameter: null, components: [], isCustom: s.isCustom === true })
  } else {
    node.useSlots = node.useSlots.filter(sl => sl.name !== s.slotName)
  }
  if (s.isActive) {
    const applied = node.useSlots.find(sl => sl.name === s.slotName)
    if (applied) applied.isCustom = s.isCustom === true
  }
  s._appliedName = s.slotName
}

function onSlotParamChange(s) {
  const node = panelNode || selectedNode.value
  if (!node) return
  const existing = (node.useSlots || []).find(sl => sl.name === s.slotName)
  if (existing) { existing.parameter = s.parameter; onTreeChange() }
}

// Props
function addProp() { (selectedNode.value.props = selectedNode.value.props||[]).push({key:'',value:'',isBind:false}); onTreeChange() }
function removeProp(i) { selectedNode.value.props.splice(i,1); onTreeChange() }
// Events
function addEvent() { (selectedNode.value.events = selectedNode.value.events||[]).push({name:'',body:''}); onTreeChange() }
function removeEvent(i) { selectedNode.value.events.splice(i,1); onTreeChange() }
// Slots
function addSlot() { (selectedNode.value.useSlots = selectedNode.value.useSlots||[]).push({name:'',parameter:null,components:[]}); onTreeChange() }
function removeSlot(i) { selectedNode.value.useSlots.splice(i,1); onTreeChange() }
// Instructions
function addIns() { (selectedNode.value.instructions = selectedNode.value.instructions||[]).push({name:'v-if',value:''}); onTreeChange() }
function removeIns(i) { selectedNode.value.instructions.splice(i,1); onTreeChange() }

// ---------- 智能面板：自定义新增（元数据之外的属性/指令/事件）----------
// self 用于排除自身，避免重命名时把自己判为重名
function isDup(list, keyName, name, self) {
  return (list || []).some(x => x !== self && x[keyName] === name)
}

// 属性
function addCustomProp() {
  if (!panelData.value) return
  const name = (newPropName.value || '').trim()
  if (!name) return ElMessage.warning('请输入属性名')
  panelData.value.properties = panelData.value.properties || []
  if (isDup(panelData.value.properties, 'propName', name, null)) return ElMessage.warning(`属性已存在: ${name}`)
  const prop = {
    propName: name, propType: 'string', enumValues: null, defaultValue: null,
    description: '自定义属性', isCommon: false, isAdvanced: false,
    isActive: true, currentValue: '', valueType: 'static',
    isCustom: true, _appliedName: name
  }
  panelData.value.properties.push(prop)
  newPropName.value = ''
  applyPropToNode(prop)
  onTreeChange()
}
function removeCustomProp(prop) {
  if (!panelData.value) return
  panelData.value.properties = (panelData.value.properties || []).filter(p => p !== prop)
  const node = panelNode || selectedNode.value
  if (node && node.props) node.props = node.props.filter(p => p.key !== (prop._appliedName ?? prop.propName))
  onTreeChange()
}
function onCustomPropRename(prop) {
  const prev = prop._appliedName
  const next = (prop.propName || '').trim()
  if (!next) { prop.propName = prev; return }
  if (isDup(panelData.value?.properties, 'propName', next, prop)) {
    prop.propName = prev
    return ElMessage.warning(`属性已存在: ${next}`)
  }
  const node = panelNode || selectedNode.value
  if (node && node.props) {
    const existing = node.props.find(p => p.key === prev)
    if (existing) { existing.key = next; existing.isCustom = true }
  }
  prop.propName = next
  prop._appliedName = next
  onTreeChange()
}

// 指令（名称需含 v- 前缀，序列化时原样输出）
function addCustomDirective() {
  if (!panelData.value) return
  const name = (newDirName.value || '').trim()
  if (!name) return ElMessage.warning('请输入指令名')
  panelData.value.directives = panelData.value.directives || []
  if (isDup(panelData.value.directives, 'directiveName', name, null)) return ElMessage.warning(`指令已存在: ${name}`)
  const d = {
    directiveName: name, description: '自定义指令', hasValue: true,
    isCommon: false, isActive: true, currentValue: '',
    isCustom: true, _appliedName: name
  }
  panelData.value.directives.push(d)
  newDirName.value = ''
  applyDirectiveToNode(d)
  onTreeChange()
}
function removeCustomDirective(d) {
  if (!panelData.value) return
  panelData.value.directives = (panelData.value.directives || []).filter(x => x !== d)
  const node = panelNode || selectedNode.value
  if (node && node.instructions) node.instructions = node.instructions.filter(i => i.name !== (d._appliedName ?? d.directiveName))
  onTreeChange()
}
function onCustomDirectiveRename(d) {
  const prev = d._appliedName
  const next = (d.directiveName || '').trim()
  if (!next) { d.directiveName = prev; return }
  if (isDup(panelData.value?.directives, 'directiveName', next, d)) {
    d.directiveName = prev
    return ElMessage.warning(`指令已存在: ${next}`)
  }
  const node = panelNode || selectedNode.value
  if (node && node.instructions) {
    const existing = node.instructions.find(i => i.name === prev)
    if (existing) { existing.name = next; existing.isCustom = true }
  }
  d.directiveName = next
  d._appliedName = next
  onTreeChange()
}

// 事件（名称不含 @，序列化时自动加 @）
function addCustomEvent() {
  if (!panelData.value) return
  const name = (newEvtName.value || '').trim()
  if (!name) return ElMessage.warning('请输入事件名')
  panelData.value.events = panelData.value.events || []
  if (isDup(panelData.value.events, 'eventName', name, null)) return ElMessage.warning(`事件已存在: ${name}`)
  const evt = {
    eventName: name, description: '自定义事件', isCommon: false,
    isSingle: false, isActive: true, currentValue: '', isSingleActive: false,
    isCustom: true, _appliedName: name
  }
  panelData.value.events.push(evt)
  newEvtName.value = ''
  applyEventToNode(evt)
  onTreeChange()
}
function removeCustomEvent(evt) {
  if (!panelData.value) return
  panelData.value.events = (panelData.value.events || []).filter(x => x !== evt)
  const node = panelNode || selectedNode.value
  if (node && node.events) node.events = node.events.filter(e => e.name !== (evt._appliedName ?? evt.eventName))
  onTreeChange()
}
function onCustomEventRename(evt) {
  const prev = evt._appliedName
  const next = (evt.eventName || '').trim()
  if (!next) { evt.eventName = prev; return }
  if (isDup(panelData.value?.events, 'eventName', next, evt)) {
    evt.eventName = prev
    return ElMessage.warning(`事件已存在: ${next}`)
  }
  const node = panelNode || selectedNode.value
  if (node && node.events) {
    const existing = node.events.find(e => e.name === prev)
    if (existing) { existing.name = next; existing.isCustom = true }
  }
  evt.eventName = next
  evt._appliedName = next
  onTreeChange()
}

// 插槽（序列化为 <template #name>）
// 注意：filteredSlots 用 map 生成拷贝，故统一按名称（而非对象引用）定位
function addCustomSlot() {
  if (!panelData.value) return
  const name = (newSlotName.value || '').trim()
  if (!name) return ElMessage.warning('请输入插槽名')
  panelData.value.slots = panelData.value.slots || []
  if (isDup(panelData.value.slots, 'slotName', name, null)) return ElMessage.warning(`插槽已存在: ${name}`)
  const s = {
    slotName: name, description: '自定义插槽', isCommon: false,
    isActive: true, parameter: '', isCustom: true, _appliedName: name
  }
  panelData.value.slots.push(s)
  newSlotName.value = ''
  applySlotToNode(s)
  onTreeChange()
}
// 插槽勾选：filteredSlots 返回拷贝，需回写源对象，否则页签计数/「仅启用」与勾选状态不同步
function setSlotActive(s, v) {
  const list = panelData.value?.slots || []
  const key = s._appliedName ?? s.slotName
  const src = list.find(x => (x._appliedName ?? x.slotName) === key)
  if (src) src.isActive = v
  s.isActive = v
  applySlotToNode(src || s)
  onTreeChange()
}
function removeCustomSlot(s) {
  if (!panelData.value) return
  const name = s._appliedName ?? s.slotName
  panelData.value.slots = (panelData.value.slots || []).filter(x => (x._appliedName ?? x.slotName) !== name)
  const node = panelNode || selectedNode.value
  if (node && node.useSlots) node.useSlots = node.useSlots.filter(sl => sl.name !== name)
  onTreeChange()
}
function onCustomSlotRename(s, nextRaw) {
  const prev = s._appliedName ?? s.slotName
  const next = (nextRaw || '').trim()
  if (!next || next === prev) return
  const list = panelData.value?.slots || []
  const src = list.find(x => (x._appliedName ?? x.slotName) === prev)
  if (isDup(list, 'slotName', next, src)) return ElMessage.warning(`插槽已存在: ${next}`)
  if (src) { src.slotName = next; src._appliedName = next }
  const node = panelNode || selectedNode.value
  if (node && node.useSlots) {
    const existing = node.useSlots.find(sl => sl.name === prev)
    if (existing) { existing.name = next; existing.isCustom = true }
  }
  onTreeChange()
}

// Delete
async function handleDelete() {
  if (!selectedNode.value) return
  try {
    await ElMessageBox.confirm('确定删除该组件？', '提示', { type: 'warning' })
    // 如果是子表节点 (gen_child_*)，联动删除 OneToManyRelation
    const genId = selectedNode.value.genId || selectedNode.value.props?.genId
    if (genId && genId.startsWith('gen_child_')) {
      const childName = genId.replace('gen_child_', '')
      try {
        await deleteChildRelation(entityId.value, childName)
        ElMessage.success(`已删除子表关系: ${childName}`)
      } catch(e) { console.warn('删除子表关系失败:', e) }
    }
    if (genId && /^gen_owned_\d+$/.test(genId)) {
      const relationId = genId.replace('gen_owned_', '')
      try {
        await deleteEntityRelation(entityId.value, relationId)
        ElMessage.success('已删除一对一组成关系')
      } catch(e) { console.warn('删除一对一组成关系失败:', e) }
    }
    const parent = findParentNode(tree.value, selectedId.value)
    if (!parent) { tree.value = tree.value.filter(n => n.id !== selectedId.value) }
    else {
      const arr = (parent.children||[]).filter(c => c.id !== selectedId.value)
      if (arr.length !== parent.children.length) { parent.children = arr }
      else {
        for (const s of (parent.useSlots||[])) {
          s.components = (s.components||[]).filter(c => c.id !== selectedId.value)
        }
      }
    }
    selectedId.value = null; selectedNode.value = null; drawerVisible.value = false
    onTreeChange()
  } catch {}
}

// Save
async function handleSave() {
  saving.value = true
  try {
    await savePageContent(entityId.value, pageType.value, JSON.stringify(tree.value))
    ElMessage.success('保存成功')
  } catch(e) { ElMessage.error('保存失败: '+e.message) }
  finally { saving.value = false }
}

const dragId = ref(null)
const dragOverId = ref(null)
const indStyle = ref({})
const undoStack = ref([])
const redoStack = ref([])

function pushUndo() {
  undoStack.value.push(JSON.stringify(tree.value))
  redoStack.value = []
  if (undoStack.value.length > 50) undoStack.value.shift()
}

function handleUndo() {
  if (undoStack.value.length === 0) return
  redoStack.value.push(JSON.stringify(tree.value))
  tree.value = JSON.parse(undoStack.value.pop())
  scheduleRefresh()
}

function handleRedo() {
  if (redoStack.value.length === 0) return
  undoStack.value.push(JSON.stringify(tree.value))
  tree.value = JSON.parse(redoStack.value.pop())
  scheduleRefresh()
}

function onDragStart(e, id) {
  wasDragging = true
  try { pushUndo() } catch(ex) { console.error('undo failed', ex) }
  dragId.value = id
  e.dataTransfer.effectAllowed = 'move'
  e.dataTransfer.setData('text/plain', id)
}

function onDragOver(e, id) {
  if ((!dragId.value && !paletteDragTag) || dragId.value === id) return
  dragOverId.value = id
  const r = e.currentTarget.getBoundingClientRect()
  const rootRect = document.querySelector('.designer-root')?.getBoundingClientRect() || { left: 0, top: 0 }
  indStyle.value = { position: 'absolute', left: (r.left - rootRect.left) + 'px', top: (r.top - rootRect.top - 28) + 'px' }
}

function onDragLeave(id) {
  // keep indicator visible until dragend
}

function onDragEnd() {
  dragId.value = null
  dragOverId.value = null
  paletteDragTag = null
  wasDragging = false
  lastDragEndedAt = Date.now()
}

function onCanvasDragOver(e) {
  if (previewMode.value) {
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'none'
    return
  }

  const paletteTag = paletteDragTag || e.dataTransfer?.getData('application/x-codemaster-component')
  const hasExistingNode = !!dragId.value
  if (!paletteTag && !hasExistingNode) return
  if (e.dataTransfer) e.dataTransfer.dropEffect = paletteTag ? 'copy' : 'move'
}

function onCanvasDrop(e) {
  if (previewMode.value) return

  const paletteTag = paletteDragTag || e.dataTransfer?.getData('application/x-codemaster-component')
  if (paletteTag) {
    pushUndo()
    const newNode = createDesignerNode(paletteTag)
    tree.value.push(newNode)
    paletteDragTag = null
    dragId.value = null
    dragOverId.value = null
    selectDesignerNode(newNode)
    return
  }

  const sourceId = dragId.value
  dragId.value = null
  dragOverId.value = null
  if (!sourceId) return

  const sourceLocation = findNodeLocation(tree.value, sourceId)
  if (!sourceLocation) return
  if (sourceLocation.collection === tree.value && sourceLocation.index === tree.value.length - 1) return

  const sourceNode = sourceLocation.node
  sourceLocation.collection.splice(sourceLocation.index, 1)
  tree.value.push(sourceNode)
  onTreeChange()
  selectedId.value = null
  selectedNode.value = null
  drawerVisible.value = false
}

function onDrop(e, targetId, explicitPos) {
  const paletteTag = paletteDragTag || e.dataTransfer?.getData('application/x-codemaster-component')
  const pos = explicitPos || 'after'
  if (paletteTag) {
    pushUndo()
    const newNode = createDesignerNode(paletteTag)
    insertDesignerNode(targetId, newNode, pos === 'swap' ? 'after' : pos)
    dragId.value = null
    dragOverId.value = null
    paletteDragTag = null
    selectDesignerNode(newNode)
    return
  }

  const sourceId = dragId.value
  dragId.value = null
  dragOverId.value = null
  if (!sourceId || sourceId === targetId) return

  const changed = pos === 'swap'
    ? swapDesignerNodes(sourceId, targetId)
    : moveDesignerNode(sourceId, targetId, pos)
  if (!changed) return
  onTreeChange()
  selectedId.value = null
  selectedNode.value = null
  drawerVisible.value = false
}
function handleBack() { router.back() }

// Keyboard shortcuts
function onKeyDown(e) {
  if ((e.ctrlKey || e.metaKey) && e.key === 'z') {
    e.preventDefault()
    if (e.shiftKey) handleRedo()
    else handleUndo()
  }
}
document.addEventListener('keydown', onKeyDown)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeyDown)
  if (refreshFrame !== null) cancelAnimationFrame(refreshFrame)
  if (selectionTimer) clearTimeout(selectionTimer)
})

onMounted(async () => {
  try {
    const res = await getPageContent(entityId.value, pageType.value)
    const json = res.treeJson || res.TreeJson
    const parsed = json ? (typeof json === 'string' ? JSON.parse(json) : json) : []
    tree.value = parsed
    refreshIds()
    nextTick(()=>setTimeout(scheduleRefresh,200))
    const sc = res.styleContent||res.StyleContent
    if(sc){const s=document.createElement('style');s.textContent=sc;document.head.appendChild(s)}
    // 加载字段级 ScriptSection
    try { const fs = await getFieldScripts(entityId.value, pageType.value); if (fs) fieldScripts.value = typeof fs === 'string' ? JSON.parse(fs) : fs }
    catch {}
    await loadProjectTranslations()
  } catch(e) { error.value=e.message }
  finally { loading.value=false }
})

watch(translationLanguage, () => {
  if (translationEnabled.value && entityId.value) loadProjectTranslations()
})
</script>

<style scoped>
.designer-root {
  color: var(--app-text, #1f2937);
  position: relative;
  isolation: isolate;
}
.toolbar { display:flex; align-items:center; gap:12px; padding:8px 16px; background:var(--app-surface-soft, #f5f7fa); border-bottom:1px solid var(--app-border, #e4e7ed); }
.translation-status { font-size:12px; color:var(--app-text-muted, #909399); white-space:nowrap; }
.main-area { display:flex; height:calc(100vh - 42px); background:var(--app-bg, #fff); overflow:hidden; }
.tree-panel {
  width: 260px;
  border-right: 1px solid var(--app-border, #e4e7ed);
  overflow: auto;
  background: var(--app-surface, #fafafa);
  flex-shrink: 0;
}
.tree-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 12px;
  border-bottom: 1px solid var(--app-border, #e4e7ed);
  font-weight: 600;
  font-size: 13px;
  position: sticky;
  top: 0;
  background: var(--app-surface, #fafafa);
  z-index: 1;
}
.tree-node {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}
.tree-tag {
  font-family: monospace;
  font-weight: 600;
  color: var(--app-text, #303133);
}
.tree-genid {
  font-size: 10px;
  color: var(--app-text-muted, #909399);
  font-family: monospace;
}
.tree-node.is-text {
  font-style: italic;
  color: var(--app-text-muted, #909399);
}
.tree-node.is-slot .tree-tag {
  color: #e6a23c;
}
.tree-text-content {
  cursor: text;
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.canvas-area { flex:1; min-width:0; overflow:auto; }
.property-sidebar {
  width: 392px;
  flex: 0 0 392px;
  min-width: 320px;
  height: 100%;
  overflow: auto;
  border-left: 1px solid var(--app-border, #e4e7ed);
  background: var(--app-surface, #fff);
}
.prop-panel { padding:10px; }
.panel-header { display:flex; justify-content:space-between; align-items:center; gap:8px; margin-bottom:8px; font-weight:600; }
.panel-actions { display:flex; align-items:center; gap:6px; flex-shrink:0; }
.prop-row { display:flex; align-items:center; gap:3px; margin-bottom:4px; }
.drag-overlay { position:absolute; top:0; left:0; pointer-events:none; z-index:10; }
.drag-handle { position:absolute; height:22px; line-height:22px; padding:0 8px; background:#409eff; font-size:12px; color:#fff; font-weight:600; border-radius:4px; cursor:grab; white-space:nowrap; pointer-events:auto; z-index:1; box-shadow:0 2px 8px rgba(0,0,0,.3); overflow:hidden; }
.drag-handle.selected { background:#f56c6c; }
.drag-handle.drag-over { background:#67c23a; transform:scale(1.1); }
.drop-indicator {
  display: flex; gap: 2px;
  z-index: 2; pointer-events: auto;
  background: var(--app-surface, #fff); border-radius: 6px;
  padding: 2px; box-shadow: 0 2px 12px rgba(0,0,0,.3);
}
.di {
  padding: 4px 14px; border-radius: 4px;
  font-size: 13px; font-weight: 700; cursor: pointer;
}
.di { background: #409eff; color: #fff; }
.di:hover { background: #f56c6c !important; }
.di-inside:hover { background: #e6a23c !important; }

.hz-wrap {
  position: absolute; inset: 0;
  display: none;
  border-radius: 3px; overflow: hidden;
}
.drag-handle.show-zones .hz-wrap { display: flex; }
.drag-handle.show-zones .handle-label { visibility: hidden; }
.hz {
  display: inline-flex; align-items: center; justify-content: center;
  height: 100%;
  font-size: 10px; font-weight: 700;
  cursor: pointer;
  flex: 1;
}
.hz-before { background: #409eff; color: #fff; border-radius: 3px 0 0 3px; }
.hz-inside { background: #67c23a; color: #fff; }
.hz-after { background: #409eff; color: #fff; border-radius: 0 3px 3px 0; }
.hz:hover { filter: brightness(1.2); }
.drag-handle.show-zones { display: flex; padding: 0; min-width: 70px; }
.drag-handle { cursor:grab; }
.drag-handle:active { cursor:grabbing; }
.field-info { font-size:12px; color:var(--app-text-muted, #909399); }
.tab-toolbar { padding:4px 8px; border-bottom:1px solid var(--app-border, #ebeef5); display:flex; gap:8px; background:var(--app-surface, #fff); }
.prop-list { padding:4px 0; max-height:50vh; overflow-y:auto; }
.prop-list .prop-row { display:flex; align-items:center; padding:3px 8px; gap:4px; }
.prop-list .prop-row:hover { background:var(--app-surface-soft, #f5f7fa); }
.prop-list .prop-row.inactive { opacity:0.5; }
.prop-list .prop-name { flex:0 0 105px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:monospace; font-size:12px; }
.prop-list .prop-value { flex:1; min-width:50px; }
.prop-list .mode-btn { padding:2px 4px; min-width:28px; font-size:12px; }
/* 智能面板「自定义新增」：底部输入行 + 自定义项可编辑名称 */
.custom-add { display:flex; align-items:center; gap:4px; padding:6px 8px; border-top:1px solid var(--app-border, #ebeef5); background:var(--app-surface-soft, #f5f7fa); }
.custom-add > :deep(.el-input) { flex:1; min-width:0; }
.custom-add > :deep(.el-button) { flex:0 0 auto; }
.prop-list .prop-name-input { flex:0 0 105px; }
.prop-list .prop-name-input :deep(.el-input__inner) { font-family:monospace; font-size:12px; padding:0 4px; }
:deep(.el-tabs--border-card){box-shadow:none;border:1px solid var(--app-border, #dcdfe6);background:var(--app-surface, #fff)}
:deep(.el-tabs--border-card>.el-tabs__header){margin:0;background:var(--app-surface-soft, #f5f7fa);border-bottom:1px solid var(--app-border, #e4e7ed)}
:deep(.el-tabs--border-card>.el-tabs__header .el-tabs__item){color:var(--app-text-muted, #64748b)}
:deep(.el-tabs--border-card>.el-tabs__header .el-tabs__item.is-active){color:var(--el-color-primary, #409eff);background:var(--app-surface, #fff)}
:deep(.el-tabs--border-card>.el-tabs__content){background:var(--app-surface, #fff)}
.script-item { padding:4px 8px; border-bottom:1px solid var(--app-border, #f0f0f0); }
.script-item code { font-size:12px; word-break:break-all; }
.script-body { background:var(--app-surface-soft, #f5f7fa); padding:8px; border-radius:4px; font-size:12px; max-height:200px; overflow-y:auto; white-space:pre-wrap; margin:4px 0; }
.script-empty { color:var(--app-text-muted, #999); font-size:12px; padding:8px; }
.tag-palette { width:160px; border-right:1px solid var(--app-border, #e4e7ed); padding:8px; overflow-y:auto; background:var(--app-surface, #fafafa); flex-shrink:0; }
.palette-header { font-weight:600; font-size:13px; margin-bottom:8px; }
.palette-list { display:flex; flex-direction:column; gap:2px; }
.palette-item { padding:3px 6px; font-size:11px; background:var(--app-surface, #fff); border:1px solid var(--app-border, #dcdfe6); border-radius:3px; cursor:grab; user-select:none; white-space:nowrap; }
.palette-item:hover { background:color-mix(in srgb, var(--el-color-primary, #409eff) 14%, var(--app-surface, #fff)); border-color:var(--el-color-primary, #409eff); }
.ctx-menu { position:fixed; z-index:10000; background:var(--app-surface, #fff); border:1px solid var(--app-border, #e4e7ed); border-radius:6px; box-shadow:var(--app-card-shadow, 0 2px 12px rgba(0,0,0,.12)); padding:4px 0; min-width:140px; font-size:12px; color:var(--app-text, #303133); }
.ctx-item { padding:6px 16px; cursor:pointer; display:flex; align-items:center; gap:6px; }
.ctx-item:hover { background:color-mix(in srgb, var(--el-color-primary, #409eff) 14%, var(--app-surface, #fff)); }
.ctx-item.disabled { color:#c0c4cc; cursor:not-allowed; }
.ctx-item.disabled:hover { background:transparent; }
.ctx-item.danger { color:#f56c6c; }
.ctx-item.danger:hover { background:color-mix(in srgb, var(--el-color-danger, #f56c6c) 14%, var(--app-surface, #fff)); }
.ctx-item span { font-size:13px; }
.ctx-sep { height:1px; background:var(--app-border, #e4e7ed); margin:4px 0; }
.ctx-overlay { position:fixed; inset:0; z-index:9998; }
</style>
