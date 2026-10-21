import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url), core = require('../../BC-LocalAI/wwwroot/js/history-core.js');
const auth = require('../../BC-LocalAI/wwwroot/js/auth-core.js'), stream = require('../../BC-LocalAI/wwwroot/js/chat-stream.js');
const A = '00000000-0000-4000-8000-0000000a11ce', B = '00000000-0000-4000-8000-000000000b0b', date = '2026-10-21T10:00:00Z';
const convo = { id: A, title: '<img src=x onerror=alert(1)>', createdAt: date, updatedAt: date };
const message = { id: B, conversationId: A, sequence: 1, role: 'assistant', content: 'สวัสดี', status: 'completed', createdAt: date, updatedAt: date };
test('whitelists public DTOs and preserves hostile text as inert text', () => {
    const parsed = core.conversation({ ...convo, ownerUserId: 'must not retain', token: 'must not retain' });
    assert.deepEqual(parsed, convo); assert.equal(Object.hasOwn(parsed, 'ownerUserId'), false); assert.equal(Object.isFrozen(parsed), true);
    assert.equal(core.message({ ...message, securityStamp: 'must not retain' }, A).securityStamp, undefined);
});
for (const change of [{ id: '../other' }, { conversationId: B }, { sequence: 0 }, { sequence: NaN }, { role: 'system' }, { status: 'invented' }, { content: '' }, { createdAt: 'bad' }]) {
    test('rejects inconsistent or malformed message ' + Object.keys(change)[0] + JSON.stringify(change), () => {
        assert.throws(() => core.message({ ...message, ...change }, A), { code: 'INVALID_RESPONSE' });
    });
}
for (const status of ['pending', 'streaming', 'failed', 'cancelled']) test('incomplete status has visible label: ' + status, () => {
    const parsed = core.message({ ...message, content: '', status }, A); assert.equal(parsed.status, status); assert.ok(core.LABELS[status]);
});
test('rejects duplicate page IDs and oversized/invalid cursor', () => {
    assert.throws(() => core.page({ items: [convo, convo], nextCursor: null }, core.conversation), { code: 'INVALID_RESPONSE' });
    for (const cursor of ['../../private', 'a'.repeat(257), '']) assert.throws(() => core.page({ items: [], nextCursor: cursor }, core.conversation), { code: 'INVALID_RESPONSE' });
});
test('history client sends only title/ID, never client-supplied resource owner', async () => {
    const calls = [], client = core.createClient({ request: async (path, options) => { calls.push({ path, options }); return convo; } }, A);
    await client.create('  เรื่องใหม่  '); await client.rename(B, 'ใหม่'); await client.remove(B);
    assert.deepEqual(calls[0].options.body, { title: 'เรื่องใหม่' }); assert.equal(calls[0].options.actor, A);
    assert.equal(calls[1].path, '/api/conversations/' + B); assert.equal(calls[2].options.method, 'DELETE');
    assert.ok(calls.every(call => !Object.hasOwn(call.options.body || {}, 'userId')));
});
test('invalid title cannot make any request', () => {
    const client = core.createClient({ request: () => assert.fail('Must not send') }, A);
    for (const title of ['', ' ', 'x'.repeat(201)]) assert.throws(() => client.create(title), { code: 'INVALID_TITLE' });
});
test('forward pagination preserves opaque cursor and rejects non-progressing page', async () => {
    let path;
    const client = core.createClient({ request: async value => { path = value; return { items: [message], nextCursor: 'opaque_next' }; } }, A);
    await client.messages(A, 'opaque_old'); assert.equal(path, '/api/conversations/' + A + '/messages?limit=20&cursor=opaque_old');
    await assert.rejects(client.messages(A, 'opaque_next'), { code: 'INVALID_RESPONSE' });
});
test('message pages must have increasing sequences', async () => {
    const client = core.createClient({ request: async () => ({ items: [message, { ...message, id: A, sequence: 1 }], nextCursor: null }) }, A);
    await assert.rejects(client.messages(A), { code: 'INVALID_RESPONSE' });
});
test('identity/navigation epoch invalidates before abort listeners can publish, even if fetch ignores abort', async () => {
    const boundary = core.createBoundary(), old = boundary.capture(); let validDuringAbort;
    old.signal.addEventListener('abort', () => { validDuringAbort = old.current(); }); boundary.reset();
    assert.equal(validDuringAbort, false); assert.equal(old.current(), false); assert.equal(boundary.capture().current(), true);
});
test('external session termination discards a late successful profile', async () => {
    let resolve;
    const pending = new Promise(done => { resolve = done; });
    const controller = auth.createSessionController({ me: () => pending });
    const checking = controller.checkSession(); controller.invalidate('AUTH_SESSION_CHANGED');
    resolve({ ok: true, user: { id: A, username: 'synthetic.user', displayName: 'สมมติ', roles: ['User'] } });
    await checking; assert.equal(controller.snapshot().status, 'expired'); assert.equal(controller.snapshot().user, null);
});
test('streamed server session termination remains a reviewed authentication code', () => {
    const session = stream.createStreamSession(); session.handle({ event: 'started', data: '{"model":"qwen3:4b"}' });
    const result = session.handle({ event: 'error', data: '{"code":"AUTH_SESSION_ENDED","message":"private server detail"}' });
    assert.equal(result.code, 'AUTH_SESSION_ENDED'); assert.equal(result.state, 'error'); assert.doesNotMatch(result.message, /private/);
});
