'use strict';
const assert = require('assert'), fs = require('fs'), vm = require('vm');
async function run() {
  const work = [], paints = [];
  const context = { Blob, Promise, performance, createImageBitmap: () => new Promise(resolve => work.push(resolve)) };
  vm.createContext(context); vm.runInContext(fs.readFileSync('web/media.js', 'utf8'), context);
  const decoder = new context.JpegDecoder(); let newest = 0;
  const first = Array.from({length:4}, (_,i) => decoder.decode(new Uint8Array([i]), () => true, () => paints.push(i)));
  assert.equal(work.length,4);
  let old = decoder.decode(new Uint8Array([5]), () => newest === 0, () => paints.push('old'));
  newest++;
  let latest = decoder.decode(new Uint8Array([6]), () => true, () => paints.push('new'));
  assert.equal(work.length,4,'decoder concurrency bounded');
  work[0]({close(){}}); await first[0]; await old;
  assert.equal(work.length,5,'superseded queued tile never decoded');
  for(let i=1;i<work.length;i++) work[i]({close(){}});
  await Promise.all([...first,latest]); assert(!paints.includes('old')); assert(paints.includes('new'));
  console.log('PASS bounded JPEG decode and obsolete queued tile cancellation');
  const sent=[], timers=new Map(), modes=[]; let number=0, current=100, callback;
  const video={style:{}, pause(){}, play(){return Promise.resolve()}, requestVideoFrameCallback(fn){callback=fn}};
  class Peer {
    constructor(){ Peer.last=this; this.connectionState='new'; this.candidates=[]; }
    addTransceiver(){return {setCodecPreferences(){}}}
    createOffer(){return Promise.resolve({type:'offer',sdp:'test-offer'})}
    setLocalDescription(){return Promise.resolve()}
    setRemoteDescription(d){this.remoteDescription=d;return Promise.resolve()}
    addIceCandidate(c){this.candidates.push(c);return Promise.resolve()}
    close(){this.closed=true}
    getStats(){return Promise.resolve(new Map())}
  }
  Object.assign(context,{RTCPeerConnection:Peer,document:{hidden:false,createElement:()=>video},
    performance:{now:()=>current},setInterval:fn=>{timers.set(++number,fn);return number},clearInterval:id=>timers.delete(id)});
  const transport = new context.RemoteVideo({world:{insertBefore(){}}, cursor:{}, send:m=>sent.push(m), view:()=>9, mode:m=>modes.push(m),stats(){}});
  transport.start({rtc:1,w:1920,h:1080,ice:[]}); await Promise.resolve(); await Promise.resolve();
  const id=sent.find(m=>m.op==='offer').id;
  assert(!transport.active()); assert(!sent.some(m=>m.op==='ready'));
  transport.handle({id,op:'candidate',data:JSON.stringify({candidate:'candidate:test'})});
  assert.equal(Peer.last.candidates.length,0);
  transport.handle({id,op:'answer',data:'test-answer'}); await Promise.resolve(); await Promise.resolve();
  assert.equal(Peer.last.candidates.length,1,'early ICE candidate is held until answer');
  Peer.last.ontrack({streams:[{}],receiver:{}}); callback();
  assert(transport.active()); assert(sent.some(m=>m.op==='ready')); assert.equal(video.hidden,false);
  current+=4000; [...timers.values()][0]();
  assert(!transport.active()); assert(Peer.last.closed); assert.equal(video.hidden,true); assert(sent.some(m=>m.op==='stop'));
  const stale=callback; transport.start({rtc:1,w:1280,h:720,ice:[]}); stale(); assert(!transport.active());
  const peer=Peer.last; transport.handle({id,op:'stop'}); assert(!peer.closed,'old PC signal cannot close replacement peer');
  transport.stop(false); assert(peer.closed); assert.equal(timers.size,0);
  console.log('PASS first decoded frame, early ICE, stall fallback, stale callbacks, disconnect cleanup');
}
run().catch(e=>{console.error(e);process.exitCode=1});
