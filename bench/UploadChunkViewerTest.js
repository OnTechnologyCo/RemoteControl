// Browser-side chunk upload logic in Node VM with mocked XHR and actual WebCrypto.
// No browser, PC service, network or production files are touched.
'use strict';
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert'),crypto=require('crypto');
const source=fs.readFileSync(path.join(__dirname,'../web/upload-chunks.js'),'utf8');
let requests=[], completed=[], errors=[], responseLost=false;
const sessions=new Map(), store=new Map();
let currentJob=null;
let nextId=1;
const storage={
  getItem:k=>store.has(k)?store.get(k):null,
  setItem:(k,v)=>store.set(k,v),removeItem:k=>store.delete(k)
};
class MockXHR {
  constructor(){this.upload={};this.status=0;this.responseText='';}
  open(method,url){this.method=method;this.url=new URL(url,'https://test.local');}
  setRequestHeader(k,v){if(k==='X-RC-Upload')this.marker=v;}
  abort(){}
  send(body){
    const op=this.url.pathname.split('/').pop(),q=this.url.searchParams,me=this;
    requests.push(op);
    Promise.resolve().then(async()=>{
      let data={ok:false,err:'unknown'},s=sessions.get(q.get('id'));
      if(this.marker!=='1'){me.status=403;data={ok:false,err:'auth'};}
      else if(op==='start'){
        if(s){
          data={ok:true,id:s.id,offset:s.offset,size:s.size,done:s.done};
        }else{
          const id=String(nextId++).padStart(32,'0');s={id,offset:0,size:Number(q.get('size')),done:false};
          sessions.set(id,s);
          data={ok:true,id,offset:0,size:s.size,done:false};
        }
      }else if(op==='status'){
        data=s?{ok:true,id:s.id,offset:s.offset,size:s.size,done:s.done}:{ok:false,code:'missing',err:'expired'};
      }else if(op==='chunk'&&s){
        const blob=body.get('f'),offset=Number(q.get('offset'));
        const bytes=Buffer.from(await blob.arrayBuffer());
        const sha=crypto.createHash('sha256').update(bytes).digest('hex');
        if(s.offset!==offset)data={ok:false,code:'offset',err:'wrong offset'};
        else if(sha!==q.get('sha256'))data={ok:false,code:'checksum',err:'hash mismatch'};
        else{
          s.offset+=bytes.length;
          data={ok:true,id:s.id,offset:s.offset,size:s.size,done:false};
          if(!responseLost){
            responseLost=true;
            return setTimeout(()=>{me.status=0;if(me.onerror)me.onerror();},1);
          }
        }
      }else if(op==='finish'&&s){
        if(s.offset!==s.size)data={ok:false,code:'incomplete',err:'incomplete'};
        else{s.done=true;data={ok:true,id:s.id,offset:s.offset,size:s.size,done:true,file:'file.bin'};}
      }else if(op==='cancel'){sessions.delete(q.get('id'));data={ok:true};}
      me.status=me.status||200;
      me.responseText=JSON.stringify(data);
      setTimeout(()=>{if(me.onload)me.onload();},1);
    }).catch(error=>{me.status=0;setTimeout(()=>{if(me.onerror)me.onerror(error);},1);});
  }
}
const ctx={
  window:{crypto:crypto.webcrypto},crypto:crypto.webcrypto,
  Blob,FormData,Uint8Array,Number,Promise,
  XMLHttpRequest:MockXHR,localStorage:storage,
  setTimeout,clearTimeout,console,
  uploadJob:null,fsProg:{firstChild:{style:{}}},
  q:(o,pc)=>new URLSearchParams({...o,pc}).toString(),
  bytes:n=>String(n),uploadStatus:()=>{},
  uploadError:(job,path,message)=>errors.push(message),
  finishUpload:(job,stop)=>{errors.push('unexpected finish: '+stop);},
  uploadNext:(job)=>{completed.push(job);},
};
vm.createContext(ctx);
vm.runInContext(source,ctx,{filename:'upload-chunks.js'});
async function until(fn,timeout=16000){
 const start=Date.now();
 while(Date.now()-start<timeout){if(fn())return;await new Promise(r=>setTimeout(r,30));}
 throw Error('mock upload did not complete: '+JSON.stringify(errors));
}
async function upload(file,dir='D:\\mock'){
 const job={pc:0,dir,done:0,files:1,total:file.size,sent:0,canceled:false,index:1};
 ctx.uploadJob=job;
 ctx.uploadChunked(job,{file,path:file.name,parent:''});
 await until(()=>job.done===1 || errors.length>0);
 assert.equal(errors.length,0,errors.join('\n'));
 return job;
}
(async()=>{
 const file=Object.assign(new Blob([crypto.randomBytes(12*1024*1024)]),{name:'movie.bin',lastModified:123456});
 const job=await upload(file);
 assert.equal(job.done,1);assert.equal(job.sent,file.size);
 assert(requests.includes('status'),'must query status after lost acknowledgement');
 assert.equal(requests.filter(x=>x==='chunk').length,6,'6 x 2 MiB chunks');
 assert.equal(store.size,0,'must clear resume ID on completion');
 console.log('PASS automatic lost-ack recovery uses status and sends no duplicated chunk');

 // Restore a previously committed 4MiB transfer and ensure only remaining pieces transfer.
 responseLost=true;
 const id=String(nextId++).padStart(32,'0');
 sessions.set(id,{id,offset:4*1024*1024,size:file.size,done:false});
 const resumeJob={pc:0,dir:'D:\\mock',done:0,files:1,total:file.size,sent:0,canceled:false,index:1};
 const resumeTask={file,path:file.name,parent:''};
 store.set(ctx.chunkUploadStoreKey(resumeJob,resumeTask),id);
 requests=[];
 ctx.uploadJob=resumeJob;
 ctx.uploadChunked(resumeJob,resumeTask);
 await until(()=>resumeJob.done===1 || errors.length>0);
 assert.equal(errors.length,0);
 assert.equal(requests.filter(x=>x==='chunk').length,4,'remaining four chunks sent');
 assert.equal(store.size,0,'resume key cleaned after completing');
 console.log('PASS reselecting a 12MiB file resumes from confirmed 4MiB offset');
 console.log('ALL 2 CHUNK VIEWER CHECKS PASSED');
})().catch(e=>{console.error(e);process.exitCode=1;});
