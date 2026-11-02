// 원격 화면 뷰어.
//
// 모바일은 트랙패드 방식이다. 노트북 터치패드처럼 "민 만큼 커서가 움직인다".
// 화면을 직접 찍는 방식은 휴대폰에서 쓸 수 없다 - 손가락이 커서를 가려서
// 어디를 누르는지 안 보이고, 작은 버튼은 정확히 찍을 수도 없다.
// 그래서 커서 위치를 이쪽에서 들고 있다가 움직인 양만 더해서 보낸다.

'use strict';

var TILE = 128;

var cv = document.getElementById('screen');
var cx = cv.getContext('2d', { alpha: false });
var stage = document.getElementById('stage');
var world = document.getElementById('world');
var curImg = document.getElementById('cur');
var jpegDecoder = typeof JpegDecoder !== 'undefined' && typeof createImageBitmap !== 'undefined' ? new JpegDecoder() : null;
var videoTransport = typeof RemoteVideo !== 'undefined' ? new RemoteVideo({
  world: world, cursor: curImg, send: send, view: function () { return viewId; },
  mode: function (mode) { document.getElementById('mWay').textContent = mode; },
  stats: function (bytes, frames) { rxBytes += bytes; rxFrames += frames; }
}) : null;


var W = 0, H = 0, cols = 0, rows = 0;
var scale = 1, offX = 0, offY = 0;
var fit = true;

var ws = null, alive = false;
var inputReady = false, viewId = 0, firstInit = true, supportsFlow = false;
var renderGen = 0, tileVersions = {}, packetJobs = [];
var heldButtons = {}, heldKeys = {};
var isTouch = matchMedia('(pointer: coarse)').matches;
if (isTouch) document.body.classList.add('touch');

// 원격 커서 위치. 트랙패드 방식이라 이쪽이 실제 위치를 기억해야 한다.
var curX = 0, curY = 0;
var hotX = 0, hotY = 0;

// 손가락을 민 거리 대비 커서가 움직이는 비율.
//
// 처음에는 1.6 이었는데 휴대폰에서 "너무 빨라서 못 쓰겠다" 는 말을 들었다.
// 화면을 줄여서 보고 있으면 scale 로 한 번 더 나누기 때문에 실제 배율은 더 커진다.
// 기본을 낮추고, 손가락 속도에 따라 배율을 바꾸는 방식(가속)을 함께 넣었다.
// 천천히 밀면 정확하게, 빠르게 튕기면 멀리 간다. 노트북 터치패드가 하는 방식이다.
var SPEEDS = [
  { n: '느림', k: 0.35 },
  { n: '보통', k: 0.60 },
  { n: '빠름', k: 1.00 },
  { n: '아주빠름', k: 1.60 }
];
var si = 1;
try { var sv = parseInt(localStorage.getItem('rcSpeed'), 10); if (sv >= 0 && sv < SPEEDS.length) si = sv; }
catch (e) { }

function msg(title, sub, spin) {
  var m = document.getElementById('msg');
  m.style.display = 'flex';
  m.querySelector('.t').textContent = title;
  document.getElementById('msgSub').innerHTML = sub || '';
  m.querySelector('.spin').style.display = spin === false ? 'none' : 'block';
}
function hideMsg() { document.getElementById('msg').style.display = 'none'; }

// ---------------------------------------------------------------- 보기 변환

function applyTransform() {
  world.style.transform = 'translate(' + offX + 'px,' + offY + 'px) scale(' + scale + ')';
  drawCursor();
}

/// 커서를 화면 위에 얹는다. 그림과 따로 움직이므로 회선이 느려도 즉시 따라온다.
///
/// 크기는 "화면에 몇 픽셀로 보일지" 를 정해 놓고 거꾸로 계산한다.
/// 그냥 1/scale 로 되돌리면 축소해서 볼 때 커서만 거대해진다 (실제로 그랬다).
var CUR_ON_SCREEN = 18;      // 휴대폰에서 보일 크기(CSS 픽셀)

function drawCursor() {
  if (curImg.hidden) return;

  var k = 1;

  if (isTouch) {
    var nat = curImg.naturalWidth || 32;
    k = CUR_ON_SCREEN / (nat * (scale > 0 ? scale : 1));
    if (k < 0.4) k = 0.4;
    if (k > 1.6) k = 1.6;
  }

  curImg.style.transform =
    'translate(' + (curX - hotX * k) + 'px,' + (curY - hotY * k) + 'px) scale(' + k + ')';
}

function doFit() {
  if (!W || !H) return;
  var sw = stage.clientWidth, sh = stage.clientHeight;
  if (sw <= 0 || sh <= 0) return;

  scale = Math.min(sw / W, sh / H);
  offX = (sw - W * scale) / 2;
  offY = (sh - H * scale) / 2;
  applyTransform();
}

function zoomAt(px, py, factor) {
  var old = scale;
  scale = Math.max(0.1, Math.min(6, scale * factor));

  var r = stage.getBoundingClientRect();
  var lx = px - r.left, ly = py - r.top;

  // 손가락이 가리키던 지점이 제자리에 있도록 이동량을 보정한다.
  var k = scale / old;
  offX = lx - (lx - offX) * k;
  offY = ly - (ly - offY) * k;

  fit = false;
  applyTransform();
}

/// 커서를 상대적으로 움직인다 (트랙패드).
///
/// dt 는 직전 손가락 움직임과의 시간 간격. 이걸로 손가락 속도를 알아내
/// 천천히 밀 때는 덜, 빠르게 튕길 때는 더 움직이게 한다.
var lastMoveAt = 0;

function moveBy(dxScreen, dyScreen) {
  var now = performance.now();
  var dt = Math.max(4, Math.min(64, now - lastMoveAt));
  lastMoveAt = now;

  var base = SPEEDS[si].k;

  // 손가락 속도(화면 픽셀/ms). 0.3 이하는 정밀 조작, 2.0 이상은 크게 옮기려는 동작.
  var v = Math.hypot(dxScreen, dyScreen) / dt;
  var gain = v < 0.25 ? 0.65
           : v > 2.0 ? 2.0
           : 0.65 + (v - 0.25) * 0.77;

  var k = base * gain / scale;

  curX += dxScreen * k;
  curY += dyScreen * k;

  curX = Math.max(0, Math.min(W - 1, curX));
  curY = Math.max(0, Math.min(H - 1, curY));

  drawCursor();
  sendMove();
  keepCursorVisible();
}

// 손가락은 초당 수십 번 움직인다. 그때마다 보내면 회선만 막히고 커서는 더 늦게 간다.
// 마지막 값만 들고 있다가 다음 화면 그릴 때 한 번 보낸다.
var movePending = false;
var moveSeq = 0;

function sendMove() {
  if (!alive || !inputReady || movePending) return;
  movePending = true;
  var gen = wsGen;
  function tickMove() {
    if (gen !== wsGen || !movePending) return;
    if (ws && ws.bufferedAmount > 4096) { requestAnimationFrame(tickMove); return; }
    flushMove();
  }
  requestAnimationFrame(tickMove);
}

function flushMove() {
  if (!movePending) return;
  movePending = false;
  if (!alive || !inputReady || !ws || ws.readyState !== 1) return;
  moveSeq++;
  ws.send(JSON.stringify({ t: 'move', x: Math.round(curX), y: Math.round(curY), n: moveSeq, view: viewId }));
}

/// 확대한 상태에서 커서가 화면 밖으로 나가면 따라가서 보여 준다.
function keepCursorVisible() {
  if (fit) return;

  var sw = stage.clientWidth, sh = stage.clientHeight;
  var sx = curX * scale + offX, sy = curY * scale + offY;
  var pad = 40, moved = false;

  if (sx < pad) { offX += pad - sx; moved = true; }
  else if (sx > sw - pad) { offX -= sx - (sw - pad); moved = true; }

  if (sy < pad) { offY += pad - sy; moved = true; }
  else if (sy > sh - pad) { offY -= sy - (sh - pad); moved = true; }

  if (moved) applyTransform();
}

// 데스크톱은 화면 좌표를 그대로 쓴다 (마우스가 있으니 찍는 편이 자연스럽다).
function toRemote(clientX, clientY) {
  var r = cv.getBoundingClientRect();
  var x = (clientX - r.left) / scale;
  var y = (clientY - r.top) / scale;
  return {
    x: Math.max(0, Math.min(W - 1, Math.round(x))),
    y: Math.max(0, Math.min(H - 1, Math.round(y)))
  };
}

// ---------------------------------------------------------------- 연결

// 지금 보고 있는 PC. 0 이면 이 PC, 그 외에는 하위 PC 의 번호다.
// 다시 붙을 때도 같은 곳으로 가야 한다 - 끊겼다고 슬그머니 다른 PC 에 붙으면
// 사용자는 모르는 채로 남의 화면을 조작하게 된다.
var curPc = 0;

// 스스로 끊은 것(PC 를 바꾸려고)과 회선이 끊긴 것을 구별한다.
// 안 그러면 PC 를 바꾸는 순간 옛 연결이 "끊겼다" 며 스스로 다시 붙어,
// 소켓 둘이 동시에 살아 화면이 뒤섞인다.
var wsGen = 0;

function connect() {
  if (videoTransport) videoTransport.stop(false);
  var gen = ++wsGen;
  resetInput(false);
  inputReady = false; firstInit = true; supportsFlow = false;
  privacyPending = false; updatePrivacy(false);
  renderGen++; tileVersions = {}; packetJobs = []; moveSeq = 0;
  curPngReady = false; curImg.hidden = true;
  var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';

  ws = new WebSocket(proto + location.host + '/ws' + (curPc ? '?pc=' + curPc : ''));
  ws.binaryType = 'arraybuffer';

  ws.onopen = function () { if (gen !== wsGen) return; alive = true; msg('화면 준비 중…', ''); };
  ws.onmessage = function (ev) { if (gen === wsGen) onMessage(ev); };

  ws.onclose = function () {
    if (gen === wsGen && videoTransport) videoTransport.stop(false);
    if (gen !== wsGen) return;          // 우리가 갈아탄 것이다. 되살리지 않는다

    alive = false; inputReady = false;
    privacyPending = false; updatePrivacy(false);
    resetInput(false); renderGen++; tileVersions = {}; packetJobs = [];
    msg('연결이 끊어졌습니다', '잠시 후 다시 연결합니다…');
    setTimeout(function () { if (gen === wsGen) connect(); }, 1500);
  };

  ws.onerror = function () { };
}

