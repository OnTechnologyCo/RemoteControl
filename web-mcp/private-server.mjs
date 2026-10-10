import {readFileSync,appendFileSync,existsSync,statSync,renameSync} from 'node:fs';
import {dirname,join,resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {isIP} from 'node:net';
import {createTailscaleServer} from './tailscale-server.mjs';

export function privateOrigin(value,port=443) {
 const u=new URL(value);
 if(port!==443 || u.protocol!=='https:' || isIP(u.hostname) || !/^[a-z0-9]+(?:[.-][a-z0-9]+)*\.[a-z]{2,}$/i.test(u.hostname) || !/^https:\/\/[a-z0-9.-]+\/?$/i.test(value) || u.pathname!=='/' || u.search || u.hash || u.username || u.password) throw Error('Invalid private tunnel MCP address');
 return u.origin;
}
export function createPrivateServer(config,options) {
 return createTailscaleServer({...config,publicPort:443},{...options,origin:privateOrigin,label:'private tunnel'});
}
if(process.argv[1] && import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
 const path=resolve(process.argv[2]),root=dirname(path),directory=join(root,'data');
 const config=JSON.parse(readFileSync(path,'utf8').replace(/^\uFEFF/,''));
 const runtime=createPrivateServer(config,{directory,readActive:()=>JSON.parse(readFileSync(join(root,'active.json'),'utf8').replace(/^\uFEFF/,'')),gatewayOptions:{audit:event=>{try{const file=join(directory,'connection-events.jsonl');if(existsSync(file)&&statSync(file).size>256*1024)renameSync(file,file+'.previous');appendFileSync(file,JSON.stringify({at:new Date().toISOString(),...event})+'\n');}catch{}}}});
 runtime.server.listen(config.listenPort,'127.0.0.1');
 runtime.server.on('error',()=>{process.exitCode=1;runtime.close();});
 for(const signal of ['SIGINT','SIGTERM'])process.on(signal,()=>runtime.close());
}
