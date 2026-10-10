'use strict';
const fs=require('fs'),path=require('path'),http=require('http'),crypto=require('crypto');
const {spawn}=require('child_process'),{once}=require('events');
const root=path.resolve(__dirname,'..'), fixture=fs.mkdtempSync(path.join(root,'artifacts/upload-test/run-'));
const destination=path.join(fixture,'received'), source=path.join(fixture,'source');
fs.mkdirSync(destination);fs.mkdirSync(source);
let backend,edge,server,cdp,passed=0;
function check(ok,label) { if(!ok)throw Error(label);console.log('PASS '+(++passed)+' '+label); }
async function poll(test) { for(let n=0;n<150;n++){if(await test())return;await new Promise(r=>setTimeout(r,100));}throw Error('fixture wait timeout'); }
async function run() {
  backend=spawn(path.join(root,'artifacts/upload-test/UploadFixture.exe'),[path.join(root,'dist/RemoteControl.exe'),fixture],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  backend.stderr.on('data',data=>process.stderr.write(data));
  const port=Number((await once(backend.stdout,'data'))[0].toString().trim());
  async function call(route,params,method='POST',body) {
    const response=await fetch('http://127.0.0.1:'+port+route+'?'+new URLSearchParams({p:destination,...params}),{method,body,signal:AbortSignal.timeout(10000)});
    return response.json();
  }
  check((await call('/fs/mkdir',{r:'자료/하위/빈폴더'})).folders===1,'actual HTTP creates nested empty folders');
  for(const bad of ['../escape','a/../../escape','/absolute','C:/outside','a\\b','good/../bad','a//b','CON/x','a./b','a /b']) {
    check(!(await call('/fs/mkdir',{r:bad})).ok,'reject unsafe relative folder '+JSON.stringify(bad));
  }
  check(!(await call('/fs/mkdir',{r:'get-mutation'},'GET')).ok,'folder upload endpoint rejects GET');
  const outside=path.join(fixture,'outside');fs.mkdirSync(outside);
  fs.symlinkSync(outside,path.join(destination,'junction'),'junction');
  check(!(await call('/fs/mkdir',{r:'junction/escape'})).ok&&!fs.existsSync(path.join(outside,'escape')),'existing junction cannot escape destination');
  const payload=crypto.randomBytes(1024*1024),form=new FormData();form.append('f',new Blob([payload]),'내용.bin');
  check((await call('/fs/put',{r:'자료/하위',n:'내용.bin'},'POST',form)).ok&&fs.readFileSync(path.join(destination,'자료/하위/내용.bin')).equals(payload),'multipart file preserves all bytes under nested folder');
  const again=new FormData();again.append('f',new Blob(['second']),'내용.bin');await call('/fs/put',{r:'자료/하위',n:'내용.bin'},'POST',again);
  check(fs.readFileSync(path.join(destination,'자료/하위/내용.bin')).equals(payload)&&fs.readFileSync(path.join(destination,'자료/하위/내용 (2).bin'),'utf8')==='second','same-name upload preserves existing file');
  const partial=http.request({host:'127.0.0.1',port,method:'POST',path:'/fs/put?'+new URLSearchParams({p:destination,n:'incomplete.bin'}),headers:{'Content-Length':1000000,'Content-Type':'multipart/form-data; boundary=partial'}});
  partial.on('error',()=>{});partial.write('--partial\r\nContent-Disposition: form-data; name="f"; filename="incomplete.bin"\r\n\r\n'+'.'.repeat(1024));
  await poll(()=>fs.existsSync(path.join(destination,'incomplete.bin.올리는중')));partial.destroy();
  await poll(()=>!fs.existsSync(path.join(destination,'incomplete.bin.올리는중')));
  check(!fs.existsSync(path.join(destination,'incomplete.bin')),'interrupted HTTP upload removes partial file');
  server=http.createServer((request,response)=>{
    if(request.url.startsWith('/fs/')) {
      const upstream=http.request({host:'127.0.0.1',port,path:request.url,method:request.method,headers:request.headers},reply=>{response.writeHead(reply.statusCode,reply.headers);reply.pipe(response);});
      upstream.on('error',()=>{response.writeHead(502);response.end();});request.pipe(upstream);return;
    }
    if(request.url==='/peers'){response.setHeader('Content-Type','application/json');response.end('{"peers":[]}');return;}
    const asset=request.url==='/viewer.js'?'viewer.js':'viewer.html';response.setHeader('Content-Type',asset.endsWith('.js')?'text/javascript':'text/html');response.end(fs.readFileSync(path.join(root,'web',asset)));
  });
  server.on('upgrade',(_,socket)=>socket.destroy());server.listen(0,'127.0.0.1');await once(server,'listening');
  const profile=path.join(fixture,'edge');
  edge=spawn('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:'ignore'});
  await poll(()=>fs.existsSync(path.join(profile,'DevToolsActivePort')));
  const debugPort=fs.readFileSync(path.join(profile,'DevToolsActivePort'),'utf8').split('\n')[0];
  const pages=await(await fetch('http://127.0.0.1:'+debugPort+'/json/list')).json();
  cdp=new WebSocket(pages.find(x=>x.type==='page').webSocketDebuggerUrl);await once(cdp,'open');
  let seq=0;const pending=new Map();
  cdp.addEventListener('message',event=>{const item=JSON.parse(event.data);if(pending.has(item.id)){const promise=pending.get(item.id);pending.delete(item.id);item.error?promise.reject(Error(item.error.message)):promise.resolve(item.result);}});
  const send=(method,params={})=>new Promise((resolve,reject)=>{pending.set(++seq,{resolve,reject});cdp.send(JSON.stringify({id:seq,method,params}));});
  const evaluate=async expression=>{const result=await send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(result.exceptionDetails)throw Error(result.exceptionDetails.text);return result.result.value;};
  await send('Page.enable');await send('Page.navigate',{url:'http://127.0.0.1:'+server.address().port+'/'});
  await poll(()=>evaluate('typeof fsGo === "function"'));
  await evaluate('fsBox.classList.add("on"); hideMsg(); fsGo('+JSON.stringify(destination)+')');
  await poll(()=>evaluate('fsCur === '+JSON.stringify(destination)));
  const pick=async(selector,paths)=>{const dom=await send('DOM.getDocument');const node=await send('DOM.querySelector',{nodeId:dom.root.nodeId,selector});await send('DOM.setFileInputFiles',{nodeId:node.nodeId,files:paths});};
  const first=path.join(source,'하나.txt'),second=path.join(source,'빈파일.txt');fs.writeFileSync(first,'한글 content');fs.writeFileSync(second,'');
  await pick('#fsPick',[first,second]);await poll(()=>evaluate('uploadJob === null'));
  check(fs.readFileSync(path.join(destination,'하나.txt'),'utf8')==='한글 content'&&fs.statSync(path.join(destination,'빈파일.txt')).size===0,'real Edge multi-file picker uploads text and zero-byte files');
  const folder=path.join(source,'선택폴더');fs.mkdirSync(path.join(folder,'하위'),{recursive:true});fs.writeFileSync(path.join(folder,'하위','중첩.txt'),'nested');
  for(let n=0;n<125;n++)fs.writeFileSync(path.join(folder,n+'.txt'),'file '+n);
  const folderStarted=Date.now();
  await pick('#fsFolderPick',[folder]);
  // 디렉터리 선택은 브라우저가 비동기로 열거한 뒤 change를 발생시킨다.
  await poll(()=>evaluate('document.getElementById("fsFolderPick").files.length === 126'));
  await poll(()=>evaluate('uploadJob === null && document.getElementById("fsUploadStatus").textContent.includes("126/126")'));
  check(fs.readFileSync(path.join(destination,'선택폴더/하위/중첩.txt'),'utf8')==='nested'&&fs.existsSync(path.join(destination,'선택폴더/124.txt')),'real Edge folder picker preserves more than 100 files and nested paths');
  console.log('MEASURE 126 small files, loopback HTTP: '+(Date.now()-folderStarted)+' ms');
  check((await evaluate('document.getElementById("fsUploadStatus").textContent')).includes('126/126'),'completion summary survives file-list refresh');
  await send('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  check(await evaluate('document.querySelector("#files .card").getBoundingClientRect().right <= innerWidth && document.querySelector("#files .hd").scrollWidth <= document.querySelector("#files .hd").clientWidth'),'upload controls fit a 390px mobile viewport');
  const shot=await send('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(fixture,'upload-mobile.png'),Buffer.from(shot.data,'base64'));
  console.log('Upload integration: '+passed+' passed');console.log('Fixture: '+fixture);
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(cdp)cdp.close();if(edge)edge.kill();if(backend)backend.kill();if(server){server.closeAllConnections();server.close();}});
