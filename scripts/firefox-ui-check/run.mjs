// FE-002: drives real Firefox (headless) over WebDriver BiDi and runs the Chat page scenarios in scenarios.mjs.
// No packages needed (Node 22+, Firefox 129+). Needs the app on http://127.0.0.1:5290 and Ollama with qwen3:4b.
// Usage: node scripts/firefox-ui-check/run.mjs scripts/firefox-ui-check/scenarios.mjs <folder for screenshots>
// Set FIREFOX to the firefox.exe path if it is not in the default location. Each line printed is one scenario's result (JSON).
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const FIREFOX = process.env.FIREFOX || 'C:\\Program Files\\Mozilla Firefox\\firefox.exe';
const PORT = 9333;
const [scenarioFile, outDir] = process.argv.slice(2);
const profile = mkdtempSync(join(tmpdir(), 'ff-bidi-'));
const ff = spawn(FIREFOX, ['--headless', '--no-remote', '--remote-debugging-port', String(PORT), '-profile', profile, '--window-size=1280,800'], { stdio: 'ignore' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ws;
for (let i = 0; i < 60; i += 1) {
    try {
        ws = new WebSocket(`ws://127.0.0.1:${PORT}/session`);
        await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
        break;
    } catch { ws = null; await sleep(500); }
}
if (!ws) { console.error('could not connect to Firefox'); ff.kill(); process.exit(2); }

let nextId = 1;
const pending = new Map();
ws.onmessage = (m) => { const msg = JSON.parse(m.data); if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); } };
const send = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, (msg) => (msg.type === 'error' ? reject(new Error(`${method}: ${msg.error} ${msg.message}`)) : resolve(msg.result)));
    ws.send(JSON.stringify({ id, method, params }));
});

try {
    const session = await send('session.new', { capabilities: {} });
    console.log(`browser: ${session.capabilities.browserName} ${session.capabilities.browserVersion}`);
    const tree = await send('browsingContext.getTree', {});
    const context = tree.contexts[0].context;
    const evaluate = async (expression) => {
        const r = await send('script.evaluate', { expression, target: { context }, awaitPromise: true, resultOwnership: 'none' });
        if (r.type === 'exception') throw new Error(r.exceptionDetails.text);
        return r.result.value;
    };
    const scenarios = (await import(pathToFileURL(resolve(scenarioFile)).href)).default;
    for (const s of scenarios) {
        await send('browsingContext.navigate', { context, url: s.url, wait: 'complete' });
        if (s.viewport) await send('browsingContext.setViewport', { context, viewport: s.viewport });
        const value = await evaluate(s.script);
        console.log(`${s.name}: ${value}`);
        if (s.screenshot) {
            const shot = await send('browsingContext.captureScreenshot', { context });
            writeFileSync(join(outDir, s.screenshot), Buffer.from(shot.data, 'base64'));
        }
    }
    await send('session.end', {}).catch(() => {});
} catch (error) {
    console.error('FAILED: ' + error.message);
    process.exitCode = 1;
} finally {
    ws.close();
    ff.kill();
}
