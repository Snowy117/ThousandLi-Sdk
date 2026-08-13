export interface PlatformMessage {
  id: string
  type: string
  payload: unknown
  error?: string
}

export interface SessionContext {
  sessionId: string
  gamePackageId: string
  defaultBranchId: string
  playerId: string
  playerName: string
  persona: string
  expertPackageId: string
  committedState?: unknown
}

export type PlatformUiType = 'sessions' | 'players' | 'ai-settings'

export interface ProxyRequestPayload {
  method: string
  url: string
  body?: unknown
}

export interface ProxyResponsePayload {
  status: number
  body: unknown
}

export interface ActionUrlRequestPayload {
  actionPayload: Record<string, unknown>
}

export type RerollUrlRequestPayload = Record<string, never>

export interface SignedUrlPayload {
  url: string
}
