import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const core = require('../../BC-LocalAI/wwwroot/js/auth-core.js');
const mocks = require('../../BC-LocalAI/wwwroot/js/auth-mock.js');
const source = readFileSync(new URL('../../BC-LocalAI/wwwroot/js/auth-preview.js', import.meta.url), 'utf8');

function mount(displayName = 'ผู้ใช้ตัวอย่าง') {
    const ids = ['login-form', 'login-username', 'login-password', 'login-submit', 'logout', 'check-session',
        'scenario', 'auth-status', 'auth-error', 'user-profile', 'display-name', 'account-name', 'user-role'];
    const events = new Map(); const windowEvents = new Map(); const documentEvents = new Map();
    const nodes = Object.fromEntries(ids.map(id => [id, {
        id, value: '', textContent: '', hidden: false, disabled: false, attributes: {},
        setAttribute(key, value) { this.attributes[key] = value; },
        addEventListener(type, callback) { events.set(id + ':' + type, callback); },
        focus() { document.activeElement = this; },
        set innerHTML(_) { throw new Error('Unsafe HTML sink'); }
    }]));
    const document = { getElementById: id => nodes[id], visibilityState: 'visible', activeElement: null,
        addEventListener: (type, callback) => documentEvents.set(type, callback) };
    const mock = mocks.createTransport(0);
    let posts = 0;
    const request = async (path, options) => {
        if (path.endsWith('/login')) posts++;
        const result = await mock.request(path, options);
        if (result.status !== 200 || !['/api/auth/login', '/api/auth/me'].includes(path)) return result;
        const data = await result.json();
        return { status: 200, json: async () => ({ user: { ...data.user, displayName } }) };
    };
    const window = { AuthCore: core, AuthMock: { createTransport: () => ({ request, setScenario: mock.setScenario }) },
        addEventListener: (type, callback) => windowEvents.set(type, callback) };
    const context = { window, document,
        fetch: () => { throw new Error('Real network must not be used'); },
        console: { log: () => { throw new Error('Credentials must not be logged'); } } };
    for (const storage of ['localStorage', 'sessionStorage']) Object.defineProperty(context, storage,
        { get() { throw new Error('Browser storage must not be used'); } });
    vm.runInNewContext(source, context);
    return { nodes, document, posts: () => posts,
        submit() { let prevented = false; events.get('login-form:submit')({ preventDefault: () => { prevented = true; } }); assert.equal(prevented, true); },
        click: id => events.get(id + ':click')(),
        scenario(value) { nodes.scenario.value = value; events.get('scenario:change')(); },
        pagehide: () => windowEvents.get('pagehide')(),
        pageshow: () => windowEvents.get('pageshow')({ persisted: true }),
        visible: () => documentEvents.get('visibilitychange')()
    };
}
async function settled(page) {
    for (let i = 0; i < 100; i++) {
        if (!page.nodes['login-submit'].disabled) return;
        await new Promise(resolve => setTimeout(resolve, 2));
    }
    assert.fail('UI did not settle');
}
async function login(page) {
    await settled(page);
    page.nodes['login-username'].value = 'demo.user';
    page.nodes['login-password'].value = 'placeholder';
    page.submit();
    assert.equal(page.nodes['login-password'].value, '');
    await settled(page);
}
test('DOM shell bootstraps anonymously and disables every action while checking', async () => {
    const page = mount();
    assert.equal(page.nodes['login-submit'].disabled, true);
    await settled(page);
    assert.equal(page.nodes['user-profile'].hidden, true);
    assert.match(page.nodes['auth-status'].textContent, /ยังไม่ได้/);
});
test('submit clears password immediately, ignores duplicate events and renders only the safe profile', async () => {
    const page = mount(); await settled(page);
    page.nodes['login-username'].value = 'demo.user'; page.nodes['login-password'].value = 'placeholder';
    page.submit(); page.submit();
    assert.equal(page.nodes['login-password'].value, '');
    assert.equal(page.nodes['login-submit'].disabled, true);
    await settled(page);
    assert.equal(page.posts(), 1); assert.equal(page.nodes['user-profile'].hidden, false);
    assert.equal(page.nodes['login-form'].hidden, true);
    assert.equal(page.nodes['account-name'].textContent, 'demo.user');
    assert.equal(page.document.activeElement.id, 'logout');
});
test('untrusted display names are displayed literally with textContent, never interpreted as HTML', async () => {
    const attack = '<img src=x onerror=alert(1)>';
    const page = mount(attack); await login(page);
    assert.equal(page.nodes['display-name'].textContent, attack);
});
test('validation focuses the invalid input and has an accessible error', async () => {
    const page = mount(); await settled(page); page.submit(); await settled(page);
    assert.equal(page.nodes['login-username'].attributes['aria-invalid'], 'true');
    assert.equal(page.nodes['auth-error'].hidden, false);
    assert.equal(page.document.activeElement.id, 'login-username');
    assert.equal(page.posts(), 0);
});
test('Logout clears identity text and old username rather than only hiding the profile', async () => {
    const page = mount(); await login(page); page.click('logout');
    assert.equal(page.nodes['account-name'].textContent, '');
    assert.equal(page.nodes['login-username'].value, '');
    await settled(page);
    assert.equal(page.nodes['display-name'].textContent, '');
    assert.equal(page.nodes['user-profile'].hidden, true);
});
test('expired session recheck clears old identity immediately and offers Login again', async () => {
    const page = mount(); await login(page); page.scenario('expired'); page.click('check-session');
    assert.equal(page.nodes['account-name'].textContent, ''); await settled(page);
    assert.match(page.nodes['auth-status'].textContent, /หมดอายุ/);
    assert.equal(page.nodes['login-form'].hidden, false);
});
for (const [mode, text] of [['invalid', /ไม่ถูกต้อง/], ['forbidden', /ไม่มีสิทธิ์/], ['csrf', /ความปลอดภัย/],
    ['limited', /บ่อยเกินไป/], ['unavailable', /ไม่พร้อม/], ['network', /เชื่อมต่อ/]])
    test(`DOM shows safe ${mode} error without authenticating`, async () => {
        const page = mount(); await settled(page); page.scenario(mode); await login(page);
        assert.match(page.nodes['auth-error'].textContent, text);
        assert.equal(page.nodes['user-profile'].hidden, true);
        assert.equal(page.nodes['display-name'].textContent, '');
    });
test('pagehide cancels a pending Login and clears credentials/identity; stale completion stays hidden', async () => {
    const page = mount(); await settled(page);
    page.nodes['login-username'].value = 'demo.user'; page.nodes['login-password'].value = 'placeholder';
    page.submit(); page.pagehide();
    await new Promise(resolve => setTimeout(resolve, 15));
    assert.equal(page.nodes['login-username'].value, ''); assert.equal(page.nodes['login-password'].value, '');
    assert.equal(page.nodes['account-name'].textContent, ''); assert.equal(page.nodes['user-profile'].hidden, true);
});
test('bfcache return and visible-tab recheck use Me, not a stored identity snapshot', async () => {
    const page = mount(); await login(page); page.pagehide();
    page.pageshow(); await settled(page);
    assert.equal(page.nodes['user-profile'].hidden, false);
    page.scenario('expired'); page.visible(); await settled(page);
    assert.equal(page.nodes['user-profile'].hidden, true); assert.equal(page.nodes['account-name'].textContent, '');
});
