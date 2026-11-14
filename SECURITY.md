# Security Policy

## Local Storage & Encryption Model

- **DPAPI Mode (default)**: When no vault password is set, `accounts.dat` is encrypted using Windows `ProtectedData.Protect` (`DataProtectionScope.CurrentUser`) with application entropy. Only the same Windows user on the same machine can decrypt it.
- **Portable Password Mode**: When a vault password is set, `accounts.dat` is encrypted using:
  - **PBKDF2-HMAC-SHA1** (100,000 iterations, random 16-byte salt) deriving a 256-bit encryption key and a 256-bit MAC key
  - **AES-256-CBC** (PKCS7 padding, random 16-byte IV)
  - **Encrypt-then-MAC** using **HMAC-SHA256** with constant-time verification
- **Loopback-Only UI Isolation**: The local desktop UI server (`LocalUiServer.cs`) binds exclusively to `127.0.0.1` (`IPAddress.Loopback`) and enforces a cryptographically random 144-bit session token (`X-MultiRLBX-Token` / `?t=`) generated at every startup. No LAN or external remote ports are opened.

## Antivirus / Windows SmartScreen False Positives

Unsigned binaries compiled locally with `csc.exe` or downloaded from GitHub Releases that interact with `ROBLOX_singletonMutex`, low-level input hooks (`MacroRecorder`), or Windows memory lists (`MemClean`) may occasionally trigger heuristic warnings in Windows Defender SmartScreen.

To verify binary integrity:
1. Check the `SHA256SUMS.txt` attached to the official GitHub Release.
2. Or inspect the source code directly and compile locally using **`Build.bat`** / **`Run.bat`** (uses Windows' built-in `.NET Framework 4` `csc.exe`).

## Reporting a Vulnerability

If you discover a security vulnerability (for example: token bypass in `LocalUiServer`, cryptographic flaw in `Store.cs`, or privilege escalation in `--memhelper`):

1. **Do not** open a public GitHub issue with exploit details.
2. Use **GitHub Security Advisories** ("Report a vulnerability" in the repository's **Security** tab) to report it privately.
3. Include steps to reproduce, affected version, and potential impact. We will investigate and publish a fix promptly.
