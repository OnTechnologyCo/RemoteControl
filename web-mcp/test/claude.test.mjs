import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {createServer,request} from 'node:http';
import {once} from 'node:events';
import {randomBytes,createHash} from 'node:crypto';
import {createGateway,CALLBACK,CIMD,CLAUDE_CALLBACK,CLAUDE_CIMD} from '../gateway.mjs';

test('Claude registration, isolated client identities, PKCE and expired LAN session',async t=>{
  const root=resolve('../artifacts/web-mcp-test');mkdirSync(root,{recursive:true});
  const directory=mkdtempSync(join(root,'claude-')),fetched=[];
  const config={publicUrl:'https://remote.example.test:59334',lanPort:59331,listenPort:59332,proxyKey:randomBytes(32).toString('base64url')};
  let expired=false;
  const metadata=async id=>{fetched.push(id);return {client_id:id,redirect_uris:[id===CIMD?CALLBACK:CLAUDE_CALLBACK],token_endpoint_auth_method:'none'};};
  const gateway=createGateway(config,{directory,metadata,upstream:async path=>path==='/login'?{status:200,headers:{'set-cookie':['rcmcpid=fixture; Path=/']}}:{status:expired?401:200,body:'{"jsonrpc":"2.0","id":1,"result":{}}'}});
  const server=createServer(gateway.app);server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
  const send=(path,body,extra={})=>new Promise((resolve,reject)=>{
    const data=body===undefined?undefined:typeof body==='string'?body:JSON.stringify(body);
    const r=request({hostname:'127.0.0.1',port:server.address().port,path,method:data===undefined?'GET':'POST',headers:{Host:'remote.example.test:59334','X-RC-Proxy-Key':config.proxyKey,'X-RC-Client-IP':'198.51.100.2','X-Forwarded-Proto':'https','Content-Type':'application/json',...extra,...(data?{'Content-Length':Buffer.byteLength(data)}:{})}},res=>{
      let text='';res.on('data',b=>text+=b);res.on('end',()=>resolve({status:res.statusCode,text,headers:res.headers}));
    });r.on('error',reject);r.end(data);
  });
  const form=(path,data,extra={})=>send(path,new URLSearchParams(data).toString(),{'Content-Type':'application/x-www-form-urlencoded',...extra});
  const store=gateway.provider.clientsStore;
  await Promise.all([store.getClient(CIMD),store.getClient(CIMD),store.getClient(CLAUDE_CIMD),store.getClient(CLAUDE_CIMD)]);
  assert.deepEqual(fetched.sort(),[CIMD,CLAUDE_CIMD].sort());
  assert.equal((await store.getClient(CLAUDE_CIMD)).client_name,'Claude');
  assert.equal((await store.getClient(CIMD)).client_name,'ChatGPT');
  assert.equal(await store.getClient('http://127.0.0.1/private'),undefined);
  for(const uri of [CLAUDE_CALLBACK+'?next=evil',CLAUDE_CALLBACK+'/',CLAUDE_CALLBACK.replace('claude.ai','claude.ai.attacker.test')]){
    assert.equal((await send('/register',{redirect_uris:[uri],token_endpoint_auth_method:'none'})).status,400);
  }
  assert.equal((await send('/register',{redirect_uris:[CALLBACK,CLAUDE_CALLBACK],token_endpoint_auth_method:'none'})).status,400);
  const registration=await send('/register',{redirect_uris:[CLAUDE_CALLBACK],client_name:'Untrusted label',token_endpoint_auth_method:'none',grant_types:['authorization_code','refresh_token'],response_types:['code']},{Origin:'https://claude.ai'});
  assert.equal(registration.status,201);const client=JSON.parse(registration.text);assert.equal(client.client_name,'Claude');
  const reopened=createGateway(config,{directory,metadata});
  assert((await reopened.provider.clientsStore.getClient(client.client_id)).redirect_uris.includes(CLAUDE_CALLBACK));reopened.close();
  // DCR and the published Claude identity both use the same real form/token flow.
  for(const clientId of [client.client_id,CLAUDE_CIMD]){
    const verifier=randomBytes(32).toString('base64url');
    const auth=await send('/authorize?'+new URLSearchParams({client_id:clientId,redirect_uri:CLAUDE_CALLBACK,response_type:'code',state:'test-state',code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',resource:config.publicUrl+'/mcp',scope:'remotecontrol'}));
    assert.equal(auth.status,200,auth.text);assert(auth.text.includes('Claude에 원격제어 연결'));assert(!auth.text.includes('ChatGPT에 원격제어 연결'));
    const csrf=/name="csrf" value="([^"]+)"/.exec(auth.text)[1],cookie=auth.headers['set-cookie'][0].split(';')[0];
    const login=await form('/connect',{csrf,user:'fixture',password:'fixture-only',decision:'allow'},{Cookie:cookie,Origin:config.publicUrl});
    assert.equal(login.status,200);assert(login.text.includes('Claude로 돌아가기'));
    const callback=new URL(/id="return-client" href="([^"]+)"/.exec(login.text)[1].replaceAll('&amp;','&'));
    const tokenBody={grant_type:'authorization_code',client_id:clientId,code:callback.searchParams.get('code'),code_verifier:verifier,redirect_uri:CLAUDE_CALLBACK,resource:config.publicUrl+'/mcp'};
    assert.equal((await form('/token',{...tokenBody,client_id:CIMD})).status,400);
    assert.equal((await form('/token',{...tokenBody,redirect_uri:CALLBACK})).status,400);
    assert.equal((await form('/token',{...tokenBody,code_verifier:'wrong'})).status,400);
    const token=await form('/token',tokenBody);assert.equal(token.status,200,token.text);
    const tokens=JSON.parse(token.text);
    const refreshBody={grant_type:'refresh_token',client_id:clientId,refresh_token:tokens.refresh_token,resource:config.publicUrl+'/mcp'};
    assert.equal((await form('/token',{...refreshBody,client_id:CIMD})).status,400);
    const refreshed=await form('/token',refreshBody);assert.equal(refreshed.status,200,refreshed.text);
    const newTokens=JSON.parse(refreshed.text);assert.notEqual(newTokens.refresh_token,tokens.refresh_token);
    expired=true;
    const result=await send('/mcp',{jsonrpc:'2.0',id:1,method:'tools/call',params:{name:'pc_get_status',arguments:{}}},{Authorization:'Bearer '+newTokens.access_token});
    assert.equal(result.status,401);assert(result.headers['www-authenticate'].includes('resource_metadata'));
    expired=false;
  }
});
