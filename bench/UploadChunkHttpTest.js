// End-to-end 130 MiB HTTP upload through the real product Server.DoFs handler.
// Runs in an isolated fixture; no operational service, user file or credentials.
// Simulates a 100 MB per-request proxy and a lost chunk acknowledgement.
'use strict';
const fs = require('fs'), path = require('path'), http = require('http'), crypto = require('crypto');
const {spawn} = require('child_process'), {once} = require('events');

const root = path.resolve(__dirname, '..'), dir = fs.mkdtempSync(path.join(root, 'artifacts/upload-test/chunk-run-'));
const destination = path.join(dir, 'received');
fs.mkdirSync(destination);
let server, backend, passed = 0;
function check(v, message) { if (!v) throw Error(message); console.log('PASS ' + (++passed) + ' ' + message); }
function url(port, op, args) { return 'http://127.0.0.1:' + port + '/fs/upload/' + op + '?' + new URLSearchParams(args); }
async function main() {
  backend = spawn(path.join(root, 'artifacts/upload-test/UploadFixture.exe'),
    [path.join(root, 'dist/RemoteControl.exe'), dir], {windowsHide:true, stdio:['ignore','pipe','pipe']});
  backend.stderr.on('data', b => process.stderr.write(b));
  const port = Number((await once(backend.stdout, 'data'))[0].toString().trim());
  if (!Number.isInteger(port) || port < 1) throw Error('Fixture port not ready');
  const limit = 100 * 1024 * 1024;
  let maxRequest = 0, lostReply = false, dropOnce = true;
  server = http.createServer((req, resp) => {
    let length = Number(req.headers['content-length'] || 0);
    maxRequest = Math.max(maxRequest, length);
    if (length > limit) { resp.writeHead(413); resp.end('simulated proxy limit'); req.resume(); return; }
    const upstream = http.request({
      hostname:'127.0.0.1', port, method:req.method, path:req.url,
      headers:req.headers
    }, response => {
      if (dropOnce && req.url.startsWith('/fs/upload/chunk?') && response.statusCode === 200) {
        dropOnce = false; lostReply = true;
        response.resume();
        response.on('end', () => resp.destroy());
        return;
      }
      resp.writeHead(response.statusCode, response.headers);
      response.pipe(resp);
    });
    upstream.on('error', err => { if (!resp.headersSent) { resp.writeHead(502); resp.end(err.message); } });
    req.pipe(upstream);
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const proxy = server.address().port;
  async function api(op,args,method='POST',body,headers={}) {
    const response = await fetch(url(proxy,op,args), {
      method, body, headers:{'X-RC-Upload':'1', ...headers},
      signal:AbortSignal.timeout(120000)
    });
    if (response.status !== 200) return {ok:false, status:response.status};
    return response.json();
  }
  const unauthorized = await fetch(url(proxy,'start',{p:destination,n:'reject.bin',size:0}),{method:'POST'});
  check(unauthorized.status === 403, 'Custom upload header required');
  const badPath = await api('start',{p:destination,r:'../escape',n:'bad.bin',size:0});
  check(!badPath.ok && badPath.code === 'path' && !fs.existsSync(path.join(dir,'escape')), 'Reject parent-path traversal');
  const tooLarge = await api('start',{p:destination,n:'too-large.bin',size:16*1024**3+1});
  check(!tooLarge.ok && tooLarge.code === 'size', 'Reject beyond 16 GiB limit without creating file');
  const original = path.join(destination,'large.bin');
  fs.writeFileSync(original, 'existing file preserved');
  const size = 130*1024*1024, part = 2*1024*1024;
  const transfer = await api('start',{p:destination,n:'large.bin',size});
  check(transfer.ok && transfer.offset===0 && transfer.size===size, 'Start 130 MiB upload');
  let id=transfer.id;
  const chunk = crypto.randomBytes(part);
  const correctHash = crypto.createHash('sha256').update(chunk).digest('hex');
  const totalHash = crypto.createHash('sha256');
  let offset=0, verifiedResume=false;
  for (let i=0;i<size/part;i++) {
    // Deliberately use the same 2 MiB data so the expected full SHA can stream.
    const form=new FormData(); form.append('f',new Blob([chunk]),'segment');
    const args={id,offset,sha256:correctHash};
    let done;
    try { done = await api('chunk',args,'POST',form); }
    catch (e) {
      // Backend already appended the chunk; the response was lost in the proxy.
      done = await api('status',{id},'GET');
      check(done.ok && done.offset===part && lostReply, 'Recover from lost first chunk acknowledgement');
    }
    if (i === 1) {
      const repeated=new FormData(); repeated.append('f',new Blob([chunk]),'segment');
      const replay = await api('chunk',{id,offset:0,sha256:correctHash},'POST',repeated);
      // Reverse proxies may turn an early 200+close response into 502 when
      // the client is still sending the rejected duplicate body.
      check(!replay.ok && (replay.code==='offset' || replay.status===502), 'Duplicate earlier chunk is rejected without append');
      const bad=new FormData(); bad.append('f',new Blob([chunk]),'segment');
      const badHash=await api('chunk',{id,offset:done.offset,sha256:'f'.repeat(64)},'POST',bad);
      check(!badHash.ok && badHash.code==='checksum', 'Corrupted claimed chunk hash is rejected');
      const stable=await api('status',{id},'GET');
      check(stable.offset===done.offset, 'Rejected chunk keeps committed offset stable');
    }
    if (i === 32) {
      const resumed=await api('start',{p:destination,n:'large.bin',size,id});
      check(resumed.ok && resumed.offset===done.offset && resumed.id===id, 'Resume at server-confirmed offset after 66 MiB');
      verifiedResume=true;
    }
    if (!done.ok || done.offset !== offset + part) throw Error('Wrong chunk ack at index '+i+': '+JSON.stringify(done));
    offset = done.offset;
    totalHash.update(chunk);
  }
  const completed=await api('finish',{id});
  check(completed.ok && completed.done && completed.size===size && completed.offset===size,'Finalize all 130 MiB');
  const again=await api('finish',{id});
  check(again.ok && again.done && again.sha256===completed.sha256,'Finalization is idempotent');
  check(completed.file==='large (2).bin' && fs.readFileSync(original,'utf8')==='existing file preserved','Existing target file is never overwritten');
  const final=path.join(destination,completed.file);
  check(fs.statSync(final).size===size,'Final on-disk file has 130 MiB');
  const actual=crypto.createHash('sha256');
  await new Promise((resolve,reject)=>{const s=fs.createReadStream(final);s.on('data',d=>actual.update(d));s.on('end',resolve);s.on('error',reject);});
  check(actual.digest('hex')===totalHash.digest('hex') && completed.sha256===correctBigHash(final),
    'Complete file hash is identical to stream of all sent chunks');
  check(maxRequest < 3*1024*1024 && verifiedResume && lostReply,'Every single proxy request stays under 3 MiB while resuming');
  fs.unlinkSync(final);
  const cancel=await api('start',{p:destination,n:'to-cancel.bin',size:part});
  check(cancel.ok,'Start additional upload to test cancellation');
  check((await api('cancel',{id:cancel.id})).ok,'Cancel removes pending transfer');
  const gone=await api('status',{id:cancel.id},'GET');
  check(!gone.ok && gone.code==='missing','Cancelled transfer cannot be resumed');
  console.log('ALL ' + passed + ' CHUNK HTTP CHECKS PASSED');
}
function correctBigHash(file) {
  // Verification that the product's returned digest matches a second independent file hash.
  const hash=crypto.createHash('sha256'),fd=fs.openSync(file,'r'),buf=Buffer.alloc(1024*1024);
  try {let n;while((n=fs.readSync(fd,buf,0,buf.length,null))>0)hash.update(buf.subarray(0,n));}
  finally {fs.closeSync(fd);}
  return hash.digest('hex');
}
main().catch(e => {console.error(e);process.exitCode=1;}).finally(async()=>{
  if(backend)backend.kill();
  if(server)await new Promise(resolve=>{server.closeAllConnections();server.close(resolve);});
  try {fs.rmSync(dir,{recursive:true,force:true});} catch {}
});
