import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,readFileSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {createServer,request} from 'node:http';
import {once} from 'node:events';
import {createHash,randomBytes} from 'node:crypto';
import {createGateway,validateConfig} from '../gateway.mjs';

const primary='https://192.168.195.141', second='https://10.20.30.40', third='https://172.22.1.10:8443';
const callback='http://127.0.0.1:34567/callback';
const baseConfig=()=>({mode:'lan',publicUrl:primary,listenPort:59338,lanPort:5901,proxyKey:randomBytes(32).toString('base64url'),allowedNetworks:'192.168.195.0/24,10.20.30.0/24,172.22.1.0/24'});
const returned=text=>new URL(/id="return-client" href="([^"]+)"/.exec(text)[1].replaceAll('&amp;','&'));

async function fixture(t,config=baseConfig()) {
  const root=resolve('../artifacts/lan-multi-test');mkdirSync(root,{recursive:true});
  const directory=mkdtempSync(join(root,'case-')), sessions=new Set(), calls=[], events=[];
  const options={directory,audit:event=>events.push(event),metadata:async()=>{throw Error('LAN must not fetch client metadata');},upstream:async(path,data={})=>{
    calls.push(path);
    if(path==='/login') {
      const value=JSON.parse(data.body);
      if(value.user!=='fixture'||value.password!=='fixture-only')return {status:401,headers:{},body:'{}'};
      const cookie='rcmcpid='+randomBytes(32).toString('base64url');sessions.add(cookie);
      return {status:200,headers:{'set-cookie':[cookie+'; HttpOnly; Path=/']},body:'{}'};
    }
    if(path==='/logout'){sessions.delete(data.cookie);return {status:200,headers:{},body:'{}'};}
    if(!sessions.has(data.cookie))return {status:401,headers:{},body:'{}'};
    const rpc=JSON.parse(data.body), result=rpc.method==='tools/call'
      ? {content:[{type:'text',text:JSON.stringify({hostname:'LAN-MULTI-FIXTURE',version:'test',allowed_roots:['C:\\fixture']})}]}
      : {};
    return {status:200,headers:{},body:JSON.stringify({jsonrpc:'2.0',id:rpc.id,result})};
  }};
  let gateway=createGateway(config,options);
  const server=createServer((req,res)=>gateway.app(req,res));server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
  const send=(origin,path,{method='GET',body,headers={}}={})=>new Promise((resolve,reject)=>{
    const req=request({hostname:'127.0.0.1',port:server.address().port,path,method,headers:{Host:new URL(origin).host,'X-RC-Proxy-Key':config.proxyKey,'X-RC-Client-IP':'192.168.195.5','X-Forwarded-Proto':'https',...headers,...(body===undefined?{}:{'Content-Length':Buffer.byteLength(body)})}},res=>{
      const chunks=[];res.on('data',c=>chunks.push(c));res.on('error',reject);res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,text:Buffer.concat(chunks).toString('utf8')}));
    });req.on('error',reject);req.end(body);
  });
  const form=(origin,path,data,headers={})=>send(origin,path,{method:'POST',body:new URLSearchParams(data).toString(),headers:{'Content-Type':'application/x-www-form-urlencoded',...headers}});
  const json=(origin,path,data,headers={})=>send(origin,path,{method:'POST',body:JSON.stringify(data),headers:{'Content-Type':'application/json',...headers}});
  const register=async origin=>{
    const response=await json(origin,'/register',{redirect_uris:[callback],token_endpoint_auth_method:'none',grant_types:['authorization_code','refresh_token'],response_types:['code']});
    assert.equal(response.status,201,response.text);return JSON.parse(response.text);
  };
  const authorize=async(origin,client,changes={})=>{
    const verifier=randomBytes(32).toString('base64url');
    const response=await send(origin,'/authorize?'+new URLSearchParams({client_id:client.client_id,redirect_uri:callback,response_type:'code',state:'multi-ip-fixture',code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource:origin+'/mcp',...changes}));
    return {...response,verifier,cookie:response.headers['set-cookie']?.[0].split(';')[0],csrf:/name="csrf" value="([A-Za-z0-9_-]+)"/.exec(response.text)?.[1]};
  };
  const login=async(origin,auth)=>{
    const response=await form(origin,'/connect',{csrf:auth.csrf,user:'fixture',password:'fixture-only',decision:'allow'},{Origin:origin,Cookie:auth.cookie});
    assert.equal(response.status,200,response.text);const target=returned(response.text);
    assert.equal(target.searchParams.get('iss'),origin+'/');assert.equal(target.searchParams.get('state'),'multi-ip-fixture');return target.searchParams.get('code');
  };
  const exchange=(origin,client,code,verifier,changes={})=>form(origin,'/token',{grant_type:'authorization_code',client_id:client.client_id,code,code_verifier:verifier,redirect_uri:callback,resource:origin+'/mcp',...changes});
  const authenticate=async origin=>{
    const client=await register(origin),auth=await authorize(origin,client);assert.equal(auth.status,200,auth.text);
    const code=await login(origin,auth),response=await exchange(origin,client,code,auth.verifier);assert.equal(response.status,200,response.text);
    return {client,tokens:JSON.parse(response.text)};
  };
  const ping=(origin,tokens)=>json(origin,'/mcp',{jsonrpc:'2.0',id:1,method:'ping'},{Authorization:'Bearer '+tokens.access_token});
  return {directory,calls,events,send,form,json,register,authorize,login,exchange,authenticate,ping,get gateway(){return gateway;},restart(next){gateway.close();gateway=createGateway(next,options);}};
}

