// Read-only public requests in an isolated browser. No passwords or user profile.
import {mkdtempSync,mkdirSync,writeFileSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {randomBytes,createHash} from 'node:crypto';
import {browser,delay} from '../web-mcp/test/browser-helper.mjs';
const root=resolve('artifacts/web-registration-diagnostic');mkdirSync(root,{recursive:true});
const directory=mkdtempSync(join(root,'browser-'));
const origin='https://najhna.dyndns.org';
const quic=process.argv.includes('--quic');
const cached=process.argv.find(arg=>arg.startsWith('--profile='))?.slice(10);
const profile=cached?resolve(cached):join(directory,'profile');
if(!profile.startsWith(root+'\\')&&!profile.startsWith(root+'/'))throw Error('Diagnostic profile must stay inside the artifact directory');
const chrome=await browser(profile,{args:quic?['--enable-quic','--origin-to-force-quic-on=najhna.dyndns.org:443']:[]});
const events=[];
try{
  chrome.on(event=>{if(event.method==='Network.responseReceived'){
    const r=event.params.response,u=new URL(r.url);if(u.origin!==origin)return;
    events.push({path:u.pathname,status:r.status,protocol:r.protocol});
  }});
  for(let i=0;i<(quic?8:20);i++){
    const query=new URLSearchParams({client_id:'https://claude.ai/oauth/mcp-oauth-client-metadata',redirect_uri:'https://claude.ai/api/mcp/auth_callback',response_type:'code',state:'diagnostic-only',code_challenge:createHash('sha256').update(randomBytes(32)).digest('base64url'),code_challenge_method:'S256',scope:'remotecontrol',resource:origin+'/mcp'});
    await chrome.command('Page.navigate',{url:origin+'/authorize?'+query});
    await delay(600);
    const body=await chrome.evaluate('document.body.innerText');
    console.log(JSON.stringify({attempt:i+1,quic,...events.at(-1),proxyRejected:body?.includes('HTTPS proxy required'),loginForm:await chrome.evaluate('!!document.querySelector("input[name=password]")')}));
  }
  writeFileSync(join(directory,'responses.json'),JSON.stringify(events,null,2));
  console.log(directory);
}finally{await chrome.close();}
