# Video components

RemoteControl launches the video encoder as a separate process. FFmpeg is not
linked into the application or into the Pion helper.

* **Pion WebRTC v4.2.25**: MIT license. Source, license, and pinned transitive
  modules: https://github.com/pion/webrtc/tree/v4.2.25 and `video-helper/go.sum`.
* **FFmpeg n9.0.2, commit 2a571b6068**: LGPL 3 or later, using the LGPL Windows
  build from BtbN, not its GPL variant. This distribution contains FFmpeg and
  third-party codec libraries. Their licenses and build configuration are in
  the exact archive pinned by `build/dependencies.json`.
  - Archive/build: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-30-13-08
  - FFmpeg source: https://github.com/FFmpeg/FFmpeg/tree/2a571b6068
  - Build recipes and third-party source locations: https://github.com/BtbN/FFmpeg-Builds
  - Bundled LGPL/GPL text: `third_party/LGPL-3.0.txt`, `third_party/GPL-3.0.txt`
  - Pion MIT text: `third_party/Pion-MIT.txt`

No FFmpeg modifications are made. Runtime extraction uses a content hash and
protected executable directory. Builders must retain the pinned archive and
its accompanying licenses/source information when distributing binary builds.
The complete corresponding source must remain available for LGPL binaries;
repository source changes alone do not constitute a binary release.
