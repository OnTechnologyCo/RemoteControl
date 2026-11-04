// Three isolated Windows processes exercise OAuth Web MCP -> bridge -> child tools.
const fs=require('fs'),path=require('path'),os=require('os'),assert=require('assert/strict'),crypto=require('crypto');
const {spawn}=require('child_process'),{createServer,request}=require('http'),{once}=require('events'),{createInterface}=require('readline'),{pathToFileURL}=require('url');
const root=path.resolve(__dirname,'../artifacts/mcp-bridge-test'),work=fs.mkdtempSync(path.join(root,'case-'));
const fixtures=[],servers=[];let gateway,web,token,peers,checks=0,rpcId=0,audit='';
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const check=(yes,label)=>{assert(yes,label);console.log('PASS '+(++checks)+' '+label);};
function http(url,body,headers={},method='POST') {
 return new Promise((resolve,reject)=>{
  const bytes=body===undefined?undefined:Buffer.from(typeof body==='string'?body:JSON.stringify(body));
  const req=request(url,{method,headers:{...headers,...(bytes?{'Content-Length':bytes.length}:{})}},res=>{
   const chunks=[];res.on('data',b=>chunks.push(b));res.on('error',reject);res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,text:Buffer.concat(chunks).toString()}));
  });req.on('error',reject);req.setTimeout(40000,()=>req.destroy(Error('test timeout')));req.end(bytes);
 });
}
async function fixture(name) {
 const p=spawn(path.join(root,'McpBridgeFixture.exe'),[path.join(work,name)],{stdio:['pipe','pipe','pipe'],windowsHide:true});fixtures.push(p);
 p.stderr.on('data',b=>audit+=b);const lines=[],waiters=[];let exited=false;
 createInterface({input:p.stdout}).on('line',line=>{const waiter=waiters.shift();if(waiter)waiter.resolve(line);else lines.push(line);});
 p.on('exit',code=>{exited=true;for(const w of waiters.splice(0))w.reject(Error('fixture exit '+code+'\n'+audit));});
 const next=()=>lines.length?Promise.resolve(lines.shift()):new Promise((resolve,reject)=>{
  if(exited)return reject(Error('fixture exited'));const timer=setTimeout(()=>reject(Error('fixture timeout\n'+audit)),20000);
  waiters.push({resolve:v=>{clearTimeout(timer);resolve(v);},reject:e=>{clearTimeout(timer);reject(e);}});
 });
 const info=JSON.parse(await next());
 return {...info,process:p,configure:async c=>{const reply=next();p.stdin.write(JSON.stringify(c)+'\n');assert.equal(await reply,'synced');}};
}
async function mock(handler){const s=createServer(handler);servers.push(s);s.listen(0,'127.0.0.1');await once(s,'listening');return s;}
function value(result){assert(!result.isError,JSON.stringify(result));return JSON.parse(result.content[0].text);}
(async()=>{
 const childA=await fixture('child-a'),childB=await fixture('child-b'),parent=await fixture('parent');
 for(const [id,c] of [[1,childA],[2,childB]])await parent.configure({op:'peer',id,port:c.port});
 const backend='http://127.0.0.1:'+parent.mcpPort;
 const login=async()=>{const r=await http(backend+'/login',{user:'fixture',password:'fixture-only'});assert.equal(r.status,200,r.text);return r.headers['set-cookie'][0].split(';')[0];};
 const cookie=await login();
 const {createGateway,CIMD}=await import(pathToFileURL(path.resolve(__dirname,'../web-mcp/gateway.mjs')));
 const config={publicUrl:'https://bridge.example.test',listenPort:59432,lanPort:parent.mcpPort,proxyKey:crypto.randomBytes(32).toString('base64url')};
 fs.mkdirSync(path.join(work,'gateway'));
 gateway=createGateway(config,{directory:path.join(work,'gateway')});web=await mock(gateway.app);
 function grant(cookie){const g={clientId:CIMD,clientName:'ChatGPT',cookie,expires:Number.MAX_SAFE_INTEGER,revoked:false};gateway.provider.grants.add(g);return gateway.provider.issue(g).access_token;}
 token=grant(cookie);
 async function rpc(method,params={},bearer=token){
  const r=await http('http://127.0.0.1:'+web.address().port+'/mcp',{jsonrpc:'2.0',id:++rpcId,method,params},
   {Host:'bridge.example.test','X-RC-Proxy-Key':config.proxyKey,'X-RC-Client-IP':'198.51.100.2','X-Forwarded-Proto':'https',Authorization:'Bearer '+bearer,'Content-Type':'application/json'});
  assert.equal(r.status,200,r.text);const j=JSON.parse(r.text);assert(!j.error,JSON.stringify(j));return j.result;
 }
 const raw=(name,args={},bearer)=>rpc('tools/call',{name,arguments:args},bearer);
 const call=async(name,args={},bearer)=>value(await raw(name,args,bearer));
 const refresh=async()=>{peers=(await call('pc_list_bridge_pcs')).pcs;return peers;};
 const target=id=>{const p=peers.find(p=>p.bridge_id===id);assert(p);return {bridge_id:id,target_revision:p.target_revision};};
 const childRaw=(id,name,args={},extra={},bearer)=>raw('pc_bridge_call',{...target(id),tool_name:name,arguments:args,...(name==='pc_get_status'?{}:{expected_hostname:os.hostname()}),...extra},bearer);
 const child=async(...args)=>value(await childRaw(...args));
 const err=async(p,code,label)=>{const r=await p;check(r.isError&&r.structuredContent?.error_code===code,label+' ['+JSON.stringify(r.structuredContent)+']');};
 const tools=(await rpc('tools/list')).tools;
 check(['pc_list_bridge_pcs','pc_bridge_list_tools','pc_bridge_call'].every(n=>tools.some(t=>t.name===n)),'OAuth gateway exposes bridge tools');
 const init=await rpc('initialize',{protocolVersion:'2025-11-25'});
 check(init.instructions.includes('pc_bridge_call'),'Web initialize explains target-specific bridge workflow');
 await refresh();check(peers.length===2&&peers.every(p=>p.password_saved&&/^[a-f0-9]{64}$/.test(p.target_revision)),'registered targets expose revisions without passwords');
 check(!JSON.stringify(peers).includes('fixture-only'),'bridge list excludes credentials');
 const listed=await call('pc_bridge_list_tools',target(1));
 check(listed.tools.some(t=>t.name==='pc_start_process')&&!listed.tools.some(t=>t.name==='pc_bridge_call'||t.name==='pc_open_file_transfer'),'child advertises native tools without nested relay or parent browser links');
 const a=await child(1,'pc_get_status'),b=await child(2,'pc_get_status'),local=await call('pc_get_status');
 check(a.pid===childA.pid&&b.pid===childB.pid&&local.pid===parent.pid,'two child targets and local target remain distinct');
 const statusRaw=await childRaw(1,'pc_get_status');
 check(statusRaw._meta.bridge_target.bridge_id===1&&statusRaw._meta.bridge_target.hostname===os.hostname(),'result includes explicit bridge target metadata');
 await err(raw('pc_bridge_call',{...target(1),tool_name:'pc_write_file',arguments:{path:path.join(childA.root,'unverified.txt'),content:'bad'}}),'INVALID_ARGUMENT','hostname verification required before file write');
 await err(childRaw(1,'pc_write_file',{path:path.join(childA.root,'wrong-host.txt'),content:'bad'},{expected_hostname:'different-pc'}),'BRIDGE_HOSTNAME_MISMATCH','wrong hostname cannot execute');
 check(!fs.existsSync(path.join(childA.root,'wrong-host.txt')),'hostname mismatch writes nothing');
 await err(childRaw(1,'pc_bridge_call',{}),'INVALID_ARGUMENT','recursive relay blocked');
 await err(raw('pc_bridge_call',{bridge_id:63,target_revision:'x',tool_name:'pc_get_status'}),'BRIDGE_NOT_FOUND','unregistered target blocked');
 const text=path.join(childA.root,'한글.txt');let info=await child(1,'pc_write_file',{path:text,content:'child A\n첫줄'});
 check(fs.readFileSync(text,'utf8').includes('첫줄')&&!fs.existsSync(path.join(parent.root,'한글.txt')),'UTF-8 write occurs on selected child');
 await err(childRaw(1,'pc_write_file',{path:text,content:'bad'}),'IO_ERROR','child overwrite hash guard preserved');
 await child(1,'pc_edit_file',{path:text,old_text:'child A',new_text:'edited',expected_sha256:info.sha256});
 check((await child(1,'pc_read_file',{path:text})).content.includes('edited'),'child read and edit retain native results');
 let screen=await childRaw(1,'pc_capture_screen',{max_width:640});
 check(!screen.isError&&screen.content.some(c=>c.type==='image'&&c.mimeType==='image/jpeg'),'image content survives bridge and OAuth gateway');
 await parent.configure({op:'set',key:'mcp_commands',value:'0'});
 await err(childRaw(1,'pc_start_process',{command:'exit 0'}),'CAPABILITY_DISABLED','parent command policy enforced for child');
 await parent.configure({op:'set',key:'mcp_commands',value:'1'});
 await childA.configure({op:'set',key:'mcp_commands',value:'0'});
 await err(childRaw(1,'pc_start_process',{command:'exit 0'}),'CAPABILITY_DISABLED','child command policy also enforced');
 await childA.configure({op:'set',key:'mcp_commands',value:'1'});
 await parent.configure({op:'set',key:'mcp_screen',value:'0'});
 await err(childRaw(1,'pc_capture_screen',{}),'CAPABILITY_DISABLED','parent screen policy enforced');
 await parent.configure({op:'set',key:'mcp_screen',value:'1'});
 await childA.configure({op:'set',key:'mcp_enabled',value:'0'});
 await err(childRaw(1,'pc_get_status'),'CAPABILITY_DISABLED','child MCP disable rejects relay immediately');
 await childA.configure({op:'set',key:'mcp_enabled',value:'1'});
 const job=await child(1,'pc_start_process',{command:"Write-Output 'bridge-job'; Start-Sleep -Seconds 60",timeout_ms:1000});
 check(job.running&&(await child(1,'pc_list_sessions')).sessions.some(s=>s.session_id===job.session_id),'child process has a tracked job');
 await err(childRaw(2,'pc_read_process_output',{session_id:job.session_id}),'INVALID_ARGUMENT','job ID cannot route to another child');
 const second=grant(await login());
 const foreign=await childRaw(1,'pc_read_process_output',{session_id:job.session_id},{},second);
 check(foreign.isError,'another parent MCP login cannot access child job');
 await childA.configure({op:'dropauth'});
 await err(childRaw(1,'pc_get_status'),'BRIDGE_AUTH_EXPIRED','expired child session reported without replay');
 check((await child(1,'pc_get_status')).pid===childA.pid,'next explicit status call renews child login');
 check((await child(1,'pc_read_process_output',{session_id:job.session_id})).session_id===job.session_id,'job ownership survives child web-session renewal');
 await child(1,'pc_stop_process',{session_id:job.session_id});
 const data=crypto.randomBytes(600000),dest=path.join(childA.root,'bridge-upload.bin');
 let transfer=await child(1,'pc_begin_upload',{path:dest,kind:'file',files:[{path:'',size:data.length,sha256:hash(data)}]});
 const tid=transfer.transfer_id;
 await err(childRaw(2,'pc_get_transfer_status',{transfer_id:tid}),'IO_ERROR','transfer ID cannot route to another child');
 const denied=await childRaw(1,'pc_get_transfer_status',{transfer_id:tid},{},second);
 check(denied.isError,'another parent login cannot access child transfer');
 const first=data.subarray(0,262144);
 await err(childRaw(1,'pc_write_upload_chunk',{transfer_id:tid,offset:0,data_base64:first.toString('base64'),sha256:'0'.repeat(64)}),'IO_ERROR','bad chunk hash rejected on child');
 await child(1,'pc_write_upload_chunk',{transfer_id:tid,offset:0,data_base64:first.toString('base64'),sha256:hash(first)});
 await parent.configure({op:'restart'});
 check((await child(1,'pc_get_transfer_status',{transfer_id:tid})).transfer_id===tid,'transfer ownership survives parent backend restart');
 await childA.configure({op:'restart'});
 await err(childRaw(1,'pc_get_status'),'BRIDGE_AUTH_EXPIRED','child restart invalidates only cached web login');
 await child(1,'pc_get_status');
 check((await child(1,'pc_get_transfer_status',{transfer_id:tid})).transfer_id===tid,'persisted transfer resumes after child restart');
 for(let offset=262144;offset<data.length;offset+=262144){const part=data.subarray(offset,offset+262144);await child(1,'pc_write_upload_chunk',{transfer_id:tid,offset,data_base64:part.toString('base64'),sha256:hash(part)});}
 await child(1,'pc_finish_upload',{transfer_id:tid});check(fs.readFileSync(dest).equals(data),'600KB multi-chunk upload resumes and verifies complete hash');
 const download=await child(1,'pc_prepare_download',{path:dest,kind:'file'}),parts=[];
 for(let offset=0;offset<data.length;offset+=262144){const part=await child(1,'pc_read_download_chunk',{transfer_id:download.transfer_id,offset});const bytes=Buffer.from(part.data_base64,'base64');assert.equal(hash(bytes),part.chunk_sha256);parts.push(bytes);}
 check(Buffer.concat(parts).equals(data),'multi-chunk child download bytes and hashes preserved');
 const old=target(1);await parent.configure({op:'peer',id:1,port:childB.port});
 await err(raw('pc_bridge_call',{...old,tool_name:'pc_get_status'}),'BRIDGE_TARGET_CHANGED','edited target rejects previous revision');
 await refresh();check((await child(1,'pc_get_status')).pid===childB.pid,'new revision selects explicitly updated target');
 await parent.configure({op:'peer',id:1,port:childA.port});await refresh();
 await parent.configure({op:'peer',id:3,port:childB.port,password:'incorrect'});await refresh();
 await err(childRaw(3,'pc_get_status'),'BRIDGE_LOGIN_FAILED','incorrect saved bridge password rejected');
 const offline=await mock((req,res)=>res.end('unused'));const offlinePort=offline.address().port;
 await new Promise(resolve=>offline.close(resolve));
 await parent.configure({op:'peer',id:6,port:offlinePort});await refresh();
 await err(childRaw(6,'pc_get_status'),'BRIDGE_OFFLINE','offline child is not reported as a login failure');
 const childUrl='http://127.0.0.1:'+childA.port;
 let r=await http(childUrl+'/bridge/mcp',{});check(r.status===401,'child protocol requires existing Remote login');
 r=await http(childUrl+'/login','u=fixture&p=fixture-only',{'Content-Type':'application/x-www-form-urlencoded'});
 const childCookie=r.headers['set-cookie'][0].split(';')[0];
 const protocol={protocol:1,operation:'call',owner:'a'.repeat(64),tool:'pc_get_status',arguments:{}};
 for(const extra of [{},{'X-RC-Bridge-MCP':'1',Origin:'https://evil.example'},{'X-RC-Bridge-MCP':'1','Sec-Fetch-Site':'same-origin'}]){
  r=await http(childUrl+'/bridge/mcp',protocol,{Cookie:childCookie,'Content-Type':'application/json',...extra});assert.equal(r.status,403);
 }
 check(true,'browser and missing-marker calls rejected even with a web login');
 r=await http(childUrl+'/bridge/mcp',protocol,{Cookie:childCookie,'X-RC-Bridge-MCP':'1','Content-Type':'application/json'},'GET');
 check(r.status===405,'bridge protocol rejects non-POST methods');
 r=await http(childUrl+'/bridge/mcp','x'.repeat(2*1024*1024+1),{Cookie:childCookie,'X-RC-Bridge-MCP':'1','Content-Type':'application/json'});
 check(r.status===413,'oversized protocol body rejected');
 const legacy=await mock((req,res)=>{if(req.url==='/login')res.setHeader('Set-Cookie','rcsid='+'a'.repeat(43));res.setHeader('Content-Type','text/html');res.end('<html>legacy</html>');});
 await parent.configure({op:'peer',id:4,port:legacy.address().port});await refresh();
 await err(childRaw(4,'pc_get_status'),'BRIDGE_UNSUPPORTED','old child version reports unsupported rather than local fallback');
 let executed=0;
 const broken=await mock((req,res)=>{
  if(req.url==='/login'){res.setHeader('Set-Cookie','rcsid='+'b'.repeat(43));res.end('ok');return;}
  req.on('data',()=>{});req.on('end',()=>{executed++;req.socket.destroy();});
 });
 await parent.configure({op:'peer',id:5,port:broken.address().port});await refresh();
 await err(childRaw(5,'pc_write_file',{path:path.join(work,'never-local.txt'),content:'test'}),'BRIDGE_RESULT_UNKNOWN','lost mutation response returns explicit unknown outcome');
 check(executed===1&&!fs.existsSync(path.join(work,'never-local.txt')),'ambiguous write sent once and never executed locally');
 check(!audit.includes('fixture-only')&&!audit.includes(cookie.split('=')[1]),'bridge audit excludes passwords and parent session tokens');
 console.log('MCP bridge: '+checks+' checks passed');
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>{
 if(gateway)try{gateway.close();}catch(e){console.error(e);process.exitCode=1;}
 for(const s of servers){s.closeAllConnections();s.close();}
 for(const p of fixtures)if(p.exitCode===null){p.stdin.end('\n');setTimeout(()=>{if(p.exitCode===null)p.kill();},3000).unref();}
});


