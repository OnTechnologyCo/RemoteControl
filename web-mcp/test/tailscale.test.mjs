import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {once} from 'node:events';
import {request} from 'node:http';
import {randomBytes,createHash} from 'node:crypto';
import {createTailscaleServer,tailscaleOrigin} from '../tailscale-server.mjs';
import {CALLBACK,CLAUDE_CALLBACK} from '../gateway.mjs';

for(const publicPort of [443,8443,10000]) {
test('Tailscale '+publicPort+' follows OAuth identity, expires safely, and supports both clients',async t=>{
 const root=resolve('../artifacts/tailscale-mcp-test');mkdirSync(root,{recursive:true});const directory=mkdtempSync(join(root,'gateway-'));
 let clock=Date.now(),active=null;
 const config={listenPort:59335,lanPort:59331,publicPort,proxyKey:randomBytes(32).toString('base64url')};
 const suffix=publicPort===443?'':':'+publicPort,authority='pc.fixture.ts.net'+suffix;
 const runtime=createTailscaleServer(config,{directory,now:()=>clock,readActive:()=>active,gatewayOptions:{upstream:async path=>path==='/login'?{status:200,headers:{'set-cookie':['rcmcpid=fixture; Path=/']}}:{status:200,body:'{"jsonrpc":"2.0","id":1,"result":{"hostname":"fixture-only"}}'}}});
 runtime.server.listen(0,'127.0.0.1');await once(runtime.server,'listening');t.after(()=>runtime.close());
 const send=(path,data,extra={})=>new Promise((resolve,reject)=>{
  const body=data===undefined?undefined:typeof data==='string'?data:JSON.stringify(data);
  const r=request({hostname:'127.0.0.1',port:runtime.server.address().port,path,method:body===undefined?'GET':'POST',headers:{Host:authority,'X-RC-Proxy-Key':config.proxyKey,'X-RC-Client-IP':'198.51.100.2','X-Forwarded-Proto':'https','Content-Type':'application/json',...extra,...(body?{'Content-Length':Buffer.byteLength(body)}:{})}},res=>{
   let text='';res.on('data',b=>text+=b);res.on('end',()=>resolve({status:res.statusCode,headers:new Headers(res.headers),text}));
  });r.on('error',reject);r.end(body);
 });
 const form=(path,body,headers)=>send(path,new URLSearchParams(body).toString(),{'Content-Type':'application/x-www-form-urlencoded',...headers});
 assert.equal((await send('/.well-known/oauth-protected-resource')).status,503);
 const origin='https://'+authority;active={publicUrl:origin,lanPort:59331,expiresAt:clock+40000};
 assert.equal(JSON.parse((await send('/.well-known/oauth-protected-resource')).text).resource,origin+'/mcp');
 assert.equal((await send('/.well-known/oauth-protected-resource',undefined,{'X-RC-Proxy-Key':'forged'})).status,403);
 const tokens=[];
 for(const callback of [CALLBACK,CLAUDE_CALLBACK]){
  const reg=await send('/register',{redirect_uris:[callback],token_endpoint_auth_method:'none'});assert.equal(reg.status,201,reg.text);const client=JSON.parse(reg.text);
  const verifier=randomBytes(32).toString('base64url');
  const auth=await send('/authorize?'+new URLSearchParams({client_id:client.client_id,redirect_uri:callback,response_type:'code',code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource:origin+'/mcp',state:'fixture'}));assert.equal(auth.status,200,auth.text);
  const csrf=/name="csrf" value="([^"]+)"/.exec(auth.text)[1];
  const login=await form('/connect',{csrf,user:'fixture',password:'fixture',decision:'allow'},{Origin:origin,Cookie:auth.headers.get('set-cookie').split(';')[0]});assert.equal(login.status,200,login.text);
  const returned=new URL(/id="return-client" href="([^"]+)"/.exec(login.text)[1].replaceAll('&amp;','&'));
  const token=await form('/token',{grant_type:'authorization_code',client_id:client.client_id,code:returned.searchParams.get('code'),code_verifier:verifier,redirect_uri:callback,resource:origin+'/mcp'});assert.equal(token.status,200,token.text);
  const bearer=JSON.parse(token.text).access_token;tokens.push(bearer);
  const status=await send('/mcp',{jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'pc_get_status',arguments:{}}},{Authorization:'Bearer '+bearer});assert.equal(status.status,200,status.text);assert(status.text.includes('fixture-only'));
 }
 active={...active,publicUrl:'https://renamed.fixture.ts.net'+suffix};
 assert.equal((await send('/mcp',{jsonrpc:'2.0',id:1,method:'tools/list'},{Host:'renamed.fixture.ts.net'+suffix,Authorization:'Bearer '+tokens[0]})).status,401);
 assert.equal(JSON.parse((await send('/.well-known/oauth-protected-resource',undefined,{Host:'renamed.fixture.ts.net'+suffix})).text).resource,active.publicUrl+'/mcp');
 clock+=45000;assert.equal((await send('/.well-known/oauth-protected-resource')).status,503);
 for(const bad of ['https://evil.test'+suffix,'https://pc.fixture.ts.net:59334',origin+'/path','https://user:pw@'+authority])assert.throws(()=>tailscaleOrigin(bad,publicPort));
 assert.equal(tailscaleOrigin('https://pc.fixture.ts.net:'+publicPort,publicPort),origin);
});
}
