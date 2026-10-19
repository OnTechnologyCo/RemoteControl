import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { spawn } from 'node:child_process';
import { createServer, request as httpRequest } from 'node:http';
import { once } from 'node:events';
import { createHash, randomBytes } from 'node:crypto';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { createGateway, lanRequest, CALLBACK, CIMD } from '../gateway.mjs';

const returnedCallback = text => new URL(/id="return-client" href="([^"]+)"/.exec(text)[1].replaceAll('&amp;', '&'));

// Node fetch는 Host를 URL에서 다시 만든다. 실제 Caddy처럼 원래 Host를 넘기는 HTTP 전송으로 시험한다.
function proxyFetch(url, options={}) {
  return new Promise((resolve,reject)=>{
    const body=options.body?.toString();
    const headers=Object.fromEntries(new Headers(options.headers));
    if(body !== undefined) headers['content-length']=Buffer.byteLength(body);
    const req=httpRequest(url,{method:options.method||'GET',headers},res=>{
      const chunks=[];res.on('data',c=>chunks.push(c));res.on('error',reject);
      res.on('end',()=>{const h=new Headers();for(const [k,v] of Object.entries(res.headers))for(const s of Array.isArray(v)?v:[v])if(s!==undefined)h.append(k,s);resolve(new Response([204,304].includes(res.statusCode)?null:Buffer.concat(chunks),{status:res.statusCode,headers:h}));});
    });req.on('error',reject);req.end(body);
  });
}

