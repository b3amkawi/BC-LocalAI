import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const core = require('../../BC-LocalAI/wwwroot/js/auth-core.js');
const mockModule = require('../../BC-LocalAI/wwwroot/js/auth-mock.js');
const user = { id: '00000000-0000-0000-0000-000000000003', username: 'demo.user', displayName: 'ผู้ใช้ตัวอย่าง', roles: ['User'] };
const reply = (status, body) => ({ status, json: async () => body });
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };

for (const [name, username, password, field] of [
    ['empty username', '', 'placeholder', 'username'],
    ['username whitespace', ' demo.user ', 'placeholder', 'username'],
    ['non ASCII username', 'ผู้ใช้', 'placeholder', 'username'],
    ['oversized username', 'a'.repeat(129), 'placeholder', 'username'],
    ['SQL-shaped username', "user' OR 1=1 --", 'placeholder', 'username'],
    ['empty password', 'demo.user', '', 'password'],
    ['oversized password', 'demo.user', 'p'.repeat(1025), 'password'],
    ['unpaired surrogate', 'demo.user', '\ud800', 'password'],
    ['stray low surrogate', 'demo.user', '\udfff', 'password']
]) test(`input validation: ${name}`, () => assert.equal(core.validate(username, password).field, field));

test('validation allows exact Thai/emoji/combining/whitespace password and boundary lengths', () => {
    for (const password of [' รหัส😀\n ', 'e\u0301', 'é', '   ', 'a'.repeat(1024)])
        assert.equal(core.validate('a'.repeat(128), password), null);
});
test('client requires explicit injected transport; never silently falls back to global fetch', () => {
    assert.throws(() => core.createClient(), TypeError);
});
test('invalid credentials shape never invokes transport', async () => {
    let calls = 0;
    const client = core.createClient(async () => { calls++; });
    assert.equal((await client.login('bad user', 'placeholder')).code, 'AUTH_INVALID_REQUEST');
    assert.equal(calls, 0);
});
test('contract: same-origin/no-store, exact POST fields and password, fresh CSRF per mutation', async () => {
    const calls = [];
    let token = 0;
    const client = core.createClient(async (path, options) => {
        calls.push([path, options]);
        if (path.endsWith('/csrf')) return reply(200, { token: 'preview-' + (++token) });
        return path.endsWith('/logout') ? reply(204) : reply(200, { user });
    });
    const exact = ' placeholder รหัส😀\n ';
    assert.equal((await client.login('demo.user', exact)).ok, true);
    assert.equal((await client.me()).ok, true);
    assert.equal((await client.logout()).ok, true);
    assert.deepEqual(calls.map(([p, o]) => [p, o.method]), [
        ['/api/auth/csrf', 'GET'], ['/api/auth/login', 'POST'], ['/api/auth/me', 'GET'],
        ['/api/auth/csrf', 'GET'], ['/api/auth/logout', 'POST']
    ]);
    assert.deepEqual(JSON.parse(calls[1][1].body), { username: 'demo.user', password: exact });
    assert.equal(calls[1][1].headers['X-CSRF-TOKEN'], 'preview-1');
    assert.equal(calls[4][1].headers['X-CSRF-TOKEN'], 'preview-2');
    assert.equal(calls[4][1].body, undefined);
    for (const [, options] of calls) {
        assert.equal(options.credentials, 'same-origin');
        assert.equal(options.cache, 'no-store');
        assert.equal(options.redirect, 'error');
    }
});
for (const [status, code] of [[400, 'AUTH_INVALID_REQUEST'], [401, 'AUTH_INVALID_CREDENTIALS'],
    [403, 'AUTH_FORBIDDEN'], [429, 'AUTH_RATE_LIMITED'], [503, 'AUTH_UNAVAILABLE'], [500, 'INTERNAL_ERROR']])
    test(`Login${status} has safe copy; never retries or exposes server details`, async () => {
        let posts = 0;
        const client = core.createClient(async path => {
            if (path.endsWith('/csrf')) return reply(200, { token: 'preview' });
            posts++; return reply(status, { error: { code: '__proto__', message: '<script>raw-secret</script>' } });
        });
        const result = await client.login('demo.user', 'placeholder');
        assert.equal(result.code, code); assert.equal(posts, 1);
        assert.doesNotMatch(JSON.stringify(result), /raw-secret|<script>/);
    });
test('CSRF failure is distinguished and never auto-replays credentials', async () => {
    let posts = 0;
    const client = core.createClient(async path => path.endsWith('/csrf') ? reply(200, { token: 'preview' }) :
        (posts++, reply(403, { error: { code: 'AUTH_CSRF_INVALID', message: 'private' } })));
    assert.equal((await client.login('demo.user', 'placeholder')).code, 'AUTH_CSRF_INVALID');
    assert.equal(posts, 1);
});
test('malformed token prevents POST', async () => {
    for (const token of ['', 'x\r\ny', 'x'.repeat(8193), null]) {
        let calls = 0;
        const client = core.createClient(async () => { calls++; return reply(200, { token }); });
        assert.equal((await client.login('demo.user', 'placeholder')).code, 'INVALID_RESPONSE');
        assert.equal(calls, 1);
    }
});
test('safe DTO discards extra sensitive fields and freezes nested role claims', () => {
    const safe = core.safeUser({ ...user, password_hash: 'private', security_stamp: 'private', token: 'private' });
    assert.deepEqual(safe, user);
    assert.equal(Object.isFrozen(safe.roles), true);
    assert.doesNotMatch(JSON.stringify(safe), /private|hash|stamp|token/);
});
for (const bad of [null, {}, { ...user, id: 'invalid' }, { ...user, roles: ['Owner'] }, { ...user, roles: [] },
    { ...user, displayName: '' }, { ...user, username: 'not valid' }])
    test(`malformed identity rejected: ${JSON.stringify(bad)}`, () => assert.equal(core.safeUser(bad), null));
