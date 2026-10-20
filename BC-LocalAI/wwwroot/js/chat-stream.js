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
        STREAMING: 'streaming',
        COMPLETED: 'completed',
        CANCELLED: 'cancelled',
        ERROR: 'error'
    });

    // What the page shows next to the answer while it is being written or when it did not finish.
    var STATUS_TEXT = Object.freeze({
        connecting: 'กำลังเชื่อมต่อ AI…',
        streaming: 'AI กำลังพิมพ์คำตอบ…',
        cancelled: 'หยุดการสร้างคำตอบแล้ว',
        incomplete: 'คำตอบไม่สมบูรณ์'
    });

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

        function snapshot() {
            return {
                state: state,
                text: text,
                model: model,
                code: code,
                message: code ? core.messageFor(code) : null,
                partial: partial
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
                    state = STATES.STREAMING;
                    return snapshot();

                case 'delta':
                    if (state !== STATES.STREAMING || !data || typeof data.text !== 'string') {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    text += data.text;
                    return snapshot();

                case 'completed':
                    if (state !== STATES.STREAMING || !data || text.trim() === '') {
                        return fail('AI_INVALID_RESPONSE');
                    }
                    state = STATES.COMPLETED;
                    return snapshot();

                case 'error':
                    return fail(data && typeof data.code === 'string' ? data.code : 'INTERNAL_ERROR');

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
            fail: function (errorCode) {
                return isTerminal(state) ? snapshot() : fail(errorCode);
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
            var failed = session.fail(core.parseErrorCode(bodyText) || 'INTERNAL_ERROR');
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
            report(session.handle(sseEvent));
        });

        try {
            for (;;) {
                var chunk = await reader.read();
                if (chunk.done) {
                    parser.feed(decoder.decode());
                    break;
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
                    : session.fail('STREAM_INTERRUPTED');
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
        createSseParser: createSseParser,
        createStreamSession: createStreamSession,
        isEventStream: isEventStream,
        readAnswer: readAnswer
    };
}));
