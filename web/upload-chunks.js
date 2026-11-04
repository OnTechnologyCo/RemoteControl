// Browser upload V2: authenticated 2 MiB multipart chunks, per-chunk SHA256,
// offset verification, retry/status reconciliation and page-reload resume.
// Keep every HTTP request small to fit Cloudflare's per-request upload ceiling.
// Uses existing viewer.js file queue and the same authenticated /fs/ origin.
'use strict';
var UPLOAD_CHUNK_BYTES = 2 * 1024 * 1024;
var UPLOAD_MIN_CHUNKED_BYTES = 8 * 1024 * 1024;

function chunkUploadStoreKey(job, task) {
  return 'rc-chunk-v2:' + JSON.stringify([job.pc, job.dir, task.path, task.file.size, task.file.lastModified]);
}
function chunkUploadSaved(key) {
  try {
    var id = localStorage.getItem(key);
    return id && /^[0-9a-f]{32}$/i.test(id) ? id : '';
  } catch (e) { return ''; }
}
function chunkUploadSave(key, id) {
  try { if (id) localStorage.setItem(key, id); else localStorage.removeItem(key); } catch (e) { }
}
function chunkUploadUrl(job, endpoint, args) {
  return '/fs/upload/' + endpoint + '?' + q(args, job.pc);
}
function chunkUploadRequest(job, task, endpoint, args, method, body, timeout, progress, complete) {
  if (job.canceled || uploadJob !== job) return;
  var xhr = new XMLHttpRequest();
  job.xhr = xhr;
  xhr.timeout = timeout || 90000;
  xhr.open(method || 'POST', chunkUploadUrl(job, endpoint, args), true);
  xhr.setRequestHeader('X-RC-Upload', '1');
  if (progress) xhr.upload.onprogress = progress;
  var ended = false;
  function respond(err, data, status) {
    if (ended || job.canceled || uploadJob !== job) return;
    ended = true;
    if (job.xhr === xhr) job.xhr = null;
    complete(err, data, status);
  }
  xhr.onload = function () {
    var data = null;
    try { data = JSON.parse(xhr.responseText); } catch (e) { }
    if (xhr.status !== 200 || !data || typeof data.ok !== 'boolean')
      return respond('서버 응답을 확인할 수 없습니다 (HTTP ' + xhr.status + ').', data, xhr.status);
    if (!data.ok) return respond(data.err || '업로드 요청이 거부되었습니다.', data, xhr.status);
    respond(null, data, xhr.status);
  };
  xhr.onerror = function () { respond('전송 연결이 끊겼습니다.', null, 0); };
  xhr.ontimeout = function () { respond('서버 응답 대기 시간이 초과됐습니다.', null, 0); };
  xhr.onabort = function () { respond('전송이 취소됐습니다.', null, 0); };
  try { xhr.send(body === undefined ? null : body); }
  catch (e) { respond('업로드 전송 실패: ' + e.message, null, 0); }
}
function chunkUploadSha256(blob) {
  // Read only the current 2 MiB chunk, never the whole file.
  return blob.arrayBuffer().then(function (buffer) {
    function fallback() {
      // Web Crypto is unavailable on ordinary HTTP origins. Reuse the file
      // portal's SHA-256 without weakening the server's checksum validation.
      return import('/sha256.mjs').then(function (module) {
        var hash = new module.Sha256(), data = new Uint8Array(buffer), at = 0;
        return new Promise(function (resolve, reject) {
          function step() {
            try {
              var end = Math.min(at + 65536, data.length);
              hash.update(data.subarray(at, end));
              at = end;
              if (at < data.length) setTimeout(step, 0);
              else resolve(hash.digest());
            } catch (error) { reject(error); }
          }
          step(); // Yield between 64 KiB batches so screen/control events run.
        });
      });
    }
    if (!window.crypto || !window.crypto.subtle) return fallback();
    return Promise.resolve().then(function () {
      return window.crypto.subtle.digest('SHA-256', buffer);
    }).then(function (digest) {
      return Array.prototype.map.call(new Uint8Array(digest), function (b) {
        return ('0' + b.toString(16)).slice(-2);
      }).join('');
    }, fallback);
  });
}
function cancelChunkUpload(job) {
  var current = job.chunkUpload;
  if (!current) return;
  job.chunkUpload = null;
  chunkUploadSave(current.key, '');
  if (!current.id) return;
  var xhr = new XMLHttpRequest();
  try {
    xhr.open('POST', chunkUploadUrl(job, 'cancel', { id: current.id }), true);
    xhr.setRequestHeader('X-RC-Upload', '1');
    xhr.timeout = 10000;
    xhr.send(null);
  } catch (e) { }
}
function uploadChunked(job, task) {
  var file = task.file;
  var resumeKey = chunkUploadStoreKey(job, task);
  var previous = chunkUploadSaved(resumeKey);
  var state = { key: resumeKey, id: '', offset: 0, retries: 0 };
  job.chunkUpload = state;

  function active() { return !job.canceled && uploadJob === job && job.chunkUpload === state; }
  function display(offset) {
    if (!active()) return;
    var fraction = job.total ? (job.sent + offset) / job.total : 0;
    fsProg.firstChild.style.width = Math.min(100, fraction * 100) + '%';
    uploadStatus('파일 ' + job.done + '/' + job.files + '개 · ' +
      bytes(job.sent + offset) + '/' + bytes(job.total) + ' · ' + task.path +
      ' (분할 전송' + (state.retries ? ', 재시도 ' + state.retries : '') + ')');
  }
  function stop(reason) {
    if (!active()) return;
    job.chunkUpload = null; // Keep saved ID to resume after reselect/reload.
    uploadError(job, task.path, reason + ' · 같은 파일을 다시 선택하면 전송 위치부터 재개합니다.');
    finishUpload(job, true);
  }
  function protocolError(err, data, status) {
    if (status === 413) return '프록시에서 분할 요청도 거부했습니다 (HTTP 413).';
    if (status === 404) return '대상 PC 원격제어 프로그램에 분할 업로드 기능이 없습니다. 최신 버전으로 업데이트해 주세요.';
    if (status === 401 || status === 403) return '로그인 인증이 만료되었거나 분할 업로드 요청이 거부되었습니다.';
    return err || (data && data.err) || '원격 파일 업로드에 실패했습니다.';
  }
  function retry(reason) {
    if (!active()) return;
    if (++state.retries > 5) return stop(reason + ' (자동 재시도 5회 초과)');
    display(state.offset);
    // The previous request may have been committed before the response was lost.
    // Ask for server position, never blindly replay an already committed piece.
    setTimeout(function () {
      if (!active()) return;
      chunkUploadRequest(job, task, 'status', { id: state.id }, 'GET', undefined, 15000, null,
        function (err, data, status) {
          if (!active()) return;
          if (err) {
            if (status === 404 || status === 401 || status === 403 ||
                (data && (data.code === 'missing' || data.code === 'auth'))) return stop(protocolError(err, data, status));
            return retry(reason);
          }
          if (!data || typeof data.offset !== 'number' || data.offset < 0 ||
              data.offset > file.size || data.offset % UPLOAD_CHUNK_BYTES !== 0 && data.offset !== file.size)
            return stop('서버의 전송 위치가 올바르지 않습니다.');
          state.offset = data.offset;
          if (data.done) return complete(data);
          uploadPart();
        });
    }, Math.min(16000, 1000 * Math.pow(2, state.retries - 1)));
  }
  function start(id, resetOnce) {
    if (!active()) return;
    var args = { p: job.dir, r: task.parent, n: file.name, size: file.size };
    if (id) args.id = id;
    chunkUploadRequest(job, task, 'start', args, 'POST', null, 20000, null,
      function (err, data, status) {
        if (!active()) return;
        if (err) {
          // A session from before the server reboot may have been discarded.
          if (id && !resetOnce && data && data.code === 'missing') {
            chunkUploadSave(resumeKey, ''); return start('', true);
          }
          return stop(protocolError(err, data, status));
        }
        if (!data.id || !/^[a-f0-9]{32}$/i.test(data.id) ||
            data.size !== file.size || !Number.isSafeInteger(data.offset) ||
            data.offset < 0 || data.offset > file.size ||
            (data.offset % UPLOAD_CHUNK_BYTES !== 0 && data.offset !== file.size))
          return stop('업로드 시작 응답이 올바르지 않습니다.');
        state.id = data.id;
        state.offset = data.offset;
        chunkUploadSave(resumeKey, state.id);
        display(state.offset);
        if (data.done) complete(data);
        else uploadPart();
      });
  }
  function uploadPart() {
    if (!active()) return;
    if (state.offset >= file.size) return finish();
    var offset = state.offset;
    var end = Math.min(file.size, offset + UPLOAD_CHUNK_BYTES);
    var segment = file.slice(offset, end);
    display(offset);
    chunkUploadSha256(segment).then(function (sha256) {
      if (!active() || state.offset !== offset) return;
      var form = new FormData();
      form.append('f', segment, 'segment');
      chunkUploadRequest(job, task, 'chunk', { id: state.id, offset: offset, sha256: sha256 },
        'POST', form, 150000, function (event) {
          if (!active() || !event.lengthComputable || state.offset !== offset) return;
          var current = Math.min(segment.size, segment.size * event.loaded / event.total);
          display(offset + current);
        }, function (err, data, status) {
          if (!active()) return;
          if (err) {
            if (status === 413 || status === 401 || status === 403 || status === 404 ||
                (data && (data.code === 'auth' || data.code === 'size' || data.code === 'boundary' ||
                          data.code === 'length' || data.code === 'missing'))) return stop(protocolError(err, data, status));
            return retry(err);
          }
          if (data.offset !== end || data.id !== state.id) return retry('전송 위치 확인에 실패했습니다.');
          state.offset = end;
          state.retries = 0;
          display(end);
          // Yield between chunks; allow screen/input/UI to remain responsive.
          setTimeout(uploadPart, 0);
        });
    }).catch(function (err) { stop(err.message || '조각 해시를 계산하지 못했습니다.'); });
  }
  function finish() {
    if (!active()) return;
    uploadStatus('파일 ' + (job.done + 1) + '/' + job.files + '개 · 저장 및 무결성 검증 중… ' + task.path);
    chunkUploadRequest(job, task, 'finish', { id: state.id }, 'POST', null, 600000, null,
      function (err, data, status) {
        if (!active()) return;
        if (err) {
          if (status === 401 || status === 403 || status === 404) return stop(protocolError(err, data, status));
          return retry(err); // Reconcile completed result after a lost commit reply.
        }
        if (!data.done || data.size !== file.size || data.offset !== file.size)
          return stop('저장 완료 검증이 실패했습니다.');
        complete(data);
      });
  }
  function complete(data) {
    if (!active()) return;
    chunkUploadSave(resumeKey, '');
    job.done++;
    job.sent += file.size;
    display(0);
    job.chunkUpload = null;
    uploadNext(job);
  }
  // The server will return a fresh ID if the process was restarted and lost
  // the old in-memory entry. A previously completed transfer is idempotent.
  start(previous, false);
}
