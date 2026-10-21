// Unit tests for the queue / busy states of the Chat UI (FE-004). Node's built-in test runner only.
//   node --test tests/ui
// The server side is BE-008 (docs/BE-008-CONFIGURATION.md). The events `queued` / `processing` and the codes AI_BUSY /
// AI_REQUEST_IN_PROGRESS are still a PROPOSED contract, so every stream below is a mock built from that draft.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../BC-LocalAI/wwwroot/js/chat-core.js');
const s = require('../../BC-LocalAI/wwwroot/js/chat-stream.js');
const FrontendHttp = require('../../BC-LocalAI/wwwroot/js/frontend-http.js');

const sse = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
const STARTED = sse('started', { model: 'qwen3:4b' });
const COMPLETED = sse('completed', { finishReason: 'stop', evalCount: 7 });
const delta = (text) => sse('delta', { text });

function run(events) {
    const session = s.createStreamSession();
    let last = session.snapshot();
    const states = [];
    for (const [event, data] of events) {
        last = session.handle({ event, data: typeof data === 'string' ? data : JSON.stringify(data) });
        states.push(last.state);
    }
    return { session, last, states };
}

function streamResponse(text, { status = 200, contentType = 'text/event-stream; charset=utf-8', headers = {} } = {}) {
    return new Response(text, { status, headers: { 'Content-Type': contentType, ...headers } });
}

// ---------------------------------------------------------------- states

test('queued -> processing -> delta -> completed, in the order the backend reported', () => {
    const { states, last } = run([['started', { model: 'm' }], ['queued', { ahead: 3 }], ['processing', {}], ['delta', { text: 'สวัสดี' }], ['completed', {}]]);
    assert.deepEqual(states, ['accepted', 'queued', 'processing', 'streaming', 'completed']);
    assert.equal(last.text, 'สวัสดี');
    assert.equal(last.code, null);
});

test('started alone is "accepted", never "processing" or "streaming"', () => {
    const { last } = run([['started', { model: 'm' }]]);
    assert.equal(last.state, 'accepted');
    assert.ok(!/กำลังพิมพ์|ประมวลผล/.test(s.STATUS_TEXT.accepted));
});

test('without queue events (backend flag off) the first delta moves accepted -> streaming', () => {
    assert.deepEqual(run([['started', { model: 'm' }], ['delta', { text: 'a' }], ['completed', {}]]).states, ['accepted', 'streaming', 'completed']);
});

test('ahead is shown only when it is a non-negative whole number; otherwise it is absent, never invented', () => {
    assert.equal(run([['started', { model: 'm' }], ['queued', { ahead: 0 }]]).last.ahead, 0);
    for (const bad of [{}, { ahead: -1 }, { ahead: 1.5 }, { ahead: '2' }, { ahead: null }, { ahead: 1e9 }]) {
        assert.equal(run([['started', { model: 'm' }], ['queued', bad]]).last.ahead, null, JSON.stringify(bad));
    }
    // The page never changes `ahead` afterwards: processing keeps the value it was told, nothing counts down.
    assert.equal(run([['started', { model: 'm' }], ['queued', { ahead: 2 }], ['processing', {}]]).last.ahead, 2);
});

test('no ETA or wait time exists in any state text', () => {
    for (const text of Object.values(s.STATUS_TEXT)) assert.ok(!/\d+\s*(วินาที|นาที)/.test(text), text);
});

test('events in the wrong order are an invalid answer, not a silent state', () => {
    assert.equal(run([['queued', { ahead: 1 }]]).last.code, 'AI_INVALID_RESPONSE'); // before started
    assert.equal(run([['started', { model: 'm' }], ['processing', {}], ['queued', { ahead: 1 }]]).last.code, 'AI_INVALID_RESPONSE');
    assert.equal(run([['started', { model: 'm' }], ['queued', { ahead: 1 }], ['delta', { text: 'x' }]]).last.code, 'AI_INVALID_RESPONSE'); // delta while waiting
    assert.equal(run([['started', { model: 'm' }], ['queued', { ahead: 1 }], ['completed', {}]]).last.code, 'AI_INVALID_RESPONSE');
});

test('Stop while queued or processing: cancelled, nothing shown as an answer', () => {
    for (const events of [[['started', { model: 'm' }], ['queued', { ahead: 1 }]], [['started', { model: 'm' }], ['processing', {}]]]) {
        const { session } = run(events);
        const stopped = session.cancel();
        assert.equal(stopped.state, 'cancelled');
        assert.equal(stopped.text, '');
        assert.equal(stopped.partial, false);
    }
});

