# Building & Installing v9 MultiRLBX

v9 MultiRLBX compiles directly on Windows 10 and Windows 11 using the built-in **.NET Framework 4.x (`csc.exe`)** compiler: **no Visual Studio, .NET SDK, or NuGet restore required**.

---

## Quick Start (End Users)

1. Download or clone the repository from **[github.com/nielopek364-netizen](https://github.com/nielopek364-netizen/)**.
2. Double-click **`Run.bat`** in the repository root.
   - If `v9 MultiRLBX.exe` is not yet built, `Run.bat` compiles it via `Build.bat` first and then launches the app.
3. Wait ~3 seconds for compilation; `v9 MultiRLBX.exe` starts with its custom icon (`app.ico`) and opens the chromeless desktop dashboard.

---

## Manual Compilation (`Build.bat`)

Run `Build.bat` from Command Prompt or double-click it to produce `v9 MultiRLBX.exe` in the root folder:

```cmd
Build.bat
```

`Build.bat` locates `%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (with x86 fallback), embeds `app.ico`, `app.manifest`, and `ui\app.html` (`MultiRLBX.app.html`), and compiles all sources under `src\*.cs`.

---

## Windows Installation Options

### Option A: Per-User Script (No Admin Required)

Run **`installer\Install.bat`** (wraps `installer\install.ps1`):
- Compiles `v9 MultiRLBX.exe` if missing
- Installs to `%LOCALAPPDATA%\Programs\MultiRLBX`
- Creates Start Menu + Desktop shortcuts with `app.ico`
- Registers an **Add or Remove Programs** entry under `HKCU`

To uninstall, use Windows Settings -> Installed Apps or run `installer\uninstall.ps1`.

### Option B: Standalone Setup Installer (Inno Setup 6+)

1. Run `Build.bat` so `v9 MultiRLBX.exe` exists in the root directory.
2. Compile `installer\MultiRLBX.iss` with [Inno Setup 6+](https://jrsoftware.org/isinfo.php):
   ```cmd
   ISCC.exe installer\MultiRLBX.iss
   ```
3. Output binary: `installer\Output\v9-MultiRLBX-Setup.exe`.

---

## GitHub Actions CI & Publishing

Run **`Publish-GitHub.bat`** (Windows) or **`./scripts/publish-github.sh`** (Git Bash / Linux / macOS) to initialize Git cleanly, verify `.gitignore`, commit source files, tag `v1.0.0`, and push to GitHub.

Tagging a commit (`git tag v1.0.0 && git push --tags`) triggers `.github/workflows/build.yml` on `windows-latest`, compiling `v9 MultiRLBX.exe`, generating `SHA256SUMS.txt`, and publishing the release zip + executable.

