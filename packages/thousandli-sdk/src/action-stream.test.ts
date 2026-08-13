import { describe, expect, it, vi } from 'vitest'
import { isActionRuntimeEvent, parseActionStream, type ActionRuntimeEvent } from './action-stream.js'

const encoder = new TextEncoder()

function responseFromChunks(...chunks: Uint8Array[]): Response {
  return new Response(new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(chunk)
      controller.close()
    },
  }))
}

function responseFromLines(...lines: string[]): Response {
  return responseFromChunks(encoder.encode(lines.join('\n')))
}

describe('action stream', () => {
  it('validates each known event from unknown input', () => {
    expect(isActionRuntimeEvent({
      type: 'started',
      sessionId: 'session',
      branchId: 'main',
      actionId: 'action',
      actionRunId: 'run',
    })).toBe(true)
    expect(isActionRuntimeEvent({ type: 'frontendEvent', frontendEvent: { eventType: 'state', payload: [] } })).toBe(false)
    expect(isActionRuntimeEvent({ type: 'error', title: 'Error', status: Number.NaN, detail: 'bad' })).toBe(false)
  })

  it('decodes split UTF-8 chunks and an unterminated terminal line in order', async () => {
    const source = [
      JSON.stringify({ type: 'started', sessionId: 'session', branchId: 'main', actionId: 'action', actionRunId: 'run' }),
      JSON.stringify({ type: 'frontendEvent', frontendEvent: { eventType: 'narrative', payload: { text: '雪' } } }),
      JSON.stringify({ type: 'committed', terminalStatus: 'Committed' }),
    ].join('\n')
    const bytes = encoder.encode(source)
    const split = encoder.encode(source.slice(0, source.indexOf('雪'))).length + 1
    const events: ActionRuntimeEvent[] = []

    await parseActionStream(responseFromChunks(bytes.slice(0, split), bytes.slice(split)), {
      onEvent: (event) => events.push(event),
    })

    expect(events.map((event) => event.type)).toEqual(['started', 'frontendEvent', 'committed'])
    expect(events[1]).toMatchObject({ frontendEvent: { payload: { text: '雪' } } })
  })

  it('forwards unknown events without treating them as terminal', async () => {
    const onUnknown = vi.fn()
    await parseActionStream(responseFromLines(
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
      '{"type":"future","value":1}',
      '{"type":"committed","terminalStatus":"Committed"}',
    ), { onEvent: () => undefined, onUnknown })
    expect(onUnknown).toHaveBeenCalledWith({ type: 'future', value: 1 })
  })

  it.each([
    ['malformed JSON', ['{bad}'], 'line 1 is not valid JSON'],
    ['malformed known event', ['{"type":"started"}'], 'invalid started event'],
    ['missing terminal', ['{"type":"future"}'], 'ended before a terminal event'],
    ['terminal before started', ['{"type":"committed","terminalStatus":"Committed"}'], 'before its started event'],
    ['duplicate started', [
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
    ], 'more than one started event'],
    ['invalid terminal status', [
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
      '{"type":"committed","terminalStatus":"Failed"}',
    ], 'invalid committed event'],
    ['event after terminal', [
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
      '{"type":"committed","terminalStatus":"Committed"}',
      '{"type":"aborted","terminalStatus":"Failed"}',
    ], 'event aborted after its terminal event'],
    ['non-terminal event after terminal', [
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
      '{"type":"committed","terminalStatus":"Committed"}',
      '{"type":"frontendEvent","frontendEvent":{"eventType":"late","payload":{}}}',
    ], 'event frontendEvent after its terminal event'],
  ])('rejects %s', async (_case, lines, message) => {
    await expect(parseActionStream(responseFromLines(...lines), { onEvent: () => undefined })).rejects.toThrow(message)
  })

  it('delivers an error terminal and then rejects with its detail', async () => {
    const onEvent = vi.fn()
    await expect(parseActionStream(responseFromLines(
      '{"type":"error","title":"Failure","status":500,"detail":"backend failed"}',
    ), { onEvent })).rejects.toThrow('backend failed')
    expect(onEvent).toHaveBeenCalledWith({ type: 'error', title: 'Failure', status: 500, detail: 'backend failed' })
  })

  it('preserves reader and consumer exceptions', async () => {
    const readerError = new DOMException('cancelled', 'AbortError')
    const response = new Response(new ReadableStream({ start: (controller) => controller.error(readerError) }))
    await expect(parseActionStream(response, { onEvent: () => undefined })).rejects.toBe(readerError)

    const consumerError = new Error('consumer failed')
    await expect(parseActionStream(responseFromLines(
      '{"type":"started","sessionId":"session","branchId":"main","actionId":"action","actionRunId":"run"}',
      '{"type":"committed","terminalStatus":"Committed"}',
    ), { onEvent: (event) => { if (event.type === 'committed') throw consumerError } })).rejects.toBe(consumerError)
  })
})
