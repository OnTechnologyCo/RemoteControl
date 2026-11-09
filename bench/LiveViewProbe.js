'use strict';
// LiveViewBench 전용. 제품 viewer.js 뒤에 붙어 합성 화면에 찍힌 시간값을 읽는다.
// viewer.js 의 동작은 바꾸지 않는다. 그리는 함수와 수신 함수를 감싸 관찰만 한다.
(function () {
  var Q = new URLSearchParams(location.search);
  var targetW = +Q.get('w') || 2560, scene = Q.get('scene') || 'scroll';
  var warmMs = (+Q.get('warm') || 4) * 1000, measureMs = (+Q.get('secs') || 10) * 1000;
  var BLOCK = 14, COLS = 8, ROWS = 4, INSET = 8, BW = COLS * BLOCK, BH = ROWS * BLOCK;
  var live = window.__live = { state: 'connecting', log: [] };
  var markers = [], seen = [], lat = [], firstAt = 0, measuring = false, startAt = 0;
  var bytes = 0, binaryMsgs = 0, tilePaints = 0, modes = {}, tiles = {}, screenHops = 0, tileBytes = {};

  function markerList(w, h) {
    if (scene === 'video') {
      var vw = Math.min(1280, w - 200), vh = Math.min(720, h - 200);
      var vx = Math.floor((w - vw) / 2 / 16) * 16, vy = Math.floor((h - vh) / 2 / 16) * 16;
      return [{ x: Math.floor((vx + 127) / 128) * 128 + INSET, y: Math.floor((vy + 127) / 128) * 128 + INSET },
              { x: Math.floor((vx + vw) / 128) * 128 - 128 + INSET, y: Math.floor((vy + vh) / 128) * 128 - 128 + INSET }];
    }
    if (scene === 'typing') return [{ x: INSET, y: 256 + INSET }];
    return [{ x: INSET, y: INSET }, { x: Math.floor(w / 128) * 128 - 128 + INSET, y: Math.floor(h / 128) * 128 - 128 + INSET }];
  }
  function decode(data) {
    var v = 0;
    for (var bit = 0; bit < 32; bit++) {
      var r = (bit / COLS) | 0, c = bit % COLS;
      var i = (((r * BLOCK + (BLOCK >> 1)) * BW) + c * BLOCK + (BLOCK >> 1)) * 4;
      if (data[i + 1] < 128) v = (v | (1 << bit)) >>> 0;
    }
    return v >>> 0;
  }
  function record(k, value, at) {
    if (value === seen[k]) return;
    seen[k] = value;
    var ms = ((Date.now() % 4294967296) - value + 4294967296) % 4294967296;
    if (ms > 10000) return; // 아직 첫 화면 전(검은 화면)이거나 잘못 읽은 값
    if (!firstAt) firstAt = at;
    if (measuring) lat[k].push(ms);
  }
  function sample(k, source) {
    var m = markers[k];
    var data = source(m);
    if (data) record(k, decode(data), performance.now());
  }

  // JPEG 경로: 표시 canvas 에 타일이 그려지는 순간 시간값을 읽는다.
  var draw = cx.drawImage;
  cx.drawImage = function (image, x, y) {
    draw.apply(cx, arguments);
    tilePaints++;
    var w = image.width, h = image.height;
    for (var k = 0; k < markers.length; k++) {
      var m = markers[k];
      if (m.x >= x && m.y >= y && m.x + BW <= x + w && m.y + BH <= y + h)
        sample(k, function (m) { return cx.getImageData(m.x, m.y, BW, BH).data; });
    }
  };

  // 영상 경로: 브라우저가 프레임을 내보낼 때마다 같은 위치를 읽는다.
  var scratch = document.createElement('canvas'); scratch.width = BW; scratch.height = BH;
  var sx = scratch.getContext('2d', { willReadFrequently: true });
  var watched = null;
  setInterval(function () {
    var v = document.querySelector('video');
    if (!v || v === watched || !v.requestVideoFrameCallback) return;
    watched = v;
    (function frame() {
      if (!v.hidden && v.videoWidth) for (var k = 0; k < markers.length; k++)
        sample(k, function (m) { sx.drawImage(v, m.x, m.y, BW, BH, 0, 0, BW, BH); return sx.getImageData(0, 0, BW, BH).data; });
      v.requestVideoFrameCallback(frame);
    })();
  }, 200);

  // 묶음 도착('frame' 알림) → 디코딩·그리기 끝(ACK 송신)까지. 전송 시간과 브라우저 처리 시간을 가른다.
  var frameAt = {}, decodeMs = [];
  var wsSend = WebSocket.prototype.send;
  WebSocket.prototype.send = function (data) {
    if (measuring && typeof data === 'string' && data.indexOf('"t":"ack"') >= 0) {
      var n = JSON.parse(data).n; if (frameAt[n]) { decodeMs.push(performance.now() - frameAt[n]); delete frameAt[n]; }
    }
    return wsSend.apply(this, arguments);
  };
  var original = onMessage;
  onMessage = function (ev) {
    if (typeof ev.data === 'string') {
      bytes += ev.data.length;
      var m = JSON.parse(ev.data);
      if (m.t === 'init') {
        markers = markerList(m.w, m.h); seen = [];
        live.log.push('init ' + m.w + 'x' + m.h + ' tile ' + m.tile + ' rtc ' + m.rtc);
        if (m.w !== targetW && screenHops < (m.screens || 1)) { screenHops++; original(ev); send({ t: 'nextScreen' }); return; }
        if (m.w === targetW && live.state === 'connecting') {
          live.state = 'warming';
          setTimeout(function () {
            lat = markers.map(function () { return []; }); bytes = 0; binaryMsgs = 0; tilePaints = 0; modes = {}; tiles = {}; tileBytes = {};
            measuring = true; startAt = performance.now(); live.state = 'measuring';
            setTimeout(finish, measureMs);
          }, warmMs);
        }
      }
      if (m.t === 'tiles') live.log.push('tiles ' + m.tile);
      if (m.t === 'frame' && measuring) frameAt[m.n] = performance.now();
      if (m.t === 'rtc') live.log.push('rtc ' + m.op + (m.op === 'encoder' || m.op === 'stop' ? ' ' + m.data : ''));
    } else {
      bytes += ev.data.byteLength; binaryMsgs++;
      if (measuring) {
        var dv = new DataView(ev.data), p = 0;
        while (p + 6 <= dv.byteLength) {
          var idx = dv.getUint16(p, true), len = dv.getUint32(p + 2, true); p += 6;
          if (idx === 0xFFFF) { p += 4 + len; continue; }
          var t = tileBytes[idx] || (tileBytes[idx] = { n: 0, b: 0 }); t.n++; t.b += len; p += len;
        }
      }
    }
    original(ev);
  };
  setInterval(function () {
    if (!measuring) return;
    var mode = document.getElementById('mWay').textContent;
    modes[mode] = (modes[mode] || 0) + 1; tiles[TILE] = (tiles[TILE] || 0) + 1;
  }, 250);

  function stats(a) {
    if (!a.length) return null;
    var s = a.slice().sort(function (x, y) { return x - y; });
    var pick = function (p) { return s[Math.min(s.length - 1, Math.floor(p * (s.length - 1)))]; };
    return { n: a.length, mean: +(a.reduce(function (x, y) { return x + y; }, 0) / a.length).toFixed(1), p50: pick(0.5), p95: pick(0.95), max: s[s.length - 1] };
  }
  function finish() {
    measuring = false;
    var secs = (performance.now() - startAt) / 1000;
    live.result = {
      scene: scene, width: W, height: H, secs: +secs.toFixed(2),
      markers: lat.map(function (a) { var st = stats(a) || {}; st.fps = +(a.length / secs).toFixed(1); return st; }),
      mbps: +(bytes * 8 / secs / 1e6).toFixed(1), packets: +(binaryMsgs / secs).toFixed(1), tilePaintsPerSec: +(tilePaints / secs).toFixed(1),
      modes: modes, tiles: tiles, log: live.log.slice(-20), decode: stats(decodeMs),
      packetKB: binaryMsgs ? +(bytes / binaryMsgs / 1024).toFixed(1) : 0,
      tileKB: Object.keys(tileBytes).map(function (k) { var t = tileBytes[k]; return [+k, t.n, +(t.b / t.n / 1024).toFixed(1)]; })
    };
    live.state = 'done';
  }
})();