/// 다른 PC 로 갈아탄다. 화면은 완전히 새로 받아야 한다.
function switchTo(pc) {
  cancelUpload();
  fsCur = ''; fsUpTo = null; fsList.innerHTML = '';
  fsBox.classList.remove('on');
  resetInput(true); inputReady = false;
  wsGen++;                              // 옛 소켓의 onclose 가 다시 붙지 못하게

  try { if (ws) ws.close(); } catch (e) { }
  ws = null;
  alive = false;

  curPc = pc;

  // 크기가 다른 PC 로 옮기면 옛 화면이 잠깐 남아 어긋나 보인다.
  // 어차피 init 이 오면 다시 잡으므로 여기서 지워 둔다.
  W = 0; H = 0;
  cx.fillStyle = '#000'; cx.fillRect(0, 0, cv.width, cv.height);

  msg('연결 중…', '');
  connect();
}

var rxBytes = 0, rxFrames = 0, lastStat = performance.now();
var paintStatGen = -1;

function markScreenPaint() {
  var gen = renderGen;
  if (paintStatGen === gen) return;
  paintStatGen = gen;
  requestAnimationFrame(function () {
    if (gen !== renderGen) return;
    paintStatGen = -1;
    rxFrames++;
  });
}

function onMessage(ev) {
  if (typeof ev.data === 'string') {
    var m = JSON.parse(ev.data);

    if (m.t === 'init') {
      var first = W === 0;
      renderGen++; tileVersions = {}; packetJobs = [];
      viewId = m.view || 0;
      TILE = m.tile || 128;
      inputReady = true;
      if (firstInit) {
        firstInit = false;
        supportsFlow = m.flow === 1;
        if (supportsFlow) send({ t: 'stream', auto: qualities[qi].q === 0, tile: m.tilemax === 1024 ? 1024 : 128, probe: true });
        send({ t: 'quality', q: supportsFlow ? qualities[qi].q : (qualities[qi].q || 70) });
        if (isTouch && m.scale === 100) send({ t: 'scale', s: 75 });
      }

      // 축소 배율이 바뀌면 그림 크기가 달라진다. 커서 위치도 같은 비율로 옮겨야
      // 화면과 어긋나지 않는다.
      var kx = W ? m.w / W : 1, ky = H ? m.h / H : 1;

      W = m.w; H = m.h; cols = m.cols; rows = m.rows;
      cv.width = W; cv.height = H;
      cx.fillStyle = '#000'; cx.fillRect(0, 0, W, H);

      if (first) { curX = W / 2; curY = H / 2; }
      else { curX *= kx; curY *= ky; }

      // 휴대폰은 화면이 작다. 원본 해상도를 다 보여 주지도 못하면서
      // 만드는 비용과 보내는 양만 커진다. 처음부터 줄여서 받는다.


      if (m.scale) { sz = SIZES.indexOf(m.scale); if (sz < 0) sz = 0; syncSizeBtn(); }
      if (m.way) document.getElementById('mWay').textContent = m.way === 'Desktop Duplication' ? 'DD' : 'GDI';

      if (m.sizes) fillRes(m.sizes, m.sw, m.sh);

      document.getElementById('bMon').textContent = '화면 ' + (m.screen + 1);
      if (fit) doFit(); else applyTransform();
      hideMsg();
      syncPrivacyButton();
      if (videoTransport) videoTransport.start(m);
    }
    else if (m.t === 'rtc') { if (videoTransport) videoTransport.handle(m); }
    else if (m.t === 'probe') { send({ t: 'probeack', n: m.n }); }
    else if (m.t === 'capture') {
      if (!videoTransport || !videoTransport.active()) document.getElementById('mWay').textContent = m.way === 'Desktop Duplication' ? 'DD' : 'GDI';
    }
    else if (m.t === 'tiles') {
      // 전송 크기만 바뀌었다. 보이던 화면은 유지하고 옛 타일 디코딩만 무효화한다.
      renderGen++; tileVersions = {}; packetJobs = [];
      TILE = m.tile; cols = m.cols; rows = m.rows; viewId = m.view;
    }
    else if (m.t === 'frame') {
      var jobs = packetJobs; packetJobs = [];
      var socket = ws, connection = wsGen, number = m.n;
      // 디코딩까지 끝나야 다음 묶음을 받는다. 소켓 수신만 확인하면 휴대폰 디코더에 쌓인다.
      Promise.all(jobs).then(function () {
        if (connection === wsGen && socket === ws && socket.readyState === 1)
          socket.send(JSON.stringify({ t: 'ack', n: number }));
      });
    }
    else if (m.t === 'pong') {
      document.getElementById('mLat').textContent = Math.round(performance.now() - m.c);
      // Existing latency ping also carries privacy state: no additional poll traffic.
      if (typeof m.privacy === 'boolean' && !privacyPending) updatePrivacy(m.privacy);
    }
    else if (m.t === 'privacy') {
      privacyPending = false;
      updatePrivacy(!!m.on);
      if (m.ok === false) flash(m.reason || '모니터 가리기를 변경하지 못했습니다.');
      else if (m.reason) flash(m.reason);
    }
    else if (m.t === 'fail') {
      if (videoTransport) videoTransport.stop(false);
      // 하위 PC 에 붙지 못했다. 스스로 다시 붙어 봐야 같은 곳에서 또 막히므로
      // 되풀이하지 않고, 이유를 보여 준 뒤 고르는 창으로 돌려보낸다.
      resetInput(false); inputReady = false; renderGen++;
      wsGen++;
      try { ws.close(); } catch (e) { }
      alive = false;

      msg('붙지 못했습니다', m.s + '<br><br>다른 PC 를 고르거나 잠시 뒤 다시 해 보세요.', false);

      setTimeout(function () { hideMsg(); showPick(); }, 2600);
    }
    else if (m.t === 'clip') { gotClip(m.s); }
    else if (m.t === 'clipok') {
      flash(m.v ? '원격 PC 클립보드에 넣었습니다 (' + m.n + '자)'
                : '원격 PC 가 클립보드를 열지 못했습니다. 잠시 뒤 다시 해 보세요.');
    }
    else if (m.t === 'note') {
      // 원격 PC 가 보내는 알림. 원격에서는 그쪽 기록을 볼 수 없으니 여기에 띄운다.
      msg('알림', m.s, false);
      setTimeout(hideMsg, 6000);
    }
    else if (m.t === 'cur') {
      curImg.hidden = !m.v || !curPngReady;

      // 원격에서 온 좌표는 회선을 건너오는 동안 이미 과거다.
      // 손가락을 빠르게 밀면 이쪽이 늘 앞서 있어서, 그 값을 받아들이면
      // 커서가 계속 뒤로 끌려간다 (제어가 안 되는 것처럼 느껴진다).
      //
      // 그래서 "내가 마지막으로 보낸 이동까지 반영된 좌표" 일 때만 받아들인다.
      // 내가 계속 움직이는 동안에는 번호가 안 맞으므로 절대 끌려가지 않고,
      // 손을 뗀 뒤(원격 프로그램이 커서를 스스로 옮겼을 때)에만 따라간다.
      if (!movePending && m.n === moveSeq && Math.hypot(m.x - curX, m.y - curY) > 8) {
        curX = m.x; curY = m.y;
      }
      drawCursor();
    }
    return;
  }

  var buf = new DataView(ev.data);
  rxBytes += ev.data.byteLength;

  var p = 0;
  while (p + 6 <= buf.byteLength) {
    var idx = buf.getUint16(p, true); p += 2;
    var len = buf.getUint32(p, true); p += 4;

    // 0xFFFF 는 타일이 아니라 커서 그림. 모양이 바뀔 때만 온다.
    if (idx === 0xFFFF) {
      if (p + 4 + len > buf.byteLength) break;
      hotX = buf.getUint16(p, true); hotY = buf.getUint16(p + 2, true); p += 4;

      setCursorImage(new Uint8Array(ev.data, p, len));
      p += len;
      continue;
    }

    if (p + len > buf.byteLength) break;

    var job = drawTile(idx, new Uint8Array(ev.data, p, len));
    if (supportsFlow) packetJobs.push(job);
    p += len;
  }
}

var curPngReady = false, curUrl = null;

function setCursorImage(bytes) {
  var gen = wsGen;
  var url = URL.createObjectURL(new Blob([bytes], { type: 'image/png' }));

  curImg.onload = function () {
    if (gen !== wsGen) { URL.revokeObjectURL(url); return; }
    curPngReady = true;
    curImg.hidden = false;
    drawCursor();
    if (curUrl) URL.revokeObjectURL(curUrl);
    curUrl = url;
  };

  curImg.src = url;
}

// 확대·축소하는 동안에는 들어온 조각을 그리지 않는다.
//
// JPEG 를 푸는 일은 휴대폰 주 스레드를 제법 잡아먹는다. 손가락을 벌리는 중에
// 그 일이 끼어들면 화면이 손가락을 못 따라와 "확대가 잘 안 되는" 느낌이 된다.
// 손을 떼면 서버에 "전부 다시 보내" 라고 해서 한 번에 새로 그린다.
var gesturing = false, gestureTimer = null;

