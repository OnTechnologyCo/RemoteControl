import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync, mkdirSync, readFileSync} from 'node:fs';
import {join, resolve} from 'node:path';
import {createServer, request} from 'node:http';
import {once} from 'node:events';
import {gzipSync, deflateSync, brotliCompressSync} from 'node:zlib';
import {randomBytes, createHash} from 'node:crypto';
import {createGateway, callbackName, CALLBACK, CIMD, CLAUDE_CALLBACK, CLAUDE_CIMD} from '../gateway.mjs';

// 운영 포트/계정/인증서와 분리한 HTTP 중계 경계에서 실제 OAuth 라우터를 검사한다.
async function fixture(t, lan) {
  const root=resolve('../artifacts/web-mcp-test'); mkdirSync(root,{recursive:true});
  const directory=mkdtempSync(join(root,'native-oauth-'));
  const config={publicUrl:lan?'https://10.20.30.40':'https://remote.example.test',
    listenPort:59432,lanPort:59431,proxyKey:randomBytes(32).toString('base64url'),
    ...(lan?{mode:'lan',allowedNetworks:'10.20.30.0/24'}:{})};
  const calls=[],fetched=[];
  const options={directory,metadata:async id=>{fetched.push(id);throw Error('Native clients must not fetch metadata');},
    upstream:async(path,data)=>{calls.push(path);return path==='/login'
      ?{status:200,headers:{'set-cookie':['rcmcpid=fixture; Path=/']}}
      :{status:200,headers:{},body:'{"jsonrpc":"2.0","id":1,"result":{}}'};}};
  let gateway=createGateway(config,options);
  const server=createServer((req,res)=>gateway.app(req,res));server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
  const send=(path,body,extra={})=>new Promise((resolve,reject)=>{
    const data=body===undefined?undefined:typeof body==='string'||Buffer.isBuffer(body)?body:JSON.stringify(body);
    const req=request({hostname:'127.0.0.1',port:server.address().port,path,method:data===undefined?'GET':'POST',
      headers:{Host:new URL(config.publicUrl).host,'X-RC-Proxy-Key':config.proxyKey,'X-RC-Client-IP':lan?'10.20.30.55':'198.51.100.2',
        'X-Forwarded-Proto':'https','Content-Type':'application/json',...extra,...(data?{'Content-Length':Buffer.byteLength(data)}:{})}},res=>{
      let text='';res.on('data',b=>text+=b);res.on('error',reject);res.on('end',()=>resolve({status:res.statusCode,text,headers:res.headers}));
    });req.setTimeout(5000,()=>req.destroy(Error('fixture timeout')));req.on('error',reject);req.end(data);
  });
  return {config,directory,calls,fetched,send,provider:()=>gateway.provider,
    restart:()=>{gateway.close();gateway=createGateway(config,options);},
    form:(path,data,extra={})=>send(path,new URLSearchParams(data).toString(),{'Content-Type':'application/x-www-form-urlencoded',...extra})};
}
function returned(r){assert.equal(r.status,200,r.text);return new URL(/id="return-client" href="([^"]+)"/.exec(r.text)[1].replaceAll('&amp;','&'));}

test('callback policy: web plus strict native loopback; LAN remains offline-only',()=>{
  for(const lan of [false,true]){
    for(const host of ['localhost','127.0.0.1','[::1]'])for(const port of [1024,33418,48123,65535]){
      assert.equal(callbackName(`http://${host}:${port}/callback`,lan),'로컬 MCP 클라이언트');
      assert.equal(callbackName(`http://${host}:${port}/oauth/callback`,lan),'로컬 MCP 클라이언트');
    }
    for(const uri of [null,42,'https://attacker.test/callback','http://192.168.0.5:48123/callback',
      'http://0.0.0.0:48123/callback','http://localhost.attacker.test:48123/callback',
      'http://127.0.0.1.attacker.test:48123/callback','http://user@localhost:48123/callback',
      'http://localhost:48123@attacker.test/callback','http://localhost:48123/callback?next=evil',
      'http://localhost:48123/callback#fragment','http://localhost:80/callback','http://localhost:1023/callback',
      'http://localhost:65536/callback','http://localhost/callback','https://localhost:48123/callback',
      'http://2130706433:48123/callback','http://127.1:48123/callback','http://%6cocalhost:48123/callback',
      'http://localhost:48123/\\evil','http://localhost:48123/call\nback'])assert.equal(callbackName(uri,lan),undefined,String(uri));
  }
  for(const uri of [CALLBACK,CLAUDE_CALLBACK]){
    assert(callbackName(uri));assert.equal(callbackName(uri,true),undefined);
    assert.equal(callbackName(uri+'?next=evil'),undefined);
  }
});

