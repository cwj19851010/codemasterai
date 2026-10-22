export function takePendingDraft(pendingDraftRef) {
  const value = typeof pendingDraftRef?.value === 'string'
    ? pendingDraftRef.value
    : ''
  if (pendingDraftRef) pendingDraftRef.value = ''
  return value
}
