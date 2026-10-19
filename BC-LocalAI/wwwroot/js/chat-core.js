/*
 * Chat UI core logic (FE-001). No DOM access here so it can be unit-tested with Node:
 *   node --test tests/ui
 * The same file is loaded by the browser and exposes window.ChatCore.
 */
(function (root, factory) {
    'use strict';
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.ChatCore = api;
    }
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    // User-facing texts for the /api/chat error contract (Issue #10, section 5).
    // NETWORK_ERROR is a browser-side case: the request never reached the API.
    var MESSAGES = Object.freeze({
        INVALID_MESSAGE: 'กรุณาพิมพ์ข้อความก่อนส่ง',
        AI_UNAVAILABLE: 'ไม่สามารถเชื่อมต่อ AI ได้ กรุณาตรวจว่า Ollama เปิดอยู่',
        AI_TIMEOUT: 'AI ใช้เวลาตอบนานเกินไป กรุณาลองใหม่',
        AI_PROVIDER_ERROR: 'AI เกิดข้อผิดพลาด กรุณาลองใหม่',
        AI_INVALID_RESPONSE: 'ได้รับคำตอบที่ไม่สมบูรณ์ กรุณาลองใหม่',
        INTERNAL_ERROR: 'ระบบเกิดข้อผิดพลาด กรุณาลองใหม่',
        NETWORK_ERROR: 'ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้ กรุณาตรวจการเชื่อมต่อแล้วลองใหม่'
    });

    function hasOwn(object, key) {
        return Object.prototype.hasOwnProperty.call(object, key);
    }

    /** Text for an error code. Unknown codes fall back to INTERNAL_ERROR; no server detail is ever shown. */
    function messageFor(code) {
        return typeof code === 'string' && hasOwn(MESSAGES, code) ? MESSAGES[code] : MESSAGES.INTERNAL_ERROR;
    }

    function failure(code) {
        var known = typeof code === 'string' && hasOwn(MESSAGES, code) ? code : 'INTERNAL_ERROR';
        return { ok: false, code: known, message: MESSAGES[known] };
    }

    /** Trims the input; returns '' when there is nothing to send (empty or only whitespace). */
    function prepareMessage(value) {
        return typeof value === 'string' ? value.trim() : '';
    }

    /** Enter sends; Shift+Enter is a new line; Enter that confirms an IME composition must not send. */
    function isSendKey(event) {
        return !!event
            && event.key === 'Enter'
            && !event.shiftKey
            && !event.isComposing
            && event.keyCode !== 229;
    }

    function tryParse(text) {
        try {
            return JSON.parse(text);
        } catch (error) {
            return undefined;
        }
    }

    /** Reads error.code from the API error contract; null when the body is not that shape. */
    function parseErrorCode(bodyText) {
        var body = tryParse(bodyText);
        if (body && typeof body === 'object' && body.error && typeof body.error.code === 'string') {
            return body.error.code;
        }
        return null;
    }

    function isNonEmptyString(value) {
        return typeof value === 'string' && value.trim() !== '';
    }

    /**
     * Turns an HTTP status and body text from POST /api/chat into either
     * { ok: true, response, model } or { ok: false, code, message }.
     * The API contract is { "response": string, "model": string }: both must be non-empty strings,
     * otherwise the answer is treated as incomplete (AI_INVALID_RESPONSE) and nothing is shown as an answer.
     */
    function classifyResponse(status, bodyText) {
        if (status >= 200 && status < 300) {
            var body = tryParse(bodyText);
            if (body && typeof body === 'object' && isNonEmptyString(body.response) && isNonEmptyString(body.model)) {
                return { ok: true, response: body.response, model: body.model };
            }
            return failure('AI_INVALID_RESPONSE');
        }

        return failure(parseErrorCode(bodyText));
    }

    return {
        MESSAGES: MESSAGES,
        messageFor: messageFor,
        failure: failure,
        prepareMessage: prepareMessage,
        isSendKey: isSendKey,
        parseErrorCode: parseErrorCode,
        classifyResponse: classifyResponse
    };
}));
