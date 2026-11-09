'use strict';
// 실제 서버 화면 경로 + 실제 viewer.js 를 headless Chrome 으로 열어 끝에서 끝까지 잰다.
//   node bench/LiveViewBench.js [--scenes scroll,video,typing] [--secs 10] [--width 2560]
//                               [--mbps 0] [--rtt 0] [--label 이름]
// --mbps/--rtt 를 주면 측정 장치 안의 중계(Shaper)가 대역폭·지연을 제한한다(무선 LAN 흉내).
// 실제 PC 화면·입력·운영 서비스는 쓰지 않는다. 결과는 artifacts/live-view 에 남긴다.
const fs = require('fs'), path = require('path');
const { spawn } = require('child_process'), { once } = require('events');
const root = path.resolve(__dirname, '..'), dest = path.join(root, 'artifacts/live-view');
const arg = (name, def) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : def; };
const scenes = arg('scenes', 'scroll,video,typing').split(','), secs = +arg('secs', 10), width = +arg('width', 2560);
const mbps = +arg('mbps', 0), rtt = +arg('rtt', 0), label = arg('label', 'run');
const browserPath = process.env.RC_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const delay = ms => new Promise(r => setTimeout(r, ms));

async function cdpOpen(port) {
  const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
  const ws = new WebSocket(pages.find(p => p.type === 'page').webSocketDebuggerUrl); await once(ws, 'open');
  let id = 0; const pending = new Map();
  ws.addEventListener('message', ev => { const r = JSON.parse(ev.data), w = pending.get(r.id); if (w) { pending.delete(r.id); r.error ? w.reject(Error(r.error.message)) : w.resolve(r.result); } });
  const send = (method, params = {}) => new Promise((resolve, reject) => { const k = ++id; pending.set(k, { resolve, reject }); ws.send(JSON.stringify({ id: k, method, params })); });
  const evaluate = async expr => { const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true }); if (r.exceptionDetails) throw Error(JSON.stringify(r.exceptionDetails)); return r.result.value; };
  return { send, evaluate, close: () => ws.close() };
}

async function runScene(scene) {
  const exe = path.join(dest, 'LiveViewBench.exe');
  const host = spawn(exe, [path.join(root, 'web'), path.join(__dirname, 'LiveViewProbe.js'), scene, String(mbps), String(rtt)], { windowsHide: true });
  let port = 0, cpu = [], measuring = false, errors = '';
  host.stderr.on('data', d => { errors += d; });
  host.stdout.on('data', d => String(d).split(/\r?\n/).forEach(line => {
    const p = /^PORT (\d+)/.exec(line); if (p) port = +p[1];
    const c = /^CPU (\d+) SCENE (\d+)/.exec(line); if (c && measuring) cpu.push({ ms: +c[1], scene: +c[2] });
  }));
  for (let i = 0; i < 100 && !port; i++) await delay(50);
  if (!port) throw Error('bench host did not start: ' + errors);
  const profile = fs.mkdtempSync(path.join(dest, 'chrome-'));
  const chrome = spawn(browserPath, ['--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-timer-throttling',
    '--window-size=1600,1000', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  try {
    for (let i = 0; i < 150 && !fs.existsSync(path.join(profile, 'DevToolsActivePort')); i++) await delay(100);
    const cdp = await cdpOpen(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]);
    await cdp.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/?w=' + width + '&scene=' + scene + '&secs=' + secs });
    const deadline = Date.now() + (secs + 40) * 1000;
    let state = '';
    while (Date.now() < deadline) {
      await delay(250);
      try { state = await cdp.evaluate('window.__live && __live.state'); } catch (e) { continue; }
      measuring = state === 'measuring';
      if (state === 'done') break;
    }
    if (state !== 'done') throw Error(scene + ': no result (state ' + state + ') ' + JSON.stringify(await cdp.evaluate('window.__live && __live.log')) + errors);
    const result = await cdp.evaluate('__live.result');
    const cpuMs = cpu.length ? cpu.reduce((a, c) => a + c.ms, 0) / cpu.length : 0;
    result.serverCores = +(cpuMs / 1000).toFixed(2); result.sceneHz = cpu.length ? +(cpu.reduce((a, c) => a + c.scene, 0) / cpu.length).toFixed(1) : 0;
    try { await cdp.send('Browser.close'); } catch (e) { }
    cdp.close();
    return result;
  } finally {
    host.kill(); chrome.kill();
    await delay(300);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) { }
  }
}

(async () => {
  fs.mkdirSync(dest, { recursive: true });
  const link = mbps || rtt ? (mbps ? mbps + 'Mbps' : '무제한') + ' / RTT ' + rtt + 'ms' : 'loopback';
  const all = [];
  for (const scene of scenes) {
    const r = await runScene(scene); r.link = link; all.push(r);
    const m = r.markers.map(x => `${x.fps}fps p50 ${x.p50}ms p95 ${x.p95}ms`).join(' | ');
    console.log(`${label.padEnd(10)} ${scene.padEnd(7)} ${link.padEnd(20)} ${r.width}x${r.height} ${JSON.stringify(r.modes)} tile ${JSON.stringify(r.tiles)}`);
    console.log(`           → ${m} | ${r.mbps} Mbps, 서버 CPU ${r.serverCores} 코어, 합성 ${r.sceneHz}장/s`);
  }
  fs.writeFileSync(path.join(dest, `result-${label}-${Date.now()}.json`), JSON.stringify(all, null, 2));
})().catch(e => { console.error(e); process.exit(1); });
