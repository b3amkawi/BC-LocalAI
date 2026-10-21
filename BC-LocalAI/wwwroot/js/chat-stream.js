/*
 * Streaming chat logic (FE-002). No DOM access here so it can be unit-tested with Node:
 *   node --test tests/ui
 * Reads POST /api/chat/stream (Server-Sent Events, contract: docs/BE-004-SSE-CONTRACT.md) and turns it into
 * UI states: idle -> connecting -> streaming -> completed | cancelled | error.
 * The same file is loaded by the browser after chat-core.js and exposes window.ChatStream.
 */
(function (root, factory) {
    'use strict';
    if (typeof module === 'object' && module.exports) {
        module.exports = factory(require('./chat-core.js'));
    } else {
        root.ChatStream = factory(root.ChatCore);
    }
}(typeof self !== 'undefined' ? self : this, function (core) {
    'use strict';

    var STATES = Object.freeze({
        IDLE: 'idle',
        CONNECTING: 'connecting',
        // FE-004: `started` only means the backend accepted the request. The model is known to work on it after
        // `processing` (when the backend sends it) or the first `delta`.
        ACCEPTED: 'accepted',
        QUEUED: 'queued',
        PROCESSING: 'processing',
        STREAMING: 'streaming',
        COMPLETED: 'completed',
        CANCELLED: 'cancelled',
        ERROR: 'error'
    });

    // What the page shows next to the answer while it is being written or when it did not finish.
    var STATUS_TEXT = Object.freeze({
        connecting: 'กำลังเชื่อมต่อ AI…',
        accepted: 'ได้รับคำถามแล้ว กำลังรอ AI เริ่มตอบ…',
        queued: 'ระบบมีผู้ใช้หลายคน คำถามของคุณอยู่ในคิว กำลังรอถึงตา…',
        processing: 'ถึงคิวแล้ว AI กำลังประมวลผล…',
        streaming: 'AI กำลังพิมพ์คำตอบ…',
        cancelled: 'หยุดการสร้างคำตอบแล้ว',
        incomplete: 'คำตอบไม่สมบูรณ์'
    });

    /** True while an answer is on its way (anything between sending and a terminal outcome): Stop applies. */
    function isActive(state) {
        return state === STATES.CONNECTING || state === STATES.ACCEPTED || state === STATES.QUEUED
            || state === STATES.PROCESSING || state === STATES.STREAMING;
    }

    function isTerminal(state) {
        return state === STATES.COMPLETED || state === STATES.CANCELLED || state === STATES.ERROR;
    }

    /**
     * Incremental SSE parser (WHATWG event-stream rules, the subset the API uses).
     * feed() takes already-decoded text in any chunking, even one character at a time or a CR and its LF in
     * different chunks. An event is dispatched only at its blank line, so an event cut off by a broken connection
     * is never delivered. Comment lines (":") and unknown fields (id, retry) are ignored.
     */
    function createSseParser(onEvent) {
        var buffer = '';
        var eventName = '';
        var dataLines = [];
        var hasData = false;
        var pendingCR = false;

        function dispatch() {
            if (hasData) {
                onEvent({ event: eventName || 'message', data: dataLines.join('\n') });
            }
            eventName = '';
            dataLines = [];
            hasData = false;
        }

        function processLine(line) {
            if (line === '') {
                dispatch();
                return;
            }
            if (line.charAt(0) === ':') {
                return;
            }
            var colon = line.indexOf(':');
            var field = colon === -1 ? line : line.slice(0, colon);
            var value = colon === -1 ? '' : line.slice(colon + 1);
            if (value.charAt(0) === ' ') {
                value = value.slice(1);
            }
            if (field === 'event') {
                eventName = value;
            } else if (field === 'data') {
                dataLines.push(value);
                hasData = true;
            }
        }

        return {
            feed: function (text) {
                for (var i = 0; i < text.length; i += 1) {
                    var ch = text.charAt(i);
                    if (pendingCR) {
                        pendingCR = false;
                        if (ch === '\n') {
                            continue; // the LF of a CRLF that was split across chunks
                        }
                    }
                    if (ch === '\r') {
                        processLine(buffer);
                        buffer = '';
                        pendingCR = true;
                    } else if (ch === '\n') {
                        processLine(buffer);
                        buffer = '';
                    } else {
                        buffer += ch;
                    }
                }
            }
        };
    }

    /**
     * Retry-After as the backend sends it: whole seconds. A date, a fraction, a negative, zero, text or an absurdly
     * large number is treated as "no guidance" (null); nothing is guessed. Capped to one hour.
     */
    function parseRetryAfter(value) {
        var whole = typeof value === 'number' ? Number.isInteger(value) : typeof value === 'string' && /^[0-9]{1,6}$/.test(value.trim());
        if (!whole) {
            return null;
        }
        var seconds = Number(value);
        return seconds >= 1 && seconds <= 3600 ? seconds : null;
    }

    function tryParseObject(text) {
        try {
            var value = JSON.parse(text);
            return value && typeof value === 'object' && !Array.isArray(value) ? value : null;
        } catch (error) {
            return null;
        }
    }

    /**
     * State of one answer. handle() applies one SSE event; the returned snapshot is what the UI shows.
     * Rules (BE-004 contract): `started` first; `delta` text is appended in order, unchanged; exactly one terminal
     * outcome; `completed` only for a non-blank answer that followed `started`. Anything else is an incomplete answer.
     */
    function createStreamSession() {
        var state = STATES.CONNECTING;
        var text = '';
        var model = null;
        var code = null;
        var partial = false;
        var ahead = null;
        var retryAfterSeconds = null;

        function snapshot() {
            return {
                state: state,
                text: text,
                model: model,
                code: code,
                message: code ? core.describeFailure(code, retryAfterSeconds) : null,
                partial: partial,
                // FE-004: only what the backend reported. `ahead` is a snapshot from when the request was accepted,
                // never an ETA and never updated by the page.
                ahead: ahead,
                retryAfterSeconds: retryAfterSeconds
            };
        }

        function fail(errorCode) {
            state = STATES.ERROR;
            code = core.failure(errorCode).code; // unknown codes become INTERNAL_ERROR; server text is never shown
            partial = text.length > 0;
            return snapshot();
        }

        return {
            snapshot: snapshot,

            handle: function (sseEvent) {
                if (isTerminal(state)) {
                    return snapshot(); // one terminal outcome only: later events never change the answer
                }

                var data = tryParseObject(sseEvent.data);
                switch (sseEvent.event) {
                case 'started':
                    if (state !== STATES.CONNECTING || !data || typeof data.model !== 'string' || data.model.trim() === '') {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    model = data.model;
                    state = STATES.ACCEPTED;
                    return snapshot();

                case 'queued':
                    if (state !== STATES.ACCEPTED || !data) {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    ahead = Number.isInteger(data.ahead) && data.ahead >= 0 && data.ahead <= 100000 ? data.ahead : null;
                    state = STATES.QUEUED;
                    return snapshot();

                case 'processing':
                    if (state !== STATES.ACCEPTED && state !== STATES.QUEUED) {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    state = STATES.PROCESSING;
                    return snapshot();

                case 'delta':
                    // A delta cannot arrive while the request is still waiting in the queue.
                    if ((state !== STATES.ACCEPTED && state !== STATES.PROCESSING && state !== STATES.STREAMING)
                        || !data || typeof data.text !== 'string') {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    state = STATES.STREAMING;
                    text += data.text;
                    return snapshot();

                case 'completed':
                    if (state !== STATES.STREAMING || !data || text.trim() === '') {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    state = STATES.COMPLETED;
                    return snapshot();

                case 'error':
                    fail(data && typeof data.code === 'string' ? data.code : 'INTERNAL_ERROR');
                    retryAfterSeconds = data && typeof data.retryAfterSeconds === 'number' ? parseRetryAfter(data.retryAfterSeconds) : null; // a JSON number only; the header form is a string
                    return snapshot();

                case 'cancelled':
                    state = STATES.CANCELLED;
                    partial = text.length > 0;
                    return snapshot();

                default:
                    return snapshot(); // unknown event names are ignored (forward compatible)
                }
            },

            /** The connection ended. Without a terminal event the answer is incomplete, whatever text arrived. */
            end: function () {
                return isTerminal(state) ? snapshot() : fail('STREAM_INTERRUPTED');
            },

            /** The user pressed Stop (or left). Keeps what arrived; never turns it into a completed answer. */
            cancel: function () {
                if (!isTerminal(state)) {
                    state = STATES.CANCELLED;
                    partial = text.length > 0;
                }
                return snapshot();
            },

            /** A failure outside the event stream (HTTP error, network, timeout). */
            fail: function (errorCode, retryAfter) {
                if (isTerminal(state)) {
                    return snapshot();
                }
                fail(errorCode);
                retryAfterSeconds = parseRetryAfter(retryAfter);
                return snapshot();
            }
        };
    }

    function isEventStream(contentType) {
        return typeof contentType === 'string' && contentType.toLowerCase().split(';')[0].trim() === 'text/event-stream';
    }

    /**
     * Runs one streamed answer from a fetch() Response and reports every change through onUpdate(snapshot).
     * Returns the final snapshot. `isAborted()` tells a user Stop from a timeout when the read is aborted.
     *  - HTTP error before the stream: the JSON error contract (e.g. 400 INVALID_MESSAGE) -> error state.
     *  - Not text/event-stream: AI_INVALID_RESPONSE.
     *  - Bytes are decoded with a streaming UTF-8 decoder, so Thai characters split across network reads stay intact.
     */
    async function readAnswer(response, onUpdate, options) {
        var session = createStreamSession();
        var settings = options || {};
        var report = typeof onUpdate === 'function' ? onUpdate : function () {};

        if (!response.ok) {
            var bodyText = '';
            try {
                bodyText = await response.text();
            } catch (error) {
                bodyText = '';
            }
            // 429 / 503 from the queue arrive here, before any event; Retry-After is used only when it is valid.
            var retryHeader = response.headers && typeof response.headers.get === 'function' ? response.headers.get('Retry-After') : null;
            var failed = session.fail(core.parseErrorCode(bodyText) || 'INTERNAL_ERROR', retryHeader);
            report(failed);
            return failed;
        }

        if (!isEventStream(response.headers.get('Content-Type')) || !response.body) {
            var invalid = session.fail('AI_INVALID_RESPONSE');
            report(invalid);
            return invalid;
        }

        var reader = response.body.getReader();
        var decoder = new TextDecoder('utf-8');
        var parser = createSseParser(function (sseEvent) {
            if (typeof settings.onEvent === 'function' && !isTerminal(session.snapshot().state)) settings.onEvent(sseEvent);
            report(session.handle(sseEvent));
        });

        var bytesRead = 0;

        try {
            for (;;) {
                var chunk = await reader.read();
                if (chunk.done) {
                    parser.feed(decoder.decode());
                    break;
                }
                bytesRead += chunk.value.byteLength;
                if (bytesRead > 2 * 1024 * 1024) {
                    var tooLarge = session.fail('AI_RESPONSE_TOO_LARGE'); report(tooLarge); return tooLarge;
                }
                parser.feed(decoder.decode(chunk.value, { stream: true }));
                if (isTerminal(session.snapshot().state)) {
                    break; // the answer is settled; do not wait for the server to close
                }
            }
        } catch (error) {
            var aborted = error && error.name === 'AbortError';
            var final = aborted && settings.isTimeout && settings.isTimeout()
                ? session.fail('AI_TIMEOUT')
                : aborted
                    ? session.cancel()
                    : session.fail(error && error.code ? error.code : 'STREAM_INTERRUPTED');
            report(final);
            return final;
        } finally {
            try {
                reader.releaseLock();
            } catch (ignored) {
                // the lock may already be gone after an abort
            }
        }

        if (isTerminal(session.snapshot().state)) {
            return session.snapshot(); // already reported when the outcome event arrived
        }
        var last = session.end();
        report(last);
        return last;
    }

    return {
        STATES: STATES,
        STATUS_TEXT: STATUS_TEXT,
        isTerminal: isTerminal,
        isActive: isActive,
        parseRetryAfter: parseRetryAfter,
        createSseParser: createSseParser,
        createStreamSession: createStreamSession,
        isEventStream: isEventStream,
        readAnswer: readAnswer
    };
}));