function setGesturing(on) {
  clearTimeout(gestureTimer);

  if (on) { gesturing = true; return; }

  // 손을 떼자마자 풀면 마지막 관성 프레임이 끼어든다. 아주 잠깐 더 참는다.
  gestureTimer = setTimeout(function () {
    gesturing = false;
    send({ t: 'refresh' });
  }, 120);
}

function drawTile(idx, bytes) {
  if (gesturing || idx >= cols * rows) return Promise.resolve();
  var gen = renderGen, version = (tileVersions[idx] || 0) + 1;
  tileVersions[idx] = version;
  var tx = (idx % cols) * TILE, ty = Math.floor(idx / cols) * TILE;
  function paint(image) {
    // JPEG 디코딩 완료 순서는 수신 순서와 다를 수 있다. 옛 화면/옛 PC로 되감지 않는다.
    if (gen === renderGen && tileVersions[idx] === version && !gesturing) {
      cx.drawImage(image, tx, ty);
      // 한 화면을 수십 패킷으로 나눠 받았다고 FPS가 수십 배로 보이면 안 된다.
      // 실제 타일을 그린 브라우저 갱신 주기만 센다. 커서 패킷/폐기된 타일은 제외한다.
      markScreenPaint();
    }
  }
  if (jpegDecoder) {
    return jpegDecoder.decode(bytes,
      function () { return gen === renderGen && tileVersions[idx] === version && !gesturing; }, paint)
      .then(function (ok) { if (!ok && gen === renderGen) send({ t: 'refresh' }); });
  }
  if (self.createImageBitmap) {
    return createImageBitmap(new Blob([bytes], { type: 'image/jpeg' }))
      .then(function (bm) { try { paint(bm); } finally { if (bm.close) bm.close(); } })
      .catch(function () { if (gen === renderGen) send({ t: 'refresh' }); });
  }
  return new Promise(function (resolve) {
    var img = new Image();
    var url = URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' }));
    function done() { URL.revokeObjectURL(url); resolve(); }
    img.onload = function () { try { paint(img); } finally { done(); } };
    img.onerror = done;
    img.src = url;
  });
}

setInterval(function () {
  var now = performance.now(), dt = (now - lastStat) / 1000;
  if (dt <= 0) return;

  document.getElementById('mFps').textContent = (rxFrames / dt).toFixed(0);
  document.getElementById('mKbps').textContent = (rxBytes / 1024 / dt).toFixed(0);
  document.getElementById('hud').classList.toggle('bad', !alive);

  rxBytes = 0; rxFrames = 0; lastStat = now;
}, 1000);

setInterval(function () { send({ t: 'ping', c: performance.now() }); }, 2000);

function send(o) {
  if (o.t === 'quality' && videoTransport) videoTransport.quality(o.q);
  if (!ws || ws.readyState !== 1) return false;
  var input = o.t === 'btn' || o.t === 'key' || o.t === 'move' || o.t === 'wheel' || o.t === 'text';
  if (input && (!alive || !inputReady)) return false;
  if (input && busyOverlay() && o.d !== false) return false;
  if (o.t === 'btn' || o.t === 'key') {
    var held = o.t === 'btn' ? heldButtons : heldKeys;
    var id = o.t === 'btn' ? o.b : o.vk;
    if (!o.d && !held[id]) return false;
    if (o.t === 'btn' && o.d && held[id]) return false;
    if (o.d) held[id] = true; else delete held[id];
  }
  if (input && o.t !== 'move') flushMove(); // 마지막 이동은 클릭/키보다 앞에 보낸다.
  if (o.t === 'move' || o.t === 'btn' || o.t === 'wheel') o.view = viewId;
  ws.send(JSON.stringify(o));
  return true;
}

function resetInput(notify) {
  movePending = false;
  if (notify && ws && ws.readyState === 1) {
    if (supportsFlow) ws.send(JSON.stringify({ t: 'release' }));
    else {
      // 구형 하위 PC에서도 실제 눌렀던 것만 푼다.
      Object.keys(heldButtons).forEach(function (b) {
        ws.send(JSON.stringify({ t: 'btn', b: +b, d: false, x: Math.round(curX), y: Math.round(curY) }));
      });
      Object.keys(heldKeys).forEach(function (k) { ws.send(JSON.stringify({ t: 'key', vk: +k, d: false })); });
    }
  }
  heldButtons = {}; heldKeys = {};
  if (mods) Object.keys(mods).forEach(function (k) {
    mods[k] = false;
    var button = document.querySelector('.mod[data-vk="' + k + '"]');
    if (button) button.classList.remove('on');
  });
  if (isTouch) {
    clearTimeout(holdTimer);
    dragging = false; last = null; startPt = null; pendingDouble = false;
    lastTapAt = 0; twoStart = null; twoPrev = null; scrollAcc = 0;
  }
  clearTimeout(gestureTimer); gesturing = false;
}

// ---------------------------------------------------------------- 조합키

var mods = { 16: false, 17: false, 18: false, 91: false };

function setMod(vk, on) {
  mods[vk] = on;
  send({ t: 'key', vk: vk, d: on });

  var b = document.querySelector('.mod[data-vk="' + vk + '"]');
  if (b) b.classList.toggle('on', on);
}

function clearMods() {
  for (var vk in mods) if (mods[vk]) setMod(parseInt(vk, 10), false);
}

function click(btn) {
  var x = Math.round(curX), y = Math.round(curY);
  send({ t: 'btn', x: x, y: y, b: btn, d: true });
  send({ t: 'btn', x: x, y: y, b: btn, d: false });
  clearMods();
}

// ---------------------------------------------------------------- 데스크톱 입력

if (!isTouch) {
  cv.addEventListener('mousemove', function (e) {
    var p = toRemote(e.clientX, e.clientY);
    curX = p.x; curY = p.y;
    drawCursor();
    sendMove();
  });

  cv.addEventListener('mousedown', function (e) {
    if (!inputReady || busyOverlay()) return;
    e.preventDefault();
    var p = toRemote(e.clientX, e.clientY);
    curX = p.x; curY = p.y; sendMove();
    send({ t: 'btn', x: p.x, y: p.y, b: e.button, d: true });
  });

  window.addEventListener('mouseup', function (e) {
    if (!heldButtons[e.button]) return;
    var p = toRemote(e.clientX, e.clientY);
    curX = p.x; curY = p.y; sendMove();
    send({ t: 'btn', x: p.x, y: p.y, b: e.button, d: false });
  });

  cv.addEventListener('contextmenu', function (e) { e.preventDefault(); });

  cv.addEventListener('wheel', function (e) {
    e.preventDefault();
    if (e.ctrlKey) { zoomAt(e.clientX, e.clientY, e.deltaY < 0 ? 1.15 : 1 / 1.15); return; }

    var p = toRemote(e.clientX, e.clientY);
    send({ t: 'wheel', x: p.x, y: p.y, d: e.deltaY > 0 ? -120 : 120, h: false });
  }, { passive: false });

  // PC 는 키 코드를 그대로 넘긴다. 한글 조합은 원격 PC 의 자판이 한다.
  // 이쪽 PC 에서 한글 자판을 켜 두면 조합 중 키 코드가 229 로 와서 쓸모가 없다.
  // 그때는 보내지 않는다 (이쪽 한글 자판은 꺼 두고 원격 쪽에서 전환해야 한다).
  window.addEventListener('keydown', function (e) {
    if (e.key === 'F11') return;

    // 덮개가 떠 있는 동안에는 원격으로 넘기지 않는다. 넘기면 그때 누른 키가
    // 엉뚱한 PC 에 들어가고, preventDefault 때문에 Tab 으로 고를 수도 없다.
    if (busyOverlay()) return;

    e.preventDefault();
    if (e.isComposing || e.keyCode === 229) return;
    send({ t: 'key', vk: e.keyCode, d: true });
  });

  window.addEventListener('keyup', function (e) {
    if (busyOverlay()) return;
    e.preventDefault();
    if (e.isComposing || e.keyCode === 229) return;
    send({ t: 'key', vk: e.keyCode, d: false });
  });
}

// ---------------------------------------------------------------- 모바일 입력 (트랙패드)
//
//  한 손가락 밀기          커서 이동
//  탭                      좌클릭
//  빠르게 두 번 탭         더블클릭
//  두 번 탭 후 누른 채 이동 드래그 (누른 상태로 이동, 떼면 놓음)
//  두 손가락 탭            우클릭
//  두 손가락 위아래        휠
//  두 손가락 벌리기/오므리기 화면 확대·축소
//  두 손가락 나란히 끌기    화면 이동

