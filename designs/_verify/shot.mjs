/* CDP 渲染键盘页 → 截图 + DOM 几何断言 + 与参考图逐点比色 */
import { spawn } from 'node:child_process';
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9411;
const PROFILE = 'C:\\Users\\x\\AppData\\Local\\Temp\\cdp-kbd-profile';
const OUT = 'D:\\code\\XAssistant\\designs\\keyboard\\_shots';
const PAGE = 'file:///D:/code/XAssistant/designs/keyboard/index.html';
const REF = 'file:///C:/Users/x/.workbuddy/clipboard-images/clipboard-2026-09-20T12-17-38-465Z-3d8d29ec.jpg';
const VW = 1280, VH = 900;

mkdirSync(OUT, { recursive: true });

async function version() {
  for (let i = 0; i < 40; i++) {
    try { const r = await fetch(`http://127.0.0.1:${PORT}/json/version`); if (r.ok) return await r.json(); } catch {}
    await sleep(300);
  }
  throw new Error('chrome debug port not up');
}
const alive = await fetch(`http://127.0.0.1:${PORT}/json/version`).then(r => r.ok, () => false);
if (!alive) {
  rmSync(PROFILE, { recursive: true, force: true });
  spawn(CHROME, [
    '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${PROFILE}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--no-proxy-server',
    '--hide-scrollbars', '--allow-file-access-from-files', `--window-size=${VW},${VH}`, 'about:blank',
  ], { detached: true, stdio: 'ignore' }).unref();
}
console.log('chrome', (await version())['Browser']);

let target;
for (let i = 0; i < 40 && !target; i++) {
  const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
  target = list.find(t => t.type === 'page');
  if (!target) await sleep(300);
}
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let seq = 0; const pending = new Map(); const exceptions = [];
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); return; }
  if (m.method === 'Runtime.exceptionThrown') exceptions.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text);
};
const send = (method, params = {}) => new Promise(res => { const id = ++seq; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
async function evaluate(expression) {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails.exception || r.result.exceptionDetails));
  return r.result?.result?.value;
}

await send('Page.enable'); await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', { width: VW, height: VH, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: PAGE });
for (let i = 0; i < 60; i++) { await sleep(200); if (await evaluate('document.querySelectorAll(".cap").length').catch(() => 0) === 104) break; }
await sleep(400);

/* ---------- 1. DOM 几何断言 ---------- */
const diag = await evaluate(`(() => {
  const board = document.getElementById('kbdBoard');
  const plate = board.querySelector('.plate');
  const bodyEl = document.getElementById('body');
  const caps = [...document.querySelectorAll('.cap')];
  const u = parseFloat(getComputedStyle(board).getPropertyValue('--u'));
  const br = board.getBoundingClientRect(), pr = plate.getBoundingClientRect(), bp = bodyEl.getBoundingClientRect();
  const byKey = k => caps.find(e => e.dataset.key === k);
  const box = k => { const r = byKey(k).getBoundingClientRect(); return { l:r.left-br.left, t:r.top-br.top, w:r.width, h:r.height }; };
  const U = (px) => +((px - (pr.left - br.left) - 0.24*u) / u).toFixed(3);   // px → u（以键区左边界为 0）
  const rows = {};
  caps.forEach(c => { const r = c.getBoundingClientRect(); rows[Math.round((r.top - bp.top)/u)] = (rows[Math.round((r.top - bp.top)/u)]||0)+1; });
  const hist = {};
  caps.forEach(c => { const k = (c.getBoundingClientRect().width / u).toFixed(2); hist[k] = (hist[k]||0)+1; });
  return {
    keyCount: caps.length,
    u: +u.toFixed(2),
    boardW: Math.round(br.width), boardH: Math.round(br.height),
    aspect: +(br.height/br.width).toFixed(4),
    platePadRatio: +(((pr.left - br.left)/u)).toFixed(3),
    keyareaW: +((bp.width/u)).toFixed(3), keyareaH: +((bp.height/u)).toFixed(3),
    textOverflow: caps.filter(c => { const l=c.querySelector('.leg'); return l && l.scrollWidth > c.clientWidth+.5; }).map(c=>c.dataset.key),
    rowSpanKeys: caps.filter(c => c.getBoundingClientRect().height > u*1.5).map(c=>c.dataset.key),
    keysPerRow: rows,
    keyWidthHistogram: hist,
    /* 关键锚点（u）：与参考图实测值并排对比 */
    anchors: {
      'Esc.left':      U(box('Esc').l),
      'F1.left':       U(box('F1').l),
      'F4.right':      U(box('F4').l + box('F4').w),
      'F5.left':       U(box('F5').l),
      'F8.right':      U(box('F8').l + box('F8').w),
      'F9.left':       U(box('F9').l),
      'F12.right':     U(box('F12').l + box('F12').w),
      'PrtSc.left':    U(box('PrtSc').l),
      'Pause.right':   U(box('Pause').l + box('Pause').w),
      'Backspace.right': U(box('Backspace').l + box('Backspace').w),
      'Ins.left':      U(box('Ins').l),
      'PgUp.right':    U(box('PgUp').l + box('PgUp').w),
      'Num.left':      U(box('Num').l),
      'numpad.right':  U(box('-').l + box('-').w),
      'arrowLeft.left':U(box('←').l),
      'arrowUp.left':  U(box('↑').l),
    },
  };
})()`);
console.log('--- geometry ---');
console.log(JSON.stringify(diag, null, 2));

