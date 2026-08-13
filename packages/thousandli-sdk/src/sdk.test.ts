import { describe, expect, it, vi } from 'vitest'
import { MSG_ACTION_URL, MSG_GET_ACTION_URL, MSG_GET_SESSION_CONTEXT, MSG_SESSION_CONTEXT } from './contract.js'
import { PlatformSdk, TimeoutError } from './sdk.js'

class TestWindow extends EventTarget {
  parent = { postMessage: vi.fn() }
}

function respond(window: TestWindow, message: unknown, origin = 'https://creator.test'): void {
  window.dispatchEvent(new MessageEvent('message', { data: message, origin }))
}

describe('PlatformSdk', () => {
  it('correlates and validates session context responses', async () => {
    const hostWindow = new TestWindow()
    const sdk = new PlatformSdk({ window: hostWindow as unknown as Window, parentWindow: hostWindow.parent })
    const request = sdk.getSessionContext()
    const outbound = hostWindow.parent.postMessage.mock.calls[0]?.[0] as { id: string; type: string }
    expect(outbound.type).toBe(MSG_GET_SESSION_CONTEXT)

    respond(hostWindow, {
      id: outbound.id,
      type: MSG_SESSION_CONTEXT,
      payload: {
        sessionId: 'session', gamePackageId: 'game', defaultBranchId: 'main', playerId: 'player',
        playerName: 'Creator', persona: '', expertPackageId: 'fake', committedState: { turn: 1 },
      },
    })
    await expect(request).resolves.toMatchObject({ sessionId: 'session', committedState: { turn: 1 } })
    sdk.destroy()
  })

  it('ignores wrong origins and wrong response types', async () => {
    vi.useFakeTimers()
    const hostWindow = new TestWindow()
    const sdk = new PlatformSdk({
      window: hostWindow as unknown as Window,
      parentWindow: hostWindow.parent,
      allowedOrigins: ['https://allowed.test'],
      timeoutMs: 10,
    })
    const request = sdk.getActionUrl({ type: 'advance' })
    const rejection = expect(request).rejects.toBeInstanceOf(TimeoutError)
    const outbound = hostWindow.parent.postMessage.mock.calls[0]?.[0] as { id: string }
    respond(hostWindow, { id: outbound.id, type: MSG_ACTION_URL, payload: { url: '/wrong' } }, 'https://wrong.test')
    respond(hostWindow, { id: outbound.id, type: MSG_SESSION_CONTEXT, payload: { url: '/wrong-type' } }, 'https://allowed.test')
    await vi.advanceTimersByTimeAsync(10)
    await rejection
    sdk.destroy()
    vi.useRealTimers()
  })

  it('requests an action URL and rejects invalid payloads', async () => {
    const hostWindow = new TestWindow()
    const sdk = new PlatformSdk({ window: hostWindow as unknown as Window, parentWindow: hostWindow.parent })
    const request = sdk.getActionUrl({ type: 'advance' })
    const outbound = hostWindow.parent.postMessage.mock.calls[0]?.[0] as { id: string; type: string; payload: unknown }
    expect(outbound).toMatchObject({ type: MSG_GET_ACTION_URL, payload: { actionPayload: { type: 'advance' } } })
    respond(hostWindow, { id: outbound.id, type: MSG_ACTION_URL, payload: { url: 42 } })
    await expect(request).rejects.toThrow('invalid')
    sdk.destroy()
  })

  it('rejects pending and future requests after destroy', async () => {
    const hostWindow = new TestWindow()
    const sdk = new PlatformSdk({ window: hostWindow as unknown as Window, parentWindow: hostWindow.parent })
    const pending = sdk.getSessionContext()
    sdk.destroy()
    await expect(pending).rejects.toThrow('destroyed')
    await expect(sdk.getSessionContext()).rejects.toThrow('destroyed')
  })
})