if (isTouch) {
  var TAP_MS = 250;        // 이 시간 안에 떼면 탭으로 본다
  var TAP_SLOP = 12;       // 이 거리 안에서 움직이면 제자리로 본다
  var DBL_MS = 300;        // 두 번째 탭이 이 시간 안에 오면 더블탭

  var DECIDE_PX = 14;      // 두 손가락이 이만큼 움직여야 무슨 동작인지 정한다
  var WHEEL_PX = 20;       // 이만큼 밀 때마다 휠 한 칸

  // 두 손가락 동작은 처음에 한 번만 정하고 뗄 때까지 바꾸지 않는다.
  var G_NONE = 0, G_ZOOM = 1, G_SCROLL = 2, G_PAN = 3;
  var gmode = G_NONE;

  var last = null;         // 직전 손가락 위치
  var startPt = null, startAt = 0, movedFar = false;
  var lastTapAt = 0;
  var dragging = false;    // 두 번 탭 후 누른 채 끄는 중
  var pendingDouble = false;
  var twoStart = null, twoPrev = null, scrollAcc = 0;
  var holdTimer = null;

  function dist(a, b) { return Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY); }
  function mid(a, b) { return { x: (a.clientX + b.clientX) / 2, y: (a.clientY + b.clientY) / 2 }; }

  stage.addEventListener('touchstart', function (e) {
    if (!alive || !inputReady || busyOverlay()) return;
    hideTip();

    if (e.touches.length === 1) {
      var t = e.touches[0];
      last = { x: t.clientX, y: t.clientY };
      startPt = { x: t.clientX, y: t.clientY };
      startAt = Date.now();
      movedFar = false;

      // 직전 탭 직후에 다시 눌렀다면 "두 번 탭" 이다.
      // 이때 손을 떼지 않고 움직이면 드래그가 된다.
      pendingDouble = (Date.now() - lastTapAt) < DBL_MS;

      if (pendingDouble) {
        holdTimer = setTimeout(function () {
          if (!dragging) {
            dragging = true;
            send({ t: 'btn', x: Math.round(curX), y: Math.round(curY), b: 0, d: true });
            if (navigator.vibrate) navigator.vibrate(20);
          }
        }, 140);
      }
    }
    else if (e.touches.length === 2) {
      clearTimeout(holdTimer);
      endDrag();

      // 시작할 때의 값을 붙잡아 둔다. 무엇을 하려는 동작인지는 "처음부터 얼마나
      // 달라졌는가" 로 판단해야 한다. 직전 프레임과의 차이로 매번 새로 판단하면
      // 손가락 떨림 때문에 확대와 스크롤을 왔다 갔다 한다 (그게 지금 증상이었다).
      twoStart = {
        d: dist(e.touches[0], e.touches[1]),
        m: mid(e.touches[0], e.touches[1]),
        at: Date.now()
      };
      twoPrev = { d: twoStart.d, m: twoStart.m };
      gmode = G_NONE;
      scrollAcc = 0;
      last = null;

      setGesturing(true);
    }
    e.preventDefault();
  }, { passive: false });

  stage.addEventListener('touchmove', function (e) {
    if (!alive || !inputReady) return;
    if (e.touches.length === 1 && last) {
      var t = e.touches[0];
      var dx = t.clientX - last.x, dy = t.clientY - last.y;
      last = { x: t.clientX, y: t.clientY };

      if (startPt && Math.hypot(t.clientX - startPt.x, t.clientY - startPt.y) > TAP_SLOP) {
        movedFar = true;
        clearTimeout(holdTimer);

        // 두 번째 탭에서 바로 끌기 시작하면 그것도 드래그로 본다.
        if (pendingDouble && !dragging) {
          dragging = true;
          send({ t: 'btn', x: Math.round(curX), y: Math.round(curY), b: 0, d: true });
        }
      }

      moveBy(dx, dy);
    }
    else if (e.touches.length === 2 && twoStart) {
      var d2 = dist(e.touches[0], e.touches[1]);
      var m2 = mid(e.touches[0], e.touches[1]);

      // 아직 무엇을 하려는지 못 정했으면, 충분히 움직일 때까지 아무것도 하지 않는다.
      // 어설프게 먼저 반응하면 확대하려다 스크롤이 되고 스크롤하려다 확대가 된다.
      if (gmode === G_NONE) {
        var dd = Math.abs(d2 - twoStart.d);                              // 손가락 사이 거리 변화
        var dmx = m2.x - twoStart.m.x, dmy = m2.y - twoStart.m.y;
        var dm = Math.hypot(dmx, dmy);                                   // 두 손가락 중심 이동

        if (dd < DECIDE_PX && dm < DECIDE_PX) { twoPrev = { d: d2, m: m2 }; e.preventDefault(); return; }

        // 벌리거나 오므리면 거리가 크게 변하고 중심은 별로 안 움직인다.
        // 나란히 밀면 그 반대다. 이 둘을 견줘서 한 번만 정하고, 뗄 때까지 바꾸지 않는다.
        if (dd > dm) gmode = G_ZOOM;
        else gmode = Math.abs(dmy) > Math.abs(dmx) * 1.2 ? G_SCROLL : G_PAN;
      }

      var pdx = m2.x - twoPrev.m.x, pdy = m2.y - twoPrev.m.y;

      if (gmode === G_ZOOM) {
        zoomAt(m2.x, m2.y, d2 / twoPrev.d);
      }
      else if (gmode === G_SCROLL) {
        scrollAcc += pdy;
        while (Math.abs(scrollAcc) >= WHEEL_PX) {
          var dir = scrollAcc > 0 ? 120 : -120;
          send({ t: 'wheel', x: Math.round(curX), y: Math.round(curY), d: dir, h: false });
          scrollAcc -= (dir > 0 ? WHEEL_PX : -WHEEL_PX);
        }
      }
      else {
        offX += pdx; offY += pdy; fit = false; applyTransform();
      }

      twoPrev = { d: d2, m: m2 };
    }
    e.preventDefault();
  }, { passive: false });

  stage.addEventListener('touchend', function (e) {
    clearTimeout(holdTimer);

    // 두 손가락 중 하나만 뗐다. 남은 손가락으로 이어서 커서를 움직일 수 있게
    // 기준점을 다시 잡아 준다. 안 그러면 다 뗄 때까지 아무 반응이 없다.
    if (e.touches.length === 1 && twoStart) {
      twoStart = null;
      setGesturing(false);
      last = { x: e.touches[0].clientX, y: e.touches[0].clientY };
      startPt = null;
      e.preventDefault();
      return;
    }

    if (e.touches.length === 0) {
      setGesturing(false);

      if (dragging) {
        endDrag();
      }
      else if (twoStart) {
        // 두 손가락을 거의 움직이지 않고 뗐다 = 우클릭
        if (gmode === G_NONE && Date.now() - twoStart.at < 350) click(2);
        twoStart = null;
      }
      else if (startPt && !movedFar && Date.now() - startAt < TAP_MS) {
        if (pendingDouble) {
          // 빠르게 두 번 = 더블클릭
          click(0); click(0);
          lastTapAt = 0;
        } else {
          click(0);
          lastTapAt = Date.now();
        }
      }

      last = null; startPt = null; pendingDouble = false; twoStart = null;
    }
    e.preventDefault();
  }, { passive: false });

  stage.addEventListener('touchcancel', function () {
    clearTimeout(holdTimer);
    endDrag();
    setGesturing(false);
    last = null; startPt = null; pendingDouble = false; twoStart = null;
  });

  function endDrag() {
    if (!dragging) return;
    dragging = false;
    send({ t: 'btn', x: Math.round(curX), y: Math.round(curY), b: 0, d: false });
  }
}

function hideTip() {
  var t = document.getElementById('tip');
  if (t) t.style.display = 'none';
}
setTimeout(hideTip, 9000);

// ---------------------------------------------------------------- 자판
//
// 자판이 올라오면 브라우저가 보이는 영역을 줄인다. 그대로 두면 화면이 자판에
// 가려서 뭘 하는지 볼 수 없다. visualViewport 로 실제 보이는 높이를 받아
// 앱 전체 높이를 거기에 맞춘다.

var kbd = document.getElementById('kbd');
var app = document.getElementById('app');

function fitViewport() {
  var vv = window.visualViewport;
  var h = vv ? vv.height : window.innerHeight;

  app.style.height = h + 'px';
  if (vv) app.style.transform = 'translateY(' + vv.offsetTop + 'px)';

  if (fit) doFit();
}

if (window.visualViewport) {
  window.visualViewport.addEventListener('resize', fitViewport);
  window.visualViewport.addEventListener('scroll', fitViewport);
}
window.addEventListener('resize', fitViewport);
window.addEventListener('orientationchange', function () { setTimeout(fitViewport, 300); });
fitViewport();

// ---- 글자 입력줄 ----
//
// 실시간으로 한 글자씩 넘기지 않는다.
// 한글은 자판이 자모를 모아 글자를 만드는데, 그 도중 값을 가져가면 조합이 깨진다.
// 자판마다 조합 알림(composition 이벤트)을 주는 정도가 달라서, 그걸 정확히 맞추는
// 방법은 어느 자판에서든 통한다고 장담하기 어렵다.
// 그래서 아예 다 적은 뒤 한 번에 보낸다. 어느 자판이든 확실히 동작하고,
// 보내기 전에 눈으로 확인할 수도 있다.

var typeBar = document.getElementById('type');
var bKbd = document.getElementById('bKbd');
var bSendEnter = document.getElementById('bSendEnter');

// 기본은 "엔터 없음".
// 켜 두었더니 글자를 보낼 때마다 검색이 실행돼 버렸다. 엔터는 사용자가
// 분명히 원할 때만 눌러야 하는 것이지, 덤으로 따라붙으면 안 된다.
var withEnter = false;
try { withEnter = localStorage.getItem('rcEnter') === '1'; } catch (e) { }

function showType(on) {
  typeBar.classList.toggle('on', on);
  bKbd.classList.toggle('on', on);

  if (on) { kbd.focus(); }
  else { kbd.blur(); }

  setTimeout(fitViewport, 60);
}

bKbd.addEventListener('click', function () { showType(!typeBar.classList.contains('on')); });

function syncEnterBtn() {
  bSendEnter.classList.toggle('on', withEnter);
  bSendEnter.textContent = withEnter ? '↵ 켬' : '↵ 끔';
  bSendEnter.title = withEnter
    ? '보낸 뒤 엔터까지 누릅니다 (검색·주소창용)'
    : '글자만 보냅니다. 엔터는 누르지 않습니다';
}

bSendEnter.addEventListener('click', function () {
  withEnter = !withEnter;
  syncEnterBtn();
  try { localStorage.setItem('rcEnter', withEnter ? '1' : '0'); } catch (e) { }
});
syncEnterBtn();

function tapEnter() {
  send({ t: 'key', vk: 13, d: true });
  send({ t: 'key', vk: 13, d: false });
}