test('isolated RemoteControl + public OAuth gateway', {timeout:60000}, async t => {
  const root = resolve('../artifacts/web-mcp-test'); mkdirSync(root,{recursive:true});
  const work = mkdtempSync(join(root,'case-')), remote = join(work,'remote'); mkdirSync(remote);
  const fixture = spawn(resolve('../artifacts/mcp-test/McpFixture.exe'), [remote], {stdio:['pipe','pipe','pipe'],windowsHide:true});
  t.after(()=>{fixture.stdin.end();});
  let errors=''; fixture.stderr.on('data',b=>errors+=b);
  const port = await new Promise((resolve,reject)=>{
    let output=''; const timer=setTimeout(()=>reject(Error('Fixture timeout '+errors)),10000);
    fixture.stdout.on('data',b=>{output+=b;if(output.includes('\n')){clearTimeout(timer);resolve(Number(output.trim().split('\n')[0]));}});
    fixture.once('error',reject); fixture.once('exit',code=>{clearTimeout(timer);reject(Error('Fixture exit '+code));});
  });
  assert(port>0);
  let clock=Date.now(); const calls=[], events=[];
  const config={publicUrl:'https://remote.example.test',listenPort:59432,lanPort:port,proxyKey:randomBytes(32).toString('base64url')};
  const metadata=async()=>({client_id:CIMD,redirect_uris:[CALLBACK],token_endpoint_auth_method:'private_key_jwt',token_endpoint_auth_methods_supported:['none','private_key_jwt']});
  const gateway = createGateway(config,{directory:work,now:()=>clock,metadata,audit:event=>events.push(event),upstream:async(path,data)=>{calls.push(path);return lanRequest(port,path,data);}});
  const server=createServer(gateway.app);server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(async()=>{gateway.close();server.closeAllConnections();await new Promise(r=>server.close(r));});
  const base='http://127.0.0.1:'+server.address().port;
  const headers={Host:'remote.example.test','X-RC-Proxy-Key':config.proxyKey,'X-RC-Client-IP':'198.51.100.2','X-Forwarded-Proto':'https'};
  const request=(path,options={})=>proxyFetch(base+path,{...options,redirect:'manual',headers:{...headers,...options.headers}});
  const form=(path,data,extra={})=>request(path,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded',...extra},body:new URLSearchParams(data)});
  const json=(path,body,extra={})=>request(path,{method:'POST',headers:{'Content-Type':'application/json',...extra},body:JSON.stringify(body)});
  const resource=config.publicUrl+'/mcp', issuer=config.publicUrl+'/';
  let clientId, verifier, tokens;
  async function authorize(id=clientId,changes={}) {
    const verify=randomBytes(32).toString('base64url');
    const params={client_id:id,redirect_uri:CALLBACK,response_type:'code',state:'opaque-state',code_challenge:createHash('sha256').update(verify).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource,...changes};
    const r=await request('/authorize?'+new URLSearchParams(params));
    const text=await r.text();
    return {r,text,verify,cookie:r.headers.get('set-cookie')?.split(';')[0],csrf:/name="csrf" value="([A-Za-z0-9_-]+)"/.exec(text)?.[1]};
  }
  async function login(id=clientId) {
    const auth=await authorize(id);assert.equal(auth.r.status,200,auth.text);
    const r=await form('/connect',{csrf:auth.csrf,user:'fixture',password:'fixture-only',decision:'allow'},{Origin:config.publicUrl,Cookie:auth.cookie});
    const page=await r.text();assert.equal(r.status,200,page); const location=returnedCallback(page);
    assert.equal(location.searchParams.get('state'),'opaque-state');assert.equal(location.searchParams.get('iss'),issuer);
    return {code:location.searchParams.get('code'),verify:auth.verify};
  }
  const exchange=(code,verify,extra={})=>form('/token',{grant_type:'authorization_code',client_id:clientId,code,code_verifier:verify,redirect_uri:CALLBACK,resource,...extra});
  await t.test('trusted proxy, host, origin and unauthenticated access',async()=>{
    assert.equal((await fetch(base+'/')).status,403);
    assert.equal((await request('/',{headers:{Host:'attacker.test'}})).status,403);
    assert.equal((await request('/',{headers:{Origin:'https://attacker.test'}})).status,403);
    assert.equal((await request('/',{headers:{Origin:'null'}})).status,403);
    assert.equal((await request('/',{headers:{'X-RC-Proxy-Key':'wrong'}})).status,403);
    assert.equal((await request('/',{headers:{'X-Forwarded-Proto':'http'}})).status,403);
    const before=calls.length; const r=await json('/mcp',{jsonrpc:'2.0',id:1,method:'tools/list'});
    assert.equal(r.status,401);assert(r.headers.get('www-authenticate').includes('/.well-known/oauth-protected-resource'));assert.equal(calls.length,before);
    assert.equal((await request('/download?path=C:/Windows')).status,404);
  });
  await t.test('metadata discovery and only exact ChatGPT redirects',async()=>{
    const resourceDoc=await (await request('/.well-known/oauth-protected-resource')).json();assert.equal(resourceDoc.resource,resource);assert.deepEqual(resourceDoc.authorization_servers,[issuer]);
    const auth=await (await request('/.well-known/oauth-authorization-server')).json();assert.equal(auth.issuer,issuer);assert.equal(auth.authorization_response_iss_parameter_supported,true);assert.deepEqual(auth.code_challenge_methods_supported,['S256']);
    const bad=await json('/register',{redirect_uris:['https://attacker.test/callback'],token_endpoint_auth_method:'none'});assert.equal(bad.status,400);
    const r=await json('/register',{redirect_uris:[CALLBACK],token_endpoint_auth_method:'none',grant_types:['authorization_code','refresh_token'],response_types:['code']});assert.equal(r.status,201);
    clientId=(await r.json()).client_id;
    assert.equal((await authorize(clientId,{redirect_uri:CALLBACK+'?evil=yes'})).r.status,400);
    const wrong=await authorize(clientId,{resource:'https://attacker.test/mcp'});assert.equal(wrong.r.status,302);assert.equal(new URL(wrong.r.headers.get('location')).searchParams.get('iss'),issuer);
    const weak=await authorize(clientId,{code_challenge_method:'plain'});assert.equal(new URL(weak.r.headers.get('location')).searchParams.get('error'),'invalid_request');
    const other=createGateway(config,{directory:work,metadata});assert(await other.provider.clientsStore.getClient(clientId));other.close();
  });
  await t.test('CIMD retrieves only fixed official metadata URL',async()=>{
    const r=await authorize(CIMD);assert.equal(r.r.status,200);assert(r.text.includes('원격제어 아이디'));
    assert.equal(await gateway.provider.clientsStore.getClient('http://127.0.0.1/private'),undefined);
    assert.equal((await gateway.provider.clientsStore.getClient(CIMD)).token_endpoint_auth_method,'none');
  });
  await t.test('login is CSRF protected, cancellable and verifies real LAN password',async()=>{
    let a=await authorize();let before=calls.length;
    let r=await form('/connect',{csrf:a.csrf,user:'fixture',password:'fixture-only',decision:'allow'},{Origin:'https://attacker.test',Cookie:a.cookie});assert.equal(r.status,403);assert.equal(calls.length,before);
    r=await form('/connect',{csrf:'invalid',user:'fixture',password:'fixture-only',decision:'allow'},{Origin:config.publicUrl,Cookie:a.cookie});assert.equal(r.status,403);assert.equal(calls.length,before);
    r=await form('/connect',{csrf:a.csrf,decision:'deny'},{Origin:config.publicUrl,Cookie:a.cookie});assert.equal(r.status,200);assert.equal(returnedCallback(await r.text()).searchParams.get('error'),'access_denied');
    a=await authorize();r=await form('/connect',{csrf:a.csrf,user:'fixture',password:'wrong',decision:'allow'},{Origin:config.publicUrl,Cookie:a.cookie});assert.equal(r.status,401);
    r=await form('/connect',{csrf:a.csrf,user:'fixture',password:'fixture-only',decision:'allow'},{Origin:config.publicUrl,Cookie:a.cookie});assert.equal(r.status,403);
  });
  await t.test('PKCE, audience, redirect URI, one-use code and token exchange',async()=>{
    const a=await login();verifier=a.verify;
    assert.equal((await exchange(a.code,'wrong-verifier')).status,400);
    assert.equal((await exchange(a.code,a.verify,{resource:'https://attacker.test/mcp'})).status,400);
    assert.equal((await exchange(a.code,a.verify,{redirect_uri:'https://attacker.test/callback'})).status,400);
    const r=await exchange(a.code,a.verify);assert.equal(r.status,200);tokens=await r.json();assert.equal(tokens.token_type,'Bearer');assert.equal(tokens.expires_in,3600);
    assert.equal((await exchange(a.code,a.verify)).status,400);
    assert(!JSON.stringify(tokens).includes('fixture-only'));
    assert(!readFileSync(join(work,'clients.json'),'utf8').includes('fixture-only'));
    for(const event of events) assert.deepEqual(Object.keys(event).sort(),['method','ms','stage','status']);
    assert(events.some(e=>e.stage==='login'&&e.status===200));
    assert(!JSON.stringify(events).includes('fixture-only'));
    assert(!JSON.stringify(events).includes(tokens.access_token));
  });
  await t.test('official MCP SDK invokes actual status and file tools with OAuth token',async()=>{
    const client=new Client({name:'web-gateway-test',version:'1'},{capabilities:{}});
    const start=performance.now();
    await client.connect(new StreamableHTTPClientTransport(new URL(base+'/mcp'),{fetch:proxyFetch,requestInit:{headers:{...headers,Authorization:'Bearer '+tokens.access_token}}}));
    try {
      const list=await client.listTools();assert(list.tools.length>=10);assert(list.tools.every(t=>t._meta?.securitySchemes?.[0]?.type==='oauth2'));
      const raw=await (await json('/mcp',{jsonrpc:'2.0',id:91,method:'tools/list'},{Authorization:'Bearer '+tokens.access_token})).json();assert(raw.result.tools.every(t=>t.securitySchemes?.[0]?.type==='oauth2'));
      const status=JSON.parse((await client.callTool({name:'pc_get_status',arguments:{}})).content[0].text);assert.equal(status.pid,fixture.pid);
      const file=join(remote,'웹 연결.txt');const write=await client.callTool({name:'pc_write_file',arguments:{path:file,content:'OAuth fixture only'}});assert(!write.isError);
      const read=await client.callTool({name:'pc_read_file',arguments:{path:file}});assert.equal(JSON.parse(read.content[0].text).content,'OAuth fixture only\r\n');assert.equal(readFileSync(file,'utf8'),'OAuth fixture only');
      console.log('SDK connect + list + status + write + read: '+Math.round(performance.now()-start)+' ms');
    } finally {await client.close();}
    assert.equal((await request('/mcp',{headers:{Authorization:'Bearer '+tokens.access_token}})).status,405);
  });
  await t.test('refresh rotates tokens; replay revokes the whole grant',async()=>{
    let r=await form('/token',{grant_type:'refresh_token',client_id:clientId,refresh_token:tokens.refresh_token,resource});assert.equal(r.status,200);const updated=await r.json();assert.notEqual(updated.access_token,tokens.access_token);
    r=await form('/token',{grant_type:'refresh_token',client_id:clientId,refresh_token:tokens.refresh_token,resource});assert.equal(r.status,400);
    r=await json('/mcp',{jsonrpc:'2.0',id:1,method:'ping'},{Authorization:'Bearer '+updated.access_token});assert.equal(r.status,401);
  });
  await t.test('LAN host restart and more than eight hours do not require login',async()=>{
    const a=await login();let r=await exchange(a.code,a.verify);assert.equal(r.status,200);const initial=await r.json();
    const stored=readFileSync(join(remote,'mcp-sessions.bin'));
    assert(!stored.includes(Buffer.from('fixture-only')));assert(!stored.includes(Buffer.from(gateway.provider.access.get(initial.access_token).grant.cookie.split('=')[1])));
    await new Promise(resolve=>{fixture.stdout.once('data',resolve);fixture.stdin.write('restart\n');});
    r=await json('/mcp',{jsonrpc:'2.0',id:1,method:'ping'},{Authorization:'Bearer '+initial.access_token});assert.equal(r.status,200);
    clock+=9*60*60000;
    r=await form('/token',{grant_type:'refresh_token',client_id:clientId,refresh_token:initial.refresh_token,resource});assert.equal(r.status,200,await r.clone().text());
    const refreshed=await r.json();assert.equal((await json('/mcp',{jsonrpc:'2.0',id:1,method:'ping'},{Authorization:'Bearer '+refreshed.access_token})).status,200);
  });
  await t.test('expired code and browser form cannot authorize',async()=>{
    const a=await login();clock+=301000;assert.equal((await exchange(a.code,a.verify)).status,400);
    const p=await authorize();clock+=11*60000;
    const r=await form('/connect',{csrf:p.csrf,user:'fixture',password:'fixture-only',decision:'allow'},{Origin:config.publicUrl,Cookie:p.cookie});assert.equal(r.status,403);
  });
  await t.test('password change invalidates existing web access without retrying commands',async()=>{
    const a=await login();const r=await exchange(a.code,a.verify);assert.equal(r.status,200);tokens=await r.json();
    await new Promise(resolve=>{fixture.stdout.once('data',resolve);fixture.stdin.write('password-change\n');});
    const before=calls.length;const reply=await json('/mcp',{jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'pc_get_status',arguments:{}}},{Authorization:'Bearer '+tokens.access_token});assert.equal(reply.status,200);assert((await reply.json()).result._meta['mcp/www_authenticate']);assert.equal(calls.slice(before).filter(p=>p==='/mcp').length,1);
    assert.equal((await json('/mcp',{jsonrpc:'2.0',id:2,method:'ping'},{Authorization:'Bearer '+tokens.access_token})).status,401);
  });
});

