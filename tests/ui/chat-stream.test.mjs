// Unit tests for the streaming Chat UI logic (FE-002). No dependencies: Node's built-in test runner and Web Streams.
//   node --test tests/ui
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../BC-LocalAI/wwwroot/js/chat-core.js');
const s = require('../../BC-LocalAI/wwwroot/js/chat-stream.js');

const sse = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
const STARTED = sse('started', { model: 'qwen3:4b' });
const COMPLETED = sse('completed', { finishReason: 'stop', evalCount: 7 });
const delta = (text) => sse('delta', { text });

function parseAll(chunks) {
    const events = [];
    const parser = s.createSseParser((e) => events.push(e));
    for (const chunk of chunks) parser.feed(chunk);
    return events;
}

function run(events) {
    const session = s.createStreamSession();
    let last = session.snapshot();
    for (const [event, data] of events) {
        last = session.handle({ event, data: typeof data === 'string' ? data : JSON.stringify(data) });
    }
    return { session, last };
}

/** A fetch() Response whose body delivers the given byte chunks, then closes (or errors / stays open). */
function streamResponse(chunks, { status = 200, contentType = 'text/event-stream; charset=utf-8', end = 'close' } = {}) {
    const encoder = new TextEncoder();
    const queue = chunks.map((chunk) => (typeof chunk === 'string' ? encoder.encode(chunk) : chunk));
    // One chunk per read, like a network: an error is raised only after everything before it was read.
    const body = new ReadableStream({
        pull(controller) {
            if (queue.length > 0) {
                controller.enqueue(queue.shift());
            } else if (end === 'close') {
                controller.close();
            } else if (end instanceof Error) {
                controller.error(end);
            } else {
                return new Promise(() => {}); // end === 'open': never closes
            }
            return undefined;
        }
    }, { highWaterMark: 0 });
    return new Response(body, { status, headers: { 'Content-Type': contentType } });
}

const abortError = () => new DOMException('The operation was aborted.', 'AbortError');

// ---------------------------------------------------------------- SSE parser

test('parser: one event per blank line, name and JSON data', () => {
    const events = parseAll([STARTED + delta('สวัสดี') + COMPLETED]);
    assert.deepEqual(events.map((e) => e.event), ['started', 'delta', 'completed']);
    assert.deepEqual(JSON.parse(events[1].data), { text: 'สวัสดี' });
});

test('parser: the same events come out however the text is split, at every position', () => {
    const whole = STARTED + delta('ภาษาไทย 😊\nบรรทัดสอง') + delta(' <b>x</b>') + COMPLETED;
    const expected = parseAll([whole]);
    for (let i = 0; i <= whole.length; i += 1) {
        assert.deepEqual(parseAll([whole.slice(0, i), whole.slice(i)]), expected, `split at ${i}`);
    }
    assert.deepEqual(parseAll([...whole]), expected, 'one character at a time');
});

test('parser: CRLF and CR line endings, including a CRLF split across chunks', () => {
    const crlf = STARTED.replace(/\n/g, '\r\n') + delta('a').replace(/\n/g, '\r');
    const expected = [{ event: 'started', data: '{"model":"qwen3:4b"}' }, { event: 'delta', data: '{"text":"a"}' }];
    assert.deepEqual(parseAll([crlf]), expected);
    const cut = crlf.indexOf('\r\n') + 1;
    assert.deepEqual(parseAll([crlf.slice(0, cut), crlf.slice(cut)]), expected);
});

test('parser: comments, unknown fields and events without data are ignored; data lines are joined', () => {
    const events = parseAll([': keep-alive\n\nid: 7\nretry: 100\nevent: delta\ndata: {"text":\ndata: "x"}\n\nevent: empty\n\n']);
    assert.deepEqual(events, [{ event: 'delta', data: '{"text":\n"x"}' }]);
});

test('parser: "data:" without a space and an event without a name', () => {
    assert.deepEqual(parseAll(['data:{"a":1}\n\n']), [{ event: 'message', data: '{"a":1}' }]);
});

