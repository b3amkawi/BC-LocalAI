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
        AUTH_REQUIRED: 'เซสชันหมดอายุ กรุณาเข้าสู่ระบบใหม่',
        AUTH_SESSION_ENDED: 'เซสชันสิ้นสุดแล้ว กรุณาเข้าสู่ระบบใหม่',
        AUTH_SESSION_CHANGED: 'บัญชีที่เข้าสู่ระบบเปลี่ยนไป กรุณาตรวจสอบเซสชันใหม่',
        AUTH_FORBIDDEN: 'ไม่มีสิทธิ์ดำเนินการนี้',
        AUTH_CSRF_INVALID: 'คำขอไม่ผ่านการตรวจสอบความปลอดภัย กรุณาลองใหม่',
        HISTORY_UNAVAILABLE: 'ไม่สามารถโหลดหรือบันทึกประวัติได้ กรุณาลองใหม่ภายหลัง',
        HISTORY_CONFLICT: 'มีคำตอบที่กำลังสร้างอยู่ กรุณารอหรือรีเฟรชประวัติ',
        CONVERSATION_NOT_FOUND: 'ไม่พบบทสนทนานี้ หรือคุณไม่มีสิทธิ์เข้าถึง',
        QUESTION_TOO_LONG: 'คำถามยาวเกินกว่าที่ระบบรองรับ กรุณาถามให้สั้นลง',
        AI_UNAVAILABLE: 'ไม่สามารถเชื่อมต่อ AI ได้ กรุณาตรวจว่า Ollama เปิดอยู่',
        AI_TIMEOUT: 'AI ใช้เวลาตอบนานเกินไป กรุณาลองใหม่',
        AI_PROVIDER_ERROR: 'AI เกิดข้อผิดพลาด กรุณาลองใหม่',
        // FE-004: queue refusals (BE-008). Distinct from AI_TIMEOUT (slow answer) and AI_UNAVAILABLE (Ollama down).
        AI_BUSY: 'ตอนนี้มีผู้ใช้จำนวนมาก ระบบยังไม่ว่างรับคำถามนี้ กรุณาลองใหม่ในอีกสักครู่',
        AI_REQUEST_IN_PROGRESS: 'คุณมีคำถามที่กำลังตอบอยู่ กรุณารอให้เสร็จหรือกดหยุดคำถามเดิมก่อนถามคำถามถัดไป',
        AI_INVALID_RESPONSE: 'ได้รับคำตอบที่ไม่สมบูรณ์ กรุณาลองใหม่',
        AI_RESPONSE_TOO_LARGE: 'คำตอบยาวเกินกว่าที่ระบบรองรับ กรุณาถามให้สั้นลงหรือแบ่งเป็นหลายคำถาม',
        INTERNAL_ERROR: 'ระบบเกิดข้อผิดพลาด กรุณาลองใหม่',
        NETWORK_ERROR: 'ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้ กรุณาตรวจการเชื่อมต่อแล้วลองใหม่',
        // FE-002: the stream ended before a terminal event (connection dropped); what arrived may be incomplete.
        STREAM_INTERRUPTED: 'การเชื่อมต่อขาดก่อนที่คำตอบจะเสร็จ คำตอบที่เห็นอาจไม่ครบ กรุณาลองใหม่'
    });

    function hasOwn(object, key) {
        return Object.prototype.hasOwnProperty.call(object, key);
    }

    var DEFAULT_REQUEST_TIMEOUT_MS = 200000;
    var MAX_REQUEST_TIMEOUT_MS = 3600000;

    /**
     * How long the browser waits for POST /api/chat. The server renders its own deadline plus a margin into the page,
     * so the server's AI_TIMEOUT arrives first. Anything missing or invalid falls back to 200 s; never unlimited.
     */
    function requestTimeoutMs(seconds) {
        var value = typeof seconds === 'string' && seconds.trim() !== '' ? Number(seconds) : seconds;
        if (typeof value !== 'number' || !isFinite(value) || value < 1) {
            return DEFAULT_REQUEST_TIMEOUT_MS;
        }
        return Math.min(Math.round(value * 1000), MAX_REQUEST_TIMEOUT_MS);
    }

    /** Text for an error code. Unknown codes fall back to INTERNAL_ERROR; no server detail is ever shown. */
    function messageFor(code) {
        return typeof code === 'string' && hasOwn(MESSAGES, code) ? MESSAGES[code] : MESSAGES.INTERNAL_ERROR;
    }

    /**
     * Error text plus, when the backend gave a valid Retry-After, how long it suggests waiting. No countdown and no
     * automatic retry: the user decides when to send again.
     */
    function describeFailure(code, retryAfterSeconds) {
        var text = messageFor(code);
        if ((code === 'AI_BUSY' || code === 'AI_REQUEST_IN_PROGRESS') && Number.isInteger(retryAfterSeconds)
            && retryAfterSeconds >= 1 && retryAfterSeconds <= 3600) {
            text += ' (ระบบแนะนำให้รออย่างน้อยประมาณ ' + retryAfterSeconds + ' วินาที)';
        }
        return text;
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
        describeFailure: describeFailure,
        failure: failure,
        prepareMessage: prepareMessage,
        isSendKey: isSendKey,
        parseErrorCode: parseErrorCode,
        classifyResponse: classifyResponse,
        requestTimeoutMs: requestTimeoutMs
    };
}));
