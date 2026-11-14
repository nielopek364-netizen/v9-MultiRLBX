# Settings & Architecture Reference

All application configuration, encrypted cookies, logs, and custom `.v9m` macros are stored locally in:

```text
%APPDATA%\MultiRLBX\
├── accounts.dat     # Encrypted cookies (DPAPI or AES-256-CBC + HMAC-SHA256)
├── settings.json    # Core, Launcher, Smart Eco, Layout, UI & Webhook settings
├── macros\          # Custom .v9m macro files
├── crash.log        # Unhandled exception diagnostics
└── audit.log        # Local account/launch audit trail
```

> **Localhost Only**: `LocalUiServer.cs` binds exclusively to `127.0.0.1` (`IPAddress.Loopback`) with an ephemeral per-session 144-bit token. No external LAN listeners or open firewall ports are used.

---

## 1. Accounts & Vault Security

- **DPAPI Mode (default)**: Uses Windows `ProtectedData.Protect` (`CurrentUser`). Only your Windows profile on the same PC can decrypt `accounts.dat`.
- **Portable Password Mode**: Set an optional vault password in the Settings/Tray menu to switch `accounts.dat` to **PBKDF2 (100,000 iterations) + AES-256-CBC + HMAC-SHA256**.

---

## 2. Launching & FastFlags (`Gfx.cs`)

All account launches use the official `roblox-player:` Protocol URI (`UseShellExecute = true`) with per-session auth tickets, avoiding binary modification or injection.

| Setting | Default | Description |
| :--- | :--- | :--- |
| `MultiInstance` | `true` | Holds `ROBLOX_singletonMutex` externally so multiple Roblox clients run together |
| `LaunchDelaySec` | `8` | Delay in seconds between sequential account launches in a batch |
| `AutoTileOnBatch` | `true` | Automatically tiles Roblox windows after a batch queue completes |
| `FpsCap` | `0` | Global FPS target written to `ClientAppSettings.json` (`0` = Roblox default) |
| `Gfx.Preset` | `Default` | Built-in FFlag profiles: `Default`, `Low`, `Potato`, `Ultrapotato`, `Custom` |
| `Gfx.Api` | `Default` | Rendering backend preference: `Default`, `D3D11`, `Vulkan`, `OpenGL` |

---

## 3. Smart Eco & Memory Cleaner (`MaxAlts.cs`, `MemClean.cs`)

| Setting | Default | Description |
| :--- | :--- | :--- |
| `MaxAlts` | `false` | Enables automatic optimization for unfocused background Roblox windows |
| `MaxAltsFps` | `10` | Background FPS target when Eco mode is active |
| `MaxAltsTrimMinMb` | `350` | Minimum working set (MB) before `EmptyWorkingSet` trims an idle process |
| `MaxAltsCores` | `0` | Caps background CPU core affinity (`0` = no core affinity limit) |
| `MaxAltsIdle` | `true` | Applies Idle process priority + Windows EcoQoS throttling |
| `AutoRelaunch` | `true` | Watchdog automatically restarts crashed or ghosted (`GhostKill`) clients |
| `AutoReconnectKick` | `false` | Detects kick / error 277 (`Disconnected` / `Crash`) & Presence drop to reconnect automatically |
| `ReconnectIdleSec` | `20` | Seconds before Presence drop confirms disconnect for Auto-Reconnect |
| `ScheduleEnabled` | `false` | Enables cyclic X-hour restarts and daily `HH:mm` start/stop Farm Scheduler |
| `ScheduleRestartHours` | `0` | Cyclic farm restart interval in hours (`0` = disabled) |
| `ScheduleStartTime` | `""` | Daily automatic launch time (`HH:mm`, optional) |
| `ScheduleStopTime` | `""` | Daily automatic stop-all time (`HH:mm`, optional) |
| `GlobalHotkeys` | `true` | Registers `Ctrl+Shift+T` (Tile), `Ctrl+Shift+M` (Clean RAM), `Ctrl+Shift+K` (Panic Stop) |
| `Mem.Threshold` | `80` | Free-RAM threshold (%) that triggers automatic standby/working-set cleaning |

---

## 4. Window Layout (`WindowLayout.cs`)

- **Modes**: `Auto` (aspect-ratio optimal grid), `Grid` (explicit rows/cols), `FixedSize`
- **Options**: `Gap` (px between windows), `Margin` (px screen inset), `Borderless` (strips title bar & frame), `Monitor` (target display index)

---

## 5. Encrypted `.v9vault` Backup & Updates (`Store.cs`, `Updater.cs`)

- **`.v9vault` Export / Import**: Serializes accounts and settings into a single encrypted payload protected with **PBKDF2-SHA1 (100,000 iterations) + AES-256-CBC + HMAC-SHA256** (`POST /api/vault/export`, `POST /api/vault/import`).
- **On-Demand Update Check**: Clicking **Check GitHub** calls `POST /api/update/check` (`Updater.CheckGitHubAsync`) to compare against GitHub Releases for `nielopek364-netizen/v9-MultiRLBX`.

---

## 6. Optional Webhooks (`Webhook.cs`)

Disabled by default. When enabled with an HTTPS URL, events (`WebhookCrash`, `WebhookLocked`, `WebhookBatch`, `DailyReport`) are dispatched sequentially through a `SemaphoreSlim` queue with automatic HTTP `429 Retry-After` handling. Zero Discord bot tokens, Rich Presence hooks, or external invite links exist in the codebase.