test('parser: an event cut off before its blank line is never delivered', () => {
    assert.deepEqual(parseAll([STARTED + 'event: completed\ndata: {"finishReason":"stop"}\n']).map((e) => e.event), ['started']);
});

// ---------------------------------------------------------------- answer session (states and contract rules)

test('session: connecting -> accepted -> streaming -> completed, text appended in order without changes', () => {
    const session = s.createStreamSession();
    assert.equal(session.snapshot().state, 'connecting');
    assert.equal(session.handle({ event: 'started', data: '{"model":"qwen3:4b"}' }).state, 'accepted');
    session.handle({ event: 'delta', data: JSON.stringify({ text: '  สวัส' }) });
    session.handle({ event: 'delta', data: JSON.stringify({ text: 'ดี\n\n😊 é ' }) });
    const done = session.handle({ event: 'completed', data: '{"finishReason":"stop","evalCount":9}' });
    assert.equal(done.state, 'completed');
    assert.equal(done.text, '  สวัสดี\n\n😊 é ');
    assert.equal(done.model, 'qwen3:4b');
    assert.equal(done.code, null);
});

test('session: markup in an answer is kept as plain text, unchanged', () => {
    const { last } = run([['started', { model: 'm' }], ['delta', { text: '<img src=x onerror=alert(1)><script>x</script>' }], ['completed', {}]]);
    assert.equal(last.text, '<img src=x onerror=alert(1)><script>x</script>');
});

for (const [name, events] of [
    ['delta before started', [['delta', { text: 'a' }]]],
    ['completed before started', [['completed', {}]]],
    ['completed with no text', [['started', { model: 'm' }], ['completed', {}]]],
    ['completed with only whitespace', [['started', { model: 'm' }], ['delta', { text: ' \n ' }], ['completed', {}]]],
    ['started twice', [['started', { model: 'm' }], ['started', { model: 'm' }]]],
    ['started without a model', [['started', { model: '' }]]],
    ['delta text not a string', [['started', { model: 'm' }], ['delta', { text: 5 }]]],
    ['data is not JSON', [['started', { model: 'm' }], ['delta', '{bad']]],
    ['data is a JSON array', [['started', '["m"]']]]
]) {
    test(`session: ${name} is an incomplete answer (AI_INVALID_RESPONSE), never completed`, () => {
        const { last } = run(events);
        assert.equal(last.state, 'error');
        assert.equal(last.code, 'AI_INVALID_RESPONSE');
        assert.equal(last.message, core.messageFor('AI_INVALID_RESPONSE'));
    });
}

test('session: server error keeps the partial text and shows the text for its code', () => {
    const { last } = run([['started', { model: 'm' }], ['delta', { text: 'ครึ่ง' }], ['error', { code: 'AI_TIMEOUT', message: 'server detail', partial: true }]]);
    assert.equal(last.state, 'error');
    assert.equal(last.code, 'AI_TIMEOUT');
    assert.equal(last.text, 'ครึ่ง');
    assert.equal(last.partial, true);
    assert.equal(last.message, core.messageFor('AI_TIMEOUT'));
    assert.ok(!last.message.includes('server detail'));
});

test('session: every BE-004 error code has its own Thai text; unknown codes fall back safely', () => {
    for (const code of ['AI_UNAVAILABLE', 'AI_TIMEOUT', 'AI_PROVIDER_ERROR', 'AI_INVALID_RESPONSE', 'AI_RESPONSE_TOO_LARGE', 'INTERNAL_ERROR']) {
        const { last } = run([['started', { model: 'm' }], ['error', { code, message: 'x', partial: false }]]);
        assert.equal(last.code, code);
        assert.equal(last.message, core.MESSAGES[code]);
    }
    assert.equal(run([['error', { code: 'SOMETHING_NEW' }]]).last.code, 'INTERNAL_ERROR');
    assert.equal(run([['error', '{}']]).last.code, 'INTERNAL_ERROR');
});

