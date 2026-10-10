import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { request } from 'node:https';
import { once } from 'node:events';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { spawn } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { createGateway, CALLBACK, CIMD, CLAUDE_CALLBACK, CLAUDE_CIMD } from '../gateway.mjs';
import { browser, delay } from './browser-helper.mjs';

test('real browser form login, OAuth redirect, and authenticated PC status over HTTPS', {timeout:60000}, async t=>{
  const root=resolve('../artifacts/web-mcp-test');mkdirSync(root,{recursive:true});
  const work=mkdtempSync(join(root,'browser-')), remote=join(work,'remote');mkdirSync(remote);
  const fixture=spawn(resolve('../artifacts/mcp-test/McpFixture.exe'),[remote],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  t.after(()=>fixture.stdin.end());
  const lanPort=await new Promise((resolve,reject)=>{
    let output='';const timer=setTimeout(()=>reject(Error('Fixture startup timed out')),10000);
    fixture.stdout.on('data',b=>{output+=b;if(output.includes('\n')){clearTimeout(timer);resolve(Number(output.trim().split('\n')[0]));}});
    fixture.once('error',reject);
  });
  async function freePort(){const s=createServer();s.listen(0,'127.0.0.1');await once(s,'listening');const port=s.address().port;await new Promise(r=>s.close(r));return port;}
  const listenPort=await freePort(),tlsPort=await freePort(),proxyKey=randomBytes(32).toString('base64url');
  const base='https://localhost:'+tlsPort;
  const hosts=[{id:CIMD,redirect:CALLBACK,name:'ChatGPT'},{id:CLAUDE_CIMD,redirect:CLAUDE_CALLBACK,name:'Claude'}];
  const gateway=createGateway({publicUrl:base,listenPort,lanPort,proxyKey},{directory:work,metadata:async id=>({client_id:id,redirect_uris:[hosts.find(h=>h.id===id).redirect],token_endpoint_auth_method:'none'})});
  const server=createServer(gateway.app);server.listen(listenPort,'127.0.0.1');await once(server,'listening');
  t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
  const caddyFile=join(work,'Caddyfile');
  writeFileSync(caddyFile,`{
admin off
auto_https disable_redirects
skip_install_trust
storage file_system {
root "${join(work,'certs').replaceAll('\\','/')}"
}
}
${base} {
bind 127.0.0.1
tls internal
reverse_proxy 127.0.0.1:${listenPort} {
header_up Host localhost:${tlsPort}
header_up X-RC-Proxy-Key ${proxyKey}
header_up X-RC-Client-IP {remote_host}
header_up X-Forwarded-Proto https
header_up -X-Forwarded-For
header_up -Forwarded
}
}`);
  const caddy=spawn(resolve('../artifacts/web-mcp-downloads/caddy-2.11.7/caddy.exe'),['run','--config',caddyFile,'--adapter','caddyfile'],{windowsHide:true,stdio:'ignore'});
  t.after(()=>caddy.kill());
  function send(path,{method='GET',body,headers={}}={}){return new Promise((resolve,reject)=>{
    // The certificate exception applies only to this isolated loopback fixture.
    const req=request({hostname:'127.0.0.1',port:tlsPort,servername:'localhost',path,method,rejectUnauthorized:false,headers:{Host:'localhost:'+tlsPort,...headers,...(body?{'Content-Length':Buffer.byteLength(body)}:{})}},res=>{
      let text='';res.on('data',b=>text+=b);res.on('end',()=>resolve({status:res.statusCode,body:text}));
    });req.on('error',reject);req.end(body);
  });}
  let ready=false;for(let i=0;i<100;i++){try{ready=(await send('/')).status===200;if(ready)break;}catch{}await delay(100);}assert(ready);
  const chrome=await browser(join(work,'profile'));t.after(()=>chrome.close());
  // This new browser profile is used only for the self-signed loopback fixture.
  await chrome.command('Security.setIgnoreCertificateErrors',{ignore:true});
  await chrome.command('Log.enable');
  let callback,blocked=false,origin,callbackReferer,returned;
  chrome.on(event=>{
    if(event.method==='Network.requestWillBeSent'&&event.params.request.url===base+'/connect')origin=event.params.request.headers.Origin;
    if(event.method==='Network.loadingFailed'&&event.params.blockedReason==='csp')blocked=true;
    if(event.method==='Log.entryAdded'&&event.params.entry.text.includes('form-action'))blocked=true;
    if(event.method==='Fetch.requestPaused'){
      if(event.params.request.url.startsWith('https://return.example.test/')){
        if(event.params.resourceType==='Document' && callback) returned=true;
        chrome.command('Fetch.fulfillRequest',{requestId:event.params.requestId,responseCode:200,responseHeaders:[{name:'Content-Type',value:'text/html'}],body:Buffer.from('<p>Isolated host return received</p>').toString('base64')}).catch(()=>{});
        return;
      }
      callback=new URL(event.params.request.url);callbackReferer=event.params.request.headers.Referer;
      chrome.command('Fetch.fulfillRequest',{requestId:event.params.requestId,responseCode:302,responseHeaders:[{name:'Location',value:'https://return.example.test/done'}]}).catch(()=>{});
    }
  });
  // Consume callbacks locally so test codes and fixture credentials never reach ChatGPT.
  await chrome.command('Fetch.enable',{patterns:[...hosts.map(h=>({urlPattern:h.redirect+'*',requestStage:'Request'})),{urlPattern:'https://return.example.test/*',requestStage:'Request'}]});
  for(const host of hosts){
  for(const mode of ['deny','allow','manual']){
    const decision=mode==='deny'?'deny':'allow';
    callback=null;blocked=false;origin=null;callbackReferer=null;returned=false;
    await chrome.command('Emulation.setScriptExecutionDisabled',{value:mode==='manual'});
    const verifier=randomBytes(32).toString('base64url');
    const url=base+'/authorize?'+new URLSearchParams({client_id:host.id,redirect_uri:host.redirect,response_type:'code',state:'isolated-browser-test',code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource:base+'/mcp'});
    await chrome.command('Page.navigate',{url});
    let loaded=false;for(let i=0;i<100;i++){if(await chrome.evaluate('!!document.querySelector("button[value=allow]")')){loaded=true;break;}await delay(100);}assert(loaded,'Browser must load actual login HTML');
    assert((await chrome.evaluate('document.querySelector("h1").textContent')).startsWith(host.name));
    await chrome.evaluate(`document.querySelector('input[name=user]').value='fixture';document.querySelector('input[name=password]').value='fixture-only';document.querySelector('button[value=${decision}]').click()`);
    if(mode==='manual'){
      let success=false;for(let i=0;i<60;i++){if(await chrome.evaluate('!!document.getElementById("return-client")')){success=true;break;}await delay(100);}assert(success,'Login must replace the original form with an explicit return button');
      assert.equal(callback,null,'With page scripts disabled, no automatic callback should happen');
      await chrome.evaluate('document.getElementById("return-client").click()');
    }
    for(let i=0;i<60&&!returned&&!blocked;i++)await delay(100);
    assert.equal(origin,base,'Browser must send the real Origin on HTML form POST');
    assert.equal(blocked,false,'CSP must allow the exact OAuth callback after form POST');
    assert(callback,mode+': Browser must follow the OAuth redirect');assert.equal(callbackReferer,undefined,'No referrer may be sent to ChatGPT');
    assert(returned,'Host redirect after the callback must not inherit the login form restriction');
    assert.equal(callback.searchParams.get('iss'),base+'/');assert.equal(callback.searchParams.get('state'),'isolated-browser-test');
    assert.equal(callback.origin+callback.pathname,host.redirect);
    if(decision==='deny'){assert.equal(callback.searchParams.get('error'),'access_denied');continue;}
    const tokenReply=await send('/token',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({grant_type:'authorization_code',client_id:host.id,code:callback.searchParams.get('code'),code_verifier:verifier,redirect_uri:host.redirect,resource:base+'/mcp'}).toString()});
    assert.equal(tokenReply.status,200,tokenReply.body);const tokens=JSON.parse(tokenReply.body);
    const rpc=async(id,method,params)=>{
      const r=await send('/mcp',{method:'POST',headers:{'Content-Type':'application/json',Authorization:'Bearer '+tokens.access_token},body:JSON.stringify({jsonrpc:'2.0',id,method,params})});
      assert.equal(r.status,200,r.body);const data=JSON.parse(r.body);assert(!data.error);return data.result;
    };
    await rpc(1,'initialize',{protocolVersion:'2025-03-26',capabilities:{},clientInfo:{name:'browser-regression',version:'1'}});
    const list=await rpc(2,'tools/list',{});assert(list.tools.some(t=>t.name==='pc_get_status'));
    const status=await rpc(3,'tools/call',{name:'pc_get_status',arguments:{}});assert(!status.isError);
    const pc=JSON.parse(status.content[0].text);assert(pc.hostname);
    writeFileSync(join(work,host.name+'-'+mode+'-result.json'),JSON.stringify({client:host.name,browserOrigin:origin,oauthCallbackAllowed:true,crossOriginReferrerOmitted:true,toolCount:list.tools.length,pcStatusSucceeded:true},null,2));
  }
  }
});
