(() => {
  const page = document.querySelector('.forum-page')
  if (!page) return

  const API = '/api/community'
  const TOKEN_KEYS = ['Admin-Token', 'access_token', 'token']
  const topicId = page.dataset.topicId || null

  const getToken = () => TOKEN_KEYS.map(key => localStorage.getItem(key)).find(Boolean)

  /* ---------- 轻提示 ---------- */

  let toastTimer
  function toast(message, tone) {
    let el = document.querySelector('.forum-toast')
    if (!el) {
      el = document.createElement('div')
      el.className = 'forum-toast'
      el.setAttribute('role', 'status')
      el.setAttribute('aria-live', 'polite')
      document.body.appendChild(el)
    }
    el.textContent = message
    el.dataset.tone = tone || 'info'
    el.classList.add('show')
    clearTimeout(toastTimer)
    toastTimer = setTimeout(() => el.classList.remove('show'), 2800)
  }

  function requireLogin() {
    if (getToken()) return true
    toast('请先登录后再操作', 'warn')
    setTimeout(() => { window.location.href = '/register' }, 700)
    return false
  }

  /* ---------- API ---------- */

  async function api(path, options = {}) {
    const headers = Object.assign({}, options.headers)
    const token = getToken()
    if (token) headers.Authorization = `Bearer ${token}`
    if (options.body) headers['Content-Type'] = 'application/json'

    const response = await fetch(API + path, Object.assign({}, options, { headers }))
    const result = await response.json().catch(() => ({}))

    if (response.status === 401) {
      toast('登录状态已过期，请重新登录', 'warn')
      setTimeout(() => { window.location.href = '/register' }, 900)
      throw new Error('unauthorized')
    }
    if (!response.ok || (result.code !== undefined && result.code !== 200)) {
      throw new Error(result.message || '操作失败，请稍后再试')
    }
    return result.data
  }

  /* ---------- 相对时间 ---------- */

  function formatRelative(date) {
    const seconds = (Date.now() - date.getTime()) / 1000
    if (Number.isNaN(seconds)) return ''
    if (seconds < 60) return '刚刚'
    if (seconds < 3600) return Math.floor(seconds / 60) + ' 分钟前'
    if (seconds < 86400) return Math.floor(seconds / 3600) + ' 小时前'
    if (seconds < 86400 * 7) return Math.floor(seconds / 86400) + ' 天前'
    return date.toLocaleDateString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit' })
  }

  function refreshRelativeTimes() {
    document.querySelectorAll('time[data-relative]').forEach(el => {
      const date = new Date(el.getAttribute('datetime'))
      if (Number.isNaN(date.getTime())) return
      el.title = date.toLocaleString('zh-CN')
      el.textContent = formatRelative(date)
    })
  }

  refreshRelativeTimes()
  setInterval(refreshRelativeTimes, 60000)

  /* ---------- 代码高亮与复制 ---------- */

  function enhanceMarkdown(root) {
    if (window.hljs) {
      root.querySelectorAll('pre code').forEach(block => {
        if (block.dataset.highlighted === 'true') return
        try {
          window.hljs.highlightElement(block)
          block.dataset.highlighted = 'true'
        } catch (error) {
          /* ignore highlight failures */
        }
      })
    }

    root.querySelectorAll('pre').forEach(pre => {
      if (pre.querySelector('.code-copy')) return
      const button = document.createElement('button')
      button.type = 'button'
      button.className = 'code-copy'
      button.textContent = '复制'
      button.addEventListener('click', () => {
        const code = pre.querySelector('code')
        const text = code ? code.innerText : pre.innerText
        copyText(text)
          .then(() => {
            button.textContent = '已复制'
            setTimeout(() => { button.textContent = '复制' }, 1600)
          })
          .catch(() => toast('复制失败，请手动选择文本', 'warn'))
      })
      pre.appendChild(button)
    })
  }

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text)
    }
    return new Promise((resolve, reject) => {
      const area = document.createElement('textarea')
      area.value = text
      area.setAttribute('readonly', '')
      area.style.position = 'fixed'
      area.style.opacity = '0'
      document.body.appendChild(area)
      area.select()
      const ok = document.execCommand('copy')
      document.body.removeChild(area)
      ok ? resolve() : reject(new Error('copy failed'))
    })
  }

  enhanceMarkdown(document)

  /* ---------- Markdown 编辑器 ---------- */

  function applyMarkdown(textarea, type) {
    const value = textarea.value
    const start = textarea.selectionStart
    const end = textarea.selectionEnd
    const selected = value.slice(start, end)

    const wrap = (before, after, placeholder) => {
      const text = selected || placeholder
      textarea.value = value.slice(0, start) + before + text + after + value.slice(end)
      textarea.focus()
      const from = start + before.length
      textarea.setSelectionRange(from, from + text.length)
    }

    const prefixLines = prefix => {
      const lineStart = value.lastIndexOf('\n', start - 1) + 1
      const block = value.slice(lineStart, end)
      const next = value.slice(0, lineStart) +
        block.split('\n').map(line => prefix + line).join('\n') +
        value.slice(end)
      const added = next.length - value.length
      textarea.value = next
      textarea.focus()
      textarea.setSelectionRange(lineStart + added, lineStart + added + (end - lineStart))
    }

    switch (type) {
      case 'bold':
        wrap('**', '**', '加粗文字')
        break
      case 'italic':
        wrap('*', '*', '斜体文字')
        break
      case 'code':
        wrap('`', '`', 'code')
        break
      case 'link':
        wrap('[', '](https://)', '链接文字')
        break
      case 'quote':
        prefixLines('> ')
        break
      case 'list':
        prefixLines('- ')
        break
      case 'codeblock':
        wrap('\n```csharp\n', '\n```\n', '// 在这里粘贴代码')
        break
      default:
        break
    }
  }

  function switchEditorTab(editor, mode) {
    const textarea = editor.querySelector('textarea')
    const preview = editor.querySelector('.md-preview')
    if (!textarea) return

    editor.querySelectorAll('[data-md-tab]').forEach(button => {
      button.classList.toggle('active', button.dataset.mdTab === mode)
    })

    if (mode === 'preview') {
      textarea.hidden = true
      if (preview) {
        preview.hidden = false
        renderPreview(editor, preview, textarea.value)
      }
    } else {
      textarea.hidden = false
      if (preview) preview.hidden = true
    }
  }

  function renderPreview(editor, preview, content) {
    if (!content.trim()) {
      preview.innerHTML = '<p class="forum-muted">还没有内容可预览。</p>'
      return
    }
    preview.innerHTML = '<p class="forum-muted">正在渲染…</p>'
    fetch(API + '/preview', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content })
    })
      .then(response => response.json())
      .then(result => {
        const html = result && result.data && result.data.html
        preview.innerHTML = html || '<p class="forum-muted">没有内容。</p>'
        enhanceMarkdown(preview)
      })
      .catch(() => {
        preview.innerHTML = '<p class="forum-muted">预览失败，请稍后再试。</p>'
      })
  }

  document.querySelectorAll('.md-editor').forEach(editor => {
    const textarea = editor.querySelector('textarea')
    const toolbar = editor.querySelector('.md-toolbar')
    if (!textarea || !toolbar) return

    toolbar.addEventListener('click', event => {
      const tab = event.target.closest('[data-md-tab]')
      if (tab) {
        switchEditorTab(editor, tab.dataset.mdTab)
        return
      }
      const tool = event.target.closest('[data-md]')
      if (tool) applyMarkdown(textarea, tool.dataset.md)
    })
  })

  /* ---------- 发帖 ---------- */

  const composer = document.getElementById('composerPanel')

  function openComposer() {
    if (!requireLogin() || !composer) return
    composer.hidden = false
    composer.scrollIntoView({ behavior: 'smooth', block: 'start' })
    const titleInput = composer.querySelector('input[name="title"]')
    if (titleInput) titleInput.focus({ preventScroll: true })
  }

  document.querySelectorAll('[data-open-composer]').forEach(button => {
    button.addEventListener('click', event => {
      event.preventDefault()
      openComposer()
    })
  })

  document.getElementById('closeComposer')?.addEventListener('click', () => {
    if (composer) composer.hidden = true
  })

  const topicForm = document.getElementById('topicForm')
  topicForm?.addEventListener('submit', async event => {
    event.preventDefault()
    if (!requireLogin()) return

    const form = event.currentTarget
    const fields = form.elements
    const message = form.querySelector('[data-form-msg]')
    const submit = form.querySelector('button[type="submit"]')
    const title = fields.title.value.trim()
    const content = fields.content.value

    if (title.length < 4) {
      message.textContent = '标题至少 4 个字。'
      fields.title.focus()
      return
    }
    if (content.trim().length < 10) {
      message.textContent = '正文至少 10 个字，方便别人理解你的问题。'
      return
    }

    message.textContent = '正在发布…'
    submit.disabled = true
    try {
      const data = await api('/topics', {
        method: 'POST',
        body: JSON.stringify({
          categoryId: fields.categoryId.value,
          title,
          content
        })
      })
      message.textContent = '发布成功，正在跳转…'
      window.location.href = '/community/topic/' + data.id
    } catch (error) {
      message.textContent = error.message
      submit.disabled = false
    }
  })

  /* ---------- 详情页：点赞 / 分享 / 删除 ---------- */

  const likeButton = document.querySelector('[data-like]')
  likeButton?.addEventListener('click', async () => {
    if (!requireLogin() || !topicId) return
    likeButton.disabled = true
    try {
      const data = await api('/topics/' + topicId + '/like', { method: 'POST' })
      likeButton.classList.toggle('active', Boolean(data.liked))
      likeButton.setAttribute('aria-pressed', data.liked ? 'true' : 'false')
      const count = likeButton.querySelector('[data-like-count]')
      if (count) count.textContent = data.likeCount
    } catch (error) {
      toast(error.message, 'warn')
    } finally {
      likeButton.disabled = false
    }
  })

  document.querySelector('[data-share]')?.addEventListener('click', () => {
    copyText(window.location.href)
      .then(() => toast('主题链接已复制'))
      .catch(() => toast('复制失败，请从地址栏复制', 'warn'))
  })

  document.querySelector('[data-scroll-reply]')?.addEventListener('click', () => {
    const composerEl = document.getElementById('replyComposer')
    composerEl?.scrollIntoView({ behavior: 'smooth', block: 'center' })
    composerEl?.querySelector('textarea')?.focus({ preventScroll: true })
  })

  document.querySelector('[data-delete-topic]')?.addEventListener('click', async () => {
    if (!topicId) return
    if (!window.confirm('删除后主题和它的回复都会被移除，确认删除？')) return
    try {
      await api('/topics/' + topicId, { method: 'DELETE' })
      toast('主题已删除')
      setTimeout(() => { window.location.href = '/community' }, 600)
    } catch (error) {
      toast(error.message, 'warn')
    }
  })

  document.querySelectorAll('[data-flag]').forEach(button => {
    button.addEventListener('click', async () => {
      if (!topicId) return
      const key = button.dataset.flag
      const current = button.classList.contains('is-on')
      const payload = {}
      payload['is' + key.charAt(0).toUpperCase() + key.slice(1)] = !current
      button.disabled = true
      try {
        await api('/topics/' + topicId + '/flags', {
          method: 'POST',
          body: JSON.stringify(payload)
        })
        toast('已更新')
        setTimeout(() => window.location.reload(), 500)
      } catch (error) {
        toast(error.message, 'warn')
        button.disabled = false
      }
    })
  })

  /* ---------- 详情页：编辑主题 ---------- */

  const editPanel = document.getElementById('topicEditPanel')
  const editForm = document.getElementById('topicEditForm')
  const editSource = document.getElementById('topicEditSource')

  document.querySelector('[data-edit-topic]')?.addEventListener('click', () => {
    if (!editPanel || !editForm || !editSource) return
    const fields = editForm.elements
    editPanel.hidden = false
    fields.title.value = page.dataset.topicTitle || ''
    fields.content.value = editSource.value
    const editor = editForm.querySelector('.md-editor')
    if (editor) switchEditorTab(editor, 'write')
    editPanel.scrollIntoView({ behavior: 'smooth', block: 'start' })
    fields.title.focus({ preventScroll: true })
  })

  document.getElementById('cancelEditTopic')?.addEventListener('click', () => {
    if (editPanel) editPanel.hidden = true
  })

  editForm?.addEventListener('submit', async event => {
    event.preventDefault()
    if (!topicId) return
    const form = event.currentTarget
    const fields = form.elements
    const message = form.querySelector('[data-form-msg]')
    const submit = form.querySelector('button[type="submit"]')
    message.textContent = '正在保存…'
    submit.disabled = true
    try {
      await api('/topics/' + topicId, {
        method: 'PUT',
        body: JSON.stringify({ title: fields.title.value.trim(), content: fields.content.value })
      })
      toast('已保存')
      setTimeout(() => window.location.reload(), 500)
    } catch (error) {
      message.textContent = error.message
      submit.disabled = false
    }
  })

  /* ---------- 详情页：回复相关 ---------- */

  let replyTargetId = null

  document.querySelectorAll('[data-reply-to]').forEach(button => {
    button.addEventListener('click', () => {
      if (!requireLogin()) return
      replyTargetId = button.dataset.replyTo
      const chip = document.getElementById('replyTarget')
      const name = document.getElementById('replyTargetName')
      if (chip && name) {
        name.textContent = button.dataset.replyName || ''
        chip.hidden = false
      }
      const composerEl = document.getElementById('replyComposer')
      composerEl?.scrollIntoView({ behavior: 'smooth', block: 'center' })
      composerEl?.querySelector('textarea')?.focus({ preventScroll: true })
    })
  })

  document.getElementById('clearReplyTarget')?.addEventListener('click', () => {
    replyTargetId = null
    const chip = document.getElementById('replyTarget')
    if (chip) chip.hidden = true
  })

  const replyForm = document.getElementById('replyForm')
  replyForm?.addEventListener('submit', async event => {
    event.preventDefault()
    if (!requireLogin() || !topicId) return

    const form = event.currentTarget
    const fields = form.elements
    const message = form.querySelector('[data-form-msg]')
    const submit = form.querySelector('button[type="submit"]')
    const content = fields.content.value

    if (content.trim().length < 2) {
      message.textContent = '回复内容太短了。'
      return
    }

    message.textContent = '正在发布…'
    submit.disabled = true
    try {
      await api('/replies', {
        method: 'POST',
        body: JSON.stringify({
          topicId,
          parentReplyId: replyTargetId,
          content
        })
      })
      window.location.reload()
    } catch (error) {
      message.textContent = error.message
      submit.disabled = false
    }
  })

  document.querySelectorAll('[data-accept]').forEach(button => {
    button.addEventListener('click', async () => {
      if (!requireLogin() || !topicId) return
      const accepted = button.dataset.accepted === 'true'
      button.disabled = true
      try {
        await api('/topics/' + topicId + '/accept/' + button.dataset.accept, { method: 'POST' })
        toast(accepted ? '已取消采纳' : '已采纳这条回复')
        setTimeout(() => window.location.reload(), 500)
      } catch (error) {
        toast(error.message, 'warn')
        button.disabled = false
      }
    })
  })

  document.querySelectorAll('[data-delete-reply]').forEach(button => {
    button.addEventListener('click', async () => {
      const reply = button.closest('.forum-reply')
      if (!reply) return
      if (!window.confirm('确认删除这条回复？')) return
      button.disabled = true
      try {
        await api('/replies/' + reply.dataset.replyId, { method: 'DELETE' })
        toast('回复已删除')
        setTimeout(() => window.location.reload(), 500)
      } catch (error) {
        toast(error.message, 'warn')
        button.disabled = false
      }
    })
  })

  document.querySelectorAll('[data-edit-reply]').forEach(button => {
    button.addEventListener('click', () => {
      const reply = button.closest('.forum-reply')
      if (!reply) return
      const body = reply.querySelector('.forum-reply-body')
      const source = reply.querySelector('.forum-edit-source')
      if (!body || !source) return

      const existing = reply.querySelector('.forum-reply-edit')
      if (existing) {
        existing.hidden = false
        body.hidden = true
        existing.querySelector('textarea')?.focus()
        return
      }

      const form = document.createElement('form')
      form.className = 'forum-reply-edit'
      const textarea = document.createElement('textarea')
      textarea.rows = 6
      textarea.value = source.value
      const foot = document.createElement('div')
      foot.className = 'forum-form-foot'
      const save = document.createElement('button')
      save.type = 'submit'
      save.className = 'forum-btn primary'
      save.textContent = '保存'
      const cancel = document.createElement('button')
      cancel.type = 'button'
      cancel.className = 'forum-btn'
      cancel.textContent = '取消'
      const message = document.createElement('span')
      message.className = 'forum-form-msg'
      foot.append(save, cancel, message)
      form.append(textarea, foot)
      body.after(form)
      body.hidden = true
      textarea.focus()

      cancel.addEventListener('click', () => {
        form.hidden = true
        body.hidden = false
      })

      form.addEventListener('submit', async event => {
        event.preventDefault()
        if (!requireLogin()) return
        const content = textarea.value
        if (content.trim().length < 2) {
          message.textContent = '回复内容太短了。'
          return
        }
        message.textContent = '正在保存…'
        save.disabled = true
        try {
          await api('/replies/' + reply.dataset.replyId, {
            method: 'PUT',
            body: JSON.stringify({ content })
          })
          toast('已保存')
          setTimeout(() => window.location.reload(), 500)
        } catch (error) {
          message.textContent = error.message
          save.disabled = false
        }
      })
    })
  })

  /* ---------- 会话同步：localStorage 令牌 → 服务端可读 Cookie ---------- */

  async function syncCookieSession() {
    if (page.dataset.loggedIn === 'true') return
    const token = getToken()
    if (!token) return

    const guardKey = 'cm-session-token'
    const marker = token.slice(-16)
    if (sessionStorage.getItem(guardKey) === marker) return

    sessionStorage.setItem(guardKey, marker)
    try {
      const response = await fetch('/api/auth/session', {
        method: 'POST',
        headers: { Authorization: 'Bearer ' + token }
      })
      if (!response.ok) {
        sessionStorage.removeItem(guardKey)
        return
      }
      window.location.reload()
    } catch {
      sessionStorage.removeItem(guardKey)
    }
  }

  syncCookieSession()

  /* ---------- 快捷键 ---------- */

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && composer && !composer.hidden) {
      composer.hidden = true
    }
  })
})()