/// 적어 둔 것을 한 번에 보낸다.
///
/// 여러 줄이면 줄마다 글자를 보내고 사이에 엔터를 넣는다.
/// 줄바꿈 문자를 글자로 그냥 넣으면 프로그램마다 다르게 받아들여서
/// 어떤 곳에서는 무시되고 어떤 곳에서는 "확인" 이 눌린 것처럼 동작한다.
function flushTyped() {
  var lines = kbd.value.split(/\r\n|\r|\n/);
  kbd.value = '';
  autoGrow();

  for (var i = 0; i < lines.length; i++) {
    if (i > 0) tapEnter();                          // 줄 사이에만 넣는다
    if (lines[i]) send({ t: 'text', s: lines[i] });
  }

  if (withEnter) tapEnter();                        // 마지막 엔터는 켜 두었을 때만

  kbd.focus();      // 이어서 계속 칠 수 있게 자판을 내리지 않는다
}

/// 적은 줄 수에 맞춰 입력칸을 키운다.
function autoGrow() {
  kbd.style.height = 'auto';
  kbd.style.height = Math.min(kbd.scrollHeight, Math.round(window.innerHeight * 0.26)) + 'px';
  setTimeout(fitViewport, 0);
}

kbd.addEventListener('input', autoGrow);

document.getElementById('bSend').addEventListener('click', flushTyped);

kbd.addEventListener('keydown', function (e) {
  // 조합 중인 키는 자판이 알아서 한다. 절대 가로채지 않는다.
  if (e.isComposing || e.keyCode === 229) return;

  // 엔터는 줄바꿈이다. 보내는 것은 [전송] 이 한다.
  // 엔터로 보내게 하면 여러 줄을 적을 수가 없다.
  // 다만 PC 자판에서는 Ctrl+Enter 로 바로 보낼 수 있게 둔다.
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); flushTyped(); return; }

  // 칸이 비었는데 지우기를 눌렀다 = 원격 쪽 글자를 지우려는 것이다.
  if (e.key === 'Backspace' && kbd.value === '') {
    e.preventDefault();
    send({ t: 'key', vk: 8, d: true });
    send({ t: 'key', vk: 8, d: false });
  }
});

Array.prototype.forEach.call(document.querySelectorAll('.mod'), function (b) {
  b.addEventListener('click', function () {
    var vk = parseInt(b.getAttribute('data-vk'), 10);
    setMod(vk, !mods[vk]);
  });
});

Array.prototype.forEach.call(document.querySelectorAll('[data-tap]'), function (b) {
  b.addEventListener('click', function () {
    var vk = parseInt(b.getAttribute('data-tap'), 10);
    send({ t: 'key', vk: vk, d: true });
    send({ t: 'key', vk: vk, d: false });
    clearMods();
  });
});

// ---------------------------------------------------------------- 도구 막대

// 검정색 가림 창은 캡처에서만 제외됩니다. 실제 디스플레이는 가려지지만
// 원격 웹 화면과 마우스·키보드 입력은 계속 사용할 수 있어야 합니다.
var bPrivacy = document.getElementById('bPrivacy');
var privacyOn = false, privacyPending = false;
function syncPrivacyButton() {
  bPrivacy.textContent = privacyOn ? '◻ 화면 복구' : '◼ 모니터 가리기';
  bPrivacy.classList.toggle('on', privacyOn);
  bPrivacy.disabled = !alive || !inputReady || privacyPending;
  bPrivacy.title = privacyOn
    ? '원격 PC의 실제 모니터 표시를 복구합니다 (긴급 해제: Ctrl+Alt+Shift+F12)'
    : '원격 PC의 물리 모니터만 검게 가립니다. 웹 화면은 계속 표시됩니다.';
}
function updatePrivacy(on) { privacyOn = !!on; syncPrivacyButton(); }
bPrivacy.addEventListener('click', function () {
  if (!alive || !inputReady || privacyPending) return;
  if (!privacyOn && !window.confirm('원격 PC의 실제 모니터를 검게 가리겠습니까?\n웹 원격 화면은 계속 표시됩니다.\n연결 종료 시 자동 복구되며, PC 키보드 Ctrl+Alt+Shift+F12로 긴급 해제할 수 있습니다.')) return;
  privacyPending = true; syncPrivacyButton();
  var generation = wsGen;
  setTimeout(function () {
    if (!privacyPending || generation !== wsGen) return;
    privacyPending = false; syncPrivacyButton();
    flash('모니터 가리기 응답이 지연되고 있습니다. 실제 상태를 다시 확인합니다.');
    send({ t: 'privacy', op: 'status' });
  }, 12000);
  if (!send({ t: 'privacy', op: privacyOn ? 'off' : 'on' })) {
    privacyPending = false; syncPrivacyButton();
    flash('연결되지 않아 모니터 가리기를 변경하지 못했습니다.');
  }
});
syncPrivacyButton();

document.getElementById('bFit').addEventListener('click', function () { fit = true; doFit(); });

// 상태 표시는 눌러서 감춘다. 작은 화면에서 그것마저 거슬릴 수 있다.
var hudEl = document.getElementById('hud');
hudEl.addEventListener('click', function () { hudEl.classList.add('off'); });

// ---- 커서 감도 (트랙패드 전용) ----
//
// 손가락을 민 거리 대비 커서가 움직이는 비율이다. 이 값은 moveBy 에서만 쓰이고
// moveBy 는 터치 조작에서만 불린다 - 마우스로 찍는 PC 에서는 좌표를 그대로 보내므로
// 이 단추가 아무 일도 하지 않는다. 그래서 터치일 때만 띄운다.
//
// 이름도 "속도" 가 아니라 "커서" 다. 옆에 [화질]·[크기] 가 있어서 "속도" 라고 적어 두면
// 화면 전송 속도를 조절하는 것으로 읽힌다 (실제로 그렇게 읽혔다).
var bSpeed = document.getElementById('bSpeed');

function syncSpeedBtn() { bSpeed.textContent = '커서 ' + SPEEDS[si].n; }

if (isTouch) {
  bSpeed.hidden = false;

  bSpeed.addEventListener('click', function () {
    si = (si + 1) % SPEEDS.length;
    syncSpeedBtn();
    try { localStorage.setItem('rcSpeed', si); } catch (e) { }
  });

  syncSpeedBtn();
}

// ---- 보내는 그림 크기 ----
//
// 화면이 느릴 때 가장 크게 듣는 손잡이다. 원격 PC 가 화면 한 장을 만드는 비용도,
// 보내는 양도 함께 줄어든다. 휴대폰 화면은 작아서 원본 해상도가 어차피 안 보인다.
var SIZES = [100, 75, 50];
var sz = 1;
var bSize = document.getElementById('bSize');

function syncSizeBtn() { bSize.textContent = '크기 ' + SIZES[sz] + '%'; }

bSize.addEventListener('click', function () {
  sz = (sz + 1) % SIZES.length;
  syncSizeBtn();
  send({ t: 'scale', s: SIZES[sz] });
});
syncSizeBtn();

// ---- 원격 PC 의 화면 해상도 ----
//
// 모니터 없는 PC 는 이게 없으면 작은 화면에 갇힌다. 원격 데스크톱을 끊고 나면
// 윈도우가 화면을 새로 만들면서 가장 작은 크기로 잡아 버리는데, 그 PC 앞에 가서
// 고칠 수가 없다. 여기서 바로 바꾼다. 목록은 그 PC 가 실제로 받아 주는 것만 온다.
var selRes = document.getElementById('selRes');
var resReady = false;

function fillRes(sizes, curW, curH) {
  selRes.innerHTML = '';

  if (!sizes.length) { selRes.style.display = 'none'; return; }
  selRes.style.display = '';

  for (var i = 0; i < sizes.length; i++) {
    var o = document.createElement('option');
    o.value = sizes[i][0] + 'x' + sizes[i][1];
    o.textContent = sizes[i][0] + '×' + sizes[i][1];
    if (sizes[i][0] === curW && sizes[i][1] === curH) o.selected = true;
    selRes.appendChild(o);
  }

  resReady = true;
}

selRes.addEventListener('change', function () {
  if (!resReady) return;

  var p = this.value.split('x');
  if (p.length !== 2) return;

  msg('화면 크기를 바꾸는 중…', '원격 PC 가 화면을 다시 그립니다');
  send({ t: 'screen', w: parseInt(p[0], 10), h: parseInt(p[1], 10) });

  // 못 바꾸는 경우도 있다. 계속 가려 두면 안 되니 스스로 걷는다.
  setTimeout(hideMsg, 6000);
});

// ---- 화질 (JPEG) ----
//
// 이름표를 꼭 붙인다. "보통" 만 적어 두면 옆 단추들과 뭐가 다른지 알 수 없다.
var qualities = [{ n: '자동', q: 0 }, { n: '낮음', q: 40 }, { n: '보통', q: 70 }, { n: '높음', q: 88 }];
var qi = 0;

document.getElementById('bQual').addEventListener('click', function () {
  qi = (qi + 1) % qualities.length;
  this.textContent = '화질 ' + qualities[qi].n;
  send({ t: 'quality', q: supportsFlow ? qualities[qi].q : (qualities[qi].q || 70) });
});

document.getElementById('bMon').addEventListener('click', function () { send({ t: 'nextScreen' }); });

document.getElementById('bOut').addEventListener('click', function () {
  resetInput(true);
  location.href = '/logout';
});

document.addEventListener('visibilitychange', function () {
  if (document.hidden) resetInput(true);
  if (supportsFlow) send({ t: 'viewpause', v: document.hidden });
  if (videoTransport) { if (document.hidden) videoTransport.stop(true); else videoTransport.resume(); }
});
window.addEventListener('blur', function () { resetInput(true); });
window.addEventListener('pagehide', function () { resetInput(true); });

// ---------------------------------------------------------------- PC 고르기

var pick = document.getElementById('pick');
var pickList = document.getElementById('pickList');
var peerData = null;

/// 덮개(고르기·파일·클립보드)가 떠 있으면 키와 마우스를 원격으로 넘기지 않는다.
/// 넘기면 파일 이름을 치는 동안 그 글자가 원격 PC 에 들어가고,
/// preventDefault 때문에 Ctrl+V·Ctrl+C 도 안 먹는다.
function busyOverlay() {
  return pick.classList.contains('on') ||
         document.getElementById('files').classList.contains('on') ||
         document.getElementById('clip').classList.contains('on');
}