for(const lan of [false,true])for(const host of ['localhost','127.0.0.1','[::1]']){
  test(`${lan?'LAN':'public'} native OAuth: ${host}, DCR, login, PKCE, binding, restart`,{timeout:20000},async t=>{
    const f=await fixture(t,lan), {send,form,config}=f;
    const registered=`http://${host}:48123/callback`,redirect=`http://${host}:49124/callback`,resource=config.publicUrl+'/mcp';
    const registration=await send('/register',{redirect_uris:[registered],client_name:'Claude (untrusted claimed name)',
      token_endpoint_auth_method:'none',grant_types:['authorization_code','refresh_token'],response_types:['code']});
    assert.equal(registration.status,201,registration.text);const client=JSON.parse(registration.text);
    assert.equal(client.client_name,'로컬 MCP 클라이언트');assert(!client.client_secret);
    assert.deepEqual(client.redirect_uris,[registered]);assert.equal(f.provider().access.size,0);
    const verifier=randomBytes(32).toString('base64url');
    const params={client_id:client.client_id,redirect_uri:redirect,response_type:'code',state:'native-test-state',
      code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource};
    const authorize=(extra={})=>send('/authorize?'+new URLSearchParams({...params,...extra}));
    assert.equal((await authorize({client_id:'unknown-client'})).status,400);
    for(const bad of [redirect.replace('/callback','/other'),redirect.replace(host,'localhost.attacker.test')]){
      const result=await authorize({redirect_uri:bad});assert.equal(result.status,400);assert(!result.headers.location);
    }
    for(const extra of [{code_challenge_method:'plain'},{resource:'https://attacker.test/mcp'}]){
      const r=await authorize(extra);assert.equal(r.status,302);const u=new URL(r.headers.location);
      assert(u.searchParams.has('error'));assert.equal(u.searchParams.get('iss'),config.publicUrl+'/');assert(!u.searchParams.has('code'));
    }
    let auth=await authorize();assert.equal(auth.status,200,auth.text);assert(auth.text.includes('로컬 MCP 클라이언트에 원격제어 연결'));
    const credentials=a=>({csrf:/name="csrf" value="([^"]+)"/.exec(a.text)[1],cookie:a.headers['set-cookie'][0].split(';')[0]});
    let a=credentials(auth);
    assert.equal((await form('/connect',{csrf:'wrong',user:'fixture',password:'fixture-only',decision:'allow'},
      {Cookie:a.cookie,Origin:config.publicUrl})).status,403);assert.equal(f.calls.length,0);
    const denied=returned(await form('/connect',{csrf:a.csrf,decision:'deny'},{Cookie:a.cookie,Origin:config.publicUrl}));
    assert.equal(denied.searchParams.get('error'),'access_denied');assert(!denied.searchParams.has('code'));assert.equal(f.calls.length,0);
    auth=await authorize();a=credentials(auth);
    const result=await form('/connect',{csrf:a.csrf,user:'fixture',password:'fixture-only',decision:'allow'},
      {Cookie:a.cookie,Origin:config.publicUrl});
    const u=returned(result);assert.equal(u.origin,new URL(redirect).origin);assert.equal(u.pathname,'/callback');
    assert.equal(u.searchParams.get('state'),'native-test-state');assert.equal(u.searchParams.get('iss'),config.publicUrl+'/');
    assert(result.headers['content-security-policy'].includes('script-src \'nonce-'));assert(!result.text.includes('fixture-only'));
    const tokenBody={grant_type:'authorization_code',client_id:client.client_id,code:u.searchParams.get('code'),
      code_verifier:verifier,redirect_uri:redirect,resource};
    for(const extra of [{code_verifier:randomBytes(32).toString('base64url')},{redirect_uri:registered},
      {client_id:'unknown-client'},{resource:'https://attacker.test/mcp'}])assert.equal((await form('/token',{...tokenBody,...extra})).status,400);
    const token=await form('/token',tokenBody);assert.equal(token.status,200,token.text);const tokens=JSON.parse(token.text);
    assert.equal((await form('/token',tokenBody)).status,400);
    assert.equal((await send('/mcp',{jsonrpc:'2.0',id:1,method:'ping'},{Authorization:'Bearer '+tokens.access_token})).status,200);
    const clientsBefore=readFileSync(join(f.directory,'clients.json'));
    f.restart();assert.deepEqual(readFileSync(join(f.directory,'clients.json')),clientsBefore);
    assert(await f.provider().clientsStore.getClient(client.client_id));assert.deepEqual(f.fetched,[]);
    const refreshed=await form('/token',{grant_type:'refresh_token',client_id:client.client_id,
      refresh_token:tokens.refresh_token,resource});assert.equal(refreshed.status,200,refreshed.text);
    assert.notEqual(JSON.parse(refreshed.text).refresh_token,tokens.refresh_token);
  });
}

test('public DCR rejects non-loopback / mixed callbacks and keeps official web identity scoped',async t=>{
  const f=await fixture(t,false);
  for(const redirects of [['http://localhost.attacker.test:48123/callback'],['http://192.168.0.5:48123/callback'],
    ['http://localhost:48123/callback?next=evil'],['http://localhost:48123/callback',CLAUDE_CALLBACK]]){
    assert.equal((await f.send('/register',{redirect_uris:redirects,token_endpoint_auth_method:'none'})).status,400);
  }
  assert.equal(await f.provider().clientsStore.getClient('http://localhost:48123/client.json'),undefined);
  assert.equal(await f.provider().clientsStore.getClient('https://attacker.test/client.json'),undefined);
  assert.deepEqual(f.fetched,[]);
});

test('OAuth compressed and malformed bodies are rejected without killing the isolated gateway',async t=>{
  const f=await fixture(t,false);
  for(const [encoding,compress] of [['gzip',gzipSync],['deflate',deflateSync],['br',brotliCompressSync]]){
    for(const path of ['/connect','/register','/token','/revoke','/authorize']){
      const form=path==='/connect';
      const data=Buffer.concat([compress(Buffer.from(form?'user=fixture':'{}')),Buffer.from('invalid-trailer')]);
      const result=await f.send(path,data,{'Content-Encoding':encoding,'Content-Type':form?'application/x-www-form-urlencoded':'application/json'});
      assert.equal(result.status,415,`${path} ${encoding}: ${result.text}`);
      assert.equal((await f.send('/.well-known/oauth-authorization-server')).status,200);
    }
  }
  const plain=await f.send('/register',{redirect_uris:['http://localhost:48123/callback'],token_endpoint_auth_method:'none',grant_types:['authorization_code','refresh_token'],response_types:['code']});
  assert.equal(plain.status,201,plain.text);
});
