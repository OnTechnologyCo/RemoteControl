'use strict';

// Bound native JPEG work. Superseded queued tiles never enter the decoder.
function JpegDecoder() {
  var queue = [], active = 0, serial = 0, pending = {}, worker = null, broken = false;
  function fallback(task) {
    return createImageBitmap(new Blob([task.bytes], { type: 'image/jpeg' }));
  }
  try {
    if (typeof Worker !== 'undefined' && typeof createImageBitmap !== 'undefined') {
      worker = new Worker('/jpeg-worker.js');
      worker.onmessage = function (ev) {
        var task = pending[ev.data.id];
        if (!task) { if (ev.data.image) ev.data.image.close(); return; }
        delete pending[ev.data.id];
        finish(task, ev.data.image);
      };
      worker.onerror = function () {
        broken = true; worker.terminate(); worker = null;
        Object.keys(pending).forEach(function (id) {
          var task = pending[id]; delete pending[id];
          fallback(task).then(function (image) { finish(task, image); }, function () { finish(task, null); });
        });
      };
    }
  } catch (e) { worker = null; }
  function finish(task, image) {
    try { if (image && task.current()) task.paint(image); }
    finally { if (image && image.close) image.close(); active--; task.resolve(!!image); pump(); }
  }
  function pump() {
    while (active < 4 && queue.length) {
      var task = queue.shift();
      if (!task.current()) { task.resolve(true); continue; }
      active++;
      if (worker && !broken) {
        pending[task.id] = task;
        // Clone a small view, not the complete multi-tile packet's backing buffer.
        var copy = task.bytes.slice();
        worker.postMessage({ id: task.id, bytes: copy }, [copy.buffer]);
      } else {
        (function (item) { fallback(item).then(function (image) { finish(item, image); }, function () { finish(item, null); }); })(task);
      }
    }
  }
  this.decode = function (bytes, current, paint) {
    return new Promise(function (resolve) {
      queue.push({ id: ++serial, bytes: bytes, current: current, paint: paint, resolve: resolve });
      pump();
    });
  };
}

