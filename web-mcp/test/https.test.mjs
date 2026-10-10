import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { request } from 'node:https';
import { once } from 'node:events';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { createGateway } from '../gateway.mjs';

test('actual Caddy HTTPS proxy, isolated loopback certificate, no machine trust changes', {timeout:30000}, async t=>{
  const root=resolve('../artifacts/web-mcp-test');mkdirSync(root,{recursive:true});const work=mkdtempSync(join(root,'https-'));
  async function freePort(){const s=createServer();s.listen(0,'127.0.0.1');await once(s,'listening');const p=s.address().port;await new Promise(r=>s.close(r));return p;}
  const tlsPort=await freePort(),listenPort=await freePort(),key=randomBytes(32).toString('base64url');
  const production=readFileSync(resolve('WebMcpConfig.ps1'),'utf8').match(/function New-WebMcpCaddyConfig[\s\S]*?\$config=@"\r?\n([\s\S]*?)\r?\n"@/)[1]
    .replaceAll('$rootRedirect','')
    .replaceAll('$cert',join(work,'tls/server.crt').replaceAll('\\','/')).replaceAll('$key',join(work,'tls/server.key').replaceAll('\\','/'))
    .replaceAll('$log',join(work,'https.log').replaceAll('\\','/'))
    .replaceAll('$($Uri.GetLeftPart([UriPartial]::Authority))','https://remote.example.test:'+tlsPort)
    .replaceAll('$($Uri.Authority)','remote.example.test:'+tlsPort).replaceAll('$GatewayPort',String(listenPort)).replaceAll('$ProxyKey',key);
  const productionFile=join(work,'production.Caddyfile');writeFileSync(productionFile,production);
  const adapted=spawnSync(resolve('../artifacts/web-mcp-downloads/caddy-2.11.7/caddy.exe'),['adapt','--config',productionFile,'--adapter','caddyfile'],{windowsHide:true,encoding:'utf8'});
  assert.equal(adapted.status,0,adapted.stderr);const settings=JSON.parse(adapted.stdout);
  assert(!settings.apps.tls.automation);
  assert(Object.values(settings.apps.http.servers).every(s=>s.automatic_https.disable));
  assert(Object.values(settings.apps.http.servers).every(s=>JSON.stringify(s.protocols)===JSON.stringify(['h1','h2'])));
  assert.deepEqual(Object.values(settings.apps.http.servers).flatMap(s=>s.listen),[':'+tlsPort]);
  const config={publicUrl:'https://localhost:'+tlsPort,listenPort,lanPort:5901,proxyKey:key};
  const gateway=createGateway(config,{directory:work});const backend=createServer(gateway.app);backend.listen(listenPort,'127.0.0.1');await once(backend,'listening');
  t.after(()=>{gateway.close();backend.closeAllConnections();backend.close();});
  const file=join(work,'Caddyfile');
  writeFileSync(file,`{
admin off
auto_https disable_redirects
skip_install_trust
storage file_system {
root "${join(work,'certs').replaceAll('\\','/')}"
}
}
https://localhost:${tlsPort} {
bind 127.0.0.1
tls internal
request_body {
max_size 2MB
}
reverse_proxy 127.0.0.1:${listenPort} {
header_up Host localhost:${tlsPort}
header_up X-RC-Proxy-Key ${key}
header_up X-RC-Client-IP {remote_host}
header_up X-Forwarded-Proto https
header_up -X-Forwarded-For
header_up -Forwarded
transport http {
response_header_timeout 40s
}
}
}`);
  const caddy=spawn(resolve('../artifacts/web-mcp-downloads/caddy-2.11.7/caddy.exe'),['run','--config',file,'--adapter','caddyfile'],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  let log='';caddy.stderr.on('data',b=>log+=b);caddy.on('error',()=>{});t.after(()=>{caddy.kill();});
  function get(path,headers={}){return new Promise((resolve,reject)=>{
    // 자기서명 인증서 예외는 이 loopback 시험 요청에만 적용한다. 전역 TLS 설정은 바꾸지 않는다.
    const req=request({hostname:'127.0.0.1',port:tlsPort,servername:'localhost',path,rejectUnauthorized:false,headers:{Host:'localhost:'+tlsPort,...headers}},res=>{let body='';res.on('data',b=>body+=b);res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,body}));});req.on('error',reject);req.end();
  });}
  let response;
  for(let i=0;i<100;i++){try{response=await get('/.well-known/oauth-protected-resource');break;}catch{if(caddy.exitCode!==null)throw Error(log);await new Promise(r=>setTimeout(r,100));}}
  assert(response,log);assert.equal(response.status,200,response.body);assert.equal(JSON.parse(response.body).resource,config.publicUrl+'/mcp');
  const auth=await get('/.well-known/oauth-authorization-server',{'X-RC-Proxy-Key':'forged','X-Forwarded-For':'attacker','X-RC-Client-IP':'attacker','X-Forwarded-Proto':'http'});assert.equal(auth.status,200);assert.equal(JSON.parse(auth.body).issuer,config.publicUrl+'/');
  const protectedReply=await get('/mcp');assert.equal(protectedReply.status,401);assert(protectedReply.headers['www-authenticate']);
  writeFileSync(join(work,'result.json'),JSON.stringify({httpsMetadata:true,overwritesUntrustedProxyHeaders:true,unauthenticatedMcpRejected:true,systemTrustChanged:false},null,2));
});
