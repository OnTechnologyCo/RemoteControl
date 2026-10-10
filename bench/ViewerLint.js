// viewer.js 를 브라우저 없이 실제로 돌려 본다.
//
// 왜 필요한가.
//   `node --check` 는 문법만 본다. 없는 변수를 쓰는 것은 잡지 못한다 - 그건 그 줄이
//   실제로 실행될 때 ReferenceError 로 터진다. 그래서 [파일] 을 누르기 전까지는
//   멀쩡해 보이다가, 누르는 순간 "읽는 중…" 에서 영원히 멈췄다. 화면에 아무 말도
//   안 나와서 원인을 찾기가 어려웠다.
//
//   여기서는 문서(DOM)와 브라우저 기능을 최소한만 흉내 내어 viewer.js 를 통째로
//   불러오고, 단추를 실제로 눌러 본다. 없는 변수를 쓰면 그 자리에서 잡힌다.
//
// 실행:  node bench\ViewerLint.js

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const root = path.join(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'web', 'viewer.html'), 'utf8');
const js = fs.readFileSync(path.join(root, 'web', 'viewer.js'), 'utf8');

let fails = 0;
function ok(what, cond, extra) {
  console.log((cond ? '  OK   ' : '  실패 ') + what + (cond || !extra ? '' : '  → ' + extra));
  if (!cond) fails++;
}

// ---------------------------------------------------------------- 가짜 DOM

// viewer.html 에 실제로 있는 id 만 만들어 준다. 없는 id 를 만지면 null 로 터지므로
// 화면과 스크립트가 어긋난 것도 여기서 잡힌다.
const ids = new Set();
for (const m of html.matchAll(/\bid=["']([^"']+)["']/g)) ids.add(m[1]);

const listeners = new Map();          // id -> { event -> [fn] }

function makeEl(id) {
  const el = {
    id,
    _cls: new Set(),
    style: {},
    dataset: {},
    hidden: false,
    value: '',
    textContent: '',
    innerHTML: '',
    firstChild: { style: {} },
    children: [],
    classList: {
      add: (c) => el._cls.add(c),
      remove: (c) => el._cls.delete(c),
      contains: (c) => el._cls.has(c),
      toggle: (c) => (el._cls.has(c) ? el._cls.delete(c) : el._cls.add(c))
    },
    addEventListener: (ev, fn) => {
      if (!listeners.has(id)) listeners.set(id, new Map());
      const m = listeners.get(id);
      if (!m.has(ev)) m.set(ev, []);
      m.get(ev).push(fn);
    },
    removeEventListener: () => {},
    appendChild: (c) => { el.children.push(c); return c; },
    removeChild: () => {},
    querySelector: () => makeEl('_q'),
    querySelectorAll: () => [],
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 600 }),
    getContext: () => ctx2d,
    focus: () => {},
    select: () => {},
    click: () => {},
    setAttribute: () => {},
    scrollIntoView: () => {},
    clientWidth: 800,
    clientHeight: 600,
    naturalWidth: 32,
    files: [],
    options: [],
    width: 0,
    height: 0
  };
  return el;
}

const ctx2d = {
  fillStyle: '', fillRect() {}, drawImage() {}, clearRect() {},
  getImageData: () => ({ data: [] }), putImageData() {}
};

const elems = new Map();
for (const id of ids) elems.set(id, makeEl(id));

const document_ = {
  body: makeEl('body'),
  documentElement: makeEl('html'),
  hidden: false,
  getElementById: (id) => elems.get(id) || null,
  querySelector: (s) => {
    const m = /^#([\w-]+)/.exec(s);
    if (m) return elems.get(m[1]) || makeEl(m[1]);
    return makeEl('_sel');
  },
  querySelectorAll: () => [],
  createElement: (tag) => makeEl('_new_' + tag),
  addEventListener: (ev, fn) => {
    if (!listeners.has('document')) listeners.set('document', new Map());
    const m = listeners.get('document');
    if (!m.has(ev)) m.set(ev, []);
    m.get(ev).push(fn);
  }
};

// ---------------------------------------------------------------- 가짜 브라우저

const sent = [];                      // WebSocket 으로 나간 것
const opened = [];                    // XHR 로 연 주소
const navigated = [];                 // location.href 로 옮겨간 곳

class FakeWS {
  constructor(url) { this.url = url; this.readyState = 1; FakeWS.last = this; }
  send(s) { sent.push(s); }
  close() { this.readyState = 3; }
}

class FakeXHR {
  constructor() { this.readyState = 0; this.status = 200; this.responseText = ''; this.upload = {}; }
  open(m, u) { this.method = m; this.url = u; opened.push(u); }
  send() { FakeXHR.last = this; }
}

const win = {
  addEventListener: (ev, fn) => {
    if (!listeners.has('window')) listeners.set('window', new Map());
    const m = listeners.get('window');
    if (!m.has(ev)) m.set(ev, []);
    m.get(ev).push(fn);
  },
  removeEventListener: () => {},
  matchMedia: () => ({ matches: false, addListener() {}, addEventListener() {} }),
  requestAnimationFrame: (fn) => { setTimeout(fn, 0); return 1; },
  devicePixelRatio: 1,
  innerWidth: 1280,
  innerHeight: 800
};

