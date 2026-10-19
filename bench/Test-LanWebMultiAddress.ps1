# 운영 NIC, 인증서 저장소, 서비스, 방화벽을 변경하지 않고 두 loopback IP에서 실제 TLS를 검증한다.
param([string]$CaddyPath='', [string]$NodePath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $CaddyPath){$CaddyPath=Join-Path $root 'dist\web-mcp\caddy.exe'}
if(-not $NodePath){$NodePath=Join-Path $root 'dist\web-mcp\node.exe'}
foreach($path in @($CaddyPath,$NodePath)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('시험 실행 파일이 없습니다: '+$path)}}
. (Join-Path $root 'web-mcp\LanWebMcpConfig.ps1')
$work=Join-Path $root ('artifacts\lan-multi-address-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$utf8=New-Object Text.UTF8Encoding($false)
function Get-ProbePort {
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any,0);$listener.Start()
    try{return $listener.LocalEndpoint.Port}finally{$listener.Stop()}
}
$httpsPort=Get-ProbePort;$gatewayPort=Get-ProbePort
$primary=[Uri]('https://127.0.0.1:'+$httpsPort)
$urls=@($primary.GetLeftPart([UriPartial]::Authority),('https://127.0.0.2:'+$httpsPort))
$key='fixture-proxy-key'
$networks='127.0.0.1/32'
$standalone=New-LanWebMcpCaddyConfig $primary $gatewayPort $key $work $urls $networks
$external='https://external.fixture.test:'+$httpsPort
$base="{`nadmin off`npersist_config off`nauto_https off`n}`n"+$external+" {`ntls internal`nrespond external-preserved`n}`n"
$shared=Merge-LanWebMcpSharedConfig $base $primary $gatewayPort $key $work $urls $networks
$single=New-LanWebMcpCaddyConfig $primary $gatewayPort $key $work @($urls[0]) $networks
$denied=Merge-LanWebMcpSharedConfig $base $primary $gatewayPort $key $work $urls '192.0.2.0/24'
$stripped=Remove-LanWebMcpSharedConfig $shared
if($stripped.Contains('LAN site BEGIN') -or -not $stripped.Contains('external-preserved') -or -not $stripped.Contains('auto_https off')){throw '공유 LAN 설정 제거에서 외부 사이트가 변경됐습니다.'}
if($shared -match '(?m)^\s*default_sni\b'){throw '공유 TLS가 하나의 IP 인증서로 고정됐습니다.'}
foreach($entry in @(@('standalone.caddy',$standalone),@('shared.caddy',$shared),@('single.caddy',$single),@('denied.caddy',$denied))){
    [IO.File]::WriteAllText((Join-Path $work $entry[0]),$entry[1],$utf8)
}
$script=@'
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {request} from 'node:https';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {readFileSync,copyFileSync,existsSync,mkdirSync} from 'node:fs';
import {createHash,X509Certificate} from 'node:crypto';
import {join} from 'node:path';
const [work,caddyPath,portArg,gatewayArg,key]=process.argv.slice(2);
const port=Number(portArg),gatewayPort=Number(gatewayArg),sleep=ms=>new Promise(r=>setTimeout(r,ms));
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const caPath=join(work,'caddy','pki','authorities','local','root.crt');
const exportDir=join(work,'certificates'),exportPath=join(exportDir,'RemoteControl-LAN-CA.crt');
let ca,fingerprint,processCaddy,errors='';
const backend=createServer((req,res)=>{
 if(req.headers['x-rc-proxy-key']!==key){res.writeHead(403);res.end('invalid fixture proxy key');return;}
 if(req.url!=='/.well-known/oauth-authorization-server'){res.writeHead(404);res.end('fixture route not found');return;}
 res.setHeader('Content-Type','application/json');
 res.end(JSON.stringify({issuer:'https://'+req.headers.host+'/',client:req.headers['x-rc-client-ip']}));
});
backend.listen(gatewayPort,'127.0.0.1');await once(backend,'listening');
function send(address,path='/.well-known/oauth-authorization-server',options={}){
 return new Promise((resolve,reject)=>{
  const req=request({hostname:address,port,path,servername:'',localAddress:'127.0.0.1',ca,agent:false,timeout:3000,...options},res=>{
   const cert=res.socket.getPeerCertificate().raw,parts=[];
   res.on('data',b=>parts.push(b));
   res.on('end',()=>resolve({status:res.statusCode,bytes:Buffer.concat(parts),certificate:cert,headers:res.headers}));res.on('error',reject);
  });
  req.on('error',reject);req.on('timeout',()=>req.destroy(Error('TLS probe timeout')));req.end();
 });
}
async function stop(){
 if(!processCaddy)return;
 const p=processCaddy;processCaddy=undefined;
 if(p.exitCode===null){p.kill();await once(p,'exit');}
 await sleep(150);
}
async function start(name){
 await stop();errors='';
 processCaddy=spawn(caddyPath,['run','--config',join(work,name),'--adapter','caddyfile'],{stdio:['ignore','ignore','pipe'],windowsHide:true});
 processCaddy.stderr.on('data',b=>errors+=b);let launchError;processCaddy.on('error',e=>launchError=e);
 for(let i=0;i<100;i++){
  if(launchError)throw launchError;
  if(processCaddy.exitCode!==null)throw Error(errors);
  if(existsSync(caPath)){
   ca=readFileSync(caPath);
   mkdirSync(exportDir,{recursive:true});copyFileSync(caPath,exportPath);
   try{
    const addresses=name==='single.caddy'?['127.0.0.1']:['127.0.0.1','127.0.0.2'];
    const responses=await Promise.all(addresses.map(address=>send(address)));
    if(responses.every(response=>[200,403].includes(response.status)))return;
   }catch{}
  }
  await sleep(100);
 }
 throw Error('Caddy did not start: '+errors);
}
async function verifyBoth(label){
 for(const address of ['127.0.0.1','127.0.0.2']){
  let response;
  for(let n=0;n<60;n++){
   try{response=await send(address);if(response.status===200)break;}catch{}
   await sleep(100);
  }
  assert.equal(response?.status,200,label+' '+address+'\n'+errors);
  assert.equal(JSON.parse(response.bytes).issuer,'https://'+address+':'+port+'/');
  assert.equal(JSON.parse(response.bytes).client,'127.0.0.1');
  assert(new X509Certificate(response.certificate).checkIP(address),label+' IP SAN mismatch');
  const downloaded=await send(address,'/ca/RemoteControl-LAN-CA.crt');
  assert.equal(downloaded.status,200);
  assert.equal(hash(downloaded.bytes),hash(readFileSync(exportPath)),'downloaded PEM file hash differs');
  assert.equal(hash(downloaded.bytes),fingerprint,'CA identity changed');
  assert.equal((await send(address,'/ca/RemoteControl-LAN-CA.crt',{method:'HEAD'})).status,200);
  assert.equal((await send(address,'/ca/RemoteControl-LAN-CA.crt',{method:'POST'})).status,404);
  for(const path of ['/ca/','/ca/root.key','/ca/RemoteControl-LAN-CA.crt/other','/caddy/pki/authorities/local/root.key','/certificates/SHA256.txt']){
   assert.equal((await send(address,path)).status,404,path+' must not expose a directory or another file');
  }
  assert.equal((await send(address,'/ca/RemoteControl-LAN-CA.crt',{localAddress:'127.0.0.2',headers:{'X-RC-Client-IP':'127.0.0.1','X-Forwarded-For':'127.0.0.1'}})).status,403,'CA must enforce the actual source CIDR');
  await assert.rejects(send(address,'/ca/RemoteControl-LAN-CA.crt',{ca:[]}),/certificate|issuer|self.signed/i);
 }
 console.log(label+': two IP SAN certificates with no SNI, per-IP issuer, public CA PEM/SHA-256, CIDR and exact routes passed');
}
try{
 await start('standalone.caddy');fingerprint=hash(ca);await verifyBoth('Standalone');
 await start('shared.caddy');await verifyBoth('Shared HTTPS');
 const external=await send('127.0.0.1','/',{servername:'external.fixture.test',headers:{Host:'external.fixture.test:'+port}});
 assert.equal(external.status,200);assert.equal(external.bytes.toString(),'external-preserved');
 assert(new X509Certificate(external.certificate).checkHost('external.fixture.test'));
 const unknown=await send('127.0.0.1','/ca/RemoteControl-LAN-CA.crt',{headers:{Host:'unconfigured.fixture.test'}});
 assert(!unknown.bytes.equals(ca),'Unconfigured Host exposed the CA');
 await start('denied.caddy');
 for(const address of ['127.0.0.1','127.0.0.2']){
  assert.equal((await send(address,'/ca/RemoteControl-LAN-CA.crt')).status,403);
  assert.equal((await send(address)).status,403);
 }
 await start('single.caddy');
 assert.equal((await send('127.0.0.1')).status,200);
 await assert.rejects(send('127.0.0.2'),/ECONNREFUSED|ECONNRESET|EPROTO|TLS probe timeout/,'Removed IP must not keep a listener');
 assert.equal(hash(readFileSync(caPath)),fingerprint);
 console.log('LAN multi-address integration passed: shared external HTTPS preserved, CA stable, removed address closed');
}finally{
 await stop();backend.closeAllConnections();await new Promise(r=>backend.close(r));
}
'@
$probe=Join-Path $work 'probe.mjs';[IO.File]::WriteAllText($probe,$script,$utf8)
& $NodePath $probe $work $CaddyPath $httpsPort $gatewayPort $key
if($LASTEXITCODE -ne 0){throw '다중 LAN IP HTTPS 통합 검사에 실패했습니다.'}
