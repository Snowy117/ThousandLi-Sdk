export const FRONTEND_PROTOCOL_VERSION = Object.freeze({ major: 1, minor: 0 })
export const PROTOCOL_PREFIX = '@thousandli/'

export const MSG_GET_SESSION_CONTEXT = `${PROTOCOL_PREFIX}getSessionContext`
export const MSG_GET_ACTION_URL = `${PROTOCOL_PREFIX}getActionUrl`
export const MSG_GET_REROLL_URL = `${PROTOCOL_PREFIX}getRerollUrl`
export const MSG_PROXY_REQUEST = `${PROTOCOL_PREFIX}proxyRequest`
export const MSG_REQUEST_PLATFORM_UI = `${PROTOCOL_PREFIX}requestPlatformUi`

export const MSG_SESSION_CONTEXT = `${PROTOCOL_PREFIX}sessionContext`
export const MSG_ACTION_URL = `${PROTOCOL_PREFIX}actionUrl`
export const MSG_REROLL_URL = `${PROTOCOL_PREFIX}rerollUrl`
export const MSG_PROXY_RESPONSE = `${PROTOCOL_PREFIX}proxyResponse`
export const MSG_PLATFORM_EVENT = `${PROTOCOL_PREFIX}platformEvent`

export const DEFAULT_TIMEOUT_MS = 30_000

export type GameToPlatformType =
  | typeof MSG_GET_SESSION_CONTEXT
  | typeof MSG_GET_ACTION_URL
  | typeof MSG_GET_REROLL_URL
  | typeof MSG_PROXY_REQUEST
  | typeof MSG_REQUEST_PLATFORM_UI

export type PlatformToGameType =
  | typeof MSG_SESSION_CONTEXT
  | typeof MSG_ACTION_URL
  | typeof MSG_REROLL_URL
  | typeof MSG_PROXY_RESPONSE
  | typeof MSG_PLATFORM_EVENT
