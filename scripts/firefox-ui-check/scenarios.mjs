// FE-002 page scenarios for run.mjs. Each script runs inside the page and returns a JSON summary.
// The mock cases replace fetch inside the page with a fake SSE stream (allowed by the issue for cases a real model cannot produce).
const URL = process.env.CHAT_URL || 'http://127.0.0.1:5290/';

// Helpers injected before every scenario (same harness as the Chromium run).
const H = String.raw`
const $ = (id) => document.getElementById(id);
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const idle = async (max = 30000) => { const t = Date.now(); while ($('composer').dataset.state !== 'idle' && Date.now() - t < max) await wait(25); };
const lastAi = () => [...document.querySelectorAll('.message--ai')].pop();
const lastItem = () => [...document.querySelectorAll('#messages > li')].pop();
const enc = new TextEncoder();
const sse = (e, d) => 'event: ' + e + '\ndata: ' + JSON.stringify(d) + '\n\n';
const realFetch = window.fetch;
const mockStream = (parts, { type = 'text/event-stream', end = 'close', delay = 20, bytes = false } = {}) => {
  window.fetch = (url, opts) => {
    window.__lastUrl = url; window.__lastBody = opts.body;
    let q = parts.map((p) => enc.encode(p));
    if (bytes) q = q.flatMap((u) => [...u].map((b) => new Uint8Array([b])));
    const body = new ReadableStream({ async pull(c) {
      await wait(delay);
      if (opts.signal.aborted) { c.error(new DOMException('aborted', 'AbortError')); return; }
      if (q.length) c.enqueue(q.shift()); else if (end === 'close') c.close(); else if (end === 'error') c.error(new TypeError('network')); else return new Promise(() => {});
    } }, { highWaterMark: 0 });
    return Promise.resolve(new Response(body, { status: 200, headers: { 'Content-Type': type } }));
  };
};
const ask = async (text) => { $('message').value = text; $('composer').requestSubmit(); await wait(30); await idle(); const ai = lastAi(); const li = lastItem();
  return { aiState: ai && ai.dataset.state, aiText: ai && ai.querySelector('.message__text').textContent, elems: ai ? ai.querySelector('.message__text').children.length : -1,
    status: ai && ai.querySelector('.message__status') ? ai.querySelector('.message__status').textContent : null, last: li.className, lastText: li.querySelector('.message__text') && li.querySelector('.message__text').textContent, inputBack: $('message').value }; };
`;
const run = (body) => `(async () => { ${H}\n${body} })()`;

