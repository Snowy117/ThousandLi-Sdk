<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  PlatformSdk,
  parseActionStream,
  type ActionRuntimeEvent,
  type SessionContext,
} from '@thousandli/sdk'

interface EventEntry {
  sequence: number
  label: string
  payload: unknown
  tone: 'active' | 'success' | 'danger' | 'neutral'
}

const sdk = new PlatformSdk()
const session = ref<SessionContext | null>(null)
const committedState = ref<unknown>(null)
const frontendSnapshot = ref<unknown>(null)
const events = ref<EventEntry[]>([])
const loading = ref(true)
const running = ref(false)
const reading = ref(false)
const error = ref('')
let sequence = 0
let actionCommitted = false

const stateText = computed(() => JSON.stringify(committedState.value, null, 2) ?? 'null')
const snapshotText = computed(() => JSON.stringify(frontendSnapshot.value, null, 2) ?? 'null')

onMounted(async () => {
  try {
    await refreshSession()
    await readState()
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    loading.value = false
  }
})

onBeforeUnmount(() => sdk.destroy())

async function refreshSession(): Promise<void> {
  const context = await sdk.getSessionContext()
  session.value = context
  if ('committedState' in context) committedState.value = context.committedState
}

async function runAction(): Promise<void> {
  if (running.value) return
  running.value = true
  error.value = ''
  events.value = []
  sequence = 0
  actionCommitted = false
  const action = { type: 'advance', command: 'continue' }

  try {
    const actionUrl = await sdk.getActionUrl(action)
    const response = await fetch(actionUrl, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(action),
    })
    if (!response.ok) throw new Error(await responseDetail(response))

    await parseActionStream(response, {
      onEvent: handleActionEvent,
      onUnknown: (payload) => appendEvent('Unknown event', payload, 'neutral'),
    })
    if (actionCommitted) await refreshSession()
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    running.value = false
  }
}

function handleActionEvent(event: ActionRuntimeEvent): void {
  switch (event.type) {
    case 'started':
      appendEvent('Action started', { actionId: event.actionId, actionRunId: event.actionRunId }, 'active')
      break
    case 'frontendEvent':
      frontendSnapshot.value = event.frontendEvent.payload
      appendEvent(event.frontendEvent.eventType, event.frontendEvent.payload, 'active')
      break
    case 'committed':
      actionCommitted = true
      appendEvent('Committed', { status: event.terminalStatus }, 'success')
      break
    case 'aborted':
      appendEvent('Aborted', { status: event.terminalStatus, error: event.errorMessage }, 'danger')
      break
    case 'error':
      appendEvent(event.title, { status: event.status, detail: event.detail }, 'danger')
      break
  }
}

async function readState(): Promise<void> {
  if (reading.value) return
  reading.value = true
  error.value = ''
  try {
    const response = await sdk.proxyRequest('POST', '/api/frontend-requests', { type: 'getState' })
    if (response.status < 200 || response.status >= 300) {
      throw new Error(`Frontend request failed with status ${response.status}.`)
    }
    frontendSnapshot.value = response.body
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    reading.value = false
  }
}

function appendEvent(label: string, payload: unknown, tone: EventEntry['tone']): void {
  sequence += 1
  events.value.push({ sequence, label, payload, tone })
}

async function responseDetail(response: Response): Promise<string> {
  const body = await response.text()
  if (!body) return `Action request failed with status ${response.status}.`
  try {
    const parsed: unknown = JSON.parse(body)
    if (isRecord(parsed) && typeof parsed.detail === 'string') return parsed.detail
  } catch {
    return body
  }
  return body
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function errorMessage(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason)
}
</script>

<template>
  <main class="tl-preview">
    <header class="tl-header">
      <div class="tl-identity">
        <span class="tl-mark" aria-hidden="true">TL</span>
        <div>
          <h1>ThousandLi.TemplateName</h1>
          <p>{{ session?.playerName ?? 'Connecting' }}</p>
        </div>
      </div>
      <span class="tl-status" :class="{ 'tl-status--ready': session && !error }">
        {{ loading ? 'Connecting' : error ? 'Needs attention' : 'Ready' }}
      </span>
    </header>

    <p v-if="error" class="tl-error" role="alert">{{ error }}</p>

    <section class="tl-workspace" aria-label="Game preview">
      <div class="tl-stage">
        <div class="tl-stage-head">
          <div>
            <span class="tl-kicker">Committed state</span>
            <h2>Current session</h2>
          </div>
          <span class="tl-session-id">{{ session?.sessionId ?? '---' }}</span>
        </div>
        <pre class="tl-state" data-testid="committed-state">{{ stateText }}</pre>
        <div class="tl-actions">
          <button class="tl-primary" type="button" :disabled="loading || running" @click="runAction">
            <span aria-hidden="true">▶</span>
            {{ running ? 'Running action' : 'Advance' }}
          </button>
          <button class="tl-secondary" type="button" :disabled="loading || reading" @click="readState">
            <span aria-hidden="true">↻</span>
            {{ reading ? 'Reading state' : 'Read state' }}
          </button>
        </div>
      </div>

      <aside class="tl-inspector" aria-label="Runtime inspector">
        <div class="tl-panel-head">
          <div>
            <span class="tl-kicker">Ordered stream</span>
            <h2>Action events</h2>
          </div>
          <span class="tl-count">{{ events.length }}</span>
        </div>
        <ol v-if="events.length" class="tl-events" aria-live="polite">
          <li v-for="entry in events" :key="entry.sequence" :class="`tl-event--${entry.tone}`">
            <span class="tl-sequence">{{ String(entry.sequence).padStart(2, '0') }}</span>
            <div>
              <strong>{{ entry.label }}</strong>
              <code>{{ JSON.stringify(entry.payload) }}</code>
            </div>
          </li>
        </ol>
        <p v-else class="tl-empty">No action events yet.</p>
        <div class="tl-readback">
          <span class="tl-kicker">Frontend request</span>
          <pre>{{ snapshotText }}</pre>
        </div>
      </aside>
    </section>
  </main>
</template>
