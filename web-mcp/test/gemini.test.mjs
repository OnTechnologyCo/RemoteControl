import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createServer, request as httpRequest } from 'node:http';
import { once } from 'node:events';
import { createHash, randomBytes } from 'node:crypto';
import { AuthState } from '../auth-state.mjs';
import { createGateway, CALLBACK, CLAUDE_CALLBACK, callbackName } from '../gateway.mjs';

const redirects = ['oauth-redirect','oauth-redirect-sandbox','oauth-redirect-test']
  .flatMap(host => ['r','a'].map(kind => 'https://' + host + '.googleusercontent.com/' + kind + '/fixture-app_123'));
const opaqueState = 'x'.repeat(1400) + '+&%한글🙂';
const returned = page => new URL(/id="return-client" href="([^"]+)"/.exec(page)[1].replaceAll('&amp;', '&'));

async function fixture(t, mode) {
  const directory = mkdtempSync(join(tmpdir(), 'rc-gemini-')), events = [];
  const config = { publicUrl:mode === 'lan' ? 'https://127.0.0.2' : 'https://remote.example.test',
    ...(mode ? {mode,allowedNetworks:'127.0.0.0/8'} : {}),
    listenPort:59432, lanPort:59431, proxyKey:randomBytes(32).toString('base64url') };
  let clock = Date.now(), upstreamStatus = 200, requests = 0;
  const options = {directory, now:()=>clock, audit:event=>events.push(event), upstream:async(path,data)=>{
    requests++;
    if (path === '/login') {
      const form = JSON.parse(data.body);
      return form.user === 'fixture' && form.password === 'fixture-password'
        ? {status:200,headers:{'set-cookie':['rcmcpid=fixture-session; HttpOnly']},body:'{}'}
        : {status:401,headers:{},body:'{}'};
    }
    return {status:upstreamStatus,body:JSON.stringify({jsonrpc:'2.0',id:1,result:{ok:true}})};
  }};
  let gateway = createGateway(config,options), server;
  const open = async()=>{
    server=createServer({maxHeaderSize:32768},gateway.app);
    server.listen(0,'127.0.0.1');await once(server,'listening');
  };
  const stop = async()=>{try {gateway.close();} finally {server.closeAllConnections();await new Promise(r=>server.close(r));}};
  await open();
  t.after(stop);
  function request(path, method='GET', body, extra={}) {
    return new Promise((resolve,reject)=>{
      const req=httpRequest({hostname:'127.0.0.1',port:server.address().port,path,method,maxHeaderSize:32768,
        headers:{Host:new URL(config.publicUrl).host,'X-RC-Proxy-Key':config.proxyKey,
          'X-Forwarded-Proto':'https','X-RC-Client-IP':'127.0.0.1',
          ...(body === undefined ? {} : {'Content-Length':Buffer.byteLength(body)}),...extra}},res=>{
        const parts=[];res.on('data',b=>parts.push(b));res.on('error',reject);res.on('end',()=>{
          const text=Buffer.concat(parts).toString();resolve({status:res.statusCode,headers:res.headers,text,
            json:()=>JSON.parse(text)});
        });
      });req.on('error',reject);req.end(body);
    });
  }
  const form=(path,data,headers)=>request(path,'POST',new URLSearchParams(data).toString(),{'Content-Type':'application/x-www-form-urlencoded',...headers});
  const register=(uris=redirects,method='client_secret_post')=>request('/register','POST',
    JSON.stringify({redirect_uris:uris,token_endpoint_auth_method:method,client_name:'untrusted name',grant_types:['authorization_code','refresh_token'],response_types:['code']}),
    {'Content-Type':'application/json'});
  const resource=config.publicUrl+'/mcp';
  const authorize=async(client,uri=redirects[0],state=opaqueState,changes={})=>{
    const verifier=randomBytes(32).toString('base64url');
    const params={client_id:client.client_id,redirect_uri:uri,response_type:'code',state,
      code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',resource,scope:'remotecontrol',...changes};
    const r=await request('/authorize?'+new URLSearchParams(params));
    return {...r,params,verifier,uri,state,csrf:/name="csrf" value="([^"]+)"/.exec(r.text)?.[1],cookie:r.headers['set-cookie']?.[0].split(';')[0]};
  };
  const login=(a,changes={},headers={})=>form('/connect',{csrf:a.csrf,user:'fixture',password:'fixture-password',decision:'allow',...changes},
    {Origin:config.publicUrl,Cookie:a.cookie,...headers});
  const exchange=(client,a,code,changes={})=>form('/token',{grant_type:'authorization_code',client_id:client.client_id,
    ...(client.client_secret ? {client_secret:client.client_secret}:{}),code,code_verifier:a.verifier,redirect_uri:a.uri,resource,...changes});
  const refresh=(client,token,changes={})=>form('/token',{grant_type:'refresh_token',client_id:client.client_id,
    ...(client.client_secret ? {client_secret:client.client_secret}:{}),refresh_token:token,resource,...changes});
  return {directory,events,config,resource,request,form,register,authorize,login,exchange,refresh,
    get provider(){return gateway.provider;},get requests(){return requests;},
    set status(value){upstreamStatus=value;},advance:ms=>{clock+=ms;},
    restart:async()=>{await stop();gateway=createGateway(config,options);await open();}};
}

