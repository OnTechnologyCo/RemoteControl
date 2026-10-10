// Execute production Caddy routes with a fixture-only listener remapped off 443.
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {request} from 'node:https';
import {once} from 'node:events';
import {spawn,spawnSync} from 'node:child_process';
import {readFileSync,writeFileSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {randomBytes} from 'node:crypto';
import {createGateway} from '../web-mcp/gateway.mjs';

const directory=resolve(process.argv[2]),key=randomBytes(32).toString('base64url');
const backend=createServer();backend.listen(0,'127.0.0.1');await once(backend,'listening');
const port=backend.address().port;
const gateway=createGateway({publicUrl:'https://localhost',listenPort:port,lanPort:5901,proxyKey:key},{directory});
backend.on('request',gateway.app);
let caddy;
try{
  const builder=join(directory,'build-default.ps1'),config=join(directory,'default.Caddyfile');
  const source=resolve('web-mcp/WebMcpConfig.ps1').replaceAll("'","''");
  writeFileSync(builder,`param($StateDir,$GatewayPort,$ProxyKey,$OutFile)
. '${source}'
$uri=Get-WebMcpUri 'https://localhost:443' 5901 $GatewayPort
$legacy=@{Origin='https://localhost';Destination='https://localhost:9330/'}
$text=New-WebMcpCaddyConfig $uri $GatewayPort $ProxyKey $StateDir $legacy
[IO.File]::WriteAllText($OutFile,$text,(New-Object Text.UTF8Encoding($false)))
`);
  const build=spawnSync('powershell.exe',['-NoProfile','-File',builder,'-StateDir',directory,'-GatewayPort',String(port),'-ProxyKey',key,'-OutFile',config],{windowsHide:true,encoding:'utf8'});
  assert.equal(build.status,0,build.stderr);
  const exe=resolve('artifacts/web-mcp-downloads/caddy-2.11.7/caddy.exe');
  const adapted=spawnSync(exe,['adapt','--config',config,'--adapter','caddyfile'],{windowsHide:true,encoding:'utf8'});
  assert.equal(adapted.status,0,adapted.stderr);
  const settings=JSON.parse(adapted.stdout),servers=Object.values(settings.apps.http.servers);
  assert.equal(servers.length,1);assert.deepEqual(servers[0].listen,[':443']);
  assert.deepEqual(servers[0].protocols,['h1','h2']);
  assert.equal(servers[0].automatic_https.disable,true);assert(!settings.apps.tls.automation);
  assert.equal((readFileSync(config,'utf8').match(/^https:\/\/localhost \{/gm)||[]).length,1);
  const free=createServer();free.listen(0,'127.0.0.1');await once(free,'listening');const tlsPort=free.address().port;await new Promise(r=>free.close(r));
  // Only the socket is changed: preserve production Host, / redirect and MCP routing.
  servers[0].listen=['127.0.0.1:'+tlsPort];
  const isolated=join(directory,'default-isolated.json');writeFileSync(isolated,JSON.stringify(settings));
  caddy=spawn(exe,['run','--config',isolated],{windowsHide:true,stdio:'ignore'});
  function get(path){return new Promise((ok,fail)=>{
    const req=request({hostname:'127.0.0.1',port:tlsPort,servername:'localhost',path,headers:{Host:'localhost'},rejectUnauthorized:false},res=>{let body='';res.on('data',b=>body+=b);res.on('end',()=>ok({status:res.statusCode,headers:res.headers,body}));});req.on('error',fail);req.end();
  });}
  let home;for(let i=0;i<60;i++){try{home=await get('/');break;}catch{await new Promise(r=>setTimeout(r,100));}}
  assert(home,'Isolated Caddy did not start');assert.equal(home.status,302);assert.equal(home.headers.location,'https://localhost:9330/');
  assert.equal(home.headers['alt-svc'],'clear');
  const discovery=await get('/.well-known/oauth-protected-resource');assert.equal(discovery.status,200);assert.equal(JSON.parse(discovery.body).resource,'https://localhost/mcp');
  assert.equal(discovery.headers['alt-svc'],'clear');
  const oauth=await get('/.well-known/oauth-authorization-server');assert.equal(oauth.status,200);assert.equal(JSON.parse(oauth.body).issuer,'https://localhost/');
  const protectedRoute=await get('/mcp');assert.equal(protectedRoute.status,401);assert(protectedRoute.headers['www-authenticate'].includes('https://localhost/.well-known/oauth-protected-resource'));
  console.log('PASS default 443 config: homepage redirect, OAuth discovery and protected MCP share one site; live ports untouched');
}finally{gateway.close();backend.closeAllConnections();backend.close();if(caddy&&caddy.exitCode===null){caddy.kill();await once(caddy,'exit');}}