// Signaling stays on the authenticated WebSocket. A decoded frame, not merely
// connected ICE, is the commit point. A stalled or failed stream restores JPEG.
function RemoteVideo(options) {
  var self = this, peer = null, serial = 0, id = 0, generation = 0;
  var video = null, timer = null, ready = false, remote = [], local = [], offered = false;
  var lastFrame = 0, started = 0, previous = null, statsPending = false;
  var maxRate = 6000000, rate = 2000000, good = 0, automatic = true, encoder = 'H.264';
  var currentInfo = null;
  function signal(op, data) { options.send({ t: 'rtc', op: op, id: id, view: options.view(), data: data || '' }); }
  function close(notify) {
    generation++;
    if (notify && peer) signal('stop');
    if (timer) clearInterval(timer); timer = null;
    var old = peer; peer = null;
    if (old) { old.ontrack = old.onicecandidate = old.onconnectionstatechange = null; old.close(); }
    if (video) { video.onloadeddata = null; video.pause(); video.srcObject = null; video.hidden = true; }
    ready = false; remote = []; local = []; offered = false; previous = null; statsPending = false;
    options.mode('JPEG');
  }
  this.stop = close;
  function firstFrame(gen) {
    if (gen !== generation || !peer) return;
    lastFrame = performance.now();
    if (!ready) { ready = true; video.hidden = false; signal('ready', String(rate)); options.mode(encoder); }
  }
  this.start = function (info) {
    close(true); currentInfo = info;
    if (!info || info.rtc !== 1 || typeof RTCPeerConnection === 'undefined' || document.hidden) return;
    var gen = generation;
    id = ++serial; started = lastFrame = performance.now(); good = 0;
    maxRate = info.w * info.h >= 3840 * 2160 ? 12000000 : 6000000;
    rate = Math.min(maxRate, info.w * info.h >= 3840 * 2160 ? 4000000 : 2000000);
    if (!video) {
      video = document.createElement('video'); video.autoplay = true; video.muted = true; video.playsInline = true;
      video.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;object-fit:fill';
      options.world.insertBefore(video, options.cursor);
    }
    video.hidden = true; video.style.width = info.w + 'px'; video.style.height = info.h + 'px';
    try {
      peer = new RTCPeerConnection({ iceServers: info.ice || [], bundlePolicy: 'max-bundle' });
      var transceiver = peer.addTransceiver('video', { direction: 'recvonly' });
      if (transceiver.setCodecPreferences && typeof RTCRtpReceiver !== 'undefined' && RTCRtpReceiver.getCapabilities) {
        var codecs = RTCRtpReceiver.getCapabilities('video').codecs.filter(function (c) { return c.mimeType.toLowerCase() === 'video/h264'; });
        if (!codecs.length) { close(false); return; }
        transceiver.setCodecPreferences(codecs);
      }
      var thisPeer = peer;
      peer.onicecandidate = function (ev) {
        if (gen !== generation || !ev.candidate) return;
        var candidate = JSON.stringify(ev.candidate.toJSON());
        if (offered) signal('candidate', candidate);
        else if (local.length < 64) local.push(candidate);
      };
      peer.onconnectionstatechange = function () {
        if (gen !== generation) return;
        if (thisPeer.connectionState === 'failed' || thisPeer.connectionState === 'closed') close(true);
      };
      peer.ontrack = function (ev) {
        if (gen !== generation) return;
        video.srcObject = ev.streams[0] || new MediaStream([ev.track]);
        // Minimize browser playout buffering when the implementation exposes it.
        if (ev.receiver && 'jitterBufferTarget' in ev.receiver) { try { ev.receiver.jitterBufferTarget = 0; } catch (e) {} }
        if (video.requestVideoFrameCallback) {
          function frame() { if (gen !== generation) return; firstFrame(gen); video.requestVideoFrameCallback(frame); }
          video.requestVideoFrameCallback(frame);
        }
        video.onloadeddata = function () {
          if (gen !== generation) return;
          video.hidden = false; // a display:none video may never schedule a presentation callback
          if (!video.requestVideoFrameCallback) firstFrame(gen);
        };
        var play = video.play(); if (play && play.catch) play.catch(function () { if (gen === generation) close(true); });
      };
      thisPeer.createOffer().then(function (offer) {
        if (gen !== generation) return;
        return thisPeer.setLocalDescription(offer).then(function () {
          if (gen !== generation) return;
          signal('offer', offer.sdp); offered = true;
          local.forEach(function (candidate) { signal('candidate', candidate); }); local = [];
        });
      }).catch(function () { if (gen === generation) close(true); });
      timer = setInterval(function () {
        if (gen !== generation || !peer) return;
        var now = performance.now();
        if ((!ready && now - started > 18000) || (ready && now - lastFrame > 3500)) { close(true); return; }
        if (statsPending) return;
        statsPending = true;
        thisPeer.getStats().then(function (reports) {
          if (gen !== generation) return;
          statsPending = false;
          reports.forEach(function (r) {
            if (r.type !== 'inbound-rtp' || (r.kind || r.mediaType) !== 'video') return;
            if (previous) {
              var frames = Math.max(0, (r.framesDecoded || 0) - (previous.framesDecoded || 0));
              var packets = Math.max(1, r.packetsReceived - previous.packetsReceived);
              var loss = Math.max(0, r.packetsLost - previous.packetsLost) / packets;
              var emitted = (r.jitterBufferEmittedCount || 0) - (previous.jitterBufferEmittedCount || 0);
              var delay = emitted > 0 ? (r.jitterBufferDelay - previous.jitterBufferDelay) / emitted : 0;
              if (frames > 0 && !video.requestVideoFrameCallback) lastFrame = now;
              options.stats(Math.max(0, r.bytesReceived - previous.bytesReceived), frames);
              if (automatic) {
                if (loss > .03 || delay > .12) { rate = Math.max(500000, Math.floor(rate * .65)); good = 0; }
                else if (++good >= 8) { rate = Math.min(maxRate, Math.floor(rate * 1.35)); good = 0; }
              }
            }
            previous = r;
          });
          if (ready) signal('health', String(rate));
        }, function () { if (gen === generation) statsPending = false; });
      }, 1000);
    } catch (e) { close(true); }
  };
  this.handle = function (m) {
    if (!peer || m.id !== id) return;
    var gen = generation, thisPeer = peer;
    if (m.op === 'stop') { close(false); return; }
    if (m.op === 'encoder') { encoder = m.data; if (ready) options.mode(encoder); return; }
    if (m.op === 'answer') {
      thisPeer.setRemoteDescription({ type: 'answer', sdp: m.data }).then(function () {
        if (gen !== generation) return;
        var candidates = remote; remote = [];
        return Promise.all(candidates.map(function (candidate) { return thisPeer.addIceCandidate(candidate); }));
      }).catch(function () { if (gen === generation) close(true); });
    } else if (m.op === 'candidate') {
      try {
        var candidate = JSON.parse(m.data);
        if (thisPeer.remoteDescription) thisPeer.addIceCandidate(candidate).catch(function () { if (gen === generation) close(true); });
        else if (remote.length < 64) remote.push(candidate);
      } catch (e) { close(true); }
    }
  };
  this.quality = function (q) { automatic = q === 0; if (!automatic) rate = Math.round(maxRate * (q < 55 ? .35 : q < 80 ? .7 : 1)); };
  this.resume = function () { if (currentInfo) self.start(currentInfo); };
  this.active = function () { return ready; };
}
