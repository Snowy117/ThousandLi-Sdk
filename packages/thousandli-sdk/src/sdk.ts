import {
  DEFAULT_TIMEOUT_MS,
  MSG_GET_ACTION_URL,
  MSG_GET_REROLL_URL,
  MSG_GET_SESSION_CONTEXT,
  MSG_PLATFORM_EVENT,
  MSG_PROXY_REQUEST,
  MSG_REQUEST_PLATFORM_UI,
  PROTOCOL_PREFIX,
} from './contract.js'
import type {
  ActionUrlRequestPayload,
  PlatformMessage,
  PlatformUiType,
  ProxyRequestPayload,
  ProxyResponsePayload,
  SessionContext,
  SignedUrlPayload,
} from './types.js'

export interface PlatformSdkOptions {
  timeoutMs?: number
  allowedOrigins?: readonly string[]
  targetOrigin?: string
  window?: Window
  parentWindow?: Pick<Window, 'postMessage'>
}

interface PendingRequest {
  expectedType: string
  resolve: (value: unknown) => void
  reject: (reason: unknown) => void
  timer: ReturnType<typeof setTimeout>
}

type EventHandler = (payload: unknown) => void

export class TimeoutError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'TimeoutError'
  }
}

export class PlatformSdk {
  private readonly timeoutMs: number
  private readonly allowedOrigins: ReadonlySet<string> | null
  private readonly targetOrigin: string
  private readonly hostWindow: Window
  private readonly parentWindow: Pick<Window, 'postMessage'>
  private readonly pending = new Map<string, PendingRequest>()
  private readonly eventHandlers = new Map<string, Set<EventHandler>>()
  private readonly boundOnMessage: (event: MessageEvent) => void
  private destroyed = false

  constructor(options: PlatformSdkOptions = {}) {
    this.timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS
    this.allowedOrigins = options.allowedOrigins ? new Set(options.allowedOrigins) : null
    this.targetOrigin = options.targetOrigin ?? '*'
    this.hostWindow = options.window ?? window
    this.parentWindow = options.parentWindow ?? this.hostWindow.parent
    this.boundOnMessage = (event) => this.onMessage(event)
    this.hostWindow.addEventListener('message', this.boundOnMessage)
  }

  getSessionContext(): Promise<SessionContext> {
    return this.request(MSG_GET_SESSION_CONTEXT, `${PROTOCOL_PREFIX}sessionContext`, isSessionContext)
  }

  async getActionUrl(actionPayload: Record<string, unknown>): Promise<string> {
    const payload: ActionUrlRequestPayload = { actionPayload }
    const result = await this.request(MSG_GET_ACTION_URL, `${PROTOCOL_PREFIX}actionUrl`, isSignedUrl, payload)
    return result.url
  }

  async getRerollUrl(): Promise<string> {
    const result = await this.request(MSG_GET_REROLL_URL, `${PROTOCOL_PREFIX}rerollUrl`, isSignedUrl)
    return result.url
  }

  requestPlatformUi(uiType: PlatformUiType): Promise<unknown> {
    return this.request(MSG_REQUEST_PLATFORM_UI, MSG_REQUEST_PLATFORM_UI, isUnknown, { uiType })
  }

  proxyRequest(method: string, url: string, body?: unknown): Promise<ProxyResponsePayload> {
    const payload: ProxyRequestPayload = body === undefined ? { method, url } : { method, url, body }
    return this.request(MSG_PROXY_REQUEST, `${PROTOCOL_PREFIX}proxyResponse`, isProxyResponse, payload)
  }

  onPlatformEvent(type: string, handler: EventHandler): () => void {
    const handlers = this.eventHandlers.get(type) ?? new Set<EventHandler>()
    handlers.add(handler)
    this.eventHandlers.set(type, handlers)
    return () => {
      handlers.delete(handler)
      if (handlers.size === 0) this.eventHandlers.delete(type)
    }
  }

  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true
    this.hostWindow.removeEventListener('message', this.boundOnMessage)
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer)
      pending.reject(new Error('PlatformSdk was destroyed.'))
    }
    this.pending.clear()
    this.eventHandlers.clear()
  }

  private request<T>(
    type: string,
    expectedType: string,
    validate: (value: unknown) => value is T,
    payload?: unknown,
  ): Promise<T> {
    if (this.destroyed) return Promise.reject(new Error('PlatformSdk was destroyed.'))

    const id = generateId()
    const message: PlatformMessage = { id, type, payload }
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new TimeoutError(`postMessage response timed out after ${this.timeoutMs}ms.`))
      }, this.timeoutMs)

      this.pending.set(id, {
        expectedType,
        resolve: (value) => {
          if (!validate(value)) {
            reject(new Error(`Platform returned an invalid ${expectedType} payload.`))
            return
          }
          resolve(value)
        },
        reject,
        timer,
      })
      this.parentWindow.postMessage(message, this.targetOrigin)
    })
  }

  private onMessage(event: MessageEvent): void {
    if (event.source !== null && event.source !== this.parentWindow) return
    if (this.allowedOrigins !== null && !this.allowedOrigins.has(event.origin)) return
    if (!isPlatformMessage(event.data) || !event.data.type.startsWith(PROTOCOL_PREFIX)) return

    const pending = this.pending.get(event.data.id)
    if (pending) {
      if (event.data.type !== pending.expectedType && event.data.error === undefined) return
      clearTimeout(pending.timer)
      this.pending.delete(event.data.id)
      if (event.data.error !== undefined) pending.reject(new Error(event.data.error))
      else pending.resolve(event.data.payload)
      return
    }

    if (event.data.type !== MSG_PLATFORM_EVENT || !isRecord(event.data.payload)) return
    const eventType = event.data.payload.type
    if (typeof eventType !== 'string') return
    for (const handler of this.eventHandlers.get(eventType) ?? []) handler(event.data.payload)
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function isPlatformMessage(value: unknown): value is PlatformMessage {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.type === 'string' &&
    'payload' in value &&
    (value.error === undefined || typeof value.error === 'string')
  )
}

function isSessionContext(value: unknown): value is SessionContext {
  return (
    isRecord(value) &&
    typeof value.sessionId === 'string' &&
    typeof value.gamePackageId === 'string' &&
    typeof value.defaultBranchId === 'string' &&
    typeof value.playerId === 'string' &&
    typeof value.playerName === 'string' &&
    typeof value.persona === 'string' &&
    typeof value.expertPackageId === 'string'
  )
}

function isSignedUrl(value: unknown): value is SignedUrlPayload {
  return isRecord(value) && typeof value.url === 'string'
}

function isProxyResponse(value: unknown): value is ProxyResponsePayload {
  return isRecord(value) && typeof value.status === 'number' && Number.isInteger(value.status) && 'body' in value
}

function isUnknown(_value: unknown): _value is unknown {
  return true
}

function generateId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `${Date.now()}-${Math.random().toString(36).slice(2, 11)}`
}