test('transport failure never repeats an executed operation',async t=>{
  const root=resolve('../artifacts/web-mcp-test');mkdirSync(root,{recursive:true});const directory=mkdtempSync(join(root,'fault-'));let executions=0;
  const c={publicUrl:'https://remote.example.test',listenPort:59432,lanPort:59431,proxyKey:randomBytes(32).toString('base64url')};
  const gateway=createGateway(c,{directory,upstream:async()=>{executions++;throw Error('lost response');}});
  const grant={clientId:'test',cookie:'rcmcpid=fixture',expires:Date.now()+60000,revoked:false};gateway.provider.grants.add(grant);const tokens=gateway.provider.issue(grant);
  const server=createServer(gateway.app);server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
  const r=await proxyFetch('http://127.0.0.1:'+server.address().port+'/mcp',{method:'POST',headers:{Host:'remote.example.test','X-RC-Proxy-Key':c.proxyKey,'X-RC-Client-IP':'198.51.100.3','X-Forwarded-Proto':'https',Authorization:'Bearer '+tokens.access_token,'Content-Type':'application/json'},body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'pc_start_process',arguments:{command:'fixture command'}}})});
  assert.equal(r.status,502);assert.equal(executions,1);
});

test('access lifetime, connection lifetime and client-bound revocation',async()=>{
  const root=resolve('../artifacts/web-mcp-test');mkdirSync(root,{recursive:true});const directory=mkdtempSync(join(root,'lifetime-'));let now=Date.now();let logouts=0;
  const c={publicUrl:'https://remote.example.test',listenPort:59432,lanPort:59431,proxyKey:randomBytes(32).toString('base64url')};
  const gateway=createGateway(c,{directory,now:()=>now,upstream:async(path)=>{if(path==='/logout')logouts++;return {status:200,body:'{}'};}});
  try {
    const grant={clientId:'owner',cookie:'fixture',expires:now+470*60000,revoked:false};gateway.provider.grants.add(grant);const tokens=gateway.provider.issue(grant);
    await gateway.provider.revokeToken({client_id:'other'},{token:tokens.access_token});assert(await gateway.provider.verifyAccessToken(tokens.access_token));
    now+=3600001;await assert.rejects(gateway.provider.verifyAccessToken(tokens.access_token));
    const updated=await gateway.provider.exchangeRefreshToken({client_id:'owner'},tokens.refresh_token,['remotecontrol'],new URL(c.publicUrl+'/mcp'));assert(await gateway.provider.verifyAccessToken(updated.access_token));
    now+=470*60000;await assert.rejects(gateway.provider.exchangeRefreshToken({client_id:'owner'},updated.refresh_token,undefined,new URL(c.publicUrl+'/mcp')));
    gateway.provider.sweep();assert.equal(logouts,1);assert.equal(gateway.provider.grants.size,0);
  }finally{gateway.close();}
});

