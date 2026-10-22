const I18N_CALL_PATTERN = /(?:\$t|\bt)\s*\(\s*(['"])([^'"\n]+)\1\s*\)/g

/**
 * Replace $t('key') / t('key') expressions for the designer preview only.
 * The stored component tree is never changed by this helper.
 */
export function resolveDesignTranslation(value, translations = {}, enabled = true) {
  if (!enabled || value === null || value === undefined || typeof value !== 'string') return value
  const source = value
  const lookup = key => {
    const trimmed = String(key || '').trim()
    if (!trimmed) return ''
    return translations[trimmed] ?? translations[trimmed.replace(/^['"]|['"]$/g, '')] ?? trimmed
  }

  const replaceCalls = text => text.replace(I18N_CALL_PATTERN, (_match, _quote, key) => lookup(key))
  return source.replace(/\{\{\s*([\s\S]*?)\s*\}\}/g, (whole, expression) => {
    if (!I18N_CALL_PATTERN.test(expression)) return whole
    I18N_CALL_PATTERN.lastIndex = 0
    return replaceCalls(expression).trim()
  }).replace(I18N_CALL_PATTERN, (_match, _quote, key) => lookup(key))
}

export function hasDesignTranslationExpression(value) {
  if (typeof value !== 'string') return false
  I18N_CALL_PATTERN.lastIndex = 0
  return I18N_CALL_PATTERN.test(value)
}

export function collectDesignTranslationKeys(nodes, target = new Set()) {
  for (const node of nodes || []) {
    const candidates = [node.content, node.label, ...(node.props || []).map(item => item.value)]
    for (const candidate of candidates) {
      if (typeof candidate !== 'string') continue
      I18N_CALL_PATTERN.lastIndex = 0
      let match
      while ((match = I18N_CALL_PATTERN.exec(candidate))) target.add(match[2])
    }
    collectDesignTranslationKeys(node.children, target)
    for (const slot of node.useSlots || []) collectDesignTranslationKeys(slot.components, target)
  }
  return target
}
