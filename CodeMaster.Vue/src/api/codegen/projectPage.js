import request from '@/utils/request'
import { executeCodegenAction } from '@/utils/codegenExecution'

// Complex-page metadata is persisted separately from legacy entity CRUD pages.
export function getList(params = {}) {
  return request({ url: '/codegen/projectpage/getlist', method: 'get', params })
}

export function getPagedList(params = {}) {
  return request({ url: '/codegen/projectpage/getpagedlist', method: 'get', params })
}

export function getById(id) {
  return request({ url: `/codegen/projectpage/getbyid/${id}`, method: 'get' })
}

export function getDefinitionSnapshot(id) {
  return request({ url: `/codegen/projectpage/getdefinitionsnapshot/${id}`, method: 'get' })
}

export function create(data) {
  return request({ url: '/codegen/projectpage/create', method: 'post', data })
}

export function update(id, data) {
  return request({ url: `/codegen/projectpage/update/${id}`, method: 'put', data })
}

export function saveDefinition(id, data) {
  return request({ url: `/codegen/projectpage/savedefinition/${id}`, method: 'put', data })
}

export function rebindComponent(id, data) {
  return request({ url: `/codegen/projectpage/rebindcomponent/${id}`, method: 'put', data })
}

export function deleteById(id) {
  return request({ url: `/codegen/projectpage/delete/${id}`, method: 'delete' })
}

export function copy(id, pageKey, pageName) {
  return request({
    url: `/codegen/projectpage/copy/${id}`,
    method: 'post',
    params: { pageKey, pageName }
  })
}

export function getLayoutPresets() {
  return request({ url: '/codegen/projectpage/getlayoutpresets', method: 'get' })
}

export function getLayoutTree(presetKey) {
  return request({
    url: '/codegen/projectpage/getlayouttree',
    method: 'get',
    params: { presetKey }
  })
}

export function getEntityServiceCapabilities(projectId) {
  return request({ url: `/codegen/projectpage/getentityservicecapabilities/${projectId}`, method: 'get' })
}

export function getDataSourceCapabilityCatalog(projectId) {
  return request({ url: `/codegen/projectdatasource/getcapabilitycatalog/${projectId}`, method: 'get' })
}

export function getProjectDataSources(params = {}) {
  return request({ url: '/codegen/projectdatasource/getpagedlist', method: 'get', params })
}

export function createProjectDataSource(data) {
  return request({ url: '/codegen/projectdatasource/create', method: 'post', data })
}

export function updateProjectDataSource(id, data) {
  return request({ url: `/codegen/projectdatasource/updatewithimpact/${id}`, method: 'put', data })
}

export function deleteProjectDataSource(id) {
  return request({ url: `/codegen/projectdatasource/deletewithimpact/${id}`, method: 'delete' })
}

export function getOpenApiDocuments(projectId) {
  return request({ url: `/codegen/openapiimport/getdocuments/${projectId}`, method: 'get' })
}

export function previewOpenApiImport(data) {
  return request({ url: '/codegen/openapiimport/preview', method: 'post', data })
}

export function applyOpenApiImport(data) {
  return request({ url: '/codegen/openapiimport/apply', method: 'post', data })
}

export function getOpenApiSchemaTree(entityId) {
  return request({ url: `/codegen/openapiimport/getschematree/${entityId}`, method: 'get' })
}

export function getDisplayTemplates(params = {}) {
  return request({ url: '/codegen/pagedisplaytemplate/getlist', method: 'get', params })
}

export function getViewBuilderCatalog(projectId) {
  return request({ url: `/codegen/projectviewbuilder/getentitycatalog/${projectId}`, method: 'get' })
}

export function previewViewSql(data) {
  return request({ url: '/codegen/projectviewbuilder/previewsql', method: 'post', data })
}

export function createProjectView(data) {
  return request({ url: '/codegen/projectviewbuilder/createview', method: 'post', data })
}

export function getExistingViews(projectId) {
  return request({ url: `/codegen/projectviewbuilder/getexistingviews/${projectId}`, method: 'get' })
}

export function registerReadOnlyViewEntity(data) {
  return request({ url: '/codegen/projectviewbuilder/registerreadonlyviewentity', method: 'post', data })
}

export function importExistingView(data) {
  return request({ url: '/codegen/projectviewbuilder/importexistingview', method: 'post', data })
}

export function getAffectedPagesByEntityId(entityId) {
  return request({ url: `/codegen/projectpage/getaffectedpagesbyentityid/${entityId}`, method: 'get' })
}

export function markNeedsSync(entityId) {
  return request({ url: `/codegen/projectpage/markneedssync/${entityId}`, method: 'post' })
}

export function computeEntitySchemaHash(entityId) {
  return request({ url: `/codegen/projectpage/computeentityschemahash/${entityId}`, method: 'get' })
}

export function generate(id, projectId, incremental = true) {
  return executeCodegenAction('generateComplexPage', { pageId: id, projectId, incremental }, () =>
    request({
      url: `/codegen/complexpagegeneration/generate/${id}`,
      method: 'post',
      params: { incremental }
    })
  )
}

export function inspectExternalChange(id) {
  return request({ url: `/codegen/complexpageexternalsync/inspect/${id}`, method: 'get' })
}

export function importExternalTemplate(id, confirmed = false) {
  return request({
    url: `/codegen/complexpageexternalsync/import/${id}`,
    method: 'post',
    params: { confirmed }
  })
}

export function previewSynchronization(id) {
  return request({ url: `/codegen/complexpagesynchronization/preview/${id}`, method: 'get' })
}

export function synchronize(id) {
  return request({ url: `/codegen/complexpagesynchronization/synchronize/${id}`, method: 'post' })
}

export function synchronizeProject(projectId) {
  return request({ url: `/codegen/complexpagesynchronization/synchronizeproject/${projectId}`, method: 'post' })
}