function picking() { return pick.classList.contains('on'); }

/// 고르지 않고 물러난다. 이미 보고 있는 화면이 있을 때만 된다 -
/// 아직 아무 데도 안 붙었는데 닫아 버리면 검은 화면만 남는다.
function closePick() {
  if (!alive) return;
  pick.classList.remove('on');
}

function showPick() {
  if (!peerData) return;

  pickList.innerHTML = '';
  document.getElementById('pickSub').textContent =
    '이 PC 와 하위 PC ' + peerData.peers.length + '대';

  document.getElementById('pickNote').innerHTML = alive
    ? '바깥을 누르면 보던 화면으로 돌아갑니다.'
    : '하위 PC 는 이 PC 를 거쳐 연결됩니다.<br>접속하면 그 PC 화면에도 표시됩니다.';

  addPc(0, peerData.self, '이 PC (' + location.host + ')', true, true);

  for (var i = 0; i < peerData.peers.length; i++) {
    var p = peerData.peers[i];
    addPc(p.id, p.name,
          p.pw ? p.addr : p.addr + '  — 비밀번호가 저장되어 있지 않습니다',
          false, p.pw);
  }

  resetInput(true);
  pick.classList.add('on');
}

function addPc(id, name, sub, self, ok) {
  var b = document.createElement('button');
  b.className = 'pc' + (self ? ' self' : '');
  b.type = 'button';
  if (!ok) b.disabled = true;

  var ic = document.createElement('div');
  ic.className = 'ic';
  ic.textContent = self ? '🖥️' : '💻';

  var tx = document.createElement('div');
  tx.className = 'tx';

  var nm = document.createElement('div');
  nm.className = 'nm';
  nm.textContent = name;                // textContent - PC 이름을 사람이 정한다

  var ad = document.createElement('div');
  ad.className = 'ad';
  ad.textContent = sub;

  tx.appendChild(nm);
  tx.appendChild(ad);
  b.appendChild(ic);
  b.appendChild(tx);

  if (ok) {
    b.addEventListener('click', function () {
      pick.classList.remove('on');
      if (id !== curPc || !alive) switchTo(id);
    });
  }

  pickList.appendChild(b);
}

document.getElementById('bPc').addEventListener('click', function () {
  clearMods();                          // 갈아타기 전에 눌린 조합키를 푼다
  showPick();
});

// 카드 바깥을 누르거나 Esc 를 누르면 물러난다.
pick.addEventListener('click', function (e) { if (e.target === pick) closePick(); });
window.addEventListener('keydown', function (e) {
  if (picking() && e.key === 'Escape') closePick();
});

// 하위 PC 가 있는지 먼저 물어본다.
//
// 없으면 고르는 창을 띄우지 않고 바로 붙는다. 고를 것이 하나뿐인데 매번 누르게
// 하면 걸리적거리기만 한다. 브릿지를 안 쓰는 PC 는 예전과 똑같이 동작한다.
function start() {
  var r = new XMLHttpRequest();
  r.open('GET', '/peers', true);
  r.timeout = 6000;

  r.onload = function () {
    try { peerData = JSON.parse(r.responseText); }
    catch (e) { peerData = null; }

    if (peerData && peerData.peers && peerData.peers.length) {
      document.getElementById('bPc').hidden = false;
      hideMsg();
      showPick();
    }
    else connect();
  };

  // 목록을 못 받아도 이 PC 는 볼 수 있어야 한다. 브릿지 때문에 본래 기능까지
  // 못 쓰게 되면 안 된다.
  r.onerror = function () { connect(); };
  r.ontimeout = function () { connect(); };
  r.send();
}

// ---------------------------------------------------------------- 클립보드
//
// 화면으로는 글자를 옮길 수 없다. 눈으로 읽어 손으로 다시 치는 수밖에 없었다.
//
// navigator.clipboard 는 https 에서만 열린다. 사내망(http)으로 들어오면 막히므로,
// 막히면 사람이 직접 Ctrl+C / Ctrl+V 할 칸을 내준다. "안 됩니다" 로 끝내면
// 정작 사내망에서 제일 자주 쓰는데 못 쓰게 된다.

var clipBox = document.getElementById('clip');
var clipTx = document.getElementById('clipTx');
var clipMode = 'up';

function clipOpen(mode, text, title) {
  clipMode = mode;
  document.getElementById('clipTi').textContent = title;
  document.getElementById('clipDo').hidden = mode !== 'up';

  clipTx.value = text || '';
  resetInput(true);
  clipBox.classList.add('on');

  setTimeout(function () {
    clipTx.focus();
    if (mode === 'down') clipTx.select();       // 받은 것은 바로 Ctrl+C 되게
  }, 50);
}

function clipClose() { clipBox.classList.remove('on'); }

document.getElementById('clipX').addEventListener('click', clipClose);
document.getElementById('clipDo').addEventListener('click', function () {
  send({ t: 'clipset', s: clipTx.value });
  clipClose();
});

// 내 클립보드 → 원격 PC
document.getElementById('bClipUp').addEventListener('click', function () {
  if (!alive) return;

  if (navigator.clipboard && navigator.clipboard.readText) {
    navigator.clipboard.readText().then(function (t) {
      send({ t: 'clipset', s: t });
      flash('내 클립보드를 원격 PC 에 넣었습니다 (' + t.length + '자)');
    }).catch(function () {
      clipOpen('up', '', '여기에 붙여넣고(Ctrl+V) [보내기]');
    });
  }
  else clipOpen('up', '', '여기에 붙여넣고(Ctrl+V) [보내기]');
});

// 원격 PC → 내 클립보드
document.getElementById('bClipDn').addEventListener('click', function () {
  if (!alive) return;
  flash('원격 클립보드를 가져오는 중…');
  send({ t: 'clipget' });
});

function gotClip(text) {
  if (!text) { flash('원격 PC 클립보드가 비어 있습니다.'); return; }

  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(function () {
      flash('원격 클립보드를 내 것에 넣었습니다 (' + text.length + '자)');
    }).catch(function () {
      clipOpen('down', text, '전부 골라 뒀습니다. Ctrl+C 로 복사하세요');
    });
  }
  else clipOpen('down', text, '전부 골라 뒀습니다. Ctrl+C 로 복사하세요');
}

/// 잠깐 떴다 사라지는 알림.
///
/// 전체 화면 덮개(#msg)를 쓰면 안 된다. 클립보드는 자주 누르는데 그때마다 화면이
/// 2초씩 가려지면 못 쓴다. 아래쪽에 작게 얹고 클릭도 통과시킨다.
var toastT = null;
function flash(s) {
  var t = document.getElementById('toast');
  t.textContent = s;
  t.classList.add('on');

  if (toastT) clearTimeout(toastT);
  toastT = setTimeout(function () { t.classList.remove('on'); }, 2400);
}

// ---------------------------------------------------------------- 파일
//
// 화면(WebSocket)이 아니라 따로 HTTP 로 간다. 같은 소켓으로 나르면 큰 파일을 받는
// 동안 화면이 밀린다. 브라우저 기본 다운로드 창·이어받기·GB 단위 파일도 HTTP 라야 된다.

var fsBox = document.getElementById('files');
var fsList = document.getElementById('fsList');
var fsCur = '';

function q(o, pc) {
  if (pc === undefined) pc = curPc;
  var s = [];
  for (var k in o) if (o[k] !== null && o[k] !== undefined) s.push(k + '=' + encodeURIComponent(o[k]));
  if (pc) s.push('pc=' + pc);
  return s.join('&');
}

function fsMsg(s, bad) {
  var e = document.getElementById('fsMsg');
  e.textContent = s;
  e.style.color = bad ? '#fca5a5' : '#94a3b8';
}

// 경로가 "" 면 드라이브 목록(내 컴퓨터)이다. 거기서 드라이브를 눌러야 그 안으로 들어간다.
// 처음부터 C: 를 열지 않는 이유: 받을 것이 D: 나 USB 에 있는 경우가 더 많고,
// 드라이브가 몇 개인지부터 보여 줘야 어디를 뒤질지 정할 수 있다.
var fsListRevision = 0;
function fsGo(path) {
  var revision = ++fsListRevision, pc = curPc;
  fsMsg(path ? '읽는 중… ' + path : '드라이브를 찾는 중…');

  var r = new XMLHttpRequest();

  // 응답이 안 오면 "읽는 중…" 에서 영원히 멈춘다. 왜 멈췄는지 알 수가 없다.
  // 하위 PC 가 꺼져 있거나 네트워크 드라이브가 죽어 있으면 실제로 그렇게 된다.
  r.timeout = 30000;

  r.onload = function () {
    if (revision !== fsListRevision || pc !== curPc) return;
    if (r.status !== 200) { fsMsg('서버 응답 ' + r.status, true); return; }

    var d = null;
    try { d = JSON.parse(r.responseText); }
    catch (e) { fsMsg('목록을 알아볼 수 없습니다 (' + r.responseText.substring(0, 60) + ')', true); return; }

    if (d.err) { fsMsg(d.err, true); return; }

    fsCur = d.path;
    fsUpTo = d.up;

    // direction:rtl 이라 긴 경로는 뒷부분이 보인다. 어느 폴더인지가 중요하다.
    document.getElementById('fsPath').textContent = d.path || '내 컴퓨터';

    draw(d.items);
    fsMsg(d.path ? d.items.length + '개' : '드라이브 ' + d.items.length + '개 — 눌러서 들어가세요');
  };

  r.onerror = function () { fsMsg('연결하지 못했습니다.', true); };
  r.ontimeout = function () { fsMsg('응답이 없습니다 (30초). 상대 PC 나 드라이브를 확인하세요.', true); };

  // 여기서 예외가 나면 요청이 아예 안 나가고 "읽는 중…" 인 채로 굳는다.
  // 실제로 그렇게 멈춰 있었다 (없는 변수를 참조해 ReferenceError 가 났다).
  try {
    r.open('GET', '/fs/list?' + q({ p: path }), true);
    r.send();
  }
  catch (e) { fsMsg('요청을 보내지 못했습니다: ' + e.message, true); }
}

