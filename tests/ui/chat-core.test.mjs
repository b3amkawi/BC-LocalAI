// Unit tests for the Chat UI logic (FE-001). No dependencies: uses Node's built-in test runner.
//   node --test tests/ui
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../BC-LocalAI/wwwroot/js/chat-core.js');

// The texts required by Issue #10, section 5.
const REQUIRED = {
    INVALID_MESSAGE: 'กรุณาพิมพ์ข้อความก่อนส่ง',
    AI_UNAVAILABLE: 'ไม่สามารถเชื่อมต่อ AI ได้ กรุณาตรวจว่า Ollama เปิดอยู่',
    AI_TIMEOUT: 'AI ใช้เวลาตอบนานเกินไป กรุณาลองใหม่',
    AI_PROVIDER_ERROR: 'AI เกิดข้อผิดพลาด กรุณาลองใหม่',
    AI_INVALID_RESPONSE: 'ได้รับคำตอบที่ไม่สมบูรณ์ กรุณาลองใหม่',
    INTERNAL_ERROR: 'ระบบเกิดข้อผิดพลาด กรุณาลองใหม่'
};

const errorBody = (code, message = 'x') => JSON.stringify({ error: { code, message } });

test('every error code shows exactly the text required by the issue', () => {
    for (const [code, text] of Object.entries(REQUIRED)) {
        assert.equal(core.messageFor(code), text, code);
    }
});

test('unknown, missing or malicious error codes fall back to INTERNAL_ERROR text', () => {
    for (const code of ['SOMETHING_NEW', '', undefined, null, 42, {}, [], '__proto__', 'constructor', 'toString', 'hasOwnProperty']) {
        assert.equal(core.messageFor(code), REQUIRED.INTERNAL_ERROR, String(code));
    }
});

test('network failure has its own readable text and never shows technical detail', () => {
    assert.equal(core.failure('NETWORK_ERROR').message, core.MESSAGES.NETWORK_ERROR);
    assert.match(core.MESSAGES.NETWORK_ERROR, /เชื่อมต่อเซิร์ฟเวอร์/);
});

test('failure() only returns known codes', () => {
    assert.deepEqual(core.failure('AI_TIMEOUT'), { ok: false, code: 'AI_TIMEOUT', message: REQUIRED.AI_TIMEOUT });
    assert.equal(core.failure('weird').code, 'INTERNAL_ERROR');
    assert.equal(core.failure(undefined).code, 'INTERNAL_ERROR');
});

test('prepareMessage trims and treats blank input as nothing to send', () => {
    assert.equal(core.prepareMessage('  สวัสดี  '), 'สวัสดี');
    assert.equal(core.prepareMessage('line 1\nline 2'), 'line 1\nline 2');
    for (const blank of ['', '   ', '\n', '\t \n  ', ' ']) {
        assert.equal(core.prepareMessage(blank), '', JSON.stringify(blank));
    }
    for (const notText of [undefined, null, 5, {}, []]) {
        assert.equal(core.prepareMessage(notText), '');
    }
});

test('Enter sends, Shift+Enter does not, IME confirmation does not', () => {
    assert.equal(core.isSendKey({ key: 'Enter', shiftKey: false, isComposing: false, keyCode: 13 }), true);
    assert.equal(core.isSendKey({ key: 'Enter', shiftKey: true, isComposing: false, keyCode: 13 }), false);
    assert.equal(core.isSendKey({ key: 'Enter', shiftKey: false, isComposing: true, keyCode: 13 }), false);
    assert.equal(core.isSendKey({ key: 'Enter', shiftKey: false, isComposing: false, keyCode: 229 }), false);
    assert.equal(core.isSendKey({ key: 'a', shiftKey: false }), false);
    assert.equal(core.isSendKey({ key: ' ', shiftKey: false }), false);
    assert.equal(core.isSendKey({ key: 'Tab', shiftKey: false }), false);
    assert.equal(core.isSendKey(undefined), false);
    assert.equal(core.isSendKey(null), false);
});

test('parseErrorCode reads only the documented error shape', () => {
    assert.equal(core.parseErrorCode(errorBody('AI_TIMEOUT')), 'AI_TIMEOUT');
    for (const bad of ['', 'not json', '<html>502 Bad Gateway</html>', 'null', '[]', '{}', '{"error":null}',
        '{"error":{}}', '{"error":{"code":5}}', '{"error":"AI_TIMEOUT"}', '{"code":"AI_TIMEOUT"}']) {
        assert.equal(core.parseErrorCode(bad), null, bad);
    }
});

test('a good 200 response gives the answer and the model', () => {
    const result = core.classifyResponse(200, JSON.stringify({ response: '2', model: 'qwen3:4b' }));
    assert.deepEqual(result, { ok: true, response: '2', model: 'qwen3:4b' });
});

test('an answer that looks like HTML is returned unchanged as plain data', () => {
    const html = '<img src=x onerror=alert(1)><script>alert(2)</script>';
    const result = core.classifyResponse(200, JSON.stringify({ response: html, model: 'qwen3:4b' }));
    assert.equal(result.ok, true);
    assert.equal(result.response, html);
});

test('a 200 response in the wrong shape is an incomplete answer', () => {
    const wrong = ['', 'not json', 'null', '[]', '{}', '{"response":""}', '{"response":"   "}', '{"response":5}',
        '{"response":null,"model":"x"}', '{"model":"qwen3:4b"}', '"2"'];
    for (const body of wrong) {
        const result = core.classifyResponse(200, body);
        assert.equal(result.ok, false, body);
        assert.equal(result.code, 'AI_INVALID_RESPONSE', body);
        assert.equal(result.message, REQUIRED.AI_INVALID_RESPONSE, body);
    }
});

test('a missing model name does not break a valid answer', () => {
    assert.deepEqual(core.classifyResponse(200, '{"response":"ok"}'), { ok: true, response: 'ok', model: '' });
});

test('each API error status maps to the required text', () => {
    const cases = [
        [400, 'INVALID_MESSAGE'], [503, 'AI_UNAVAILABLE'], [504, 'AI_TIMEOUT'],
        [502, 'AI_PROVIDER_ERROR'], [502, 'AI_INVALID_RESPONSE'], [500, 'INTERNAL_ERROR']
    ];
    for (const [status, code] of cases) {
        const result = core.classifyResponse(status, errorBody(code, 'secret detail'));
        assert.equal(result.ok, false);
        assert.equal(result.code, code);
        assert.equal(result.message, REQUIRED[code]);
        assert.ok(!result.message.includes('secret detail'), 'server text must never be shown');
    }
});

test('errors without the API contract (proxy page, empty body, unknown code) show the generic text', () => {
    for (const [status, body] of [[500, '<html>stack trace at Foo.Bar()</html>'], [404, ''], [413, 'Request Entity Too Large'],
        [502, errorBody('BRAND_NEW_CODE')], [400, '{"errors":{"$":["bad"]}}'], [301, ''], [0, '']]) {
        const result = core.classifyResponse(status, body);
        assert.equal(result.ok, false, `${status} ${body}`);
        assert.equal(result.message, REQUIRED.INTERNAL_ERROR, `${status} ${body}`);
    }
});
