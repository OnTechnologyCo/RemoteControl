// Explicit external transport test. Never hosts PC tools: only a fixed fixture status.
import {createServer} from 'node:http';
import {once} from 'node:events';
import {spawn} from 'node:child_process';
import {mkdirSync,mkdtempSync,writeFileSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {randomBytes,createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {createTailscaleServer} from '../web-mcp/tailscale-server.mjs';
import {CALLBACK,CLAUDE_CALLBACK} from '../web-mcp/gateway.mjs';
import {browser,delay} from '../web-mcp/test/browser-helper.mjs';

const root=resolve('artifacts/tailscale-public-test');mkdirSync(root,{recursive:true});const work=mkdtempSync(join(root,'run-'));
const secret=randomBytes(24).toString('base64url');
const proxyKey=randomBytes(32).toString('base64url');
const publicPort=Number(process.env.RC_TEST_PUBLIC_PORT||443);
assert([443,8443,10000].includes(publicPort));
let active=null,runtime,proxy,chrome;
const screen=createServer((req,res)=>{res.end('ok');});screen.listen(0,'127.0.0.1');await once(screen,'listening');
try{
 runtime=createTailscaleServer({listenPort:59335,lanPort:59331,publicPort,proxyKey},{directory:work,readActive:()=>active,gatewayOptions:{upstream:async(path,{body}={})=>{
  if(path==='/login'){const value=JSON.parse(body);return value.user==='fixture'&&value.password===secret?{status:200,headers:{'set-cookie':['rcmcpid=fixture; Path=/']}}:{status:401};}
  return {status:200,body:JSON.stringify({jsonrpc:'2.0',id:1,result:{hostname:'ISOLATED-TAILSCALE-FIXTURE',livePcAccess:false}})};
 }}});
 runtime.server.listen(0,'127.0.0.1');await once(runtime.server,'listening');
 proxy=spawn(resolve('dist/tsproxy.exe'),[],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,BACKEND:'http://127.0.0.1:'+screen.address().port,TS_HOSTNAME:'rc-mcp-check-'+randomBytes(4).toString('hex'),TS_STATEDIR:join(work,'tsnet'),TS_EPHEMERAL:'1',RC_MCP_BACKEND:'http://127.0.0.1:'+runtime.server.address().port,RC_MCP_PUBLIC_PORT:String(publicPort),RC_MCP_PROXY_KEY:proxyKey}});
 let output='',base='',screenUrl='';
 proxy.stdout.on('data',data=>{output+=data;for(const line of output.split('\n')){
  if(line.startsWith('MCP_URL=')){base=line.slice(8).trim();active={publicUrl:base,lanPort:59331,expiresAt:Date.now()+40000};}
  if(line.startsWith('FUNNEL_URL='))screenUrl=line.slice(11).trim();
 }});
 proxy.stderr.on('data',()=>{});
 const heartbeat=setInterval(()=>{if(active)active.expiresAt=Date.now()+40000;},10000);heartbeat.unref();
 const until=Date.now()+180000;
 while(!screenUrl && Date.now()<until){if(proxy.exitCode!==null)throw Error('Isolated Funnel helper exited before ready');await delay(500);}
 assert(base&&screenUrl,'Funnel did not generate both addresses');
 if(publicPort===443)assert.equal(base,screenUrl,'Default HTTPS must share the existing screen origin');
 console.log('Public screen and MCP Funnel addresses generated. Testing HTTPS and OAuth.');
 const request=async(path,data,headers={})=>{
  const r=await fetch(base+path,{method:data===undefined?'GET':'POST',body:data===undefined?undefined:typeof data==='string'?data:JSON.stringify(data),headers:{'Content-Type':'application/json',...headers},signal:AbortSignal.timeout(12000),redirect:'manual'});
  return {status:r.status,headers:r.headers,text:await r.text()};
 };
 let meta;
 for(let i=0;i<12;i++){try{meta=await request('/.well-known/oauth-protected-resource');if(meta.status===200)break;}catch{}await delay(1500);}
 assert.equal(meta?.status,200);assert.equal(JSON.parse(meta.text).resource,base+'/mcp');
 assert.equal((await request('/mcp',{jsonrpc:'2.0',id:1,method:'tools/list'})).status,401);
 const screenResponse=await fetch(screenUrl+'/ping',{signal:AbortSignal.timeout(12000)});assert.equal(await screenResponse.text(),'ok');
 chrome=await browser(join(work,'browser'));
 let callback=null;
 chrome.on(event=>{if(event.method==='Fetch.requestPaused'){
  callback=new URL(event.params.request.url);
  chrome.command('Fetch.fulfillRequest',{requestId:event.params.requestId,responseCode:200,responseHeaders:[{name:'Content-Type',value:'text/html'}],body:Buffer.from('Isolated callback received').toString('base64')}).catch(()=>{});
 }});
 await chrome.command('Fetch.enable',{patterns:[CALLBACK,CLAUDE_CALLBACK].map(url=>({urlPattern:url+'*',requestStage:'Request'}))});
 for(const [name,redirect] of [['ChatGPT',CALLBACK],['Claude',CLAUDE_CALLBACK]]){
  const registration=await request('/register',{redirect_uris:[redirect],token_endpoint_auth_method:'none'});assert.equal(registration.status,201,registration.text);const client=JSON.parse(registration.text);
  for(const manual of [false,true]){
   callback=null;const verifier=randomBytes(32).toString('base64url');
   await chrome.command('Emulation.setScriptExecutionDisabled',{value:manual});
   await chrome.command('Page.navigate',{url:base+'/authorize?'+new URLSearchParams({client_id:client.client_id,redirect_uri:redirect,response_type:'code',code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',resource:base+'/mcp',scope:'remotecontrol',state:'isolated'})});
   let loaded=false;for(let i=0;i<120;i++){if(await chrome.evaluate('!!document.querySelector("button[value=allow]")')){loaded=true;break;}await delay(100);}assert(loaded,'Real login page missing');
   await chrome.evaluate(`document.querySelector('input[name=user]').value='fixture';document.querySelector('input[name=password]').value=${JSON.stringify(secret)};document.querySelector('button[value=allow]').click()`);
   if(manual){let returned=false;for(let i=0;i<120;i++){if(await chrome.evaluate('!!document.getElementById("return-client")')){returned=true;break;}await delay(100);}assert(returned);await chrome.evaluate('document.getElementById("return-client").click()');}
   for(let i=0;i<120&&!callback;i++)await delay(100);assert(callback,'OAuth callback did not return');assert.equal(callback.origin+callback.pathname,redirect);
   const tokens=await request('/token',new URLSearchParams({grant_type:'authorization_code',client_id:client.client_id,code:callback.searchParams.get('code'),code_verifier:verifier,redirect_uri:redirect,resource:base+'/mcp'}).toString(),{'Content-Type':'application/x-www-form-urlencoded'});assert.equal(tokens.status,200,tokens.text);
   const status=await request('/mcp',{jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'pc_get_status',arguments:{}}},{Authorization:'Bearer '+JSON.parse(tokens.text).access_token});assert.equal(status.status,200,status.text);assert(status.text.includes('ISOLATED-TAILSCALE-FIXTURE'));
   console.log('PASS '+name+' '+(manual?'manual':'automatic')+' browser return, PKCE and fixture status over public Tailscale HTTPS');
  }
 }
 writeFileSync(join(work,'verification.json'),JSON.stringify({publicPort,sharedScreenOrigin:base===screenUrl,screenHttps:true,mcpHttps:true,oauthClients:['ChatGPT','Claude'],automaticAndManualReturn:true,fixtureOnly:true,livePcAccess:false,telegramSent:false},null,2));
 console.log('Report: '+join(work,'verification.json'));
}finally{
 await chrome?.close();if(proxy&&proxy.exitCode===null)proxy.kill();runtime?.close();screen.closeAllConnections();screen.close();
}
