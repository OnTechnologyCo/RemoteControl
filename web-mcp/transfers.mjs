import { randomBytes, timingSafeEqual } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { request } from 'node:http';
import express from 'express';
const cookieName='__Host-rc-files';
const random=()=>randomBytes(32).toString('base64url');
const same=(a,b)=>typeof a==='string'&&typeof b==='string'&&Buffer.byteLength(a)===Buffer.byteLength(b)&&timingSafeEqual(Buffer.from(a),Buffer.from(b));
const html=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const allowed=new Set(['pc_get_status','pc_get_file_info','pc_list_directory','pc_begin_upload','pc_write_upload_chunk','pc_finish_upload','pc_get_transfer_status','pc_cancel_transfer','pc_prepare_download']);
export const fileTool={name:'pc_open_file_transfer',description:'Open the authenticated browser file/folder upload and download page. Return its URL to the user. Uses the current WebMCP login; no local connector install. The link expires in five minutes and is single use. Browser chunks and MCP transfer tools use the same verified remote storage.',inputSchema:{type:'object',properties:{},additionalProperties:false},annotations:{readOnlyHint:true,destructiveHint:false,idempotentHint:false,openWorldHint:false}};
export function attachTransfers(app,provider,config,now=Date.now){
  const links=new Map(),sessions=new Map();
  const sweep=()=>{for(const table of [links,sessions])for(const [key,row]of table)if(row.until<=now()||row.grant.revoked)table.delete(key);};
  const open=grant=>{sweep();if(links.size>=128)throw Error('전송 화면 요청이 많습니다. 잠시 후 다시 요청하세요.');const code=random();links.set(code,{grant,until:now()+300000});return {url:config.publicUrl+'/files/open?code='+code,expires_in:300,instructions:'이 링크를 열어 파일 또는 폴더를 선택하세요. 같은 WebMCP 로그인을 사용합니다.'};};
  const auth=(req,res,next)=>{sweep();const value=(req.headers.cookie||'').split(';').map(x=>x.trim()).find(x=>x.startsWith(cookieName+'='))?.slice(cookieName.length+1),row=sessions.get(value);if(!row||row.grant.revoked||row.grant.expires<=now())return res.status(401).json({error:'전송 화면 링크를 다시 요청하세요.'});req.filesSession=row;next();};
  async function call(row,name,args={}){
    if(!allowed.has(name))throw Error('전송 도구만 호출할 수 있습니다.');
    const reply=await provider.upstream('/mcp',{cookie:row.grant.cookie,body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/call',params:{name,arguments:args}})});
    if(reply.status===401){provider.drop(row.grant);const e=Error('원격제어 연결이 해제됐습니다.');e.status=401;throw e;}
    if(reply.status!==200)throw Error('원격 PC에 연결할 수 없습니다. 상태를 확인한 뒤 재개하세요.');
    const rpc=JSON.parse(reply.body);if(rpc.error||rpc.result?.isError)throw Error(rpc.result?.content?.[0]?.text||rpc.error?.message||'전송 실패');
    return JSON.parse(rpc.result.content[0].text);
  }
  app.get('/files/open',(req,res)=>{sweep();const code=req.query.code,row=links.get(code);if(!row)return res.status(401).send('전송 화면 링크가 만료됐습니다. 새 링크를 요청하세요.');links.delete(code);if(sessions.size>=128)return res.status(503).send('전송 화면이 많습니다. 잠시 후 다시 요청하세요.');const id=random();sessions.set(id,{grant:row.grant,csrf:random(),until:now()+3600000});res.cookie(cookieName,id,{httpOnly:true,secure:true,sameSite:'lax',path:'/',maxAge:3600000});res.set('Referrer-Policy','no-referrer');res.redirect(303,'/files');});
  app.get('/files',auth,async(req,res,next)=>{try{const status=await call(req.filesSession,'pc_get_status');res.set('Content-Security-Policy',"default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'");res.type('html').send(page(req.filesSession.csrf,status));}catch(e){next(e);}});
  app.get('/files/client.js',auth,(req,res)=>res.type('application/javascript').send(readFileSync(new URL('./transfer-client.mjs',import.meta.url),'utf8')));
  app.post('/files/api',auth,(req,res,next)=>{if(req.get('origin')!==config.publicUrl||!same(req.get('x-rc-files-csrf'),req.filesSession.csrf))return res.status(403).json({error:'전송 화면에서 다시 시도하세요.'});next();},express.json({limit:'2mb'}),async(req,res)=>{try{res.json(await call(req.filesSession,req.body?.name,req.body?.arguments||{}));}catch(e){res.status(e.status||400).json({error:e.message});}});
  app.get('/files/download',auth,async(req,res,next)=>{
    try{
      const id=req.query.id;if(!/^[a-f0-9]{32}$/.test(id||''))return res.status(400).send('전송 ID 오류');
      const row=await call(req.filesSession,'pc_get_transfer_status',{transfer_id:id});if(row.status!=='ready'||!row.kind.endsWith('-download'))return res.status(409).send('다운로드 준비가 완료되지 않았습니다.');
      const headers={Cookie:req.filesSession.grant.cookie};if(req.get('range'))headers.Range=req.get('range');
      const upstream=request({hostname:'127.0.0.1',port:config.lanPort,path:'/transfer-download?id='+id,method:'GET',headers},reply=>{
        if(![200,206].includes(reply.statusCode)){reply.resume();res.status(reply.statusCode===401?401:409).send('다운로드 원본 또는 전송 상태가 변경됐습니다. 다시 준비하세요.');return;}
        res.status(reply.statusCode);for(const key of ['content-type','content-length','content-disposition','content-range','accept-ranges','x-content-sha256'])if(reply.headers[key])res.set(key,reply.headers[key]);
        reply.on('error',()=>res.destroy());reply.pipe(res);
      });
      // 유휴 연결만 끊고 활성 스트리밍의 전체 시간에는 제한을 두지 않는다.
      upstream.setTimeout(120000,()=>upstream.destroy(Error('다운로드 연결 시간 초과')));upstream.on('error',e=>{if(res.headersSent)res.destroy();else next(e);});res.once('close',()=>upstream.destroy());upstream.end();
    }catch(e){next(e);}
  });
  return {open};
}
function page(csrf,status){return `<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="csrf" content="${html(csrf)}"><title>원격제어 · 파일 전송</title><style>[hidden]{display:none!important}body{font:16px system-ui;background:#101a29;color:#edf3fc;margin:0;padding:24px}main{max-width:800px;margin:auto}section{background:#1c2c42;border-radius:14px;padding:22px;margin:18px 0}h1{font-size:26px}h2{font-size:20px}label{display:block;margin:12px 0}input:not([type=file]),select{box-sizing:border-box;width:100%;padding:12px;font:inherit;border:1px solid #8091ab;border-radius:7px}input[type=file]{max-width:100%}button,a.download{display:inline-block;font:inherit;background:#78d9ee;color:#10202c;border:0;border-radius:8px;padding:12px 18px;margin:8px 4px 0 0;cursor:pointer}button.secondary{background:#3b506b;color:white}button:disabled{opacity:.5;cursor:default}small,p{line-height:1.6;overflow-wrap:anywhere}progress{width:100%;height:20px}#log{white-space:pre-wrap;overflow-wrap:anywhere;color:#bbe2f3}code{overflow-wrap:anywhere}</style></head><body><main><h1>파일 · 폴더 전송</h1><p>대상 PC: <strong>${html(status.hostname)}</strong> · v${html(status.version)}</p><p>허용 폴더: <code id="roots">${html(status.allowed_roots.join(' | '))}</code></p><section><h2>이 기기 → 원격 PC</h2><label>전송 종류<select id="kind"><option value="file">파일</option><option value="folder">폴더</option></select></label><label>파일 / 폴더 선택<input id="pick" type="file"><input id="folder-pick" type="file" webkitdirectory multiple hidden></label><button id="directory-picker" class="secondary" hidden>폴더 선택 (빈 폴더 포함)</button><label>원격 대상 경로<input id="target" placeholder="파일 전체 경로 또는 새 폴더 경로"></label><label>기존 파일을 교체할 때 현재 SHA-256<input id="expected" placeholder="새 파일은 비워 두세요"></label><button id="upload">전송 / 재개</button><button id="pause" class="secondary">일시 정지</button><progress id="progress" value="0" max="1"></progress><p>파일은 4GiB, 폴더 합계는 8GiB까지입니다. 중단한 경우 같은 파일·폴더와 경로를 다시 선택하고 재개하세요. 전송 후 검증이 끝나야 대상 경로에 반영됩니다.</p></section><section><h2>원격 PC → 이 기기</h2><label>전송 종류<select id="download-kind"><option value="file">파일</option><option value="folder">폴더 ZIP</option></select></label><label>받을 원격 경로<input id="source" placeholder="허용 폴더 안의 전체 경로"></label><button id="prepare">다운로드 준비</button><a id="download" class="download" hidden>파일 받기</a><p id="checksum"></p><small>준비된 원본의 SHA-256을 확인한 뒤 스트리밍합니다. 폴더 ZIP에는 하위 파일과 빈 폴더가 포함됩니다.</small></section><p id="log" role="status" aria-live="polite">파일 또는 폴더를 선택하세요.</p></main><script type="module" src="/files/client.js"></script></body></html>`;}
