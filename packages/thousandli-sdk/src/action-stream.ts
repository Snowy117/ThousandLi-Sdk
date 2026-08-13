export type ActionRuntimeEvent =
  | {
      type: 'started'
      gamePackageId?: string
      sessionId: string
      branchId: string
      actionId: string
      actionRunId: string
    }
  | {
      type: 'frontendEvent'
      frontendEvent: {
        eventType: string
        payload: Record<string, unknown>
      }
    }
  | {
      type: 'committed'
      terminalStatus: string
    }
  | {
      type: 'aborted'
      terminalStatus: string
      errorMessage?: string
    }
  | {
      type: 'error'
      title: string
      status: number
      detail: string
    }

export interface ActionStreamHandlers {
  onEvent: (event: ActionRuntimeEvent) => void
  onUnknown?: (raw: unknown) => void
}

const knownEventTypes = new Set(['started', 'frontendEvent', 'committed', 'aborted', 'error'])
const terminalEventTypes = new Set(['committed', 'aborted', 'error'])

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function isActionRuntimeEvent(value: unknown): value is ActionRuntimeEvent {
  if (!isRecord(value) || typeof value.type !== 'string') return false

  switch (value.type) {
    case 'started':
      return (
        typeof value.sessionId === 'string' &&
        typeof value.branchId === 'string' &&
        typeof value.actionId === 'string' &&
        typeof value.actionRunId === 'string' &&
        (value.gamePackageId === undefined || typeof value.gamePackageId === 'string')
      )
    case 'frontendEvent':
      return (
        isRecord(value.frontendEvent) &&
        typeof value.frontendEvent.eventType === 'string' &&
        isRecord(value.frontendEvent.payload)
      )
    case 'committed':
      return value.terminalStatus === 'Committed'
    case 'aborted':
      return (
        (value.terminalStatus === 'Aborted' || value.terminalStatus === 'Failed') &&
        (value.errorMessage === undefined || typeof value.errorMessage === 'string')
      )
    case 'error':
      return (
        typeof value.title === 'string' &&
        typeof value.status === 'number' &&
        Number.isFinite(value.status) &&
        typeof value.detail === 'string'
      )
    default:
      return false
  }
}

export async function parseActionStream(
  response: Response,
  handlers: ActionStreamHandlers,
): Promise<void> {
  const reader = response.body?.getReader()
  if (!reader) throw new Error('Action stream response does not have a readable body.')

  const decoder = new TextDecoder()
  let buffer = ''
  let lineNumber = 0
  let terminalSeen = false
  let startedSeen = false
  let transportErrorDetail: string | null = null

  const handleLine = (line: string, currentLineNumber: number): void => {
    const trimmed = line.trim()
    if (!trimmed) return

    let raw: unknown
    try {
      raw = JSON.parse(trimmed)
    } catch {
      throw new Error(`Action stream line ${currentLineNumber} is not valid JSON.`)
    }

    if (!isActionRuntimeEvent(raw)) {
      const type = isRecord(raw) ? raw.type : undefined
      if (typeof type === 'string' && knownEventTypes.has(type)) {
        throw new Error(`Action stream line ${currentLineNumber} has an invalid ${type} event.`)
      }
      handlers.onUnknown?.(raw)
      return
    }

    if (terminalSeen) {
      throw new Error(`Action stream line ${currentLineNumber} has event ${raw.type} after its terminal event.`)
    }

    if (raw.type === 'started') {
      if (startedSeen) throw new Error(`Action stream line ${currentLineNumber} has more than one started event.`)
      startedSeen = true
    } else if (raw.type !== 'error' && !startedSeen) {
      throw new Error(`Action stream line ${currentLineNumber} has event ${raw.type} before its started event.`)
    } else if (raw.type === 'error' && startedSeen) {
      throw new Error(`Action stream line ${currentLineNumber} has a transport error after its started event.`)
    }

    if (terminalEventTypes.has(raw.type)) {
      terminalSeen = true
    }

    handlers.onEvent(raw)
    if (raw.type === 'error') transportErrorDetail = raw.detail
  }

  while (true) {
    const { done, value } = await reader.read()
    if (done) break

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const line of lines) {
      lineNumber += 1
      handleLine(line, lineNumber)
    }
  }

  buffer += decoder.decode()
  if (buffer.trim()) {
    lineNumber += 1
    handleLine(buffer, lineNumber)
  }

  if (!terminalSeen) throw new Error('Action stream ended before a terminal event.')
  if (transportErrorDetail !== null) throw new Error(transportErrorDetail)
}
