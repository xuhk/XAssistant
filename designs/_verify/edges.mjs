/* 用暗色描边定位参考图每行按键的真实左右边界，换算成 u，与我的模型逐键对齐 */
import { spawn } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9415;
const PROFILE = 'C:\\Users\\x\\AppData\\Local\\Temp\\cdp-kbd-edge';
const REF = 'file:///C:/Users/x/.workbuddy/clipboard-images/clipboard-2026-09-20T12-17-38-465Z-3d8d29ec.jpg';

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
  const c = document.createElement('canvas'); c.width = W; c.height = H;
  const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
  const d = ctx.getImageData(0, 0, W, H).data;
  const lum = (x, y) => { const i = (y*W+x)*4; return 0.299*d[i] + 0.587*d[i+1] + 0.114*d[i+2]; };

  // 纵向:先找 6 行的中心。用「每行内暗像元(描边)横向计数」的纵向分布来找行心
  const darkPerRow = [];
  for (let y = 0; y < H; y++) { let n = 0; for (let x = 0; x < W; x += 2) if (lum(x, y) < 180) n++; darkPerRow.push(n); }
  const rowPeaks = [];
  for (let y = 2; y < H-2; y++) if (darkPerRow[y] > 120 && darkPerRow[y] >= darkPerRow[y-1] && darkPerRow[y] > darkPerRow[y+1]) rowPeaks.push(y);
  // 合并相邻峰
  const merged = [];
  for (const p of rowPeaks) { const last = merged[merged.length-1]; if (last && p - last[last.length-1] < 20) last.push(p); else merged.push([p]); }
  const rowCenters = merged.map(g => Math.round(g.reduce((a,b)=>a+b,0)/g.length));

  // 贴着键帽下缘取样,避开字形笔画(只穿左右竖描边)
  const SCAN_Y = [96, 182, 505, 545];

  // 给定 y,找暗描边的簇中心(= 按键左右边界)
  const edgesAt = (y) => {
    const xs = [];
    for (let x = 0; x < W; x++) if (lum(x, y) < 185) xs.push(x);
    const groups = [];
    for (const x of xs) { const last = groups[groups.length-1]; if (last && x - last[last.length-1] <= 6) last.push(x); else groups.push([x]); }
    return groups.filter(g => g.length <= 8).map(g => Math.round(g.reduce((a,b)=>a+b,0)/g.length));
  };

  // 用第一行的键边界推断 u 与左基点:第一行 13 个 1u 键 + Backspace 2u,共 15u
  const analyze = (y) => {
    const e = edgesAt(y);
    return e;
  };
  return { size: [W,H], rowCenters, rows: SCAN_Y.map(y => ({ y, e: analyze(y) })) };
})()`);

const U = 83, X0 = 30;                       // 由数字行推出的单位长度与左基点
const norm = arr => arr.map(x => +((x - X0) / U).toFixed(2));

console.log('size', res.size);
console.log('行内暗像元峰(y)', JSON.stringify(res.rowCenters));
for (const { y, e } of res.rows) {
  console.log('\\n--- y=' + y + ' 共 ' + e.length + ' 条 ---');
  console.log('px :', e.join(', '));
  console.log('u  :', norm(e).join(', '));
}
ws.close(); process.exit(0);