test('session: exactly one outcome; nothing after it changes the answer', () => {
    const { session, last } = run([['started', { model: 'm' }], ['delta', { text: 'ok' }], ['completed', {}]]);
    assert.equal(session.handle({ event: 'delta', data: '{"text":"extra"}' }).text, 'ok');
    assert.equal(session.handle({ event: 'error', data: '{"code":"AI_TIMEOUT"}' }).state, 'completed');
    assert.equal(session.end().state, 'completed');
    assert.equal(session.cancel().state, 'completed');
    assert.deepEqual(session.snapshot(), last);

    const failed = run([['started', { model: 'm' }], ['error', { code: 'AI_TIMEOUT' }]]).session;
    assert.equal(failed.handle({ event: 'completed', data: '{}' }).state, 'error');
});

test('session: server "cancelled" event and unknown events', () => {
    assert.equal(run([['started', { model: 'm' }], ['cancelled', {}]]).last.state, 'cancelled');
    assert.equal(run([['started', { model: 'm' }], ['heartbeat', {}]]).last.state, 'accepted');
});

test('session: the connection ending without an outcome is STREAM_INTERRUPTED, with the partial text kept', () => {
    const { session } = run([['started', { model: 'm' }], ['delta', { text: 'ครึ่งทาง' }]]);
    const last = session.end();
    assert.equal(last.state, 'error');
    assert.equal(last.code, 'STREAM_INTERRUPTED');
    assert.equal(last.text, 'ครึ่งทาง');
    assert.equal(last.partial, true);
    assert.equal(last.message, core.MESSAGES.STREAM_INTERRUPTED);
});

test('session: Stop keeps what arrived and is never shown as completed', () => {
    const { session } = run([['started', { model: 'm' }], ['delta', { text: 'บาง' }]]);
    const last = session.cancel();
    assert.equal(last.state, 'cancelled');
    assert.equal(last.text, 'บาง');
    assert.equal(last.partial, true);
    assert.equal(s.createStreamSession().cancel().partial, false);
});

test('session: sessions share nothing', () => {
    const a = s.createStreamSession();
    const b = s.createStreamSession();
    a.handle({ event: 'started', data: '{"model":"m"}' });
    b.handle({ event: 'started', data: '{"model":"m"}' });
    a.handle({ event: 'delta', data: '{"text":"A"}' });
    b.handle({ event: 'delta', data: '{"text":"B"}' });
    assert.equal(a.snapshot().text, 'A');
    assert.equal(b.snapshot().text, 'B');
});

// ---------------------------------------------------------------- readAnswer: a real Response / ReadableStream

test('readAnswer: complete answer, every state reported in order', async () => {
    const states = [];
    const final = await s.readAnswer(streamResponse([STARTED, delta('สวัสดี'), delta('ครับ'), COMPLETED]), (snap) => states.push(snap.state));
    assert.equal(final.state, 'completed');
    assert.equal(final.text, 'สวัสดีครับ');
    assert.deepEqual(states, ['accepted', 'streaming', 'streaming', 'completed']);
});

test('readAnswer: Thai and emoji split in the middle of a character, byte by byte', async () => {
    const bytes = new TextEncoder().encode(STARTED + delta('ภาษาไทย 😊 ok') + COMPLETED);
    const chunks = [...bytes].map((b) => new Uint8Array([b]));
    const final = await s.readAnswer(streamResponse(chunks), null);
    assert.equal(final.state, 'completed');
    assert.equal(final.text, 'ภาษาไทย 😊 ok');
});

test('readAnswer: HTTP 400 before the stream uses the JSON error contract', async () => {
    const response = new Response(JSON.stringify({ error: { code: 'INVALID_MESSAGE', message: 'Message is required' } }),
        { status: 400, headers: { 'Content-Type': 'application/json' } });
    const final = await s.readAnswer(response, null);
    assert.equal(final.state, 'error');
    assert.equal(final.code, 'INVALID_MESSAGE');
    assert.equal(final.message, core.MESSAGES.INVALID_MESSAGE);
});