var fsUpTo = null;

// 지금 고른 것들. 폴더를 옮기면 비운다 - 안 비우면 보이지 않는 곳의 파일이
// 딸려 나가고, 사람은 몇 개를 받는지 알 수 없게 된다.
var picked = [];

function pickClear() {
  picked = [];
  syncPick();
}

function syncPick() {
  var n = picked.length;
  var btn = document.getElementById('fsGet');

  btn.hidden = n === 0;
  btn.textContent = n === 1 ? '고른 것 받기' : '고른 ' + n + '개 받기';

  var all = document.getElementById('fsAll');
  all.checked = n > 0 && n === fsList.children.length;

  if (n > 0) {
    var sum = 0;
    for (var i = 0; i < picked.length; i++) sum += picked[i].s || 0;
    fsMsg(n + '개 선택' + (sum ? ' · ' + bytes(sum) : '') +
          (picked.some(function (x) { return x.d; }) ? '  (폴더는 안의 것까지 함께)' : ''));
  }
}

function draw(items) {
  fsList.innerHTML = '';
  picked = [];

  for (var i = 0; i < items.length; i++) {
    (function (it) {
      var row = document.createElement('div');
      row.className = 'row' + (it.d ? ' dir' : '');

      var isDrive = !fsCur;

      // 드라이브는 고르는 칸을 안 준다. C: 를 통째로 묶으면 수백 GB 가 흘러나온다.
      var ck = null;
      if (!isDrive) {
        ck = document.createElement('input');
        ck.type = 'checkbox';
        ck.className = 'ck';
        ck._it = it;                        // [모두] 가 이걸 보고 목록을 다시 짠다

        // 줄을 누르면 들어가고, 칸을 누르면 고른다. 섞이면 안 된다.
        ck.addEventListener('click', function (e) { e.stopPropagation(); });
        ck.addEventListener('change', function () {
          if (ck.checked) { picked.push(it); row.classList.add('sel'); }
          else {
            var at = picked.indexOf(it);
            if (at >= 0) picked.splice(at, 1);
            row.classList.remove('sel');
          }
          syncPick();
        });

        row.appendChild(ck);
      }

      var ic = document.createElement('span');
      ic.className = 'ic';
      ic.textContent = isDrive ? '💽' : (it.d ? '📁' : '📄');

      var nm = document.createElement('span');
      nm.className = 'nm';
      nm.textContent = it.n;                    // textContent - 파일 이름은 남이 정한다

      var sz = document.createElement('span');
      sz.className = 'sz';
      sz.textContent = it.d ? '' : bytes(it.s);

      var dt = document.createElement('span');
      dt.className = 'dt';
      dt.textContent = it.t ? when(it.t) : '';

      row.appendChild(ic);
      row.appendChild(nm);
      row.appendChild(sz);
      row.appendChild(dt);

      // 폴더는 통째로 받을 수 있다. tar 라 압축하지 않아 빠르고, 크기를 미리 알려 준다.
      // 드라이브에는 안 붙인다 - C: 통째로를 실수로 누르면 수백 GB 가 흘러나온다.
      if (it.d && !isDrive) {
        var tar = document.createElement('span');
        tar.className = 'tar';
        tar.textContent = 'tar';
        tar.addEventListener('click', function (e) {
          e.stopPropagation();
          fsMsg('폴더를 훑는 중… 파일이 많으면 시작까지 몇 초 걸립니다');
          location.href = '/fs/tar?' + q({ p: it.p });
        });
        row.appendChild(tar);
      }

      row.addEventListener('click', function () {
        if (it.d) fsGo(it.p);
        else { location.href = '/fs/get?' + q({ p: it.p }); fsMsg('받는 중: ' + it.n); }
      });

      fsList.appendChild(row);
    })(items[i]);
  }

  syncPick();
}

// ---- 고른 것 받기 ----

document.getElementById('fsAll').addEventListener('change', function () {
  var on = this.checked;

  picked = [];
  var rows = fsList.children;

  for (var i = 0; i < rows.length; i++) {
    var ck = rows[i].firstChild;
    if (!ck || ck.type !== 'checkbox') continue;

    ck.checked = on;
    if (on) { picked.push(ck._it); rows[i].classList.add('sel'); }
    else rows[i].classList.remove('sel');
  }

  syncPick();
});

document.getElementById('fsGet').addEventListener('click', function () {
  if (!picked.length) return;

  // 파일 하나만 골랐으면 묶지 않는다. 원본 그대로 받는 것이 맞다 -
  // 엑셀 파일 하나 받으려고 tar 를 풀게 하면 안 된다.
  if (picked.length === 1 && !picked[0].d) {
    location.href = '/fs/get?' + q({ p: picked[0].p });
    fsMsg('받는 중: ' + picked[0].n);
    return;
  }

  // 여럿이면 경로가 길어져 주소에 다 못 싣는다. 목록을 먼저 맡기고 표를 받는다.
  var body = [];
  for (var i = 0; i < picked.length; i++) body.push(picked[i].p);

  var name = fsCur ? fsCur.replace(/[\\/]+$/, '').split(/[\\/]/).pop() : '고른파일';
  if (!name) name = '고른파일';

  fsMsg('목록을 보내는 중… ' + picked.length + '개');

  var r = new XMLHttpRequest();
  r.timeout = 30000;

  r.onload = function () {
    var d = null;
    try { d = JSON.parse(r.responseText); } catch (e) { }

    if (!d || !d.ok) { fsMsg(d && d.err ? d.err : '묶지 못했습니다.', true); return; }

    fsMsg(d.n + '개를 훑는 중… 파일이 많으면 시작까지 몇 초 걸립니다');
    location.href = '/fs/tar?' + q({ id: d.id });
  };

  r.onerror = function () { fsMsg('연결하지 못했습니다.', true); };
  r.ontimeout = function () { fsMsg('응답이 없습니다 (30초).', true); };

  try {
    r.open('POST', '/fs/pack?' + q({ n: name }), true);
    r.setRequestHeader('Content-Type', 'text/plain; charset=utf-8');
    r.send(body.join('\n'));
  }
  catch (e) { fsMsg('요청을 보내지 못했습니다: ' + e.message, true); }
});

function bytes(n) {
  if (n < 1024) return n + ' B';
  if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
  if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
  return (n / 1073741824).toFixed(2) + ' GB';
}

function when(t) {
  var d = new Date(t * 1000);
  function p(x) { return (x < 10 ? '0' : '') + x; }
  return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes());
}

document.getElementById('bFiles').addEventListener('click', function () {
  clearMods();                                  // 열기 전에 눌린 조합키를 푼다
  resetInput(true);
  fsBox.classList.add('on');
  if (!fsCur) fsGo('');
});

document.getElementById('fsClose').addEventListener('click', function () { fsBox.classList.remove('on'); });
document.getElementById('fsHome').addEventListener('click', function () { fsGo(''); });

document.getElementById('fsUp').addEventListener('click', function () {
  // 드라이브 뿌리에서는 up 이 "" 라 내 컴퓨터로 돌아간다.
  // 내 컴퓨터에서는 null 이라 더 올라갈 곳이 없다.
  if (fsUpTo === null || fsUpTo === undefined) { fsMsg('여기가 맨 위입니다.'); return; }
  fsGo(fsUpTo);
});

document.getElementById('fsNew').addEventListener('click', function () {
  if (!fsCur) { fsMsg('먼저 폴더로 들어가세요.', true); return; }

  var n = prompt('새 폴더 이름');
  if (!n) return;

  var r = new XMLHttpRequest();
  r.open('GET', '/fs/mkdir?' + q({ p: fsCur, n: n }), true);
  r.onload = function () {
    var d = null;
    try { d = JSON.parse(r.responseText); } catch (e) { }
    if (d && d.ok) fsGo(fsCur);
    else fsMsg(d && d.err ? d.err : '만들지 못했습니다.', true);
  };
  r.send();
});

// ---- 파일 · 폴더 올리기 ----

var fsPick = document.getElementById('fsPick');
var fsFolderPick = document.getElementById('fsFolderPick');
var fsProg = document.getElementById('fsProg');
var uploadJob = null;

function uploadStatus(text) { document.getElementById('fsUploadStatus').textContent = text; }
function uploadError(job, path, message) {
  job.failed++;
  if (job.errors.length < 100) job.errors.push(path + ' — ' + message);
  document.getElementById('fsUploadErrors').hidden = false;
  document.getElementById('fsUploadErrorList').textContent = job.errors.join('\n') +
    (job.failed > 100 ? '\n… 총 ' + job.failed + '개 실패' : '');
}
function beginUpload() {
  if (!fsCur) { fsMsg('먼저 올릴 폴더로 들어가세요.', true); return null; }
  if (uploadJob) { fsMsg('전송 중입니다. 끝난 뒤 추가하거나 전송을 취소하세요.', true); return null; }
  // PC와 폴더를 시작할 때 고정한다. 다음 파일을 보낼 때 curPc를 읽으면 다른 PC에 올릴 수 있다.
  var job = { pc: curPc, dir: fsCur, tasks: [], dirs: Object.create(null), blocked: [],
    files: 0, total: 0, done: 0, sent: 0, folders: 0, failed: 0, errors: [], index: 0, xhr: null, canceled: false };
  uploadJob = job;
  fsProg.classList.add('on'); fsProg.firstChild.style.width = '0';
  document.getElementById('fsUploadCancel').hidden = false;
  document.getElementById('fsUploadErrors').hidden = true;
  document.getElementById('fsUploadErrorList').textContent = '';
  uploadStatus('올릴 항목을 확인하는 중…');
  return job;
}
function finishUpload(job, stopped) {
  if (uploadJob !== job) return;
  uploadJob = null; job.xhr = null;
  fsProg.classList.remove('on');
  document.getElementById('fsUploadCancel').hidden = true;
  uploadStatus((stopped ? '전송 중지 · ' : '전송 완료 · ') + '파일 ' + job.done + '/' + job.files +
    '개 · 폴더 ' + job.folders + '개' + (job.failed ? ' · 실패 ' + job.failed + '개' : '') +
    (stopped ? ' (완료된 파일은 유지됩니다)' : ''));
  if (job.pc === curPc && job.dir === fsCur) fsGo(job.dir);
}
function cancelUpload() {
  var job = uploadJob;
  if (!job) return;
  job.canceled = true;
  var xhr = job.xhr;
  finishUpload(job, true);
  if (xhr) xhr.abort();
}
document.getElementById('fsUploadCancel').addEventListener('click', cancelUpload);