test('Gemini registers six exact Google callbacks with both supported authentication methods',async t=>{
  const f=await fixture(t);
  for(const method of ['client_secret_post','none']) {
    const r=await f.register(redirects,method);
    assert.equal(r.status,201,r.text);
    const client=r.json();assert.deepEqual(client.redirect_uris,redirects);assert.equal(client.client_name,'Google (Gemini)');
    for(const uri of redirects) {
      const a=await f.authorize(client,uri);assert.equal(a.status,200,a.text);assert.match(a.text,/Google \(Gemini\)/);
    }
  }
});

test('long opaque state survives the browser callback without reinterpretation',async t=>{
  const f=await fixture(t);
  // Also reproduces the old state limit independently of Google callback support.
  const registration=await f.register([CALLBACK],'none');assert.equal(registration.status,201);
  const a=await f.authorize(registration.json(),CALLBACK,opaqueState);assert.equal(a.status,200,a.text);
  const r=await f.login(a);assert.equal(r.status,200,r.text);
  assert.equal(returned(r.text).searchParams.get('state'),opaqueState);
});


test('Google registration rejects mixed, duplicate, oversized and noncanonical callback lists',async t=>{
  const f=await fixture(t);
  const invalid = [[],[redirects[0],redirects[0]],[...redirects,redirects[0]+'/extra'],[redirects[0],CALLBACK],
    [redirects[0],CLAUDE_CALLBACK],[redirects[0],'http://localhost:49123/cb'],
    [redirects[0]+'?x=1'],[redirects[0]+'#fragment'],[redirects[0]+'\n'],
    [redirects[0].replace('.com/','.com:443/')],[redirects[0].replace('.com/','.com.evil.test/')],
    [redirects[0].replace('https://','https://owner@')],[redirects[0].replace('/r/','/r/%2e%2e/')],
    [redirects[0].replace('https://','http://')],[redirects[0].replace('/r/','\\r\\')]];
  for(const uris of invalid) {
    const before=f.provider.clientsStore.clients.size;
    const r=await f.register(uris);assert.equal(r.status,400,JSON.stringify(uris));
    assert.equal(f.provider.clientsStore.clients.size,before);
  }
  assert.equal((await f.register(redirects,'client_secret_basic')).status,400);
  assert.equal(f.provider.clientsStore.clients.size,0);
  assert(f.events.some(e=>e.reason==='callback_invalid'));
  assert(f.events.some(e=>e.reason==='auth_method_unsupported'));
  for(const uri of redirects) {assert.equal(callbackName(uri),'Google (Gemini)');assert.equal(callbackName(uri,true),undefined);}
});

test('each registered Google callback binds its code, secret, PKCE and resource exactly',async t=>{
  const f=await fixture(t), client=(await f.register()).json();
  assert.equal((await f.authorize(client,redirects[0]+'-unregistered')).status,400);
  for(const uri of redirects) {
    const a=await f.authorize(client,uri);
    const page=await f.login(a);assert.equal(page.status,200,page.text);
    const callback=returned(page.text), code=callback.searchParams.get('code');
    assert.equal(callback.origin+callback.pathname,uri);assert.equal(callback.searchParams.get('state'),opaqueState);
    const another=redirects.find(r=>r!==uri);
    assert.equal((await f.exchange(client,a,code,{redirect_uri:another})).status,400);
    assert.equal((await f.exchange(client,a,code,{client_secret:'wrong'})).status,400);
    assert.equal((await f.exchange(client,a,code,{code_verifier:'wrong'})).status,400);
    assert.equal((await f.exchange(client,a,code,{resource:'https://other.example.test/mcp'})).status,400);
    const r=await f.exchange(client,a,code);assert.equal(r.status,200,r.text);
    assert.equal((await f.exchange(client,a,code)).status,400);
    const tokens=r.json();
    assert.equal((await f.request('/mcp','POST',JSON.stringify({jsonrpc:'2.0',id:1,method:'ping'}),
      {'Content-Type':'application/json',Authorization:'Bearer '+tokens.access_token})).status,200);
  }
});

