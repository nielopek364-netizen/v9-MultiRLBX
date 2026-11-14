# Changelog

All notable features and updates for **v9 MultiRLBX** by **v9pv** are documented in this file.

## [1.0.0] - Initial Public Release

### Highlights
- **1-Button Login Hub & 5 Auth Methods (`WebAuth.cs`, `Api.cs`, `QrCode.cs`, `ExportImport.cs`, `ui/app.html`)**: Unified **`+ Login / Add`** modal supporting **QR Code & 6-Char Quick Login**, **Isolated Chromium Web Login** (`Edge` / `Chrome` / `Brave` temporary profiles + `127.0.0.1` CDP capture), `.ROBLOSECURITY` token paste, bulk `.txt` (`user:pass:cookie`), and **JSON** cookie import.
- **Real Roblox Avatars & Game Picker (`Api.cs`, `LocalUiServer.cs`, `ui/app.html`)**: Live `150x150` Roblox headshots resolved by username or UserID with TTL caching and blocky portrait fallback, plus a built-in **Game Picker** featuring trending titles, search by name, Place ID / URL resolution, live CCU/likes/visits stats, and one-click Server Browser launch.
- **First-Run Quick Guide (`ui/app.html`)**: Launches straight into the main dashboard with an interactive 4-step **PL / EN** Quick Guide modal accessible anytime from the sidebar.
- **`.v9m` Macro Recorder & Overlay (`MacroForm.cs`, `Macro.cs`, `LocalUiServer.cs`, `ui/app.html`)**: Floating always-on-top **Macro Recorder** (`F9` Record/Stop, `F10` Play/Stop, `.v9m` Save/Open dialogs, Speed, Repeats, Pause ms, Continuous loop, Record Mouse Moves, Trim Leading Idle) plus dashboard `.v9m` library & `/api/macros/open-window`.
- **Dedicated Anti-AFK & Guard View (`AntiAfk.cs`, `MaxAlts.cs`, `LocalUiServer.cs`, `ui/app.html`)**: Consolidated Keep-Alive scheduling, manual **`POST /api/afk/pulse`**, Watchdog auto-relaunch, Error 277 / kick Auto-Reconnect, Crash Breaker thresholds, Ghost Killer, idle UI lock, and cyclic/daily Farm Scheduler.
- **Smart Eco & RAM Cleaner (`MaxAlts.cs`, `MemClean.cs`)**: Single `RobloxPids()` enumeration per tick, cached per-PID throttle state, Windows EcoQoS, CPU core pinning, and `MaxAltsTrimMinMb` memory trim floor.
- **Auto-Tile Window Layouts & FastFlags (`WindowLayout.cs`, `Gfx.cs`)**: Multi-monitor `Auto`, `Grid`, and `FixedSize` tiling, borderless mode, server browser Copy Job ID & ping badges, and `ClientAppSettings.json` profiles (`Default`, `Low`, `Potato`, `Ultrapotato`, `Custom`).
- **Live Presence, Tags & Global Hotkeys (`Api.cs`, `Hotkeys.cs`, `Models.cs`)**: Batch-check live Roblox Presence (`InGame`, `Website`, `Studio`, `Offline`), organize accounts with searchable comma-separated tags, and trigger global Windows hotkeys (`Ctrl+Shift+T` Auto-Tile, `Ctrl+Shift+M` Clean RAM, `Ctrl+Shift+K` Panic Stop All).
- **Encrypted Vault, `.v9vault` Backup & GitHub Updater (`Store.cs`, `Updater.cs`)**: Windows DPAPI or portable PBKDF2 + AES-256-CBC + HMAC-SHA256 encryption, single-file `.v9vault` export/import, and on-demand GitHub Releases version check.
- **Hardened Localhost Architecture (`DesktopShell.cs`, `LocalUiServer.cs`, `Launcher.cs`)**: Strictly binds to `IPAddress.Loopback` (`127.0.0.1`) with an ephemeral 144-bit session token, `InstancesLock` thread-safe process tracking, `roblox-player:` URI protocol launching, and optional `SemaphoreSlim` HTTPS webhooks.
- **Zero-SDK Windows Build, Installer & GitHub Release Automation (`Build.bat`, `Run.bat`, `Publish-GitHub.bat`, `installer/`, `.github/`)**: One-click compilation via built-in `.NET Framework 4.x` (`csc.exe`), custom multi-res icon (`app.ico`), per-user `Install.bat`, Inno Setup script, GitHub publish script, and automated GitHub Actions Release pipeline producing standalone `.exe`, portable `.zip`, Setup `.exe`, and `SHA256SUMS.txt`.
