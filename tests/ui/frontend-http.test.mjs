import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { create } = require('../../BC-LocalAI/wwwroot/js/frontend-http.js');
const auth = require('../../BC-LocalAI/wwwroot/js/auth-core.js');
const stream = require('../../BC-LocalAI/wwwroot/js/chat-stream.js');
const A = '00000000-0000-4000-8000-0000000a11ce', B = '00000000-0000-4000-8000-000000000b0b';
const json = (data, status = 200, actor = A) => new Response(JSON.stringify(data), { status,
    headers: { 'Content-Type': 'application/json', ...(actor ? { 'X-BC-User': actor } : {}) } });
test('real transport enforces HTTPS before any credential or CSRF request', async () => {
    let calls = 0; const http = create(() => { calls++; }, { protocol: 'http:' });
    await assert.rejects(http.request('/api/auth/me'), { code: 'AUTH_HTTPS_REQUIRED' }); assert.equal(calls, 0);
});
for (const path of ['https://other.invalid/api/auth/me', '//other.invalid/api/auth/me', '/api/../private', '/api/auth/me#secret', '/api/auth/me?returnUrl=evil']) {
    test('rejects non-contract destination ' + path, async () => {
        const http = create(() => assert.fail('Must not send'));
        await assert.rejects(http.request(path), { code: 'INVALID_REQUEST' });
    });
}
test('fresh CSRF for every mutation, cookies same-origin, no-store and no redirects', async () => {
    const seen = []; let tokens = 0;
    const http = create(async (path, init) => { seen.push({ path, init });
        return path === '/api/auth/csrf' ? json({ token: 'fresh_' + (++tokens) }) : json({}); });
    await http.request('/api/conversations', { method: 'POST', actor: A, body: { title: 'หนึ่ง' } });
    await http.request('/api/conversations', { method: 'POST', actor: A, body: { title: 'สอง' } });
    assert.equal(tokens, 2); assert.equal(seen.length, 4);
    for (const { init } of seen) { assert.equal(init.credentials, 'same-origin'); assert.equal(init.cache, 'no-store'); assert.equal(init.redirect, 'error'); }
    assert.equal(seen[1].init.headers['X-CSRF-TOKEN'], 'fresh_1'); assert.equal(seen[3].init.headers['X-CSRF-TOKEN'], 'fresh_2');
    assert.equal(seen[1].init.headers['X-BC-Expected-User'], A);
    assert.deepEqual(JSON.parse(seen[1].init.body), { title: 'หนึ่ง' });
});
test('CSRF failure never submits or retries a mutation', async () => {
    let calls = 0; const http = create(async () => { calls++; return json({}, 503); });
    await assert.rejects(http.request('/api/conversations', { method: 'POST', actor: A, body: { title: 'x' } }), { code: 'AUTH_UNAVAILABLE' });
    assert.equal(calls, 1);
});
for (const actor of [null, B]) test('rejects missing/wrong response actor before reading private body: ' + actor, async () => {
    let reads = 0;
    const http = create(async () => ({ status: 200, ok: true, headers: new Headers(actor ? { 'X-BC-User': actor } : {}),
        body: { locked: false, cancel: async () => {}, getReader: () => { reads++; assert.fail('Private body must not be read'); } } }));
    await assert.rejects(http.request('/api/conversations', { actor: A }), { code: 'AUTH_SESSION_CHANGED' }); assert.equal(reads, 0);
});
for (const status of [401, 403]) test('malformed ' + status + ' still ends protected access', async () => {
    const http = create(async () => new Response('<html>internal detail</html>', { status, headers: { 'Content-Type': 'text/html' } }));
    await assert.rejects(http.request('/api/conversations', { actor: A }), { status, code: status === 401 ? 'AUTH_REQUIRED' : 'AUTH_FORBIDDEN' });
});
test('CSRF403 is reviewed code only, never server text', async () => {
    const client = auth.createClient(create(async () => json({ error: { code: 'AUTH_CSRF_INVALID', message: '<script>private</script>' } }, 403)).authTransport);
    const result = await client.me(); assert.equal(result.code, 'AUTH_CSRF_INVALID'); assert.doesNotMatch(result.message, /script|private/);
});
test('Login401 stays generic invalid credentials, never retries submitted password', async () => {
    const calls = []; const http = create(async path => { calls.push(path); return path.endsWith('/csrf') ? json({ token: 'fresh_csrf' }) : json({}, 401); });
    const result = await auth.createClient(http.authTransport).login('synthetic.user', 'placeholder'); // ci-allow-secret: synthetic test input, not a credential.
    assert.equal(result.code, 'AUTH_INVALID_CREDENTIALS'); assert.deepEqual(calls, ['/api/auth/csrf', '/api/auth/login']);
});
test('profile actor must match public DTO even when initial expected identity is unknown', async () => {
    const user = { id: B, username: 'synthetic.user', displayName: 'สมมติ', roles: ['User'] };
    const result = await auth.createClient(create(async () => json({ user }, 200, A)).authTransport).me();
    assert.equal(result.code, 'AUTH_REQUIRED');
});
test('deadline covers a hanging fetch even when transport ignores abort', async () => {
    const http = create(() => new Promise(() => {}), { timeoutMs: 10 });
    await assert.rejects(http.request('/api/auth/me'), { code: 'REQUEST_TIMEOUT' });
});
test('deadline covers body reads after headers', async () => {
    let cancelled = false;
    const body = new ReadableStream({ pull: () => new Promise(() => {}), cancel() { cancelled = true; } });
    const http = create(async () => new Response(body, { headers: { 'Content-Type': 'application/json' } }), { timeoutMs: 10 });
    await assert.rejects(http.request('/api/auth/me'), { code: 'REQUEST_TIMEOUT' }); assert.equal(cancelled, true);
});
test('bounded JSON body rejects excessive advertised length', async () => {
    const http = create(async () => new Response('{}', { headers: { 'Content-Type': 'application/json', 'Content-Length': '3000000' } }));
    await assert.rejects(http.request('/api/auth/me'), { code: 'INVALID_RESPONSE' });
});
test('external cancellation covers body and does not retry', async () => {
    const controller = new AbortController(); let calls = 0;
    const http = create(async () => { calls++; controller.abort(); return json({}); });
    await assert.rejects(http.request('/api/auth/me', { signal: controller.signal }), { code: 'CANCELLED' }); assert.equal(calls, 1);
});
test('SSE identity mismatch fails before any update or private bytes', async () => {
    let updates = 0;
    const http = create(async path => path.endsWith('/csrf') ? json({ token: 'fresh' }) : new Response('private SSE', {
        headers: { 'Content-Type': 'text/event-stream', 'X-BC-User': B } }));
    await assert.rejects(http.stream({ message: 'x' }, { actor: A, consume: response => stream.readAnswer(response, () => updates++) }), { code: 'AUTH_SESSION_CHANGED' });
    assert.equal(updates, 0);
});
test('terminal SSE cancels upstream without waiting for close', async () => {
    let cancelled = false;
    const bytes = new TextEncoder().encode('event: started\ndata: {"model":"qwen3:4b"}\n\nevent: delta\ndata: {"text":"สอง"}\n\nevent: completed\ndata: {}\n\n');
    const body = new ReadableStream({ start(controller) { controller.enqueue(bytes); }, cancel() { cancelled = true; } });
    const http = create(async path => path.endsWith('/csrf') ? json({ token: 'fresh' }) : new Response(body, { headers: { 'Content-Type': 'text/event-stream', 'X-BC-User': A } }));
    const result = await http.stream({ message: '1+1' }, { actor: A, consume: response => stream.readAnswer(response) });
    assert.equal(result.state, 'completed'); assert.equal(result.text, 'สอง'); assert.equal(cancelled, true);
});