test('partial text stays and is marked incomplete on error and on cancel, never completed', () => {
    const failed = run([['started', { model: 'm' }], ['processing', {}], ['delta', { text: 'ครึ่ง' }], ['error', { code: 'AI_PROVIDER_ERROR' }]]).last;
    assert.deepEqual([failed.state, failed.text, failed.partial], ['error', 'ครึ่ง', true]);
    const stopped = run([['started', { model: 'm' }], ['delta', { text: 'ครึ่ง' }]]).session.cancel();
    assert.deepEqual([stopped.state, stopped.text, stopped.partial], ['cancelled', 'ครึ่ง', true]);
});

test('isActive covers every in-flight state (Stop applies) and no terminal one', () => {
    for (const state of ['connecting', 'accepted', 'queued', 'processing', 'streaming']) assert.ok(s.isActive(state), state);
    for (const state of ['idle', 'completed', 'cancelled', 'error']) assert.ok(!s.isActive(state), state);
});

// ---------------------------------------------------------------- errors: after SSE has started

test('AI_BUSY error event after started: its own message, partial=false, retry guidance from retryAfterSeconds', () => {
    const { last } = run([['started', { model: 'm' }], ['queued', { ahead: 1 }], ['error', { code: 'AI_BUSY', message: 'internal detail', partial: false, retryAfterSeconds: 10 }]]);
    assert.equal(last.state, 'error');
    assert.equal(last.code, 'AI_BUSY');
    assert.equal(last.partial, false);
    assert.equal(last.retryAfterSeconds, 10);
    assert.ok(last.message.startsWith(core.messageFor('AI_BUSY')));
    assert.ok(last.message.includes('10 วินาที'));
    assert.ok(!last.message.includes('internal detail'));
    assert.notEqual(core.messageFor('AI_BUSY'), core.messageFor('AI_TIMEOUT'));
    assert.notEqual(core.messageFor('AI_BUSY'), core.messageFor('AI_UNAVAILABLE'));
});

test('retryAfterSeconds that is missing or malformed gives the neutral text without any number', () => {
    for (const bad of [undefined, null, 0, -5, 2.5, '10', 'soon', 99999, [], {}]) {
        const { last } = run([['started', { model: 'm' }], ['error', { code: 'AI_BUSY', retryAfterSeconds: bad }]]);
        assert.equal(last.retryAfterSeconds, null, String(bad));
        assert.equal(last.message, core.messageFor('AI_BUSY'), String(bad));
    }
});

// ---------------------------------------------------------------- errors: HTTP before SSE (429 / 503)

test('429 before SSE: AI_REQUEST_IN_PROGRESS, text tells the user to wait or stop the earlier question', async () => {
    const response = streamResponse(JSON.stringify({ error: { code: 'AI_REQUEST_IN_PROGRESS', message: 'x' } }),
        { status: 429, contentType: 'application/json', headers: { 'Retry-After': '10' } });
    const seen = [];
    const final = await s.readAnswer(response, (snap) => seen.push(snap.state));
    assert.deepEqual([final.state, final.code, final.retryAfterSeconds], ['error', 'AI_REQUEST_IN_PROGRESS', 10]);
    assert.match(final.message, /คำถามที่กำลังตอบอยู่/);
    assert.deepEqual(seen, ['error']);
});

test('503 before SSE: AI_BUSY is not shown as a timeout or as Ollama being down', async () => {
    const response = streamResponse(JSON.stringify({ error: { code: 'AI_BUSY', message: 'x' } }),
        { status: 503, contentType: 'application/json', headers: { 'Retry-After': '10' } });
    const final = await s.readAnswer(response, null);
    assert.equal(final.code, 'AI_BUSY');
    assert.ok(!final.message.includes('Ollama'));
    assert.ok(!final.message.includes('นานเกินไป'));
});

test('Retry-After missing, a date, zero, negative, fractional, huge or text: neutral message, no number invented', async () => {
    for (const header of [undefined, 'Wed, 21 Oct 2026 07:28:00 GMT', '0', '-3', '1.5', '999999999', 'abc', '']) {
        const headers = header === undefined ? {} : { 'Retry-After': header };
        const final = await s.readAnswer(streamResponse(JSON.stringify({ error: { code: 'AI_BUSY' } }),
            { status: 503, contentType: 'application/json', headers }), null);
        assert.equal(final.retryAfterSeconds, null, String(header));
        assert.equal(final.message, core.messageFor('AI_BUSY'), String(header));
    }
});

