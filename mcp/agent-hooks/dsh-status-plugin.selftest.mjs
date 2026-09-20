import assert from 'node:assert/strict'
import { approvalPayload, preToolPayload, sessionEventPayload, toolResultPayload } from './dsh-status-plugin.mjs'

const session = {
  id: 'session-1',
  header: { cwd: 'G:\\repo' },
  events: [{ type: 'session/title', data: { title: 'DSH notification test' } }],
}
const agent = { id: session.id, session }

const completed = sessionEventPayload(session, {
  type: 'turn/end', data: { reason: { kind: 'completed' } },
})
assert.equal(completed.hook_event_name, 'Stop')
assert.equal(completed.session_title, 'DSH notification test')
assert.equal(completed.cwd, 'G:\\repo')

const failure = sessionEventPayload(session, {
  type: 'turn/end', data: { reason: { kind: 'error', error: { code: 'RATE_LIMIT', message: 'too many requests' } } },
})
assert.deepEqual(
  { event: failure.hook_event_name, type: failure.error_type, error: failure.error },
  { event: 'StopFailure', type: 'RATE_LIMIT', error: 'too many requests' },
)

assert.equal(sessionEventPayload(session, {
  type: 'turn/end', data: { reason: { kind: 'max-tokens' } },
}).hook_event_name, 'Warning')
assert.equal(sessionEventPayload(session, {
  type: 'turn/end', data: { reason: { kind: 'blocked' } },
}).warning_type, 'blocked')
assert.equal(sessionEventPayload(session, {
  type: 'turn/end', data: { reason: { kind: 'aborted', reason: { kind: 'user' } } },
}), null)

const approval = approvalPayload({ agent, toolName: 'pwsh', reason: 'needs wider access' })
assert.equal(approval.hook_event_name, 'PermissionRequest')
assert.equal(approval.tool_name, 'pwsh')

const question = preToolPayload({ name: 'ask_user_question', agent })
assert.equal(question.hook_event_name, 'Notification')
assert.equal(question.notification_type, 'ask_user_question')
assert.equal(preToolPayload({ name: 'read', agent }), null)

const toolFailure = toolResultPayload(
  { name: 'pwsh', callId: 'call-1', agent },
  { isError: true, error: { code: 'COMMAND_FAILED' }, content: [{ type: 'text', text: 'exit 1' }] },
)
assert.equal(toolFailure.hook_event_name, 'PostToolUseFailure')
assert.match(toolFailure.error, /COMMAND_FAILED/u)
assert.equal(toolResultPayload(
  { name: 'read', callId: 'call-2', agent },
  { isError: false, content: [{ type: 'text', text: 'ok' }] },
), null)

console.log('dsh-status-plugin: 15 assertions passed')