test('LAN public URLs retain the primary, normalize explicit addresses and reject invalid origins',()=>{
  const config=baseConfig();
  assert.deepEqual(validateConfig(config).publicUrls,[primary]);
  assert.deepEqual(validateConfig({...config,publicUrls:[]}).publicUrls,[primary]);
  assert.deepEqual(validateConfig({...config,publicUrls:[second,primary+':443/',second,third]}).publicUrls,[primary,second,third]);
  for(const publicUrls of [null,second,{},Array(33).fill(second),['https://example.test'],['http://10.20.30.40'],[second+'/mcp'],[second+'?x=1'],[second+'#fragment'],['https://user@10.20.30.40'],['https://[::1]'],[42]])
    assert.throws(()=>validateConfig({...config,publicUrls}),JSON.stringify(publicUrls));
  // LAN 목록은 외부 DDNS/Tailscale/Private 모드의 공개 주소로 확장하지 않는다.
  const external=validateConfig({...config,mode:'ddns',publicUrl:'https://remote.example.test',publicUrls:[second]});
  assert.equal(external.publicUrl,'https://remote.example.test');
});

test('LAN multiple IPs isolate OAuth and transfers and preserve sessions across reconfiguration',{timeout:30000},async t=>{
  const config=baseConfig(),f=await fixture(t,config);let first,other;
  await t.test('the existing single-IP registration and encrypted token file remain usable',async()=>{
    first=await f.authenticate(primary);
    assert.equal(f.gateway.provider.state.file,join(f.directory,'auth-state.bin'));
    assert.equal((await f.ping(primary,first.tokens)).status,200);
    const sealed=readFileSync(join(f.directory,'auth-state.bin'));
    assert(!sealed.includes(Buffer.from(first.tokens.access_token)));
    assert(!sealed.includes(Buffer.from('fixture-only')));
  });
  await t.test('each explicit IP advertises its own issuer, resource and OAuth endpoints',async()=>{
    f.restart({...config,publicUrls:[primary,second,third]});
    assert.equal((await f.ping(primary,first.tokens)).status,200);
    assert.equal(f.gateway.provider.resource,primary+'/mcp');
    for(const origin of [primary,second,third]) {
      const resource=await f.send(origin,'/.well-known/oauth-protected-resource/mcp');assert.equal(resource.status,200,resource.text);
      assert.deepEqual(JSON.parse(resource.text).authorization_servers,[origin+'/']);assert.equal(JSON.parse(resource.text).resource,origin+'/mcp');
      const response=await f.send(origin,'/.well-known/oauth-authorization-server'),auth=JSON.parse(response.text);
      assert.equal(response.status,200,response.text);assert.equal(auth.issuer,origin+'/');assert.equal(auth.client_id_metadata_document_supported,false);
      for(const [key,path] of [['authorization_endpoint','/authorize'],['token_endpoint','/token'],['registration_endpoint','/register'],['revocation_endpoint','/revoke']])assert.equal(auth[key],origin+path);
      assert.deepEqual(auth.code_challenge_methods_supported,['S256']);
      const denied=await f.ping(origin,{access_token:randomBytes(32).toString('base64url')});assert.equal(denied.status,401);assert(denied.headers['www-authenticate'].includes(origin+'/.well-known/oauth-protected-resource'));
    }
  });
  await t.test('unknown Host, invalid proxy, client networks and cross-IP Origin are rejected before upstream access',async()=>{
    const before=f.calls.length;
    for(const origin of [primary,second,third]) {
      for(const headers of [{Host:'192.168.195.142'},{Host:new URL(origin).host+'.evil.test'},{'X-RC-Proxy-Key':'forged'},{'X-Forwarded-Proto':'http'},{'X-Forwarded-Proto':'https,http'},{'X-RC-Client-IP':'203.0.113.5'},{'X-RC-Client-IP':'192.168.195.5, 203.0.113.5'},{Origin:'null'},{Origin:origin===primary?second:primary}])
        assert.equal((await f.send(origin,'/',{headers})).status,403,JSON.stringify(headers));
      const good=await f.send(origin,'/.well-known/oauth-protected-resource',{headers:{'X-Forwarded-Host':'attacker.test','X-RC-Client-IP':'::ffff:192.168.195.5'}});
      assert.equal(good.status,200,good.text);assert.equal(JSON.parse(good.text).resource,origin+'/mcp');
    }
    assert.equal(f.calls.length,before);
  });
  await t.test('DCR, pending cookies, authorization codes, PKCE, tokens and revocation stay bound to one IP',async()=>{
    assert.equal((await f.authorize(second,first.client)).status,400);
    const client=await f.register(second);
    for(const bad of ['https://claude.ai/api/mcp/auth_callback','http://10.20.30.55:34567/callback','http://127.0.0.1:34567/callback?next=evil'])
      assert.equal((await f.json(second,'/register',{redirect_uris:[bad],token_endpoint_auth_method:'none'})).status,400);
    const wrongResource=await f.authorize(second,client,{resource:primary+'/mcp'});assert.equal(wrongResource.status,302);
    assert.equal(new URL(wrongResource.headers.location).searchParams.get('error'),'invalid_target');assert.equal(new URL(wrongResource.headers.location).searchParams.get('iss'),second+'/');
    const auth=await f.authorize(second,client);assert.equal(auth.status,200,auth.text);assert(auth.text.includes(second));
    const body={csrf:auth.csrf,user:'fixture',password:'fixture-only',decision:'allow'};
    assert.equal((await f.form(primary,'/connect',body,{Origin:primary,Cookie:auth.cookie})).status,403);
    assert.equal((await f.form(second,'/connect',body,{Origin:primary,Cookie:auth.cookie})).status,403);
    const code=await f.login(second,auth);
    assert.equal((await f.exchange(primary,first.client,code,auth.verifier)).status,400);
    assert.equal((await f.exchange(second,client,code,auth.verifier,{resource:primary+'/mcp'})).status,400);
    assert.equal((await f.exchange(second,client,code,randomBytes(32).toString('base64url'))).status,400);
    const response=await f.exchange(second,client,code,auth.verifier);assert.equal(response.status,200,response.text);other={client,tokens:JSON.parse(response.text)};
    assert.equal((await f.exchange(second,client,code,auth.verifier)).status,400);
    assert.equal((await f.ping(second,first.tokens)).status,401);assert.equal((await f.ping(primary,other.tokens)).status,401);
    assert.equal((await f.form(second,'/token',{grant_type:'refresh_token',client_id:client.client_id,refresh_token:first.tokens.refresh_token,resource:second+'/mcp'})).status,400);
    assert.equal((await f.form(second,'/revoke',{client_id:client.client_id,token:first.tokens.refresh_token,token_type_hint:'refresh_token'})).status,200);
    assert.equal((await f.ping(primary,first.tokens)).status,200);assert.equal((await f.ping(second,other.tokens)).status,200);
  });
  await t.test('transfer links, browser cookies, redirects and CSRF use the selected IP only',async()=>{
    for(const [origin,session] of [[primary,first],[second,other]]) {
      const opened=await f.json(origin,'/mcp',{jsonrpc:'2.0',id:2,method:'tools/call',params:{name:'pc_open_file_transfer',arguments:{}}},{Authorization:'Bearer '+session.tokens.access_token});assert.equal(opened.status,200,opened.text);
      const link=new URL(JSON.parse(JSON.parse(opened.text).result.content[0].text).url);assert.equal(link.origin,origin);
      const foreign=origin===primary?second:primary;
      assert.equal((await f.send(foreign,link.pathname+link.search)).status,401);
      const portal=await f.send(origin,link.pathname+link.search);assert.equal(portal.status,303);assert.equal(portal.headers.location,origin+'/files');
      const cookie=portal.headers['set-cookie'][0].split(';')[0];assert(!portal.headers['set-cookie'][0].includes('Domain='));
      assert.equal((await f.send(foreign,'/files',{headers:{Cookie:cookie}})).status,401);
      const page=await f.send(origin,'/files',{headers:{Cookie:cookie}});assert.equal(page.status,200,page.text);assert(page.text.includes('LAN-MULTI-FIXTURE'));
      const csrf=/<meta name="csrf" content="([A-Za-z0-9_-]+)"/.exec(page.text)[1],headers={Cookie:cookie,'X-RC-Files-CSRF':csrf};
      assert.equal((await f.json(origin,'/files/api',{name:'pc_get_status',arguments:{}},{...headers,Origin:foreign})).status,403);
      assert.equal((await f.json(origin,'/files/api',{name:'pc_get_status',arguments:{}},{...headers,Origin:origin})).status,200);
    }
  });
  await t.test('adding, removing, reordering and changing the primary preserve tokens for each retained IP',async()=>{
    const initialFile=f.gateway.providers.get(primary).state.file,secondaryFile=f.gateway.providers.get(second).state.file;
    assert.notEqual(initialFile,secondaryFile);
    f.restart({...config,publicUrl:third,publicUrls:[third,second,primary]});
    assert.equal(f.gateway.provider.resource,third+'/mcp');
    assert.equal(f.gateway.providers.get(primary).state.file,initialFile);assert.equal(f.gateway.providers.get(second).state.file,secondaryFile);
    assert.equal((await f.ping(primary,first.tokens)).status,200);assert.equal((await f.ping(second,other.tokens)).status,200);
    f.restart({...config,publicUrl:third,publicUrls:[third,second]});
    assert.equal((await f.ping(primary,first.tokens)).status,403);assert.equal((await f.ping(second,other.tokens)).status,200);
    f.restart({...config,publicUrl:second,publicUrls:[second,primary,third]});
    assert.equal(f.gateway.providers.get(primary).state.file,initialFile);assert.equal(f.gateway.provider.state.file,secondaryFile);
    assert.equal((await f.ping(primary,first.tokens)).status,200);assert.equal((await f.ping(second,other.tokens)).status,200);
    const rotated=await f.form(second,'/token',{grant_type:'refresh_token',client_id:other.client.client_id,refresh_token:other.tokens.refresh_token,resource:second+'/mcp'});assert.equal(rotated.status,200,rotated.text);other.tokens=JSON.parse(rotated.text);
    assert.equal((await f.ping(second,other.tokens)).status,200);
    assert.equal((await f.form(second,'/revoke',{client_id:other.client.client_id,token:other.tokens.refresh_token,token_type_hint:'refresh_token'})).status,200);
    assert.equal((await f.ping(second,other.tokens)).status,401);assert.equal((await f.ping(primary,first.tokens)).status,200);
    f.restart(config);assert.equal((await f.ping(primary,first.tokens)).status,200);assert.equal((await f.ping(second,other.tokens)).status,403);
    for(const event of f.events)assert.deepEqual(Object.keys(event).sort(),['method','ms','stage','status']);
    assert(!JSON.stringify(f.events).includes(first.tokens.access_token));assert(!JSON.stringify(f.events).includes('fixture-only'));
  });
});

test('adding LAN IPs does not multiply global request or login limits',async t=>{
  const config={...baseConfig(),publicUrls:[primary,second]},f=await fixture(t,config);
  const first=await f.send(primary,'/'),next=await f.send(second,'/');
  assert.equal(Number(next.headers['ratelimit-remaining']),Number(first.headers['ratelimit-remaining'])-1);
  for(let i=0;i<11;i++) {
    const origin=i%2?primary:second,response=await f.form(origin,'/connect',{}, {Origin:origin});
    assert.equal(response.status,i<10?403:429,response.text);
  }
  assert.equal(f.calls.length,0);
});

test('LAN address lists never enable IP hosts on the external gateway',async t=>{
  const config={...baseConfig(),mode:'ddns',publicUrl:'https://remote.example.test',publicUrls:[second]},f=await fixture(t,config);
  assert.equal((await f.send(second,'/')).status,403);
  const response=await f.send(config.publicUrl,'/.well-known/oauth-protected-resource');assert.equal(response.status,200,response.text);assert.equal(JSON.parse(response.text).resource,config.publicUrl+'/mcp');
});