test('temporary refresh failure returns HTTP 503 and retry succeeds without login',async t=>{
  const root=resolve('../artifacts/web-mcp-test');mkdirSync(root,{recursive:true});const directory=mkdtempSync(join(root,'retry-'));
  const c={publicUrl:'https://remote.example.test',listenPort:59432,lanPort:59431,proxyKey:randomBytes(32).toString('base64url')};let offline=true;
  const gateway=createGateway(c,{directory,upstream:async()=>{if(offline)throw Error('temporary outage');return {status:200,body:'{}'};}});
  const client=await gateway.provider.clientsStore.registerClient({redirect_uris:[CALLBACK],token_endpoint_auth_method:'none'});
  const grant={clientId:client.client_id,cookie:'rcmcpid='+randomBytes(32).toString('base64url'),expires:Number.MAX_SAFE_INTEGER,revoked:false};gateway.provider.grants.add(grant);const tokens=gateway.provider.issue(grant);
  const server=createServer(gateway.app);server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
  const send=()=>proxyFetch('http://127.0.0.1:'+server.address().port+'/token',{method:'POST',headers:{Host:'remote.example.test','X-RC-Proxy-Key':c.proxyKey,'X-RC-Client-IP':'198.51.100.3','X-Forwarded-Proto':'https','Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({grant_type:'refresh_token',client_id:client.client_id,refresh_token:tokens.refresh_token,resource:c.publicUrl+'/mcp'})});
  let r=await send();assert.equal(r.status,503);assert.equal((await r.json()).error,'temporarily_unavailable');assert.equal(r.headers.get('retry-after'),'30');
  offline=false;r=await send();assert.equal(r.status,200);assert(await gateway.provider.verifyAccessToken((await r.json()).access_token));
});
