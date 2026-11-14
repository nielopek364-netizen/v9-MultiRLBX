# Contributing to v9 MultiRLBX

Thank you for your interest in contributing to **v9 MultiRLBX**!

## Critical Compiler Constraint: C# 5.0 Only

To let every Windows 10/11 user compile directly from source with **zero SDK downloads**, `Build.bat` uses the built-in .NET Framework 4 compiler:

```cmd
%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

That compiler supports **up to C# 5.0**. Pull requests that use C# 6+ syntax will fail the GitHub Actions CI build and will not be merged.

### Syntax to Avoid in `src/*.cs`
- String interpolation (`$"hello {name}"`) -> use `"hello " + name` or `string.Format(...)`
- Null-conditional operators (`obj?.Prop`, `arr?[0]`) -> use explicit `if (obj != null)` checks
- Inline `out` variable declarations (`int.TryParse(s, out int n)`) -> declare `int n;` beforehand
- `nameof(...)` expressions -> use string literals
- Expression-bodied members (`=>`) for methods/properties -> use `{ return ...; }` (lambda expressions `x => ...` in LINQ are fine)
- Pattern matching (`is Type x`), switch expressions, or C# 7 value tuples `(a, b)`

## Project Structure

- `src/Program.cs`: Single-instance mutex, crash handlers, `--memhelper` entry point, vault unlock prompt
- `src/TrayHost.cs`: System tray icon, context menu, and single-instance wake thread
- `src/DesktopShell.cs`: Chromeless browser app shell launcher with default-browser fallback
- `src/LocalUiServer.cs`: Embedded loopback-only HTTP/JSON server (`127.0.0.1`)
- `src/AppEngine.cs`: Launch queues, fast/slow timers, watchdog, farm scheduler, `.v9m` runner, RAM cleaner bridge
- `src/Hotkeys.cs`: Global Windows hotkeys (`Ctrl+Shift+T` Tile, `Ctrl+Shift+M` RAM Clean, `Ctrl+Shift+K` Stop All)
- `src/Launcher.cs`: `MultiInstance` singleton handle holder, crash breaker, Auto-Reconnect, WMI tracker, process launcher
- `src/Api.cs`: Roblox HTTP client with CSRF handshake, batched cookie checks, and live Presence API
- `src/Store.cs`: Thread-safe persistence with Windows DPAPI or AES-256-CBC + HMAC-SHA256 and `.v9vault` export/import
- `src/Updater.cs`: On-demand GitHub Releases check for `nielopek364-netizen/v9-MultiRLBX`
- `ui/app.html`: Single-file desktop web UI embedded as a compiled assembly resource (`MultiRLBX.app.html`)

## Development Workflow

1. Edit source files in `src/` or `ui/app.html`.
2. Run `Build.bat` (or `Run.bat`) to verify compilation with `csc.exe`.
3. Note that `LocalUiServer.cs` prefers a local `ui/app.html` file next to the `.exe` during development and falls back to the embedded resource `MultiRLBX.app.html` in standalone builds.
4. Keep XML documentation comments (`/// <summary>`) and `docs/` guides synchronized with code changes.

## Pull Request Checklist

- [ ] Compiles cleanly with `Build.bat` on .NET Framework 4 `csc.exe` (no C# 6+ syntax).
- [ ] No external NuGet/SDK dependencies or network sockets outside `127.0.0.1` loopback + Roblox APIs.
- [ ] No hardcoded secrets, personal tokens, telemetry, or third-party promotional links.
- [ ] UI changes tested in both **PL** and **EN**, and in both **Dark** and **Light** themes.