test('state byte limit, encoded request size, restart and encrypted pending expiry',async t=>{
  const f=await fixture(t), client=(await f.register()).json();
  const worst='%'.repeat(8192), a=await f.authorize(client,redirects[0],worst);
  assert.equal(a.status,200,a.text);
  const pendingBytes=readFileSync(join(f.directory,'auth-state.bin'));
  assert(!pendingBytes.includes(Buffer.from(worst)));
  await f.restart();
  assert.deepEqual((await f.provider.clientsStore.getClient(client.client_id)).redirect_uris,redirects);
  const page=await f.login(a);assert.equal(page.status,200,page.text);
  const callback=returned(page.text);assert.equal(callback.searchParams.get('state'),worst);
  assert.equal((await f.exchange(client,a,callback.searchParams.get('code'))).status,200);
  for(const state of ['', 'x'.repeat(8193), '한'.repeat(2731)]) {
    const r=await f.authorize(client,redirects[0],state);assert.equal(r.status,302);
    assert.equal(new URL(r.headers.location).searchParams.get('error'),'invalid_request');
  }
  const unicode='한'.repeat(2730)+'ab', u=await f.authorize(client,redirects[1],unicode);
  assert.equal(Buffer.byteLength(unicode),8192);assert.equal(u.status,200);
  const upage=await f.login(u);assert.equal(returned(upage.text).searchParams.get('state'),unicode);
  const pending=await f.authorize(client);
  f.advance(11*60000);await f.restart();assert.equal((await f.login(pending)).status,403);
});

test('Google confidential refresh is persistent, authenticated, isolated and recoverable',async t=>{
  const f=await fixture(t), client=(await f.register()).json(), other=(await f.register()).json();
  const a=await f.authorize(client), page=await f.login(a);
  const tokens=(await f.exchange(client,a,returned(page.text).searchParams.get('code'))).json();
  assert.equal((await f.refresh(client,tokens.refresh_token,{client_secret:'wrong'})).status,400);
  assert.equal((await f.refresh(other,tokens.refresh_token)).status,400);
  assert.equal((await f.refresh(client,tokens.refresh_token,{scope:'unrelated'})).status,400);
  f.status=503;assert.equal((await f.refresh(client,tokens.refresh_token)).status,503);f.status=200;
  const write=f.provider.state.write;
  f.provider.state.write=()=>{throw Error('fixture storage failure');};
  assert.equal((await f.refresh(client,tokens.refresh_token)).status,503);
  assert.equal(f.provider.refresh.get(tokens.refresh_token).used,false);
  f.provider.state.write=write;
  await f.restart();
  for(let i=0;i<3;i++){
    const r=await f.refresh(client,tokens.refresh_token);assert.equal(r.status,200,r.text);
    const updated=r.json();assert.equal(updated.refresh_token,tokens.refresh_token);assert.notEqual(updated.access_token,tokens.access_token);
  }
  const r=await f.form('/revoke',{client_id:client.client_id,client_secret:client.client_secret,token:tokens.refresh_token});
  assert.equal(r.status,200);assert.equal((await f.refresh(client,tokens.refresh_token)).status,400);
  await f.restart();assert.equal((await f.refresh(client,tokens.refresh_token)).status,400);
  for(const secret of [client.client_id,client.client_secret,tokens.refresh_token,tokens.access_token,opaqueState,redirects[0],'fixture-password','fixture-session']) {
    assert(!JSON.stringify(f.events).includes(secret));
  }
  for(const event of f.events.filter(e=>e.stage==='register')) {
    assert.deepEqual(Object.keys(event).sort(),['method','ms','reason','stage','status']);assert.equal(event.reason,'accepted');
  }
});

