# RemoteControl v1.8.10

[한국어](README.md) | [English](README.en.md)

A Windows remote desktop and file transfer application with a browser viewer, LAN PC bridging, optional SSH or Tailscale tunnels, and MCP integrations. The viewer works in a browser without a separate desktop client.

## Features

- Desktop capture with DXGI Desktop Duplication and a GDI fallback
- Browser mouse, keyboard, touch controls, clipboard, and resolution controls
- File and folder uploads, individual downloads, and uncompressed TAR downloads
- Windows service, interactive-session agent, tray, and standalone modes
- LAN PC bridging through one host
- Optional SSH and Tailscale external connections
- A separate LAN MCP listener and an optional Node.js OAuth gateway for web MCP clients

## Build

Use 64-bit Windows with the .NET Framework 4.x C# compiler. The Go helper declares Go 1.26.6 in `tsproxy/go.mod`. The complete installer build also requires Node.js 24.14.0 or newer and npm. Dependency and runtime downloads may require internet access.

Run from the project root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\빌드_setup.ps1
```

This builds the Go helper, application, MCP packages, web gateway package, and installer. Outputs are written to the project's `dist` directory. To build the application only, first create the helper resource with `빌드_tsproxy.ps1`, then run `빌드.ps1`.

Building does not install or restart the live service. Installation is a separate action and requires administrator rights.

Signed virtual display driver binaries are excluded from this source repository. The `vdd` directory contains driver metadata only. Supply the matching driver files before using the optional driver installation feature.

## Repository token configuration

The original local project is preserved. In this repository copy, the embedded Telegram bot token has been removed. `Notify.BotToken` reads the process environment variable **REMOTE_CONTROL_TELEGRAM_BOT_TOKEN**. Leave it unset to disable bot-dependent notification features.

For a locally launched application, set the variable in that process environment before starting the application. A Windows service and its agent must receive the variable in their own execution environment; setting it only in a user shell does not configure an already running service. Do not commit the token or a real environment file.

The installer excludes saved Tailscale keys and Telegram chat IDs by default. The existing `-IncludeTailscaleKey` option explicitly includes saved configuration; generated packages and any included secrets are outside this repository.

## Source layout

| Path | Purpose |
| --- | --- |
| `src/` | C# application, desktop agent, service, authentication, file transfer, and MCP |
| `web/` | Embedded browser login and viewer |
| `tsproxy/` | Go Tailscale helper |
| `web-mcp/` | Node.js OAuth gateway, configuration scripts, and tests |
| `plugins/` | LAN MCP plugin and Claude extension manifests |
| `bench/` | Isolated test fixtures and measurement tools |
| `docs/` | Feature notes and recorded validation results |
| `Setup*.cs` | Windows installer |
| `빌드*.ps1` | Build and packaging scripts |

See [LAN MCP usage](docs/mcp-lan.md), [web MCP](web-mcp/README.md), [external tunnel settings](docs/private-tunnel.md), and [performance notes](docs/4k-performance.md). Recorded measurements are historical results, not new measurements from this repository upload.

## Repository scope

This repository is private. It includes source, documentation, icons, build scripts, and test code. Live settings, passwords, tokens, OAuth state, certificates, tunnel identity, browser profiles, logs, test results, third-party runtime binaries, and compiled packages are excluded. Existing local source files and the running installation remain unchanged.

No new project license has been assigned by this upload. Third-party dependencies retain their respective licenses.
