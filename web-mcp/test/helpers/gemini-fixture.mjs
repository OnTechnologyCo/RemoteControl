import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createServer, request as httpRequest } from 'node:http';
import { once } from 'node:events';
import { createHash, randomBytes } from 'node:crypto';
import { createGateway } from '../../gateway.mjs';
const redirects = ['oauth-redirect','oauth-redirect-sandbox','oauth-redirect-test']
  .flatMap(host => ['r','a'].map(kind => 'https://' + host + '.googleusercontent.com/' + kind + '/fixture-app_123'));
const opaqueState = 'x'.repeat(1400) + '+&%한글🙂';
const returned = page => new URL(/id="return-client" href="([^"]+)"/.exec(page)[1].replaceAll('&amp;', '&'));

export async function fixture(t, mode, realUpstream) {
  const directory = mkdtempSync(join(tmpdir(), 'rc-gemini-')), events = [];
  const config = { publicUrl:mode === 'lan' ? 'https://127.0.0.2' : 'https://remote.example.test',
    ...(mode ? {mode,allowedNetworks:'127.0.0.0/8'} : {}),
    listenPort:59432, lanPort:59431, proxyKey:randomBytes(32).toString('base64url') };
  let clock = Date.now(), upstreamStatus = 200, requests = 0;
  const options = {directory, now:()=>clock, audit:event=>events.push(event), upstream:async(path,data)=>{
    requests++;
    if (realUpstream) return realUpstream(path,data);
    if (path === '/login') {
      const form = JSON.parse(data.body);
      return form.user === 'fixture' && form.password === 'fixture-password'
        ? {status:200,headers:{'set-cookie':['rcmcpid=fixture-session; HttpOnly']},body:'{}'}
        : {status:401,headers:{},body:'{}'};
    }
    return {status:upstreamStatus,body:JSON.stringify({jsonrpc:'2.0',id:1,result:{ok:true}})};
  }};
  let gateway = createGateway(config,options), server;
  const open = async()=>{
    server=createServer({maxHeaderSize:32768},gateway.app);
    server.listen(0,'127.0.0.1');await once(server,'listening');
  };
  const stop = async()=>{try {gateway.close();} finally {server.closeAllConnections();await new Promise(r=>server.close(r));}};
  await open();
  t.after(stop);
  function request(path, method='GET', body, extra={}) {
    return new Promise((resolve,reject)=>{
      const req=httpRequest({hostname:'127.0.0.1',port:server.address().port,path,method,maxHeaderSize:32768,
        headers:{Host:new URL(config.publicUrl).host,'X-RC-Proxy-Key':config.proxyKey,
          'X-Forwarded-Proto':'https','X-RC-Client-IP':'127.0.0.1',
          ...(body === undefined ? {} : {'Content-Length':Buffer.byteLength(body)}),...extra}},res=>{
        const parts=[];res.on('data',b=>parts.push(b));res.on('error',reject);res.on('end',()=>{
          const text=Buffer.concat(parts).toString();resolve({status:res.statusCode,headers:res.headers,text,
            json:()=>JSON.parse(text)});
        });
      });req.on('error',reject);req.end(body);
    });
  }
  const form=(path,data,headers)=>request(path,'POST',new URLSearchParams(data).toString(),{'Content-Type':'application/x-www-form-urlencoded',...headers});
  const register=(uris=redirects,method='client_secret_post')=>request('/register','POST',
    JSON.stringify({redirect_uris:uris,token_endpoint_auth_method:method,client_name:'untrusted name',grant_types:['authorization_code','refresh_token'],response_types:['code']}),
    {'Content-Type':'application/json'});
  const resource=config.publicUrl+'/mcp';
  const authorize=async(client,uri=redirects[0],state=opaqueState,changes={})=>{
    const verifier=randomBytes(32).toString('base64url');
    const params={client_id:client.client_id,redirect_uri:uri,response_type:'code',state,
      code_challenge:createHash('sha256').update(verifier).digest('base64url'),code_challenge_method:'S256',resource,scope:'remotecontrol',...changes};
    const r=await request('/authorize?'+new URLSearchParams(params));
    return {...r,params,verifier,uri,state,csrf:/name="csrf" value="([^"]+)"/.exec(r.text)?.[1],cookie:r.headers['set-cookie']?.[0].split(';')[0]};
  };
  const login=(a,changes={},headers={})=>form('/connect',{csrf:a.csrf,user:'fixture',password:'fixture-password',decision:'allow',...changes},
    {Origin:config.publicUrl,Cookie:a.cookie,...headers});
  const exchange=(client,a,code,changes={})=>form('/token',{grant_type:'authorization_code',client_id:client.client_id,
    ...(client.client_secret ? {client_secret:client.client_secret}:{}),code,code_verifier:a.verifier,redirect_uri:a.uri,resource,...changes});
  const refresh=(client,token,changes={})=>form('/token',{grant_type:'refresh_token',client_id:client.client_id,
    ...(client.client_secret ? {client_secret:client.client_secret}:{}),refresh_token:token,resource,...changes});
  return {directory,events,config,resource,request,form,register,authorize,login,exchange,refresh,
    get provider(){return gateway.provider;},get requests(){return requests;},
    set status(value){upstreamStatus=value;},advance:ms=>{clock+=ms;},
    restart:async()=>{await stop();gateway=createGateway(config,options);await open();}};
}

export {redirects, returned};