const sandbox = {
  window: win,
  document: document_,
  navigator: { clipboard: null, serviceWorker: undefined, userAgent: 'node' },
  location: {
    protocol: 'http:', host: '127.0.0.1:5900', search: '', pathname: '/',
    set href(v) { navigated.push(v); },
    get href() { return 'http://127.0.0.1:5900/'; }
  },
  history: { replaceState() {} },
  localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
  performance: { now: () => Date.now() },
  WebSocket: FakeWS,
  XMLHttpRequest: FakeXHR,
  Image: function () { return makeEl('_img'); },
  Blob: function () {},
  URL: { createObjectURL: () => 'blob:x', revokeObjectURL() {} },
  JSON, Math, Date, parseInt, parseFloat, isNaN, encodeURIComponent, decodeURIComponent,
  setTimeout, clearTimeout, setInterval: () => 0, clearInterval,
  console, prompt: () => '새폴더', alert: () => {}, confirm: () => true
};
sandbox.globalThis = sandbox;
sandbox.self = sandbox;
Object.assign(sandbox, { matchMedia: win.matchMedia, requestAnimationFrame: win.requestAnimationFrame });

// ---------------------------------------------------------------- 돌려 보기

console.log('[1] viewer.js 를 통째로 불러온다');

let loaded = false;
try {
  vm.createContext(sandbox);
  new vm.Script(js, { filename: 'viewer.js' }).runInContext(sandbox);
  loaded = true;
  ok('불러오는 중 예외 없음', true);
} catch (e) {
  ok('불러오는 중 예외 없음', false, e.message + '  (' + (e.stack || '').split('\n')[1] + ')');
}

if (!loaded) { console.log('\n' + fails + '건 실패'); process.exit(1); }

console.log();
console.log('[2] viewer.html 에 없는 id 를 만지지 않는다');

// getElementById 가 null 을 준 적이 있으면 화면과 스크립트가 어긋난 것이다.
const missing = [];
for (const m of js.matchAll(/getElementById\(\s*['"]([^'"]+)['"]\s*\)/g))
  if (!ids.has(m[1])) missing.push(m[1]);

ok('없는 id 참조 ' + missing.length + '건', missing.length === 0, missing.join(', '));

console.log();
console.log('[3] 단추를 실제로 눌러 본다');

function click(id) {
  const m = listeners.get(id);
  if (!m || !m.has('click')) return '단추에 동작이 안 붙어 있다';
  try { for (const fn of m.get('click')) fn({ preventDefault() {}, stopPropagation() {}, target: elems.get(id) }); }
  catch (e) { return e.message; }
  return '';
}

// 화면이 붙어 있는 척해야 클립보드 단추가 동작한다 (alive 검사가 있다).
sandbox.alive = true;
sandbox.ws = new FakeWS('ws://x/ws');

for (const [id, label] of [
  ['bFiles', '파일'], ['bClipUp', '클립 ↑'], ['bClipDn', '클립 ↓'],
  ['fsHome', '내 컴퓨터'], ['fsUp', '위로'], ['fsClose', '닫기'],
  ['fsNew', '새 폴더'], ['fsSend', '올리기'], ['bPc', 'PC'], ['bOut', '종료']
]) {
  const err = click(id);
  ok('[' + label + '] 눌러도 안 터진다', err === '', err);
}

console.log();
console.log('[4] 실제로 요청이 나갔는가');

const fsCall = opened.filter(u => u.indexOf('/fs/list') === 0);
ok('[파일] 이 /fs/list 를 불렀다  (' + (fsCall[0] || '없음') + ')', fsCall.length > 0);

ok('처음에는 드라이브 목록을 부른다 (p 가 비어 있다)',
   opened.some(u => /^\/fs\/list\?p=(&|$)/.test(u)));

ok('[클립 ↓] 가 clipget 을 보냈다',
   sent.some(s => JSON.parse(s).t === 'clipget'));

console.log();
console.log('[5] 서버가 보내는 메시지를 받아 본다');

function recv(obj) {
  try { FakeWS.last.onmessage({ data: JSON.stringify(obj) }); return ''; }
  catch (e) { return e.message; }
}

// connect() 가 onmessage 를 걸어 두게 한 번 부른다
try { sandbox.connect(); } catch (e) { }

for (const [o, label] of [
  [{ t: 'clip', s: '가져온 글자' }, 'clip (원격 클립보드)'],
  [{ t: 'clipok', v: 1, n: 5 }, 'clipok (넣기 결과)'],
  [{ t: 'fail', s: '붙지 못했습니다' }, 'fail (브릿지 실패)'],
  [{ t: 'note', s: '알림' }, 'note'],
  [{ t: 'pong', c: 1 }, 'pong'],
  [{ t: 'cur', x: 1, y: 2, v: 1, n: 0 }, 'cur (커서)'],
  [{ t: 'init', w: 1920, h: 1080, cols: 15, rows: 9, screen: 0, screens: 1, scale: 100, way: 'GDI', sizes: [[1920, 1080]], sw: 1920, sh: 1080 }, 'init (화면 정보)']
]) {
  const err = recv(o);
  ok(label + ' 처리해도 안 터진다', err === '', err);
}

console.log();
console.log(fails === 0 ? '전부 통과' : fails + '건 실패');
process.exit(fails === 0 ? 0 : 1);
