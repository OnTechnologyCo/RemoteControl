import {readFileSync,appendFileSync,existsSync,statSync,renameSync} from 'node:fs';
import {dirname,join,resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {createServer} from 'node:http';
import {createGateway,validateConfig} from './gateway.mjs';

export function tailscaleOrigin(value,port) {
  const u=new URL(value);
  if(u.protocol!=='https:' || !u.hostname.endsWith('.ts.net') || ![443,8443,10000].includes(port) || Number(u.port||443)!==port || u.pathname!=='/' || u.search || u.hash || u.username || u.password) throw Error('Invalid Tailscale MCP address');
  return u.origin;
}

// LocalService owns the gateway. SYSTEM writes only the short-lived address handoff.
export function createTailscaleServer(config,{directory,readActive,now=Date.now,gatewayOptions={},origin=tailscaleOrigin,label='Tailscale'}) {
  validateConfig({...config,publicUrl:'https://validation.ts.net:'+config.publicPort});
  origin('https://validation.ts.net'+(config.publicPort===443?'':':'+config.publicPort),config.publicPort);
  let gateway=null,identity='';
  function sync() {
    let next=null;
    try {
      const active=readActive();
      if(active.expiresAt>now() && active.expiresAt<=now()+60000) {
        const publicUrl=origin(active.publicUrl,config.publicPort);
        next=validateConfig({...config,publicUrl,lanPort:active.lanPort});
      }
    }catch{}
    const key=next?JSON.stringify([next.publicUrl,next.lanPort]):'';
    if(key===identity)return;
    gateway?.close();gateway=null;identity='';
    if(next){gateway=createGateway(next,{directory,...gatewayOptions});identity=key;}
  }
  const server=createServer({maxHeaderSize:16384,requestTimeout:45000,headersTimeout:10000},(req,res)=>{
    sync();
    if(!gateway){res.writeHead(503,{'Content-Type':'text/plain','Cache-Control':'no-store'});res.end('Waiting for the RemoteControl '+label+' connection');return;}
    gateway.app(req,res);
  });
  server.maxConnections=32;
  const timer=setInterval(sync,1000);timer.unref();sync();
  return {server,sync,close(){clearInterval(timer);gateway?.close();server.closeAllConnections();server.close();}};
}

if(process.argv[1] && import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
  const path=resolve(process.argv[2]),root=dirname(path),directory=join(root,'data');
  const config=JSON.parse(readFileSync(path,'utf8').replace(/^\uFEFF/,''));
  const runtime=createTailscaleServer(config,{directory,readActive:()=>JSON.parse(readFileSync(join(root,'active.json'),'utf8').replace(/^\uFEFF/,'')),
    gatewayOptions:{audit:event=>{try{const file=join(directory,'connection-events.jsonl');if(existsSync(file)&&statSync(file).size>256*1024)renameSync(file,file+'.previous');appendFileSync(file,JSON.stringify({at:new Date().toISOString(),...event})+'\n');}catch{}}}});
  runtime.server.listen(config.listenPort,'127.0.0.1');
  runtime.server.on('error',()=>{process.exitCode=1;runtime.close();});
  for(const signal of ['SIGINT','SIGTERM'])process.on(signal,()=>runtime.close());
}
