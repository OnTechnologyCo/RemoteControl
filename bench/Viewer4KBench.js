'use strict';
// 로그인/실제 PC에 연결하지 않고 제품 viewer.js의 JPEG 디코딩과 4K canvas를 실행한다.
const fs = require('fs'), path = require('path'), http = require('http');
const { spawn } = require('child_process'), { once } = require('events');
const root = path.resolve(__dirname, '..'), dest = path.join(root, 'artifacts/frame-transport');
const profile = fs.mkdtempSync(path.join(dest, 'edge-'));
let server, edge, cdp;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function poll(fn) { for (let n = 0; n < 150; n++) { if (await fn()) return; await delay(100); } throw Error('Fixture wait timeout'); }
async function run() {
  const geometry = JSON.parse(fs.readFileSync(path.join(dest, 'frame.json'), 'utf8'));
  const js = fs.readFileSync(path.join(root, 'web/viewer.js'), 'utf8').replace(/start\(\);\s*$/, '');
  const html = fs.readFileSync(path.join(root, 'web/viewer.html'));
  server = http.createServer((req, res) => {
    const body = req.url === '/media.js' || req.url === '/jpeg-worker.js' ? fs.readFileSync(path.join(root, 'web', req.url.slice(1))) : req.url === '/viewer.js' ? js : req.url === '/frame.bin' ? fs.readFileSync(path.join(dest, 'frame.bin')) : req.url === '/' ? html : '';
    res.writeHead(body ? 200 : 404, { 'Content-Type': req.url.endsWith('.js') ? 'text/javascript; charset=utf-8' : req.url === '/frame.bin' ? 'application/octet-stream' : 'text/html; charset=utf-8' }); res.end(body);
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  edge = spawn(process.env.RC_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', ['--headless=new', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  await poll(() => fs.existsSync(path.join(profile, 'DevToolsActivePort')));
  const port = fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
  cdp = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl); await once(cdp, 'open');
  let id = 0; const pending = new Map();
  cdp.addEventListener('message', event => {
    const result = JSON.parse(event.data), waiter = pending.get(result.id);
    if (waiter) { pending.delete(result.id); clearTimeout(waiter.timer); result.error ? waiter.reject(Error(result.error.message)) : waiter.resolve(result.result); }
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const key = ++id; const timer = setTimeout(() => { pending.delete(key); reject(Error('CDP timeout ' + method)); }, 30000);
    pending.set(key, { resolve, reject, timer }); cdp.send(JSON.stringify({ id: key, method, params }));
  });
  const evaluate = async expression => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw Error(JSON.stringify(result.exceptionDetails)); return result.result.value;
  };
  await send('Page.navigate', { url: 'http://127.0.0.1:' + server.address().port + '/' });
  await poll(() => evaluate('typeof onMessage === "function"'));
  const result = await evaluate(`(async function () {
    var frame = await (await fetch('/frame.bin')).arrayBuffer(), done, acks = 0;
    firstInit = false; supportsFlow = true; alive = true;
    ws = { readyState: 1, send: function (s) { if (JSON.parse(s).t === 'ack') { acks++; done(); } } };
    onMessage({data: JSON.stringify({t:'init',w:3840,h:2160,cols:${geometry.cols},rows:${geometry.rows},tile:${geometry.tile},scale:100,flow:1,view:1,sizes:[]})});
    var samples = [], paintMs = 0, originalDraw = cx.drawImage;
    cx.drawImage = function () { var t = performance.now(); originalDraw.apply(cx, arguments); paintMs += performance.now()-t; };
    for (var n = 0; n < 32; n++) {
      var start = performance.now();
      await new Promise(function (resolve) {
        done = resolve; onMessage({data:frame}); onMessage({data:JSON.stringify({t:'frame',n:n+1})});
      });
      if (n >= 2) samples.push(performance.now() - start);
      await new Promise(requestAnimationFrame);
    }
    samples.sort(function (a,b) {return a-b;});
    return { meanMs:samples.reduce(function(a,b){return a+b;},0)/samples.length, p95Ms:samples[28], acks:acks, paintMs:paintMs/32,
      width:cv.width,height:cv.height,pixel:Array.from(cx.getImageData(127,127,1,1).data) };
  })()`);
  if (result.acks !== 32 || result.width !== 3840 || result.height !== 2160 || result.pixel[3] !== 255) throw Error('Invalid 4K rendering result');
  console.log('Headless Edge 4K JPEG decode + canvas (tile ' + geometry.tile + '): mean=' + result.meanMs.toFixed(1) + 'ms p95=' + result.p95Ms.toFixed(1) + 'ms; drawImage=' + result.paintMs.toFixed(1) + 'ms; 32 complete ACKs, opaque 3840x2160 canvas');
  await send('Browser.close');
}
run().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (cdp) cdp.close(); if (server) server.close();
  if (edge && edge.exitCode === null) { await delay(1000); if (edge.exitCode === null) edge.kill(); }
});
