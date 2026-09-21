/* 裁参考图局部原始像素 -> PNG（不做任何色彩处理），用于判断真实明暗关系 */
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9414;
const PROFILE = 'C:\\Users\\x\\AppData\\Local\\Temp\\cdp-kbd-crop';
const REF = 'file:///C:/Users/x/.workbuddy/clipboard-images/clipboard-2026-09-20T12-17-38-465Z-3d8d29ec.jpg';
const OUTDIR = 'D:\\code\\XAssistant\\designs\\keyboard\\_shots\\';

spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--no-proxy-server',
  '--hide-scrollbars', '--allow-file-access-from-files', '--window-size=1200,800', 'about:blank',
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
const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails.exception || r.result.exceptionDetails));
  return r.result?.result?.value;
};

await send('Page.enable'); await send('Runtime.enable');
await send('Page.navigate', { url: 'file:///D:/code/XAssistant/designs/_probe/compare.html' });
await sleep(400);

const res = await evaluate(`(async () => {
  const img = new Image(); img.src = ${JSON.stringify(REF)}; await img.decode();
  const W = img.naturalWidth, H = img.naturalHeight;
  const src = document.createElement('canvas'); src.width = W; src.height = H;
  src.getContext('2d').drawImage(img, 0, 0);
  const shots = {
    'ref-topleft':  [0, 0, 340, 130, 2],
    'ref-row1':     [0, 105, 340, 130, 2],
    'ref-topright': [W-360, 0, 360, 130, 2],
    'ref-bottom':   [0, H-140, 900, 140, 1.6],
  };
  const out = {};
  for (const [name, [x, y, w, h, s]] of Object.entries(shots)) {
    const cc = document.createElement('canvas'); cc.width = Math.round(w*s); cc.height = Math.round(h*s);
    const cx = cc.getContext('2d');
    cx.imageSmoothingEnabled = false;
    cx.drawImage(src, x, y, w, h, 0, 0, cc.width, cc.height);
    out[name] = cc.toDataURL('image/png').slice(22);
  }
  // 同时给出若干关键像素
  const px = (x, y) => { const d = src.getContext('2d').getImageData(x, y, 1, 1).data; return '#'+[d[0],d[1],d[2]].map(n=>n.toString(16).padStart(2,'0')).join(''); };
  return { shots: out, probe: {
      '背景·左上(2,2)': px(2,2), '背景·上边中(960,2)': px(960,2),
      '背景·左中(2,300)': px(2,300), '背景·左下(2,585)': px(2,585),
      'Esc键面(56,40)': px(56,40), 'Esc上边框(56,8)': px(56,8),
      '基板·Esc右间隙(105,40)': px(105,40),
    } };
})()`);

for (const [name, b64] of Object.entries(res.shots)) {
  writeFileSync(OUTDIR + name + '.png', Buffer.from(b64, 'base64'));
  console.log('saved', name);
}
console.log('probe', JSON.stringify(res.probe, null, 2));
ws.close(); process.exit(0);
