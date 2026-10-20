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

const INCOMPLETE = REQUIRED.AI_INVALID_RESPONSE;
const incomplete = (body, label) => {
    const result = core.classifyResponse(200, typeof body === 'string' ? body : JSON.stringify(body));
    assert.equal(result.ok, false, label);
    assert.equal(result.code, 'AI_INVALID_RESPONSE', label);
    assert.equal(result.message, INCOMPLETE, label);
    assert.equal('response' in result, false, `${label}: nothing may be shown as an answer`);
};

test('a 200 response that is not the contract shape is an incomplete answer', () => {
    for (const body of ['', 'not json', 'null', '[]', '{}', '"2"', '5', 'true', '{"response":""}', '{"response":"   "}',
        '{"response":5}', '{"response":null,"model":"x"}', '{"model":"qwen3:4b"}']) {
        incomplete(body, body);
    }
});

test('a missing or invalid model makes the answer incomplete (review of PR #12)', () => {
    const answer = '2';
    const badModels = [
        ['model missing', undefined], ['model null', null], ['model empty', ''], ['model spaces only', '   '],
        ['model tab/newline only', '\t\n'], ['model number', 4], ['model zero', 0], ['model boolean true', true],
        ['model boolean false', false], ['model object', { name: 'qwen3:4b' }], ['model array', ['qwen3:4b']],
        ['model empty object', {}], ['model empty array', []]
    ];
    for (const [label, model] of badModels) {
        const body = model === undefined ? { response: answer } : { response: answer, model };
        incomplete(body, label);
    }
});

test('a missing or invalid response makes the answer incomplete even when model is valid', () => {
    const badAnswers = [
        ['response missing', undefined], ['response null', null], ['response empty', ''], ['response spaces only', '   '],
        ['response newlines only', '\n\n'], ['response number', 2], ['response boolean', false],
        ['response object', { text: '2' }], ['response array', ['2']]
    ];
    for (const [label, response] of badAnswers) {
        const body = response === undefined ? { model: 'qwen3:4b' } : { response, model: 'qwen3:4b' };
        incomplete(body, label);
    }
});

test('both values invalid is incomplete as well', () => {
    incomplete({}, 'neither');
    incomplete({ response: '', model: '' }, 'both empty');
    incomplete({ response: 1, model: 2 }, 'both numbers');
});

test('every combination of response and model: success only when BOTH are non-empty strings', () => {
    const values = ['2', 'qwen3:4b', ' x ', 'ก', '<b>', '0', undefined, null, '', ' ', 0, 1, false, true, {}, []];
    const good = (v) => typeof v === 'string' && v.trim() !== '';
    for (const response of values) {
        for (const model of values) {
            const body = {};
            if (response !== undefined) body.response = response;
            if (model !== undefined) body.model = model;
            const result = core.classifyResponse(200, JSON.stringify(body));
            const label = JSON.stringify(body);
            if (good(response) && good(model)) {
                assert.deepEqual(result, { ok: true, response, model }, label);
            } else {
                assert.equal(result.ok, false, label);
                assert.equal(result.code, 'AI_INVALID_RESPONSE', label);
            }
        }
    }
});

test('extra fields and any 2xx status do not matter when both values are good', () => {
    for (const status of [200, 201, 202, 299]) {
        const result = core.classifyResponse(status, '{"response":"ok","model":"qwen3:4b","extra":{"a":1}}');
        assert.deepEqual(result, { ok: true, response: 'ok', model: 'qwen3:4b' }, String(status));
    }
});

test('a missing model is never turned into a fake model name', () => {
    const result = core.classifyResponse(200, '{"response":"ok"}');
    assert.equal(result.ok, false);
    assert.equal('model' in result, false);
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