test('public Google, ChatGPT and Claude keep refresh rotation and replay revocation',async t=>{
  const f=await fixture(t);
  for(const uris of [redirects,[CALLBACK],[CLAUDE_CALLBACK]]) {
    const client=(await f.register(uris,'none')).json(), a=await f.authorize(client,uris[0]);
    const page=await f.login(a), first=(await f.exchange(client,a,returned(page.text).searchParams.get('code'))).json();
    const r=await f.refresh(client,first.refresh_token);assert.equal(r.status,200,r.text);
    const second=r.json();assert.notEqual(second.refresh_token,first.refresh_token);
    assert.equal((await f.refresh(client,first.refresh_token)).status,400);
    await assert.rejects(f.provider.verifyAccessToken(second.access_token));
  }
});

test('Google login keeps CSRF, same-origin approval and password checks',async t=>{
  const f=await fixture(t), client=(await f.register()).json(), a=await f.authorize(client);
  const before=f.requests;
  assert.equal((await f.login(a,{csrf:'wrong'})).status,403);
  assert.equal((await f.login(a,{}, {Origin:'https://gemini.google.com'})).status,403);
  assert.equal(f.requests,before);
  const deny=await f.login(a,{decision:'deny'});assert.equal(returned(deny.text).searchParams.get('error'),'access_denied');
  assert.equal(f.requests,before);
  const wrong=await f.authorize(client);assert.equal((await f.login(wrong,{password:'wrong'})).status,401);
  const weak=await f.authorize(client,redirects[0],opaqueState,{code_challenge_method:'plain'});
  assert.equal(weak.status,302);assert.equal(new URL(weak.headers.location).searchParams.get('error'),'invalid_request');
});

test('registration storage and capacity errors are bounded, retryable and secret-free',async t=>{
  const f=await fixture(t), file=f.provider.clientsStore.file;
  f.provider.clientsStore.file=join(f.directory,'missing','clients.json');
  assert.equal((await f.register()).status,503);assert.equal(f.provider.clientsStore.clients.size,0);
  f.provider.clientsStore.file=file;
  const accepted=await f.register();assert.equal(accepted.status,201);
  for(let i=1;i<128;i++)f.provider.clientsStore.clients.set('fixture-'+i,{});
  assert.equal((await f.register()).status,503);
  assert(f.events.some(e=>e.reason==='storage_unavailable'));
  assert(f.events.some(e=>e.reason==='capacity_reached'));
  assert(f.events.every(e=>Object.values(e).every(v=>typeof v!=='string'||!v.includes('http'))));
});

test('authorization storage failure preserves the previous snapshot and allows retry',async t=>{
  const f=await fixture(t), client=(await f.register()).json();
  const write=f.provider.state.write;
  f.provider.state.write=()=>{throw Error('fixture disk full');};
  const failed=await f.authorize(client);assert.equal(failed.status,302);assert.equal(f.provider.pending.size,0);
  f.provider.state.write=write;
  const a=await f.authorize(client);assert.equal(a.status,200);
  f.provider.state.write=()=>{throw Error('fixture disk full');};
  assert.equal((await f.login(a)).status,503);
  f.provider.state.write=write;
  const page=await f.login(a);assert.equal(page.status,200);
  const code=returned(page.text).searchParams.get('code');
  f.provider.state.write=()=>{throw Error('fixture disk full');};
  assert.equal((await f.exchange(client,a,code)).status,503);
  f.provider.state.write=write;
  assert.equal((await f.exchange(client,a,code)).status,200);
});

test('LAN HTTPS keeps Google cloud callbacks disabled',async t=>{
  const f=await fixture(t,'lan');
  for(const uris of [[redirects[0]],redirects])assert.equal((await f.register(uris)).status,400);
  assert.equal(f.provider.clientsStore.clients.size,0);
});




test('form POST carries worst-case encoded state and snapshot size remains bounded',async t=>{
  const f=await fixture(t), client=(await f.register()).json();
  const a=await f.authorize(client,redirects[0],'%'.repeat(8192));
  assert.equal((await f.form('/authorize',a.params)).status,200);
  const state=new AuthState(f.directory,f.config.proxyKey,f.config.publicUrl+'/');
  const previous=readFileSync(state.file);
  assert.throws(()=>state.write({oversized:'x'.repeat(2*1024*1024)}),/capacity/);
  assert.deepEqual(readFileSync(state.file),previous);
});
