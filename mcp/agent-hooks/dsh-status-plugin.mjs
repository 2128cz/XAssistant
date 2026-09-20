import { spawn } from 'node:child_process'

export const name = 'xassistant-dsh-status'

const DEFAULT_POWERSHELL = 'powershell.exe'
const DEFAULT_TIMEOUT_MS = 10_000

function clean(value, limit = 120) {
  if (value === undefined || value === null) return ''
  const text = String(value).replace(/[\r\n\t]+/gu, ' ').trim()
  return text.length > limit ? `${text.slice(0, limit)}...` : text
}

function titleOf(session) {
  const title = [...session.events].findLast(event => event.type === 'session/title')
  return title?.data?.title === undefined ? '' : clean(title.data.title, 80)
}

function base(session, eventName) {
  return {
    hook_event_name: eventName,
    session_id: String(session.id),
    cwd: session.header?.cwd ?? process.cwd(),
    session_title: titleOf(session),
  }
}

export function sessionEventPayload(session, event) {
  if (event.type !== 'turn/end') return null

  const reason = event.data.reason
  switch (reason.kind) {
    case 'completed':
      return { ...base(session, 'Stop'), stop_hook_active: false }
    case 'error':
      return {
        ...base(session, 'StopFailure'),
        error_type: clean(reason.error?.code, 40),
        error: clean(reason.error?.message, 120),
      }
    case 'max-tokens':
      return { ...base(session, 'Warning'), warning_type: 'token-limit', message: '输出达到 token 上限' }
    case 'blocked':
      return { ...base(session, 'Warning'), warning_type: 'blocked', message: '本轮请求被策略阻止' }
    case 'interrupted':
      return { ...base(session, 'StopFailure'), error_type: 'interrupted', error: '会话恢复时发现未正常结束的上一轮' }
    case 'aborted':
      if (reason.reason?.kind === 'user' || reason.reason?.kind === 'disposed') return null
      return {
        ...base(session, 'StopFailure'),
        error_type: clean(reason.reason?.kind, 40) || 'aborted',
        error: clean(reason.reason?.reason, 120),
      }
    default:
      return { ...base(session, 'Warning'), warning_type: clean(reason.kind, 40), message: '会话以未知状态结束' }
  }
}

export function approvalPayload(request) {
  return {
    ...base(request.agent.session, 'PermissionRequest'),
    tool_name: clean(request.toolName, 40),
    reason: clean(request.reason, 80),
  }
}

export function preToolPayload(exec) {
  if (exec.name !== 'ask_user_question' || exec.agent === undefined) return null
  return {
    ...base(exec.agent.session, 'Notification'),
    notification_type: 'ask_user_question',
    title: 'AskUserQuestion',
    message: '等待人类回答',
  }
}

function resultText(result) {
  const text = result.content
    ?.filter(block => block?.type === 'text')
    .map(block => block.text)
    .join(' ')
  return clean(text, 120)
}

export function toolResultPayload(exec, result) {
  if (!result.isError || exec.agent === undefined) return null
  const text = resultText(result)
  const detail = result.error?.code ? `${result.error.code}${text ? `: ${text}` : ''}` : text
  return {
    ...base(exec.agent.session, 'PostToolUseFailure'),
    tool_name: clean(exec.name, 40),
    tool_use_id: String(exec.callId),
    error: clean(detail, 120),
    is_interrupt: result.error?.code === 'ABORTED',
  }
}

function isRoot(ctx, agent) {
  if (agent === undefined) return false
  const registry = ctx.get?.('agents')
  if (registry === undefined) return true
  return registry.get(agent.id) === agent && registry.roots().includes(agent)
}

export function apply(ctx, config = {}) {
  const statusScript = clean(config.statusScript, 4096)
  if (!statusScript) throw new Error('xassistant-dsh-status: config.statusScript is required')

  const powershell = clean(config.powershell, 4096) || DEFAULT_POWERSHELL
  const timeoutMs = Number.isInteger(config.timeoutMs) && config.timeoutMs > 0
    ? config.timeoutMs
    : DEFAULT_TIMEOUT_MS
  const includeSubagents = config.includeSubagents === true
  const children = new Set()

  function accepts(agent) {
    return includeSubagents || isRoot(ctx, agent)
  }

  function send(payload) {
    if (payload === null) return
    let child
    try {
      child = spawn(powershell, [
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', statusScript,
        '-Platform', 'dsh',
      ], {
        windowsHide: true,
        stdio: ['pipe', 'ignore', 'ignore'],
      })
      children.add(child)
      const timer = setTimeout(() => child.kill(), timeoutMs)
      timer.unref?.()
      child.once('close', () => {
        clearTimeout(timer)
        children.delete(child)
      })
      child.once('error', (error) => {
        clearTimeout(timer)
        children.delete(child)
        ctx.logger?.warn?.(`xassistant-dsh-status: notification process failed: ${String(error)}`)
      })
      // The child error above owns diagnostics; a fast spawn failure may also reject the stdin pipe.
      child.stdin.on('error', () => undefined)
      child.stdin.end(`${JSON.stringify(payload)}\n`, 'utf8')
    } catch (error) {
      child?.kill()
      ctx.logger?.warn?.(`xassistant-dsh-status: notification spawn failed: ${String(error)}`)
    }
  }

  ctx.on('session/event', (session, event) => {
    const agent = ctx.get?.('agents')?.get(session.id)
    if (!accepts(agent)) return
    send(sessionEventPayload(session, event))
  })

  ctx.on('approval/request', (request, next) => {
    if (accepts(request.agent)) send(approvalPayload(request))
    return next()
  })

  ctx.on('tools/pre-execute', (exec, next) => {
    if (accepts(exec.agent)) send(preToolPayload(exec))
    return next()
  })

  ctx.on('tools/result', (exec, result) => {
    if (accepts(exec.agent)) send(toolResultPayload(exec, result))
  })

  ctx.effect(() => () => {
    for (const child of children) child.kill()
    children.clear()
  }, 'xassistant-dsh-status: stop notification processes')
}