test('readAnswer: an HTTP error that is not JSON is a safe INTERNAL_ERROR', async () => {
    const final = await s.readAnswer(new Response('<html>502 Bad Gateway</html>', { status: 502 }), null);
    assert.equal(final.code, 'INTERNAL_ERROR');
    assert.ok(!final.message.includes('Bad Gateway'));
});

test('readAnswer: a 200 that is not an event stream is AI_INVALID_RESPONSE', async () => {
    const final = await s.readAnswer(new Response('{"response":"x","model":"m"}', { status: 200, headers: { 'Content-Type': 'application/json' } }), null);
    assert.equal(final.code, 'AI_INVALID_RESPONSE');
});

test('readAnswer: the connection closes before the outcome -> STREAM_INTERRUPTED, partial text kept', async () => {
    const final = await s.readAnswer(streamResponse([STARTED, delta('ยังไม่จบ')]), null);
    assert.equal(final.state, 'error');
    assert.equal(final.code, 'STREAM_INTERRUPTED');
    assert.equal(final.text, 'ยังไม่จบ');
});

test('readAnswer: a cut-off "completed" (no blank line) is not a completed answer', async () => {
    const final = await s.readAnswer(streamResponse([STARTED, delta('x'), 'event: completed\ndata: {}\n']), null);
    assert.equal(final.code, 'STREAM_INTERRUPTED');
});

test('readAnswer: a network failure in the middle -> STREAM_INTERRUPTED', async () => {
    const final = await s.readAnswer(streamResponse([STARTED, delta('a')], { end: new TypeError('network error') }), null);
    assert.equal(final.code, 'STREAM_INTERRUPTED');
    assert.equal(final.text, 'a');
});

test('readAnswer: Stop (abort) in the middle -> cancelled, not an error', async () => {
    const final = await s.readAnswer(streamResponse([STARTED, delta('บางส่วน')], { end: abortError() }), null, { isTimeout: () => false });
    assert.equal(final.state, 'cancelled');
    assert.equal(final.text, 'บางส่วน');
});

test('readAnswer: the browser time limit (abort marked as timeout) -> AI_TIMEOUT', async () => {
    const final = await s.readAnswer(streamResponse([STARTED], { end: abortError() }), null, { isTimeout: () => true });
    assert.equal(final.code, 'AI_TIMEOUT');
});

test('readAnswer: returns at the outcome even if the server keeps the connection open', async () => {
    const final = await Promise.race([
        s.readAnswer(streamResponse([STARTED, delta('ok'), COMPLETED], { end: 'open' }), null),
        new Promise((_, reject) => setTimeout(() => reject(new Error('readAnswer did not return')), 2000))
    ]);
    assert.equal(final.state, 'completed');
});

test('readAnswer: two answers streaming at the same time never mix', async () => {
    const [a, b] = await Promise.all([
        s.readAnswer(streamResponse([STARTED, delta('A1'), delta('A2'), COMPLETED]), null),
        s.readAnswer(streamResponse([STARTED, delta('B1'), delta('B2'), COMPLETED]), null)
    ]);
    assert.equal(a.text, 'A1A2');
    assert.equal(b.text, 'B1B2');
});

test('isEventStream accepts parameters and case, rejects other types', () => {
    assert.ok(s.isEventStream('text/event-stream'));
    assert.ok(s.isEventStream('Text/Event-Stream; charset=utf-8'));
    assert.ok(!s.isEventStream('application/json'));
    assert.ok(!s.isEventStream(null));
});

test('STREAM_INTERRUPTED has its own Thai text', () => {
    assert.equal(core.messageFor('STREAM_INTERRUPTED'), 'การเชื่อมต่อขาดก่อนที่คำตอบจะเสร็จ คำตอบที่เห็นอาจไม่ครบ กรุณาลองใหม่');
});
