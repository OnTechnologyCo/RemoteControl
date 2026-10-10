import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { once } from 'node:events';

export const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
export async function browser(profile, {args=[]} = {}) {
  const executable = process.env.RC_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
  if (!existsSync(executable)) throw Error('Browser test needs Chrome or RC_BROWSER pointing at Edge/Chrome.');
  const child = spawn(executable, ['--headless=new', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir='+profile, ...args, 'about:blank'], {windowsHide:true, stdio:'ignore'});
  let launchError; child.on('error', error => { launchError=error; });
  let socket, sequence=0;
  const pending = new Map(), listeners = new Set();
  async function close() {
    if (socket?.readyState === WebSocket.OPEN) { socket.send(JSON.stringify({id:++sequence,method:'Browser.close'})); await delay(250); socket.close(); }
    if (child.exitCode === null) child.kill();
    for (const entry of pending.values()) {clearTimeout(entry.timer);entry.reject(Error('Browser closed'));}
    pending.clear();
  }
  try {
    const active = join(profile,'DevToolsActivePort');
    let targets;
    for(let i=0;i<100;i++) {
      if(launchError)throw launchError;
      if(existsSync(active))try{
        const port=readFileSync(active,'utf8').split('\n')[0];
        const found=await (await fetch('http://127.0.0.1:'+port+'/json/list')).json();
        if(found.some(t=>t.type==='page')){targets=found;break;}
      }catch{}
      await delay(100);
    }
    if(!targets) throw Error('Browser debugger did not start.');
    socket=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);
    await once(socket,'open');
    socket.addEventListener('message',event=>{
      const message=JSON.parse(event.data);
      if(message.id) {const entry=pending.get(message.id);if(!entry)return;pending.delete(message.id);clearTimeout(entry.timer);if(message.error)entry.reject(Error(message.error.message));else entry.resolve(message.result);}
      else for(const listener of listeners) listener(message);
    });
    const command=(method,params={})=>new Promise((resolve,reject)=>{
      const id=++sequence;
      const timer=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout: '+method));},10000);
      pending.set(id,{resolve,reject,timer});socket.send(JSON.stringify({id,method,params}));
    });
    const evaluate=async expression=>(await command('Runtime.evaluate',{expression,returnByValue:true})).result?.value;
    await command('Network.enable');await command('Page.enable');
    return {command,evaluate,on:listener=>listeners.add(listener),close};
  } catch(error) {await close();throw error;}
}
