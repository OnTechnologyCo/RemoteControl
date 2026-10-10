'use strict';
// 기존 ViewerLint의 DOM fixture를 재사용하고 이벤트/디코딩 완료 순서를 직접 제어한다.
const fs = require('fs'), path = require('path'), vm = require('vm'), assert = require('assert');
const lint = fs.readFileSync(path.join(__dirname, 'ViewerLint.js'), 'utf8');
const prefix = lint.slice(0, lint.indexOf('// ---------------------------------------------------------------- 돌려 보기'));
function fixture(touch) {
  const h = new Function('require', '__dirname', prefix + '\nreturn {sandbox, sent, elems, listeners, js, FakeWS, ctx2d};')(require, __dirname);
  const frames = [], timers = new Map(), decodes = [], painted = [];
  let timer = 0;
  h.sandbox.setTimeout = fn => { timers.set(++timer, fn); return timer; };
  h.sandbox.clearTimeout = id => timers.delete(id);
  h.sandbox.requestAnimationFrame = fn => { frames.push(fn); };
  h.sandbox.matchMedia = () => ({ matches: touch });
  h.sandbox.createImageBitmap = () => new Promise(resolve => decodes.push(resolve));
  h.ctx2d.drawImage = image => painted.push(image.name);
  vm.createContext(h.sandbox); new vm.Script(h.js).runInContext(h.sandbox);
  function fire(id, event, extra = {}) {
    for (const fn of (h.listeners.get(id)?.get(event) || []))
      fn({ preventDefault() {}, stopPropagation() {}, clientX: 40, clientY: 40, button: 0, ...extra });
  }
  function init() {
    h.FakeWS.last.onmessage({ data: JSON.stringify({ t: 'init', w: 256, h: 256, cols: 2, rows: 2, scale: 100, flow: 1, view: 1, sizes: [] }) });
  }
  function connect() { h.sandbox.connect(); h.FakeWS.last.onopen(); init(); }
  connect(); h.sent.length = 0;
  return Object.assign(h, { frames, timers, decodes, painted, fire, connect, init });
}
let checks = 0;
function check(name, test) { test(); checks++; console.log('PASS ' + name); }
function messages(h, type) { return h.sent.map(JSON.parse).filter(m => !type || m.t === type); }
async function run() {
  const h = fixture(false);
  check('outside-window right mouseup never sends an unmatched remote click', () => {
    h.fire('window', 'mouseup', { button: 2 }); assert.equal(messages(h, 'btn').length, 0);
  });
  check('desktop mouse movement coalesces to one latest position per paint', () => {
    for (let i = 0; i < 100; i++) h.fire('screen', 'mousemove', { clientX: i });
    assert.equal(messages(h, 'move').length, 0);
    h.frames.shift()(); assert.equal(messages(h, 'move').length, 1);
  });
  check('pending movement is sent before the click edge', () => {
    h.sent.length = 0;
    h.fire('screen', 'mousemove', { clientX: 42 }); h.fire('screen', 'mousedown', { button: 2 });
    assert.deepEqual(messages(h).map(m => m.t), ['move', 'btn']);
    h.fire('window', 'mouseup', { button: 2 });
    assert.deepEqual(messages(h, 'btn').map(m => m.d), [true, false]);
  });
  check('disconnect resets held state and cannot replay a release into the new connection', () => {
    h.fire('screen', 'mousedown'); h.FakeWS.last.close(); h.FakeWS.last.onclose();
    assert.equal(Object.keys(h.sandbox.heldButtons).length, 0);
    h.connect(); h.sent.length = 0; h.fire('window', 'mouseup'); assert.equal(messages(h, 'btn').length, 0);
  });
  check('blur releases only this session without manufacturing right-up', () => {
    h.fire('window', 'keydown', { key: 'Control', keyCode: 17 }); h.sent.length = 0;
    h.fire('window', 'blur'); assert.deepEqual(messages(h).map(m => m.t), ['release']);
  });
  check('input is disabled until new screen geometry arrives', () => {
    h.sandbox.connect(); h.FakeWS.last.onopen(); h.sent.length = 0;
    h.fire('screen', 'mousedown', { button: 2 }); assert.equal(messages(h, 'btn').length, 0); h.init();
  });
  const first = h.sandbox.drawTile(0, new Uint8Array([1])), second = h.sandbox.drawTile(0, new Uint8Array([2]));
  h.decodes[1]({ name: 'new', close() {} }); await second;
  h.decodes[0]({ name: 'old', close() {} }); await first;
  check('late older JPEG cannot overwrite newer tile', () => assert.deepEqual(h.painted, ['new']));
  const stale = h.sandbox.drawTile(1, new Uint8Array([3])); h.init();
  h.decodes[2]({ name: 'old-view', close() {} }); await stale;
  check('previous geometry JPEG cannot paint after init', () => assert.deepEqual(h.painted, ['new']));
  h.sent.length = 0;
  const frame = new ArrayBuffer(7), data = new DataView(frame); data.setUint16(0, 0, true); data.setUint32(2, 1, true);
  h.sandbox.onMessage({ data: frame }); h.sandbox.onMessage({ data: JSON.stringify({ t: 'frame', n: 123 }) });
  check('ACK waits for decoding instead of socket receipt', () => assert.equal(messages(h, 'ack').length, 0));
  h.decodes[3]({ name: 'ack-frame', close() {} }); await new Promise(setImmediate);
  check('decoded batch produces matching ACK', () => assert.equal(messages(h, 'ack')[0].n, 123));
  h.sent.length = 0;
  h.sandbox.onMessage({ data: frame }); h.sandbox.onMessage({ data: JSON.stringify({ t: 'frame', n: 124 }) });
  h.connect(); h.sent.length = 0; h.decodes[4]({ name: 'old-pc', close() {} }); await new Promise(setImmediate);
  check('stale completion cannot ACK or paint into a replacement connection', () => {
    assert.equal(messages(h, 'ack').length, 0); assert(!h.painted.includes('old-pc'));
  });
  const mobile = fixture(true);
  mobile.sandbox.lastTapAt = Date.now();
  mobile.fire('stage', 'touchstart', { touches: [{ clientX: 1, clientY: 1 }] });
  const before = mobile.sent.length;
  mobile.FakeWS.last.close(); mobile.FakeWS.last.onclose(); mobile.connect(); mobile.sent.length = 0;
  for (const fn of mobile.timers.values()) fn();
  check('touch drag timer from disconnected session cannot click after reconnect', () => assert.equal(messages(mobile, 'btn').length, 0));
  const stats = fixture(false);
  const a = stats.sandbox.drawTile(0, new Uint8Array([1])), b = stats.sandbox.drawTile(1, new Uint8Array([2]));
  stats.decodes[0]({ name: 'a', close() {} }); stats.decodes[1]({ name: 'b', close() {} }); await Promise.all([a, b]);
  check('multiple tiles do not count as multiple rendered frames', () => {
    assert.equal(stats.sandbox.rxFrames, 0); assert.equal(stats.frames.length, 1);
    stats.frames.shift()(); assert.equal(stats.sandbox.rxFrames, 1);
  });
  const old = stats.sandbox.drawTile(0, new Uint8Array([3]));
  stats.decodes[2]({ name: 'old', close() {} }); await old; stats.init(); stats.frames.shift()();
  check('old viewport paint callback cannot increment new viewport stats', () => assert.equal(stats.sandbox.rxFrames, 1));
  const grid = fixture(false), positions = [];
  grid.ctx2d.drawImage = (image, x, y) => positions.push({ name: image.name, x, y });
  grid.sandbox.firstInit = true;
  grid.sandbox.onMessage({ data: JSON.stringify({ t:'init',w:3840,h:2160,cols:30,rows:17,scale:100,flow:1,view:1,tile:128,tilemax:1024,sizes:[] }) });
  check('new viewer explicitly negotiates large-tile support', () => assert.equal(messages(grid, 'stream').pop().tile, 1024));
  grid.sandbox.onMessage({ data: JSON.stringify({ t:'probe',n:7 }) });
  check('viewer echoes RTT probes independently of JPEG completion', () => assert.equal(messages(grid, 'probeack').pop().n, 7));
  const obsolete = grid.sandbox.drawTile(1, new Uint8Array([1]));
  grid.sandbox.setCursorImage(new Uint8Array([1]));
  grid.sandbox.onMessage({ data: JSON.stringify({ t:'tiles',tile:1024,cols:4,rows:3,view:2 }) });
  grid.sandbox.curImg.onload();
  grid.decodes[0]({ name:'obsolete',close(){} }); await obsolete;
  const corner = grid.sandbox.drawTile(11, new Uint8Array([1]));
  grid.decodes[1]({ name:'corner',close(){} }); await corner;
  check('tile-grid switch preserves 4K dimensions and rejects old decodes', () => {
    assert.equal(grid.sandbox.W, 3840); assert.equal(grid.sandbox.H, 2160);
    assert.deepEqual(positions, [{name:'corner',x:3072,y:2048}]); assert.equal(grid.sandbox.viewId, 2);
  });
  check('cursor PNG can finish loading across a tile-grid change', () => assert.equal(grid.sandbox.curPngReady, true));
  grid.sandbox.onMessage({ data: JSON.stringify({ t:'tiles',tile:128,cols:30,rows:17,view:3 }) });
  const small = grid.sandbox.drawTile(509, new Uint8Array([1]));
  grid.decodes[2]({name:'small',close(){}}); await small;
  check('slow-link fallback restores small-tile coordinates', () => assert.deepEqual(positions[1], {name:'small',x:3712,y:2048}));
  console.log('ALL ' + checks + ' VIEWER CONTROL CHECKS PASSED');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