export default [
    {
        name: 'real Thai answer (states, in-place updates)',
        url: URL,
        screenshot: 'firefox-streaming-completed.png',
        script: run(String.raw`
const rec = []; new MutationObserver(() => { const ai = lastAi(); rec.push((ai ? ai.dataset.state : '-') + '|' + $('composer').dataset.state + '|' + (ai ? ai.querySelector('.message__text').textContent.length : 0) + '|' + !$('stop').hidden); }).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true });
$('message').value = 'อธิบายว่า Local AI คืออะไร ตอบ 3 ประโยค'; $('composer').requestSubmit(); await wait(50); await idle(60000);
const seq = []; for (const r of rec) { const k = r.split('|').slice(0, 2).join('/'); if (seq[seq.length - 1] !== k) seq.push(k); }
const lens = new Set(rec.map((r) => r.split('|')[2]));
const stopWhileStreaming = rec.filter((r) => r.startsWith('streaming|')).every((r) => r.endsWith('|true'));
return JSON.stringify({ seq, textSteps: lens.size, final: lastAi().dataset.state, len: lastAi().querySelector('.message__text').textContent.length, stopWhileStreaming, sendBack: !$('send').hidden, chip: $('model-chip').textContent, statusLeft: lastAi().querySelectorAll('.message__status').length });`)
    },
    {
        name: 'real Stop button mid-answer',
        url: URL,
        screenshot: 'firefox-stopped.png',
        script: run(String.raw`
$('message').value = 'Write the numbers from 1 to 250 separated by commas, nothing else.'; $('composer').requestSubmit();
for (let k = 0; k < 400 && !(lastAi() && lastAi().querySelector('.message__text').textContent.length > 20); k++) await wait(25);
$('stop').click(); await idle();
const ai = lastAi(); return JSON.stringify({ state: ai.dataset.state, len: ai.querySelector('.message__text').textContent.length, status: ai.querySelector('.message__status').textContent, sendBack: !$('send').hidden, errors: document.querySelectorAll('.message--error').length });`)
    },
    {
        name: 'real Esc mid-answer',
        url: URL,
        script: run(String.raw`
$('message').value = 'Write the numbers from 1 to 250 separated by commas, nothing else.'; $('composer').requestSubmit();
for (let k = 0; k < 400 && !(lastAi() && lastAi().querySelector('.message__text').textContent.length > 20); k++) await wait(25);
document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); await idle();
const ai = lastAi(); return JSON.stringify({ state: ai.dataset.state, len: ai.querySelector('.message__text').textContent.length, sendBack: !$('send').hidden });`)
    },
    {
        name: 'mock cases (XSS, interrupted, partial error, two in a row, network, not SSE, Thai byte by byte)',
        url: URL,
        script: run(String.raw`
const r = {};
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: '<img src=x onerror="window.__xss=1"><script>window.__xss=2<\/script>' }), sse('completed', {})]);
r.xss = await ask('xss'); r.xss.executed = window.__xss || 0; r.xss.elementsInMessages = document.querySelectorAll('#messages img, #messages script').length;
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: 'ครึ่งคำตอบ' })]); r.interrupted = await ask('interrupted');
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: 'บางส่วน' }), sse('error', { code: 'AI_RESPONSE_TOO_LARGE', message: 'detail', partial: true })]); r.partialError = await ask('partial error');
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: 'A' }), sse('completed', {})]); r.first = (await ask('first')).aiText;
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: 'B' }), sse('completed', {})]); r.second = (await ask('second')).aiText;
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: 'x' })], { end: 'error' }); r.network = (await ask('network')).lastText;
mockStream(['{"response":"x","model":"m"}'], { type: 'application/json' }); r.notSse = (await ask('not sse')).lastText;
mockStream([sse('started', { model: 'qwen3:4b' }), sse('delta', { text: 'ภาษาไทย 😊 é ok' }), sse('completed', {})], { bytes: true, delay: 1 }); r.bytes = (await ask('bytes')).aiText;
r.url = window.__lastUrl; window.fetch = realFetch;
return JSON.stringify(r);`)
    },
    {
        name: 'keyboard: Enter on empty input, Shift+Enter',
        url: URL,
        script: run(String.raw`
const input = $('message'); input.value = '   ';
input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })); await wait(50);
const hint = $('hint').textContent; const sentEmpty = document.querySelectorAll('.message--user').length;
input.value = 'line one'; const ev = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, bubbles: true, cancelable: true }); input.dispatchEvent(ev); await wait(50);
return JSON.stringify({ hint, sentEmpty, shiftEnterSent: document.querySelectorAll('.message--user').length, shiftEnterPrevented: ev.defaultPrevented });`)
    },
    {
        name: 'phone width 375 while streaming',
        url: URL,
        viewport: { width: 375, height: 812 },
        screenshot: 'firefox-mobile.png',
        script: run(String.raw`
$('message').value = 'อธิบายประโยชน์ของการใช้ AI ในบริษัท 4 ข้อ'; $('composer').requestSubmit();
for (let k = 0; k < 400 && !(lastAi() && lastAi().querySelector('.message__text').textContent.length > 10); k++) await wait(25);
const res = { state: $('composer').dataset.state, stopVisible: !$('stop').hidden, stopInside: $('stop').getBoundingClientRect().right <= innerWidth, overflowX: document.documentElement.scrollWidth > innerWidth };
await idle(60000); res.final = lastAi().dataset.state; return JSON.stringify(res);`)
    }
];