/* ---------- 2. 截图 ---------- */
const clip = await evaluate(`(()=>{const r=document.getElementById('kbdBoard').getBoundingClientRect();return{x:Math.floor(r.x),y:Math.floor(r.y),width:Math.ceil(r.width),height:Math.ceil(r.height)};})()`);
const shot = await send('Page.captureScreenshot', { format: 'png', clip: { ...clip, scale: 1 }, captureBeyondViewport: true });
writeFileSync(OUT + '\\kbd-light.png', Buffer.from(shot.result.data, 'base64'));

/* ---------- 3. 与参考图比对：外接框比例 + 色板直方图 ---------- */
const cmp = await evaluate(`(async () => {
  const analyze = async (src, tag) => {
    const img = new Image(); img.src = src; await img.decode();
    const W = img.naturalWidth, H = img.naturalHeight;
    const c = document.createElement('canvas'); c.width = W; c.height = H;
    const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
    const d = ctx.getImageData(0, 0, W, H).data;
    let x0 = W, y0 = H, x1 = -1, y1 = -1;
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const i = (y*W+x)*4;
      const l = 0.299*d[i] + 0.587*d[i+1] + 0.114*d[i+2];
      if (l < 250) { if (x<x0)x0=x; if (x>x1)x1=x; if (y<y0)y0=y; if (y>y1)y1=y; }
    }
    const hist = new Map();
    for (let y = y0; y <= y1; y += 2) for (let x = x0; x <= x1; x += 2) {
      const i = (y*W+x)*4;
      const k = ((d[i]>>3)<<10) | ((d[i+1]>>3)<<5) | (d[i+2]>>3);
      hist.set(k, (hist.get(k)||0) + 1);
    }
    const tot = [...hist.values()].reduce((a,b)=>a+b,0);
    const top = [...hist.entries()].sort((a,b)=>b[1]-a[1]).slice(0,12).map(([k,n]) => {
      const r=((k>>10)&31)<<3, g=((k>>5)&31)<<3, b=(k&31)<<3;
      return '#' + [r,g,b].map(v=>v.toString(16).padStart(2,'0')).join('') + '  ' + (n/tot*100).toFixed(1) + '%';
    });
    // 亮度分位数（最稳的整体明暗对比）
    const lums = [];
    for (let y = y0; y <= y1; y += 2) for (let x = x0; x <= x1; x += 2) {
      const i = (y*W+x)*4;
      lums.push(0.299*d[i] + 0.587*d[i+1] + 0.114*d[i+2]);
    }
    lums.sort((a,b)=>a-b);
    const q = p => Math.round(lums[Math.min(lums.length-1, Math.floor(lums.length*p))]);
    // 按内容框的相对位置取同名点
    const cw = x1-x0, ch = y1-y0;
    const pt = (fx, fy) => {
      const x = Math.round(x0 + cw*fx), y = Math.round(y0 + ch*fy);
      const i = (y*W+x)*4;
      return '#' + [d[i],d[i+1],d[i+2]].map(v=>v.toString(16).padStart(2,'0')).join('');
    };
    const pts = {
      '左上角(1,1)':     pt(0.0005, 0.002),
      '右上角':          pt(0.9995, 0.002),
      '左下角':          pt(0.0005, 0.998),
      '右下角':          pt(0.9995, 0.998),
      '顶边中点':        pt(0.5, 0.002),
      '基板·Esc与F1之间':    pt(0.083, 0.108),
      '键面·F5 上部':        pt(0.393, 0.051),
      '键面·Q 右上部':       pt(0.089, 0.346),
      '键面·Esc 上部':       pt(0.019, 0.051),
      '下缘·波浪键底边':     pt(0.023, 0.309),
    };
    // 暗部色（键帽边框 / 文字）
    const dark = new Map();
    let minL = 999, maxL = -1;
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
      const i = (y*W+x)*4;
      const l = 0.299*d[i] + 0.587*d[i+1] + 0.114*d[i+2];
      if (l < minL) minL = l; if (l > maxL) maxL = l;
      if (l < 190) { const k = ((d[i]>>3)<<10)|((d[i+1]>>3)<<5)|(d[i+2]>>3); dark.set(k,(dark.get(k)||0)+1); }
    }
    const dTot = [...dark.values()].reduce((a,b)=>a+b,0) || 1;
    const darkTop = [...dark.entries()].sort((a,b)=>b[1]-a[1]).slice(0,6).map(([k,n]) => {
      const r=((k>>10)&31)<<3, g=((k>>5)&31)<<3, b=(k&31)<<3;
      return '#' + [r,g,b].map(v=>v.toString(16).padStart(2,'0')).join('') + '  ' + (n/dTot*100).toFixed(1) + '%';
    });
    return { tag, size: [W,H], bbox: [x0,y0,x1,y1],
             contentW: x1-x0+1, contentH: y1-y0+1,
             aspect: +((y1-y0+1)/(x1-x0+1)).toFixed(4),
             lumRange: [Math.round(minL), Math.round(maxL)],
             darkShare: +((dTot/lums.length)*100).toFixed(1) + '%',
             lum: { p05: q(0.05), p25: q(0.25), p50: q(0.5), p75: q(0.75), p95: q(0.95) },
             pts, top, darkTop };
  };
  return {
    ref:  await analyze(${JSON.stringify(REF)}, 'reference'),
    mine: await analyze('data:image/png;base64,' + ${JSON.stringify(shot.result.data)}, 'mine'),
  };
})()`);
console.log('--- reference ---');
console.log(JSON.stringify(cmp.ref, null, 2));
console.log('--- mine ---');
console.log(JSON.stringify(cmp.mine, null, 2));
if (exceptions.length) console.log('EXCEPTIONS:', exceptions);

/* ---------- 4. 三套主题各截一张 ---------- */
const grab = async (name, script) => {
  if (script) await evaluate(script);
  await sleep(650);
  const s = await send('Page.captureScreenshot', { format: 'png', clip: { ...clip, scale: 1 }, captureBeyondViewport: true });
  writeFileSync(OUT + '\\' + name, Buffer.from(s.result.data, 'base64'));
  console.log('saved', name);
};
await grab('kbd-ref.png');
await grab('kbd-light.png', `document.querySelector('#themeSeg button[data-theme="light"]').click();`);
await grab('kbd-dark-heat.png', `document.querySelector('#themeSeg button[data-theme="dark"]').click(); document.getElementById('heatBtn').click();`);
console.log('saved to', OUT);
ws.close(); process.exit(0);