test('malformed success JSON and absent identity fail closed', async () => {
    const malformed = core.createClient(async () => ({ status: 200, json: async () => { throw new SyntaxError(); } }));
    assert.equal((await malformed.me()).code, 'INVALID_RESPONSE');
    const noUser = core.createClient(async () => reply(200, {}));
    assert.equal((await noUser.me()).code, 'INVALID_RESPONSE');
});
test('unknown error keys do not read inherited Object properties', () => {
    for (const code of ['__proto__', 'constructor', 'toString', null]) assert.equal(core.failure(code).code, 'INTERNAL_ERROR');
});
test('network failure is sanitized', async () => {
    const client = core.createClient(async () => { throw new Error('private connection details'); });
    assert.equal((await client.me()).message, core.MESSAGES.NETWORK_ERROR);
});
test('pre-cancelled requests never invoke transport', async () => {
    const abort = new AbortController(); abort.abort();
    let calls = 0;
    const client = core.createClient(async () => { calls++; });
    assert.equal((await client.me(abort.signal)).code, 'CANCELLED'); assert.equal(calls, 0);
});
test('anonymous bootstrap, authenticated profile, expiry and Logout follow the five-state contract', async () => {
    const mock = mockModule.createTransport(0);
    const snapshots = [];
    const controller = core.createSessionController(core.createClient(mock.request), state => snapshots.push(state));
    assert.equal(controller.snapshot().status, 'checking');
    await controller.checkSession(); assert.equal(controller.snapshot().status, 'unauthenticated');
    await controller.login('demo.user', 'placeholder'); assert.equal(controller.snapshot().status, 'authenticated');
    mock.setScenario('expired');
    await controller.checkSession(); assert.equal(controller.snapshot().status, 'expired');
    assert.equal(controller.snapshot().user, null);
    mock.setScenario('success');
    await controller.login('demo.user', 'placeholder'); await controller.logout();
    assert.equal(controller.snapshot().status, 'unauthenticated'); assert.equal(controller.snapshot().user, null);
    assert.doesNotMatch(JSON.stringify(snapshots), /placeholder|preview-\d/);
});
test('duplicate submit and overlapping identity checks cannot create extra Login requests', async () => {
    const pending = deferred(); let calls = 0;
    const controller = core.createSessionController({ login: () => { calls++; return pending.promise; }, me: () => { calls++; } });
    const first = controller.login('demo.user', 'placeholder');
    const duplicate = controller.login('demo.user', 'placeholder');
    const overlappingCheck = controller.checkSession();
    // Assert invocation counts before awaiting: a removed guard must fail an assertion, not hang a deferred promise.
    assert.equal(calls, 1);
    assert.equal(await duplicate, false);
    assert.equal(await overlappingCheck, false);
    pending.resolve({ ok: true, user }); await first;
    assert.equal(controller.snapshot().status, 'authenticated');
});
test('pending checks immediately clear an earlier identity', async () => {
    const pending = deferred();
    const controller = core.createSessionController({ login: async () => ({ ok: true, user }), me: () => pending.promise });
    await controller.login('demo.user', 'placeholder');
    const check = controller.checkSession();
    assert.equal(controller.snapshot().user, null); assert.equal(controller.snapshot().status, 'checking');
    pending.resolve(core.failure('AUTH_REQUIRED')); await check;
    assert.equal(controller.snapshot().status, 'expired');
});
test('stale Login completion cannot restore user after clear/navigation or replace a new user', async () => {
    const old = deferred(); let count = 0;
    const second = { ...user, id: '00000000-0000-0000-0000-000000000004', username: 'second' };
    const controller = core.createSessionController({ login: () => ++count === 1 ? old.promise : Promise.resolve({ ok: true, user: second }) });
    const first = controller.login('demo.user', 'placeholder');
    controller.clear();
    await controller.login('second', 'placeholder');
    old.resolve({ ok: true, user }); await first;
    assert.equal(controller.snapshot().user.username, 'second');
});
test('failed Logout hides identity without falsely showing logout success', async () => {
    const controller = core.createSessionController({ login: async () => ({ ok: true, user }), logout: async () => core.failure('NETWORK_ERROR') });
    await controller.login('demo.user', 'placeholder'); await controller.logout();
    assert.equal(controller.snapshot().status, 'error'); assert.equal(controller.snapshot().user, null);
    assert.equal(controller.snapshot().code, 'NETWORK_ERROR');
});
test('403 and503 checks clear the previous identity and leave a safe retryable error', async () => {
    for (const code of ['AUTH_FORBIDDEN', 'AUTH_UNAVAILABLE']) {
        const controller = core.createSessionController({ login: async () => ({ ok: true, user }), me: async () => core.failure(code) });
        await controller.login('demo.user', 'placeholder'); await controller.checkSession();
        assert.equal(controller.snapshot().status, 'error'); assert.equal(controller.snapshot().user, null);
    }
});
test('mock modes are explicit; Mock does not need a real server or persist across instances', async () => {
    for (const scenario of mockModule.SCENARIOS) {
        const mock = mockModule.createTransport(0); mock.setScenario(scenario);
        const result = await core.createClient(mock.request).login('demo.user', 'placeholder');
        assert.equal(result.ok, ['success', 'expired'].includes(scenario), scenario);
    }
    const first = core.createClient(mockModule.createTransport(0).request);
    await first.login('demo.user', 'placeholder');
    const second = core.createClient(mockModule.createTransport(0).request);
    assert.equal((await second.me()).code, 'AUTH_REQUIRED');
});
