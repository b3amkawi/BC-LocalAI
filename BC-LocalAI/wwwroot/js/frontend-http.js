/* Real FE-003 transport: same-origin HTTPS, bounded bodies, full-operation deadlines, no retries/storage/logging. */
(function (root, factory) {
    'use strict';
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.FrontendHttp = factory();
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';
    const CODES = new Set(['AUTH_REQUIRED', 'AUTH_FORBIDDEN', 'AUTH_CSRF_INVALID', 'AUTH_HTTPS_REQUIRED', 'AUTH_SESSION_CHANGED',
        'AUTH_SESSION_ENDED', 'AUTH_UNAVAILABLE', 'HISTORY_UNAVAILABLE', 'HISTORY_CONFLICT', 'CONVERSATION_NOT_FOUND',
        'INVALID_ID', 'INVALID_TITLE', 'INVALID_CURSOR', 'INVALID_REQUEST', 'INVALID_MESSAGE', 'QUESTION_TOO_LONG',
        'AI_UNAVAILABLE', 'AI_TIMEOUT', 'AI_INVALID_RESPONSE', 'AI_BUSY', 'AI_RATE_LIMITED']);
    function fault(code, status = 0) { const e = new Error(code); e.code = code; e.status = status; return e; }
    function wait(promise, signal) {
        if (signal.aborted) return Promise.reject(fault('CANCELLED'));
        return new Promise((resolve, reject) => {
            const stop = () => reject(fault('CANCELLED'));
            signal.addEventListener('abort', stop, { once: true });
            Promise.resolve(promise).then(resolve, reject).finally(() => signal.removeEventListener('abort', stop));
        });
    }
    function create(fetcher, { protocol = 'https:', getActor = () => null, timeoutMs = 15000 } = {}) {
        if (typeof fetcher !== 'function') throw new TypeError('An explicit transport is required');
        function validPath(path) {
            if (typeof path !== 'string' || path.length > 1024 || !path.startsWith('/api/')) return false;
            const url = new URL(path, 'https://local.invalid');
            return !url.hash && url.origin === 'https://local.invalid' &&
                (/^\/api\/auth\/(csrf|me|login|logout)$/.test(url.pathname) || /^\/api\/chat(?:\/stream)?$/.test(url.pathname) ||
                /^\/api\/conversations(?:\/[0-9a-f-]{36}(?:\/messages)?)?$/i.test(url.pathname)) &&
                [...url.searchParams.keys()].every(key => key === 'limit' || key === 'cursor');
        }
        async function operation(path, init, actor, limit, consume) {
            if (protocol !== 'https:') throw fault('AUTH_HTTPS_REQUIRED');
            if (!validPath(path)) throw fault('INVALID_REQUEST');
            const controller = new AbortController();
            let expired = false, response;
            const relay = () => controller.abort();
            init.signal?.addEventListener('abort', relay, { once: true });
            if (init.signal?.aborted) relay();
            const timer = setTimeout(() => { expired = true; controller.abort(); }, Math.min(Math.max(limit, 1), 620000));
            try {
                const headers = { ...init.headers, Accept: init.headers?.Accept || 'application/json' };
                if (actor) headers['X-BC-Expected-User'] = actor;
                response = await wait(fetcher(path, { ...init, headers, signal: controller.signal,
                    credentials: 'same-origin', cache: 'no-store', redirect: 'error' }), controller.signal); // ci-allow-secret: Fetch mode only.
                if (response.status === 401) throw fault('AUTH_REQUIRED', 401);
                if (response.status === 403) {
                    let rejected;
                    try { rejected = await readJson(response, controller.signal); } catch { /* Status remains forbidden even for a malformed body. */ }
                    throw fault(rejected?.error?.code === 'AUTH_CSRF_INVALID' ? 'AUTH_CSRF_INVALID' : 'AUTH_FORBIDDEN', 403);
                }
                if (actor && response.status !== 403 && response.headers.get('X-BC-User') !== actor)
                    throw fault('AUTH_SESSION_CHANGED', 401);
                return await wait(consume(response, controller.signal), controller.signal);
            } catch (error) {
                if (expired) throw fault('REQUEST_TIMEOUT');
                if (controller.signal.aborted) throw fault('CANCELLED');
                throw error?.code ? error : fault('NETWORK_ERROR');
            } finally {
                clearTimeout(timer); init.signal?.removeEventListener('abort', relay);
                // readAnswer releases its reader after a terminal event. Stop an upstream that keeps sending beyond it.
                if (response?.body && !response.body.locked) { try { await response.body.cancel(); } catch { /* Already closed. */ } }
            }
        }
        async function readJson(response, signal) {
            if (response.status === 204) return null;
            if (!/^application\/json(?:\s*;|$)/i.test(response.headers.get('Content-Type') || '')) throw fault('INVALID_RESPONSE');
            const max = response.ok ? 2 * 1024 * 1024 : 16384;
            if (Number(response.headers.get('Content-Length')) > max || !response.body) throw fault('INVALID_RESPONSE');
            const reader = response.body.getReader(), decoder = new TextDecoder('utf-8', { fatal: true });
            let size = 0, text = '';
            try {
                for (;;) {
                    const chunk = await wait(reader.read(), signal);
                    if (chunk.done) break;
                    size += chunk.value.byteLength;
                    if (size > max) throw fault('INVALID_RESPONSE');
                    text += decoder.decode(chunk.value, { stream: true });
                }
                return JSON.parse(text + decoder.decode());
            } catch (error) { throw error?.code ? error : fault('INVALID_RESPONSE'); }
            finally { try { await reader.cancel(); } catch { /* Already aborted. */ } reader.releaseLock(); }
        }
        function raw(path, init = {}, actor = null) {
            return operation(path, init, actor, timeoutMs, async (response, signal) =>
                ({ status: response.status, ok: response.ok, headers: response.headers, data: await readJson(response, signal) }));
        }
        async function request(path, { method = 'GET', body, signal, actor = null } = {}) {
            const headers = { Accept: 'application/json' };
            if (method !== 'GET') {
                const csrf = await raw('/api/auth/csrf', { method: 'GET', signal });
                if (csrf.status !== 200 || !/^[A-Za-z0-9_-]{1,8192}$/.test(csrf.data?.token || '')) throw fault('AUTH_UNAVAILABLE');
                headers['X-CSRF-TOKEN'] = csrf.data.token;
            }
            if (body !== undefined) headers['Content-Type'] = 'application/json';
            const response = await raw(path, { method, headers, signal, ...(body === undefined ? {} : { body: JSON.stringify(body) }) }, actor);
            if (!response.ok) throw fault(CODES.has(response.data?.error?.code) ? response.data.error.code :
                response.status === 403 ? 'AUTH_FORBIDDEN' : 'HISTORY_UNAVAILABLE', response.status);
            return response.data;
        }
        return Object.freeze({ request,
            authTransport: async (path, init) => {
                // AuthCore needs the original Login401 to preserve the generic invalid-credential message.
                try {
                    const response = await raw(path, init, ['/api/auth/me', '/api/auth/logout'].includes(path) ? getActor() : null);
                    if (path === '/api/auth/me' && response.status === 200 && response.headers.get('X-BC-User') !== response.data?.user?.id)
                        throw fault('AUTH_SESSION_CHANGED', 401);
                    return { status: response.status, json: async () => response.data };
                } catch (error) {
                    if (error.status === 401 || error.status === 403) return { status: error.status, json: async () => ({ error: { code: error.code } }) };
                    throw error;
                }
            },
            stream: async (body, { actor, signal, timeout = 200000, consume }) => {
                const csrf = await request('/api/auth/csrf', { signal });
                if (!/^[A-Za-z0-9_-]{1,8192}$/.test(csrf?.token || '')) throw fault('AUTH_UNAVAILABLE');
                return operation('/api/chat/stream', { method: 'POST', signal,
                    headers: { Accept: 'text/event-stream', 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.token },
                    body: JSON.stringify(body) }, actor, timeout, async (response, deadlineSignal) => {
                    if (!response.ok) {
                        const data = await readJson(response, deadlineSignal);
                        throw fault(CODES.has(data?.error?.code) ? data.error.code : response.status === 403 ? 'AUTH_FORBIDDEN' : 'HISTORY_UNAVAILABLE', response.status);
                    }
                    return consume(response, deadlineSignal);
                });
            }
        });
    }
    return Object.freeze({ create, fault });
}));
