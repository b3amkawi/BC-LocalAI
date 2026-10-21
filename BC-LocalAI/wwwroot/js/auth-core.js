/* FE-003 auth contract/state logic. No DOM, storage, logging or default network transport. */
(function (root, factory) {
    'use strict';
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.AuthCore = factory();
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';
    const MESSAGES = Object.freeze({
        AUTH_INVALID_REQUEST: 'กรุณาตรวจสอบชื่อบัญชีและรหัสผ่าน',
        AUTH_INVALID_CREDENTIALS: 'ชื่อบัญชีหรือรหัสผ่านไม่ถูกต้อง', // ci-allow-secret: public error label, not a credential.
        AUTH_REQUIRED: 'เซสชันหมดอายุ กรุณาเข้าสู่ระบบใหม่',
        AUTH_SESSION_CHANGED: 'บัญชีที่เข้าสู่ระบบเปลี่ยนไป กรุณาตรวจสอบเซสชันใหม่',
        AUTH_SESSION_ENDED: 'เซสชันสิ้นสุดแล้ว กรุณาเข้าสู่ระบบใหม่',
        AUTH_FORBIDDEN: 'ไม่มีสิทธิ์ดำเนินการนี้',
        AUTH_CSRF_INVALID: 'คำขอไม่ผ่านการตรวจสอบความปลอดภัย กรุณาลองใหม่',
        AUTH_RATE_LIMITED: 'พยายามเข้าสู่ระบบบ่อยเกินไป กรุณารอก่อนลองใหม่',
        AUTH_UNAVAILABLE: 'ระบบเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง',
        NETWORK_ERROR: 'เชื่อมต่อระบบไม่ได้ กรุณาลองใหม่',
        INVALID_RESPONSE: 'ได้รับข้อมูลเข้าสู่ระบบที่ไม่สมบูรณ์',
        CANCELLED: '',
        INTERNAL_ERROR: 'เกิดข้อผิดพลาด กรุณาลองใหม่'
    });
    function failure(code, field = null) {
        const safe = Object.hasOwn(MESSAGES, code) ? code : 'INTERNAL_ERROR';
        return { ok: false, code: safe, message: MESSAGES[safe], field };
    }
    function wellFormed(value) {
        for (let i = 0; i < value.length; i++) {
            const c = value.charCodeAt(i);
            if (c >= 0xd800 && c <= 0xdbff) {
                const next = value.charCodeAt(++i);
                if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
            } else if (c >= 0xdc00 && c <= 0xdfff) return false;
        }
        return true;
    }
    function validate(username, password) {
        if (typeof username !== 'string' || !/^[A-Za-z0-9._-]{1,128}$/.test(username))
            return failure('AUTH_INVALID_REQUEST', 'username');
        if (typeof password !== 'string' || password.length === 0 || password.length > 1024 ||
            !wellFormed(password) || new TextEncoder().encode(password).length > 4096)
            return failure('AUTH_INVALID_REQUEST', 'password');
        return null; // Preserve the exact password, including spaces and combining forms.
    }
    function safeUser(value) {
        if (!value || typeof value.id !== 'string' || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value.id) ||
            typeof value.username !== 'string' || !/^[A-Za-z0-9._-]{1,128}$/.test(value.username) ||
            typeof value.displayName !== 'string' || !value.displayName.trim() || value.displayName.length > 200 ||
            !Array.isArray(value.roles) || value.roles.length === 0 || value.roles.length > 2 ||
            value.roles.some(role => role !== 'User' && role !== 'Admin')) return null;
        // Whitelist the public DTO; never retain added hash/stamp/token fields, even from a bad response.
        return Object.freeze({ id: value.id, username: value.username, displayName: value.displayName,
            roles: Object.freeze([...value.roles]) });
    }
    function createClient(transport) {
        if (typeof transport !== 'function') throw new TypeError('An explicit auth transport is required.');
        async function send(path, method, signal, body, token) {
            if (signal?.aborted) return failure('CANCELLED');
            try {
                const headers = { Accept: 'application/json' };
                if (body !== undefined) headers['Content-Type'] = 'application/json';
                if (token) headers['X-CSRF-TOKEN'] = token;
                const response = await transport(path, { method, headers, credentials: 'same-origin', // ci-allow-secret: Fetch mode, not a credential.
                    cache: 'no-store', redirect: 'error', signal,
                    ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
                if (signal?.aborted) return failure('CANCELLED');
                if (response.status === 204 && path === '/api/auth/logout') return { ok: true };
                if (response.status === 200 && path !== '/api/auth/logout') {
                    let data;
                    try { data = await response.json(); }
                    catch { return failure('INVALID_RESPONSE'); }
                    if (path === '/api/auth/csrf') return typeof data?.token === 'string' &&
                        /^[A-Za-z0-9_-]{1,8192}$/.test(data.token) ? { ok: true, token: data.token } : failure('INVALID_RESPONSE');
                    const user = safeUser(data?.user);
                    return user ? { ok: true, user } : failure('INVALID_RESPONSE');
                }
                const codes = { 400: 'AUTH_INVALID_REQUEST', 401: path === '/api/auth/login' ? 'AUTH_INVALID_CREDENTIALS' : 'AUTH_REQUIRED', // ci-allow-secret: public error codes only.
                    403: 'AUTH_FORBIDDEN', 413: 'AUTH_INVALID_REQUEST', 429: 'AUTH_RATE_LIMITED', 503: 'AUTH_UNAVAILABLE' };
                // Never show or log response messages. Only distinguish the reviewed CSRF code from ordinary403.
                if (response.status === 403) {
                    try { if ((await response.json())?.error?.code === 'AUTH_CSRF_INVALID') return failure('AUTH_CSRF_INVALID'); }
                    catch { /* Keep a generic403; a malformed error body is never a reason to retry credentials. */ }
                }
                return failure(codes[response.status] || 'INTERNAL_ERROR');
            } catch { return failure(signal?.aborted ? 'CANCELLED' : 'NETWORK_ERROR'); }
        }
        async function mutation(path, signal, body) {
            // Fresh token for every mutation: Login/Logout change principal; never retry a submitted password.
            const csrf = await send('/api/auth/csrf', 'GET', signal);
            if (!csrf.ok) return csrf;
            return send(path, 'POST', signal, body, csrf.token);
        }
        return Object.freeze({
            me: signal => send('/api/auth/me', 'GET', signal),
            login: (username, password, signal) => {
                const invalid = validate(username, password);
                return invalid ? Promise.resolve(invalid) : mutation('/api/auth/login', signal, { username, password });
            },
            logout: signal => mutation('/api/auth/logout', signal)
        });
    }
    function createSessionController(client, onChange = () => {}) {
        let state = Object.freeze({ status: 'checking', busy: false, user: null, code: null, message: '', field: null });
        let active = null, generation = 0, knownSession = false;
        function publish(status, user = null, result = null, busy = false) {
            state = Object.freeze({ status, busy, user, code: result?.code || null,
                message: result?.message || '', field: result?.field || null });
            onChange(state);
        }
        async function run(kind, action) {
            if (active) return false; // No duplicate Login/Logout or overlapping identity checks.
            const current = ++generation;
            active = new AbortController();
            const signal = active.signal;
            publish(kind === 'login' ? 'unauthenticated' : 'checking', null, null, true);
            let result;
            try { result = await action(signal); }
            catch { result = failure('NETWORK_ERROR'); }
            if (current !== generation) return false; // A prior identity must never reappear after navigation/reset.
            active = null;
            if (result.ok && kind !== 'logout') {
                const user = safeUser(result.user);
                if (user) { knownSession = true; publish('authenticated', user); return true; }
                result = failure('INVALID_RESPONSE');
            }
            if (result.ok && kind === 'logout') { knownSession = false; publish('unauthenticated'); return true; }
            if (result.code === 'AUTH_REQUIRED') {
                const expired = knownSession;
                knownSession = false;
                publish(expired ? 'expired' : 'unauthenticated', null, expired ? result : null);
            } else {
                publish(['AUTH_INVALID_REQUEST', 'AUTH_INVALID_CREDENTIALS', 'AUTH_RATE_LIMITED'].includes(result.code)
                    ? 'unauthenticated' : 'error', null, result);
            }
            return false;
        }
        return Object.freeze({
            snapshot: () => state,
            checkSession: () => run('me', signal => client.me(signal)),
            login: (username, password) => run('login', signal => client.login(username, password, signal)),
            logout: () => run('logout', signal => client.logout(signal)),
            invalidate: code => {
                generation++; active?.abort(); active = null; knownSession = false;
                publish(['AUTH_REQUIRED', 'AUTH_SESSION_CHANGED', 'AUTH_SESSION_ENDED'].includes(code) ? 'expired' : 'error', null, failure(code));
            },
            clear: () => { generation++; active?.abort(); active = null; knownSession = false; publish('unauthenticated'); }
        });
    }
    return Object.freeze({ MESSAGES, failure, validate, safeUser, createClient, createSessionController });
}));
