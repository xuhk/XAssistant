/* 截对照页整页 */
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9412;
const PROFILE = 'C:\\Users\\x\\AppData\\Local\\Temp\\cdp-kbd-cmp';
const PAGE = 'file:///D:/code/XAssistant/designs/_probe/compare.html';
const OUT = 'D:\\code\\XAssistant\\designs\\keyboard\\_shots\\compare.png';

spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--no-proxy-server',
  '--hide-scrollbars', '--allow-file-access-from-files', '--window-size=1200,2400', 'about:blank',
], { detached: true, stdio: 'ignore' }).unref();

let v = null;
for (let i = 0; i < 40 && !v; i++) {
  try { const r = await fetch(`http://127.0.0.1:${PORT}/json/version`); if (r.ok) v = await r.json(); } catch {}
  if (!v) await sleep(300);
}
let target;
for (let i = 0; i < 40 && !target; i++) {
  const l = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
  target = l.find(t => t.type === 'page');
  if (!target) await sleep(300);
}
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let seq = 0; const pending = new Map();
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const send = (method, params = {}) => new Promise(r => { const id = ++seq; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });

await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(2500);
const h = await send('Runtime.evaluate', { expression: 'Math.ceil(document.body.scrollHeight)', returnByValue: true });
const height = h.result.result.value;
const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: 1160, height, scale: 1 } });
writeFileSync(OUT, Buffer.from(shot.result.data, 'base64'));
console.log('saved', OUT, 'height', height);
ws.close(); process.exit(0);