function uploadPath(path) {
  var parts = path.split('/');
  if (parts.length > 64 || parts.some(function (x) { return !x || x === '.' || x === '..' || /[\\:]/.test(x); }))
    throw new Error('폴더 경로가 올바르지 않습니다: ' + path);
  return parts;
}
function uploadDirectory(job, path) {
  if (!path) return;
  var parts = uploadPath(path), parent = '';
  parts.forEach(function (part) {
    parent += (parent ? '/' : '') + part;
    var key = parent.toLowerCase();
    if (!job.dirs[key]) {
      job.dirs[key] = true;
      job.tasks.push({ path: parent, dir: true });
    }
  });
}
function uploadFile(job, file, path) {
  var parts = uploadPath(path); parts.pop();
  uploadDirectory(job, parts.join('/'));
  job.tasks.push({ path: path, parent: parts.join('/'), file: file });
  job.files++; job.total += file.size;
}
function uploadLimit(job) {
  if (job.tasks.length > 50000) throw new Error('한 번에 50,000개까지 전송할 수 있습니다. 폴더를 나누어 올려 주세요.');
  if (job.canceled) throw new Error('취소됨');
}
function uploadFiles(files, folder) {
  var job = beginUpload(); if (!job) return;
  try {
    for (var i = 0; i < files.length; i++) {
      var relative = folder ? files[i].webkitRelativePath : files[i].name;
      if (!relative) throw new Error('이 브라우저에서 폴더 경로를 읽지 못했습니다. 폴더를 끌어 놓아 주세요.');
      uploadFile(job, files[i], relative); uploadLimit(job);
    }
    if (!job.tasks.length) throw new Error(folder ? '선택한 폴더에 파일이 없습니다. 빈 폴더는 끌어 놓기로 올려 주세요.' : '고른 파일이 없습니다.');
    uploadNext(job);
  } catch (error) { uploadError(job, '선택한 항목', error.message); finishUpload(job, true); }
}
function bindUploadPicker(label, input, folder) {
  document.getElementById(label).addEventListener('click', function (e) {
    if (!fsCur || uploadJob) {
      e.preventDefault();
      fsMsg(uploadJob ? '전송이 끝난 뒤 추가하거나 전송을 취소하세요.' : '먼저 올릴 폴더로 들어가세요.', true);
      return;
    }
    input.value = '';
  });
  input.addEventListener('change', function () { uploadFiles(input.files, folder); });
}
// label로 실제 input을 연다. 스크립트 input.click()은 일부 보안 제품에서 차단된다.
bindUploadPicker('fsSend', fsPick, false);
bindUploadPicker('fsSendFolder', fsFolderPick, true);

function uploadEntry(job, entry, parent) {
  uploadLimit(job);
  var path = parent ? parent + '/' + entry.name : entry.name;
  uploadPath(path);
  if (entry.isFile) return new Promise(function (resolve, reject) {
    entry.file(function (file) {
      try { uploadLimit(job); uploadFile(job, file, path); resolve(); } catch (error) { reject(error); }
    }, reject);
  });
  if (!entry.isDirectory) return Promise.reject(new Error('읽을 수 없는 항목: ' + path));
  uploadDirectory(job, path); uploadLimit(job);
  var reader = entry.createReader();
  function batch() {
    uploadLimit(job);
    return new Promise(function (resolve, reject) { reader.readEntries(resolve, reject); }).then(function (entries) {
      if (!entries.length) return;
      // readEntries는 한 번에 일부만 준다. 빈 배열을 받을 때까지 읽어야 큰 폴더가 잘리지 않는다.
      var next = Promise.resolve();
      entries.forEach(function (child) { next = next.then(function () { return uploadEntry(job, child, path); }); });
      return next.then(batch);
    });
  }
  return batch();
}
fsBox.addEventListener('dragover', function (e) { e.preventDefault(); });
fsBox.addEventListener('drop', function (e) {
  e.preventDefault();
  var job = beginUpload(); if (!job) return;
  // 드롭 이벤트가 끝나면 DataTransfer를 읽을 수 없다. 엔트리를 지금 확보한 뒤 비동기로 훑는다.
  var entries = [], files = [], items = e.dataTransfer.items || [];
  for (var i = 0; i < items.length; i++) {
    if (items[i].kind !== 'file') continue;
    var getEntry = items[i].getAsEntry || items[i].webkitGetAsEntry;
    var entry = getEntry ? getEntry.call(items[i]) : null;
    if (entry) entries.push(entry);
    else { var file = items[i].getAsFile(); if (file) files.push(file); }
  }
  if (!items.length) for (var j = 0; j < e.dataTransfer.files.length; j++) files.push(e.dataTransfer.files[j]);
  var chain = Promise.resolve();
  entries.forEach(function (entry) { chain = chain.then(function () { return uploadEntry(job, entry, ''); }); });
  chain.then(function () {
    uploadLimit(job);
    files.forEach(function (file) { uploadFile(job, file, file.name); uploadLimit(job); });
    if (!job.tasks.length) throw new Error('파일을 읽지 못했습니다. 파일/폴더 올리기 단추를 사용하세요.');
    uploadNext(job);
  }).catch(function (error) {
    if (job.canceled) return;
    uploadError(job, '폴더 읽기', error.message || '폴더 접근이 거부됐습니다.'); finishUpload(job, true);
  });
});

function uploadNext(job) {
  if (job.canceled || uploadJob !== job) return;
  if (job.index >= job.tasks.length) { finishUpload(job, false); return; }
  var task = job.tasks[job.index++];
  if (job.blocked.some(function (path) { return task.path.toLowerCase().indexOf(path + '/') === 0; })) {
    uploadError(job, task.path, '상위 폴더를 만들지 못했습니다.');
    setTimeout(function () { uploadNext(job); }, 0); return;
  }
  uploadStatus('파일 ' + job.done + '/' + job.files + '개 · ' + bytes(job.sent) + '/' + bytes(job.total) + ' · ' + task.path);
  var xhr = new XMLHttpRequest(); job.xhr = xhr;
  if (task.dir) xhr.timeout = 30000;
  xhr.upload.onprogress = function (e) {
    if (job.canceled || uploadJob !== job || task.dir || !e.lengthComputable) return;
    var current = task.file.size * Math.min(1, e.loaded / e.total);
    var pct = job.total ? (job.sent + current) / job.total * 100 : 0;
    fsProg.firstChild.style.width = Math.min(100, pct) + '%';
    uploadStatus('파일 ' + job.done + '/' + job.files + '개 · ' + bytes(job.sent + current) + '/' + bytes(job.total) + ' · ' + task.path);
  };
  function stop(message) {
    if (job.canceled || uploadJob !== job) return;
    uploadError(job, task.path, message); finishUpload(job, true);
  }
  xhr.onload = function () {
    if (job.canceled || uploadJob !== job) return;
    var data = null;
    try { data = JSON.parse(xhr.responseText); } catch (error) { }
    if (!data || xhr.status !== 200) { stop('서버 응답을 확인할 수 없습니다. 로그인과 연결 상태를 확인하세요. (' + xhr.status + ')'); return; }
    if (task.dir && data.ok && data.folders !== 1) { stop('대상 PC가 폴더 전송을 지원하지 않습니다. 원격제어를 업데이트하세요.'); return; }
    if (data.ok) {
      if (task.dir) job.folders++;
      else { job.done++; job.sent += task.file.size; }
    } else {
      uploadError(job, task.path, data.err || '전송 실패');
      if (task.dir) job.blocked.push(task.path.toLowerCase());
    }
    uploadNext(job);
  };
  xhr.onerror = function () { stop('연결이 끊겼습니다. 자동 재전송은 하지 않았습니다.'); };
  xhr.ontimeout = function () { stop('응답 시간이 초과되었습니다.'); };
  xhr.onabort = function () { stop('전송이 취소되었습니다.'); };
  try {
    xhr.open('POST', (task.dir ? '/fs/mkdir?' : '/fs/put?') + q({ p: job.dir,
      r: task.dir ? task.path : task.parent, n: task.dir ? null : task.file.name }, job.pc), true);
    if (task.dir) xhr.send(null);
    else {
      // 파일은 한 개씩 스트리밍한다. 폴더 전체를 ZIP/메모리로 묶지 않고 화면과 별도 HTTP로 보낸다.
      var body = new FormData(); body.append('f', task.file, task.file.name); xhr.send(body);
    }
  } catch (error) { stop('보내지 못했습니다: ' + error.message); }
}

// 서비스 워커. 캐시는 하지 않고 설치만 되게 한다 (/sw.js 주석 참고).
// http 로 들어오면 보안 컨텍스트가 아니라 등록 자체가 거부된다 - 그래도 괜찮다.
if ('serviceWorker' in navigator) {
  window.addEventListener('load', function () {
    try { navigator.serviceWorker.register('/sw.js'); } catch (e) { }
  });
}

start();