test('a 503 that is not the queue (AI_UNAVAILABLE) keeps its own text; unknown codes never show server text', async () => {
    const unavailable = await s.readAnswer(streamResponse(JSON.stringify({ error: { code: 'AI_UNAVAILABLE' } }), { status: 503, contentType: 'application/json' }), null);
    assert.equal(unavailable.code, 'AI_UNAVAILABLE');
    const unknown = await s.readAnswer(streamResponse(JSON.stringify({ error: { code: 'WHATEVER', message: '<script>alert(1)</script>' } }), { status: 503, contentType: 'application/json' }), null);
    assert.equal(unknown.code, 'INTERNAL_ERROR');
    assert.ok(!unknown.message.includes('<script>'));
});

test('parseRetryAfter accepts only whole seconds from 1 to 3600', () => {
    assert.equal(s.parseRetryAfter('10'), 10);
    assert.equal(s.parseRetryAfter(' 7 '), 7);
    assert.equal(s.parseRetryAfter(3600), 3600);
    for (const bad of ['3601', '0', '1e2', '+5', '5s', 4.2, NaN, null, undefined, true]) assert.equal(s.parseRetryAfter(bad), null, String(bad));
});

// ---------------------------------------------------------------- readAnswer end to end (mock of the draft contract)

test('readAnswer reports queued then processing then the answer, and ends completed', async () => {
    const states = [];
    const body = STARTED + sse('queued', { ahead: 2 }) + sse('processing', {}) + delta('ตอบ') + COMPLETED;
    const final = await s.readAnswer(streamResponse(body), (snap) => states.push(snap.state));
    assert.deepEqual(states, ['accepted', 'queued', 'processing', 'streaming', 'completed']);
    assert.equal(final.text, 'ตอบ');
});

test('an old backend (no queue events) still works unchanged', async () => {
    const final = await s.readAnswer(streamResponse(STARTED + delta('a') + delta('b') + COMPLETED), null);
    assert.deepEqual([final.state, final.text], ['completed', 'ab']);
});

test('connection drops while queued: STREAM_INTERRUPTED, no text, not completed', async () => {
    const final = await s.readAnswer(streamResponse(STARTED + sse('queued', { ahead: 1 })), null);
    assert.deepEqual([final.state, final.code, final.text], ['error', 'STREAM_INTERRUPTED', '']);
});

test('markup inside queue/busy data is never turned into text from the server', () => {
    const { last } = run([['started', { model: 'm' }], ['error', { code: 'AI_BUSY', message: '<img src=x onerror=alert(1)>', retryAfterSeconds: 5 }]]);
    assert.ok(!last.message.includes('<'));
});

// ---------------------------------------------------------------- authenticated page transport (frontend-http)

test('frontend-http: 429 and 503 from the stream keep their code and a valid Retry-After', async () => {
    async function attempt(status, code, retryAfter) {
        const fetcher = async (path) => {
            if (path === '/api/auth/csrf') return new Response(JSON.stringify({ token: 'tok' }), { status: 200, headers: { 'Content-Type': 'application/json' } });
            return new Response(JSON.stringify({ error: { code } }), { status, headers: { 'Content-Type': 'application/json', ...(retryAfter ? { 'Retry-After': retryAfter } : {}) } });
        };
        const http = FrontendHttp.create(fetcher, { protocol: 'https:' });
        try { await http.stream({ message: 'x' }, { actor: null, signal: new AbortController().signal, consume: async () => null }); }
        catch (error) { return error; }
        assert.fail('expected a refusal');
    }
    const busy = await attempt(503, 'AI_BUSY', '10');
    assert.deepEqual([busy.code, busy.status, busy.retryAfterSeconds], ['AI_BUSY', 503, 10]);
    const inProgress = await attempt(429, 'AI_REQUEST_IN_PROGRESS', '10');
    assert.deepEqual([inProgress.code, inProgress.status, inProgress.retryAfterSeconds], ['AI_REQUEST_IN_PROGRESS', 429, 10]);
    const noHint = await attempt(503, 'AI_BUSY', 'Wed, 21 Oct 2026 07:28:00 GMT');
    assert.equal(noHint.retryAfterSeconds, undefined);
    assert.equal(core.describeFailure(noHint.code, noHint.retryAfterSeconds), core.messageFor('AI_BUSY'));
});
