'use strict';
const fs = require('fs'), path = require('path'), vm = require('vm'), assert = require('assert');
const lint = fs.readFileSync(path.join(__dirname, 'ViewerLint.js'), 'utf8');
const prefix = lint.slice(0, lint.indexOf('// ---------------------------------------------------------------- 돌려 보기'));
let passed = 0;
function check(name, test) { test(); console.log('PASS ' + (++passed) + ' ' + name); }
function fixture(reply) {
  const h = new Function('require', '__dirname', prefix + '\nreturn {sandbox, elems, listeners, js};')(require, __dirname);
  const requests = [];
  class Xhr {
    constructor() { this.upload = {}; }
    open(method, url) { this.method = method; this.url = new URL(url, 'http://test'); }
    send(body) {
      this.body = body;
      if (!['/fs/put','/fs/mkdir'].includes(this.url.pathname)) return;
      requests.push(this);
      if (reply === false) return;
      setImmediate(() => { if (!this.aborted) this.respond(reply ? reply(this) : {ok:true,folders:1}); });
    }
    respond(data) { this.status = 200; this.responseText = JSON.stringify(data); this.onload(); }
    abort() { this.aborted = true; if (this.onabort) this.onabort(); }
  }
  h.sandbox.XMLHttpRequest = Xhr;
  h.sandbox.FormData = class { append(name, file) { this.file = file; } };
  vm.createContext(h.sandbox); new vm.Script(h.js).runInContext(h.sandbox);
  h.sandbox.fsCur = 'C:\\destination';
  h.fire = (id, event, data) => { for (const fn of h.listeners.get(id).get(event)) fn({preventDefault(){},...data}); };
  h.requests = requests; return h;
}
async function done(h) { for (let n=0;n<2000;n++) { if (!h.sandbox.uploadJob) return; await new Promise(setImmediate); } throw Error('queue did not finish'); }
function file(name, relative) { return {name,size:3,webkitRelativePath:relative || ''}; }
function entry(name, children) {
  return children ? {name,isDirectory:true,createReader() { let n=0;return {readEntries(ok) { ok(children.slice(n,n+=100)); }}; }}
    : {name,isFile:true,file(ok) { ok(file(name)); }};
}
async function run() {
  let h = fixture(); h.sandbox.uploadFiles([file('a.txt'),file('b.txt')],false); await done(h);
  check('multiple files use separate multipart requests', () => assert.deepEqual(h.requests.map(x=>x.body.file.name),['a.txt','b.txt']));
  h = fixture(); h.sandbox.uploadFiles([file('x.txt','자료/sub/x.txt'),file('z.txt','다른/z.txt')],true); await done(h);
  check('folder picker preserves both roots and child paths', () => assert.deepEqual(h.requests.filter(x=>x.url.pathname==='/fs/put').map(x=>x.url.searchParams.get('r')),['자료/sub','다른']));
  h = fixture();
  const entries = [entry('large',Array.from({length:130},(_,n)=>entry(n+'.txt'))),entry('empty',[])];
  h.fire('files','drop',{dataTransfer:{items:entries.map(x=>({kind:'file',webkitGetAsEntry:()=>x})),files:[]}}); await done(h);
  check('dragged folders read every directory batch beyond 100 entries', () => assert.equal(h.requests.filter(x=>x.url.pathname==='/fs/put').length,130));
  check('empty dragged folders are included', () => assert(h.requests.some(x=>x.url.searchParams.get('r')==='empty')));
  h = fixture(false); h.sandbox.curPc=7; h.sandbox.uploadFiles([file('a'),file('b')],false); h.sandbox.curPc=9;
  h.requests[0].respond({ok:true}); h.requests[1].respond({ok:true});
  check('every request retains its original PC even during a change', () => assert(h.requests.every(x=>x.url.searchParams.get('pc')==='7')));
  h = fixture(false); h.sandbox.uploadFiles([file('a'),file('b')],false); const first=h.requests[0]; h.sandbox.switchTo(3); first.respond({ok:true});
  check('switching PC aborts the transfer and ignores late completion', () => {assert(first.aborted);assert.equal(h.requests.length,1);assert.equal(h.sandbox.fsCur,'');assert.equal(h.sandbox.uploadJob,null);});
  h = fixture(()=>({ok:true})); h.sandbox.uploadFiles([file('a','root/a')],true); await done(h);
  check('older servers cannot silently flatten folder uploads', () => {assert.equal(h.requests.length,1);assert(h.elems.get('fsUploadErrorList').textContent.includes('업데이트'));});
  h = fixture(x=>x.url.pathname==='/fs/mkdir'?{ok:false,err:'denied'}:{ok:true});
  h.sandbox.uploadFiles([file('a','root/a'),file('b','root/sub/b'),file('c','c')],true); await done(h);
  check('failed folders skip descendants while unrelated files continue', () => assert.deepEqual(h.requests.filter(x=>x.url.pathname==='/fs/put').map(x=>x.body.file.name),['c']));
  h = fixture(false); h.sandbox.uploadFiles([file('a')],false); h.sandbox.uploadFiles([file('b')],false);
  check('a second selection cannot start a competing queue', () => assert.equal(h.requests.length,1)); h.sandbox.cancelUpload();
  h = fixture(); h.sandbox.uploadFiles([file('x','../escape/x')],true); await done(h);
  check('invalid relative paths send no requests', () => assert.equal(h.requests.length,0));
  console.log('Viewer uploads: '+passed+' passed');
}
run().catch(e=>{console.error(e);process.exitCode=1;});
