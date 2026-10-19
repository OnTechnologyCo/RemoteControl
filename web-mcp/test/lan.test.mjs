import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,readFileSync,writeFileSync,existsSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {spawn,spawnSync} from 'node:child_process';
import {createServer} from 'node:http';
import {request} from 'node:https';
import {once} from 'node:events';
import {networkInterfaces} from 'node:os';
import {randomBytes,createHash,X509Certificate} from 'node:crypto';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StreamableHTTPClientTransport} from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import {createGateway,validateConfig,callbackName,parseNetworks,networkAllows} from '../gateway.mjs';
import {browser,delay} from './browser-helper.mjs';
const hash=x=>createHash('sha256').update(x).digest('hex');
async function freePort(host='127.0.0.1'){const s=createServer();s.listen(0,host);await once(s,'listening');const port=s.address().port;await new Promise(r=>s.close(r));return port;}

test('LAN HTTPS IP: offline OAuth, real MCP SDK, browser transfer and persistent tokens',{timeout:120000},async t=>{
 const address=Object.values(networkInterfaces()).flat().find(x=>x.family==='IPv4'&&!x.internal)?.address || '127.0.0.2';
 const root=resolve('../artifacts/lan-mcp-test');mkdirSync(root,{recursive:true});const work=mkdtempSync(join(root,'lan-')),remote=join(work,'remote');mkdirSync(remote);
 const fixture=spawn(resolve('../artifacts/mcp-test/McpFixture.exe'),[remote],{stdio:['pipe','pipe','pipe'],windowsHide:true});t.after(()=>fixture.stdin.end());
 const backend=await new Promise((resolve,reject)=>{let output='',error='';const timer=setTimeout(()=>reject(Error('fixture timeout '+error)),10000);fixture.stderr.on('data',b=>error+=b);fixture.stdout.on('data',b=>{output+=b;if(output.includes('\n')){clearTimeout(timer);resolve(Number(output.trim().split('\n')[0]));}});fixture.once('error',reject);});
 const httpsPort=await freePort(address),listenPort=await freePort(),proxyKey=randomBytes(32).toString('base64url'),origin='https://'+address+':'+httpsPort;
 const config={mode:'lan',publicUrl:origin,listenPort,lanPort:backend,proxyKey,allowedNetworks:address+'/32'};
 let gateway=createGateway(config,{directory:work}),calls=[];
 const server=createServer((req,res)=>gateway.app(req,res));server.listen(listenPort,'127.0.0.1');await once(server,'listening');t.after(()=>{gateway.close();server.closeAllConnections();server.close();});
 const nativeFetch=globalThis.fetch;globalThis.fetch=(url,...args)=>{calls.push(String(url));if(new URL(url).hostname!=='127.0.0.1')throw Error('Offline test blocked outbound fetch');return nativeFetch(url,...args);};t.after(()=>{globalThis.fetch=nativeFetch;});
 const caddyFile=join(work,'Caddyfile'),caFolder=join(work,'caddy');
 const publicPort=await freePort(),publicOrigin='https://external.fixture.test:'+httpsPort,publicWork=join(work,'public');mkdirSync(publicWork);
 const publicGateway=createGateway({...config,mode:'ddns',publicUrl:publicOrigin,listenPort:publicPort},{directory:publicWork});const publicServer=createServer(publicGateway.app);publicServer.listen(publicPort,'127.0.0.1');await once(publicServer,'listening');t.after(()=>{publicGateway.close();publicServer.closeAllConnections();publicServer.close();});
 const publicBase=`{\nauto_https off\nadmin off\npersist_config off\n}\n${publicOrigin} {\ntls internal\nreverse_proxy 127.0.0.1:${publicPort} {\nheader_up Host external.fixture.test:${httpsPort}\nheader_up X-RC-Proxy-Key ${proxyKey}\nheader_up X-RC-Client-IP {remote_host}\nheader_up X-Forwarded-Proto https\nheader_up -X-Forwarded-For\nheader_up -Forwarded\n}\n}\n`;const publicBaseFile=join(work,'public-base.caddy');writeFileSync(publicBaseFile,publicBase);
 const ps=`. '${resolve('../web-mcp/LanWebMcpConfig.ps1').replaceAll("'","''")}'; Merge-LanWebMcpSharedConfig ([IO.File]::ReadAllText('${publicBaseFile.replaceAll("'","''")}')) ([Uri]'${origin}') ${listenPort} '${proxyKey}' '${work.replaceAll("'","''")}'`;
 const configResult=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',ps],{encoding:'utf8',windowsHide:true});assert.equal(configResult.status,0,configResult.stderr);writeFileSync(caddyFile,configResult.stdout.replace(/^\uFEFF/,''));
 const caddy=spawn(resolve('../artifacts/web-mcp-downloads/caddy-2.11.7/caddy.exe'),['run','--config',caddyFile,'--adapter','caddyfile'],{windowsHide:true,stdio:['ignore','ignore','pipe']});let caddyErrors='';caddy.stderr.on('data',b=>caddyErrors+=b);t.after(()=>caddy.kill());
 const caFile=join(caFolder,'pki','authorities','local','root.crt');for(let i=0;i<100&&!existsSync(caFile);i++)await delay(100);assert(existsSync(caFile),caddyErrors);const ca=readFileSync(caFile);
 function send(path,{body,method=body===undefined?'GET':'POST',headers={},trust=ca}={}){return new Promise((resolve,reject)=>{const req=request(origin+path,{method,ca:trust,agent:false,headers:{...headers,...(body!==undefined?{'Content-Length':Buffer.byteLength(body)}:{})}},res=>{const certificate=res.socket?.getPeerCertificate()?.raw;const parts=[];res.on('data',b=>parts.push(b));res.on('end',()=>{const bytes=Buffer.concat(parts);resolve({status:res.statusCode,headers:res.headers,text:bytes.toString(),bytes,certificate});});res.on('error',reject);});req.on('error',reject);req.end(body);});}
 let ready=false;for(let i=0;i<100;i++){try{ready=(await send('/')).status===200;if(ready)break;}catch{}await delay(100);}assert(ready,caddyErrors);
 const form=(path,data,headers={})=>send(path,{body:new URLSearchParams(data).toString(),headers:{'Content-Type':'application/x-www-form-urlencoded',...headers}});
 const json=(path,data,headers={})=>send(path,{body:JSON.stringify(data),headers:{'Content-Type':'application/json',...headers}});
 let client,tokens,callback,verifier;
 await t.test('default 443 config shares one listener, retains external domain and is reversible',async()=>{
  const defaultFile=join(work,'default443.caddy');writeFileSync(defaultFile,configResult.stdout.replaceAll(':'+httpsPort,'').replace(/^\uFEFF/,''));
  const adapted=spawnSync(resolve('../artifacts/web-mcp-downloads/caddy-2.11.7/caddy.exe'),['adapt','--config',defaultFile,'--adapter','caddyfile'],{encoding:'utf8',windowsHide:true});assert.equal(adapted.status,0,adapted.stderr);const settings=JSON.parse(adapted.stdout);assert.equal(Object.keys(settings.apps.http.servers).length,1);assert.deepEqual(Object.values(settings.apps.http.servers)[0].listen,[':443']);assert.equal(settings.apps.pki.certificate_authorities.local.install_trust,false);
  const domain=await new Promise((resolve,reject)=>{const req=request({hostname:address,port:httpsPort,servername:'external.fixture.test',path:'/.well-known/oauth-protected-resource',ca,headers:{Host:'external.fixture.test:'+httpsPort}},res=>{let text='';res.on('data',b=>text+=b);res.on('end',()=>resolve({status:res.statusCode,text}));});req.on('error',reject);req.end();});assert.equal(domain.status,200,domain.text);assert.equal(JSON.parse(domain.text).resource,publicOrigin+'/mcp');
  const strip=spawnSync('powershell.exe',['-NoProfile','-Command',`. '${resolve('../web-mcp/LanWebMcpConfig.ps1')}'; Remove-LanWebMcpSharedConfig ([IO.File]::ReadAllText('${caddyFile}'))`],{encoding:'utf8',windowsHide:true});assert.equal(strip.status,0,strip.stderr);assert(!strip.stdout.includes('LAN site BEGIN'));assert(strip.stdout.includes('auto_https off'));assert(strip.stdout.includes('external.fixture.test'));
 });
 await t.test('strict TLS, IP SAN, network ACL, Host and proxy spoofing',async()=>{
  await assert.rejects(send('/',{trust:[]}),/certificate|issuer|self.signed/i);
  const r=await send('/');assert(new X509Certificate(r.certificate).checkIP(address));
  try{const foreign=await send('/',{headers:{Host:'evil.test'}});assert([403,404,421].includes(foreign.status)||foreign.text==='','Foreign Host must not reach either gateway');}catch(error){assert.equal(error.code,'EPROTO','Foreign SNI must be rejected at TLS');}
  // Caddy replaces an attacker-supplied client address and key, so allow only actual IP.
  assert.equal((await send('/',{headers:{'X-RC-Client-IP':'203.0.113.99','X-RC-Proxy-Key':'forged'}})).status,200);
  const blocked=createGateway({...config,allowedNetworks:'203.0.113.0/24'},{directory:mkdtempSync(join(root,'denied-'))});const previous=gateway;gateway=blocked;assert.equal((await send('/')).status,403);gateway=previous;blocked.close();
  assert.throws(()=>validateConfig({...config,mode:'ddns'}));assert.throws(()=>validateConfig({...config,allowedNetworks:'0.0.0.0/0'}));assert(networkAllows('::ffff:192.168.2.3',parseNetworks('192.168.0.0/16')));assert(!networkAllows('192.169.0.1',parseNetworks('192.168.0.0/16')));
 });
 const callbackServer=createServer((req,res)=>{res.end('LAN OAuth callback received');});callbackServer.listen(0,'127.0.0.1');await once(callbackServer,'listening');t.after(()=>{callbackServer.closeAllConnections();callbackServer.close();});callback='http://127.0.0.1:'+callbackServer.address().port+'/callback';
 const returned=new Promise(resolve=>callbackServer.once('request',req=>resolve(new URL(req.url,callback))));
 let chrome;
 await t.test('no cloud metadata: DCR and browser login return to local loopback',async()=>{
  const discovery=JSON.parse((await send('/.well-known/oauth-authorization-server')).text);assert.equal(discovery.client_id_metadata_document_supported,false);
  for(const bad of ['https://chatgpt.com/connector_platform_oauth_redirect','http://192.168.0.2:3344/cb','http://localhost:3344/cb?next=evil','http://user@127.0.0.1:3344/cb'])assert.equal((await json('/register',{redirect_uris:[bad],token_endpoint_auth_method:'none'})).status,400,bad);
  assert.equal(callbackName('http://[::1]:3344/callback',true),'로컬 MCP 클라이언트');
  const registered=await json('/register',{redirect_uris:[callback],token_endpoint_auth_method:'none',grant_types:['authorization_code','refresh_token'],response_types:['code']});assert.equal(registered.status,201,registered.text);client=JSON.parse(registered.text);
  verifier=randomBytes(32).toString('base64url');const authorize=origin+'/authorize?'+new URLSearchParams({client_id:client.client_id,redirect_uri:callback,response_type:'code',code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource:origin+'/mcp',state:'lan-fixture'});
  const cert=new X509Certificate((await send('/')).certificate);const spki=createHash('sha256').update(cert.publicKey.export({format:'der',type:'spki'})).digest('base64');
  // Only this fixture certificate is pinned; no system trust or global TLS bypass.
  chrome=await browser(join(work,'browser'),{args:['--ignore-certificate-errors-spki-list='+spki]});t.after(()=>chrome.close());await chrome.command('Page.navigate',{url:authorize});
  for(let i=0;i<100;i++){if(await chrome.evaluate("!!document.querySelector('input[name=password]')"))break;await delay(100);}
  assert(await chrome.evaluate("document.title.includes('로컬 MCP 클라이언트')"));
  await chrome.evaluate("document.querySelector('[name=user]').value='fixture';document.querySelector('[name=password]').value='fixture-only';document.querySelector('[value=allow]').click()");
  const callbackResult=await Promise.race([returned,delay(15000).then(()=>{throw Error('Browser loopback callback timeout');})]);assert.equal(callbackResult.searchParams.get('state'),'lan-fixture');assert.equal(callbackResult.searchParams.get('iss'),origin+'/');
  const wrong=await form('/token',{grant_type:'authorization_code',client_id:client.client_id,code:callbackResult.searchParams.get('code'),code_verifier:randomBytes(32).toString('base64url'),redirect_uri:callback,resource:origin+'/mcp'});assert.equal(wrong.status,400);
  const token=await form('/token',{grant_type:'authorization_code',client_id:client.client_id,code:callbackResult.searchParams.get('code'),code_verifier:verifier,redirect_uri:callback,resource:origin+'/mcp'});assert.equal(token.status,200,token.text);tokens=JSON.parse(token.text);
 });
 const rpc=async(name,args={})=>{const response=await json('/mcp',{jsonrpc:'2.0',id:1,method:'tools/call',params:{name,arguments:args}},{Authorization:'Bearer '+tokens.access_token});assert.equal(response.status,200,response.text);const result=JSON.parse(response.text).result;assert(!result.isError,JSON.stringify(result));return JSON.parse(result.content[0].text);};
 await t.test('official StreamableHTTP SDK initializes and lists tools over strict HTTPS IP',async()=>{
  const localFetch=async(url,options={})=>{assert.equal(new URL(url).origin,origin);const r=await send(new URL(url).pathname+new URL(url).search,{method:options.method,body:options.body,headers:Object.fromEntries(new Headers(options.headers))});return new Response([202,204,205].includes(r.status)?null:r.bytes,{status:r.status,headers:r.headers});};
  const transport=new StreamableHTTPClientTransport(new URL(origin+'/mcp'),{requestInit:{headers:{Authorization:'Bearer '+tokens.access_token}},fetch:localFetch});const sdk=new Client({name:'lan-test',version:'1'},{capabilities:{}});await sdk.connect(transport);const tools=await sdk.listTools();assert(tools.tools.some(x=>x.name==='pc_get_status'));assert(tools.tools.some(x=>x.name==='pc_open_file_transfer'));await sdk.callTool({name:'pc_get_status',arguments:{}});await sdk.close();
 });
 await t.test('browser upload and native HTTPS download keep SHA-256 without internet',async()=>{
  const link=await rpc('pc_open_file_transfer');assert.equal(new URL(link.url).origin,origin);await chrome.command('Page.navigate',{url:link.url});
  let transferLoaded=false;for(let i=0;i<100;i++){if(await chrome.evaluate("document.readyState==='complete' && location.pathname==='/files' && !!document.getElementById('pick') && !!document.getElementById('upload')")){transferLoaded=true;break;}await delay(100);}assert(transferLoaded,'Transfer document and file input must finish loading');
  const data=randomBytes(2*1024*1024+37),local=join(work,'upload.bin'),target=join(remote,'LAN-한글.bin');writeFileSync(local,data);
  const pick=await chrome.command('Runtime.evaluate',{expression:"document.getElementById('pick')",returnByValue:false});assert(pick.result.objectId,'File input object must exist');await chrome.command('DOM.setFileInputFiles',{objectId:pick.result.objectId,files:[local]});await chrome.command('Runtime.releaseObject',{objectId:pick.result.objectId});await chrome.evaluate(`document.getElementById('target').value=${JSON.stringify(target)};document.getElementById('upload').click()`);for(let i=0;i<200&&!existsSync(target);i++)await delay(50);assert(existsSync(target),await chrome.evaluate("document.getElementById('log').textContent"));assert.equal(hash(readFileSync(target)),hash(data));
  let state=await rpc('pc_prepare_download',{path:target,kind:'file'});for(let i=0;state.status==='preparing'&&i<200;i++){await delay(50);state=await rpc('pc_get_transfer_status',{transfer_id:state.transfer_id});}assert.equal(state.sha256,hash(data));
  const second=await rpc('pc_open_file_transfer'),u=new URL(second.url),opened=await send(u.pathname+u.search);assert.equal(opened.status,303);const cookie=opened.headers['set-cookie'][0].split(';')[0];const downloaded=await send('/files/download?id='+state.transfer_id,{headers:{Cookie:cookie}});assert.equal(downloaded.status,200,downloaded.text.slice(0,100));assert.equal(hash(downloaded.bytes),hash(data));
 });
 await t.test('restart preserves authorization and offline refresh rotation/revocation',async()=>{
  gateway.close();gateway=createGateway(config,{directory:work});await rpc('pc_get_status');
  const refresh=await form('/token',{grant_type:'refresh_token',client_id:client.client_id,refresh_token:tokens.refresh_token,resource:origin+'/mcp'});assert.equal(refresh.status,200,refresh.text);tokens=JSON.parse(refresh.text);await rpc('pc_get_status');
  const revoked=await form('/revoke',{client_id:client.client_id,token:tokens.refresh_token,token_type_hint:'refresh_token'});assert.equal(revoked.status,200,revoked.text);assert.equal((await json('/mcp',{jsonrpc:'2.0',id:1,method:'ping'},{Authorization:'Bearer '+tokens.access_token})).status,401);
  assert(calls.every(url=>new URL(url).hostname==='127.0.0.1'));assert(!caddyErrors.includes('installing root certificate'));assert(caddyErrors.includes('trust store installation disabled'),caddyErrors);
 });
 console.log('LAN fixture address '+origin+'; external metadata fetches: 0');
});
