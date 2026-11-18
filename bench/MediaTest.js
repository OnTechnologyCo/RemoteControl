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

  // 1440p: helper 가 알린 LAN 시작 전송률을 첫 ready 에 그대로 쓰고, 전송률을 바꾼 직후 5초는
  // 인코더 재시작 흔적(지터 지연)을 보고 또 깎지 않는다 (예전에는 연쇄로 500kbps 까지 떨어졌다).
  sent.length = 0;
  let stat = { type:'inbound-rtp', kind:'video', framesDecoded:0, packetsReceived:0, packetsLost:0, jitterBufferEmittedCount:0, jitterBufferDelay:0, bytesReceived:0 };
  Peer.prototype.getStats = function () { return Promise.resolve(new Map([['v', Object.assign({}, stat)]])); };
  const rtc = new context.RemoteVideo({world:{insertBefore(){}}, cursor:{}, send:m=>sent.push(m), view:()=>9, mode(){}, stats(){}});
  rtc.start({rtc:1,w:2560,h:1440,ice:[]}); await Promise.resolve(); await Promise.resolve();
  const rid = sent.find(m=>m.op==='offer').id;
  rtc.handle({id:rid,op:'answer',data:'a'}); await Promise.resolve(); await Promise.resolve();
  rtc.handle({id:rid,op:'bitrate',data:'12000000'});
  Peer.last.ontrack({streams:[{}],receiver:{}}); callback();
  assert.equal(sent.find(m=>m.op==='ready').data, '12000000', 'LAN start rate reaches the first ready signal');
  const health = () => sent.filter(m=>m.op==='health').map(m=>+m.data);
  async function second(jitterSeconds) {
    current += 1000; callback();
    stat = Object.assign({}, stat, { framesDecoded:stat.framesDecoded+30, packetsReceived:stat.packetsReceived+1000,
      jitterBufferEmittedCount:stat.jitterBufferEmittedCount+30, jitterBufferDelay:stat.jitterBufferDelay+30*jitterSeconds });
    const fns = [...timers.values()]; fns[fns.length-1]();
    for (let i = 0; i < 6; i++) await Promise.resolve();
  }
  for (let s = 0; s < 60; s++) await second(s % 10 < 5 ? .3 : 0);
  assert(health().every(r=>r===12000000), 'mixed windows including brief jitter keep the LAN start rate');
  for (let s = 1; s <= 60; s++) {
    await second(.3);
    assert.equal(health().pop(), s < 60 ? 12000000 : 7800000, 'downgrade after two congested windows at ' + s);
  }
  for (let s = 1; s <= 120; s++) {
    await second(s < 5 ? .3 : 0);
    assert.equal(health().pop(), s < 120 ? 7800000 : 10530000, 'recovery after four good windows at ' + s);
  }
  for (let s = 0; s < 30; s++) await second(.3);
  rtc.quality(40); await second(.3);
  assert.equal(health().pop(), 4200000, 'manual setting takes effect on the next heartbeat');
  for (let s = 0; s < 70; s++) await second(.3);
  assert.equal(health().pop(), 4200000, 'manual rate stays fixed');
  rtc.quality(0);
  for (let s = 0; s < 59; s++) await second(.3);
  assert.equal(health().pop(), 4200000, 'return to auto discards previous congestion history');
  await second(.3);
  assert.equal(health().pop(), 2730000, 'fresh sustained congestion after returning to auto');
  rtc.stop(false);
  console.log('PASS LAN start bitrate, 30-second auto-quality windows and manual override');
}
run().catch(e=>{console.error(e);process.exitCode=1});
