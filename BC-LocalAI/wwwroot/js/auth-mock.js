/* Mock transport for the opt-in localhost Development preview only. No fetch, cookies or storage. */
(function (root, factory) {
    'use strict';
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.AuthMock = factory();
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';
    const SCENARIOS = Object.freeze(['success', 'invalid', 'forbidden', 'csrf', 'limited', 'unavailable', 'network', 'expired']);
    function createTransport(delayMs = 250) {
        let session = null, token = null, nextToken = 0, scenario = 'success';
        const reply = (status, body = null) => ({ status, json: async () => body });
        function wait(signal) {
            return new Promise((resolve, reject) => {
                if (signal?.aborted) { reject(new Error('Cancelled')); return; }
                const abort = () => { clearTimeout(timer); signal.removeEventListener('abort', abort); reject(new Error('Cancelled')); };
                const timer = setTimeout(() => { signal?.removeEventListener('abort', abort); resolve(); }, delayMs);
                signal?.addEventListener('abort', abort, { once: true });
            });
        }
        async function request(path, options) {
            await wait(options.signal);
            if (scenario === 'network') throw new Error('Synthetic transport failure');
            if (scenario === 'unavailable') return reply(503);
            if (path === '/api/auth/csrf' && options.method === 'GET') {
                token = 'preview-' + (++nextToken); // ci-allow-secret: synthetic counter, no real antiforgery secret.
                return reply(200, { token });
            }
            if (path === '/api/auth/me' && options.method === 'GET') {
                if (scenario === 'forbidden') return reply(403);
                if (scenario === 'expired') session = null;
                return session ? reply(200, { user: session }) : reply(401);
            }
            if (options.method !== 'POST' || !token || options.headers['X-CSRF-TOKEN'] !== token || scenario === 'csrf')
                return reply(403, { error: { code: 'AUTH_CSRF_INVALID' } });
            if (path === '/api/auth/logout') { session = null; token = null; return reply(204); }
            if (path !== '/api/auth/login') return reply(404);
            if (scenario === 'invalid') return reply(401);
            if (scenario === 'limited') return reply(429);
            if (scenario === 'forbidden') return reply(403);
            const body = JSON.parse(options.body);
            session = { id: '00000000-0000-0000-0000-000000000003', username: body.username,
                displayName: 'ผู้ใช้ตัวอย่าง', roles: ['User'] };
            token = null;
            return reply(200, { user: session });
        }
        return Object.freeze({ request,
            setScenario: value => { if (!SCENARIOS.includes(value)) throw new TypeError('Unknown preview scenario.'); scenario = value; },
            reset: () => { session = null; token = null; }
        });
    }
    return Object.freeze({ SCENARIOS, createTransport });
}));
