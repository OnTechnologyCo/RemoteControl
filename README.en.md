# RemoteControl v1.8.14

[한국어](README.md) | [English](README.en.md)

A Windows remote desktop and file transfer application with a browser viewer, LAN PC bridging, optional SSH or Tailscale tunnels, and MCP integrations. The viewer works in a browser without a separate desktop client.

## v1.8.14 private tunnel MCP

Select automatic private tunnel connection in the web ChatGPT / Claude settings to use the HTTPS address issued by SSHTunnel for both the browser viewer and `/mcp`. Exact MCP, OAuth, discovery, and file transfer routes go to a separate authenticated loopback gateway. Manual DDNS settings and existing viewer routes are preserved.

Authentication is restored after a restart when the public address stays the same. A new random address after an SSH reconnect requires registering the new `/mcp` URL in the app and signing in again. See [web MCP settings](web-mcp/README.md) and [private tunnel usage](docs/private-tunnel.md).

## MCP authentication and file transfer

Web and LAN MCP sessions survive application or gateway restarts and temporary network failures. Logout, app disconnection, and Remote ID or password changes revoke the corresponding authentication. Passwords are not stored; Windows DPAPI and AES-256-GCM protect persistent authentication state. Browser viewer sessions keep their existing eight-hour policy.

The `pc_open_file_transfer` tool opens a one-time browser transfer page for uploading files and folders or downloading individual files and folder ZIP archives. Direct MCP uploads use 256 KiB chunks with SHA-256 checks and can resume for 24 hours. Limits are 4 GiB per file, 8 GiB per folder transfer, and 10,000 entries. See [file transfer usage](docs/webmcp-file-transfer.md).

## Features

- Desktop capture with DXGI Desktop Duplication and a GDI fallback
- Browser mouse, keyboard, touch controls, clipboard, and resolution controls
- File and folder uploads, individual downloads, and uncompressed TAR downloads
- Windows service, interactive-session agent, tray, and standalone modes
- LAN PC bridging through one host
- SSHTunnel-issued random HTTPS addresses and Tailscale external connections
- A separate LAN MCP listener and an optional Node.js OAuth gateway for web MCP clients

## Build

Use 64-bit Windows with the .NET Framework 4.x C# compiler. The Tailscale helper declares Go 1.26.6 in `tsproxy/go.mod`. The private tunnel helper uses the SSHTunnel 1.3 Go SDK through the sibling source folder `../SSHTunnel` (`sshtunnel-client/go.mod`). The complete installer build also requires Node.js 24.14.0 or newer and npm. Dependency and runtime downloads may require internet access.

Run from the project root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\빌드_setup.ps1
```

This builds the Tailscale and private tunnel Go helpers, application, MCP packages, web gateway package, and installer. Outputs are written to the project's `dist` directory. To build the application only, first prepare the Tailscale helper resource with `빌드_tsproxy.ps1`, then run `빌드.ps1`; it also builds the private tunnel helper.

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
| `sshtunnel-client/` | SSHTunnel Go SDK helper and MCP routing |
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
