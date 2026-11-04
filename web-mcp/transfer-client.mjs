import { Sha256 } from './sha256.mjs';
export { Sha256 } from './sha256.mjs';
if(typeof document!=='undefined'){
const $=id=>document.getElementById(id),chunk=262144;let paused=false,busy=false,directoryFiles=null;
const log=text=>{$('log').textContent=text;};
async function api(name,args){const r=await fetch('/files/api',{method:'POST',headers:{'Content-Type':'application/json','X-RC-Files-CSRF':document.querySelector('meta[name=csrf]').content},body:JSON.stringify({name,arguments:args})});const data=await r.json();if(!r.ok)throw Error(data.error||'전송 실패');return data;}
const hash=data=>new Sha256().update(data).digest();
async function fileHash(file){const state=new Sha256();for(let at=0;at<file.size;at+=chunk){if(paused)throw Error('일시 정지했습니다. 같은 파일을 선택하고 재개하세요.');state.update(new Uint8Array(await file.slice(at,at+chunk).arrayBuffer()));}return state.digest();}
function base64(data){let s='';for(let at=0;at<data.length;at+=8192)s+=String.fromCharCode(...data.subarray(at,at+8192));return btoa(s);}
const key=target=>'rc-transfer:'+target;
$('kind').onchange=()=>{const folder=$('kind').value==='folder';$('pick').hidden=folder;$('folder-pick').hidden=!folder;directoryFiles=null;};
if(typeof showDirectoryPicker==='function'){$('directory-picker').hidden=false;$('directory-picker').onclick=async()=>{try{const root=await showDirectoryPicker(),files=[];async function walk(dir,prefix=''){let count=0;for await(const entry of dir.values()){count++;const path=prefix+entry.name;if(entry.kind==='file')files.push({file:await entry.getFile(),path});else{files.push({path,directory:true});await walk(entry,path+'/');}if(files.length>10000)throw Error('파일·폴더는 10000개까지입니다.');}}await walk(root);directoryFiles=files;$('kind').value='folder';$('pick').hidden=true;$('folder-pick').hidden=false;log('선택한 폴더: '+root.name+' · '+files.length+'개 항목');}catch(e){log(e.message);}};}
$('pick').onchange=$('folder-pick').onchange=()=>{directoryFiles=null;log('선택했습니다. 대상 경로를 입력하고 전송하세요.');};
$('pause').onclick=()=>{paused=true;log('현재 조각을 마친 뒤 일시 정지합니다.');};
$('upload').onclick=async()=>{if(busy)return;busy=true;paused=false;$('upload').disabled=true;try{
 const kind=$('kind').value,target=$('target').value.trim();if(!target)throw Error('원격 대상 경로를 입력하세요.');
 const selected=kind==='folder'?(directoryFiles||Array.from($('folder-pick').files,file=>({file,path:file.webkitRelativePath.split('/').slice(1).join('/')}))):Array.from($('pick').files,file=>({file,path:''}));if((!selected.length&&directoryFiles===null)||kind==='file'&&selected.length!==1)throw Error('전송할 파일 또는 폴더를 선택하세요.');
 const total=selected.reduce((n,x)=>n+(x.file?.size||0),0);if(total>8589934592||selected.some(x=>x.file?.size>4294967296))throw Error('파일 4GiB, 폴더 합계 8GiB를 초과했습니다.');
 const manifest=[];for(const x of selected){log('검증값 계산: '+(x.path||x.file.name));manifest.push({path:x.path,size:x.file?.size||0,sha256:x.directory?'':await fileHash(x.file),directory:!!x.directory});}
 let saved;try{saved=JSON.parse(sessionStorage.getItem(key(target)));}catch{}
 const signature=JSON.stringify({kind,manifest,expected:$('expected').value.trim()});if(!saved||saved.signature!==signature){saved={signature,id:crypto.randomUUID().replaceAll('-','')};sessionStorage.setItem(key(target),JSON.stringify(saved));}
 let state=await api('pc_begin_upload',{path:target,kind,files:manifest,expected_sha256:$('expected').value.trim(),request_id:saved.id});if(state.status==='completed'){log('이미 전송·검증을 완료했습니다.');sessionStorage.removeItem(key(target));return;}
 let done=state.files.reduce((n,x)=>n+x.received,0);$('progress').max=total||1;$('progress').value=done;
 for(const x of selected){if(x.directory)continue;let at=state.files.find(y=>y.path===x.path).received;while(at<x.file.size){if(paused)throw Error('일시 정지했습니다. 같은 파일·폴더와 경로로 재개하세요.');const bytes=new Uint8Array(await x.file.slice(at,at+chunk).arrayBuffer());const reply=await api('pc_write_upload_chunk',{transfer_id:state.transfer_id,relative_path:x.path,offset:at,data_base64:base64(bytes),sha256:hash(bytes)});done+=reply.next_offset-at;at=reply.next_offset;$('progress').value=done;log('전송 중: '+done.toLocaleString()+' / '+total.toLocaleString()+' 바이트');}}
 await api('pc_finish_upload',{transfer_id:state.transfer_id});sessionStorage.removeItem(key(target));$('progress').value=$('progress').max;log('전송과 SHA-256 검증을 완료했습니다. '+target);
}catch(e){log(e.message+'\n중단된 업로드는 같은 파일·경로로 재개할 수 있습니다.');}finally{busy=false;$('upload').disabled=false;}};
$('prepare').onclick=async()=>{$('prepare').disabled=true;$('download').hidden=true;try{let state=await api('pc_prepare_download',{path:$('source').value.trim(),kind:$('download-kind').value});while(state.status==='preparing'){log('폴더 ZIP 준비 중…');await new Promise(r=>setTimeout(r,1000));state=await api('pc_get_transfer_status',{transfer_id:state.transfer_id});}if(state.status!=='ready')throw Error(state.error||'다운로드 준비 실패');$('download').href='/files/download?id='+state.transfer_id;$('download').hidden=false;$('checksum').textContent='SHA-256: '+state.sha256+' · '+state.size.toLocaleString()+' 바이트';log('준비 완료. 파일 받기를 눌러 저장하세요.');}catch(e){log(e.message);}finally{$('prepare').disabled=false;}};
}
