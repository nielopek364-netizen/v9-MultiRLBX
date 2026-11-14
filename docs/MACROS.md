# Custom `.v9m` Macro Format (`#v9m 2`)

**v9 MultiRLBX** ships with an open, human-readable **`.v9m`** macro format designed by **v9pv** for farm automation and humanization.

Files are saved in UTF-8 under:

```text
%APPDATA%\MultiRLBX\macros\<name>.v9m
```

---

## File Structure

```text
#v9m 2
#name=Auto Jump & Collect
#author=v9pv
#speed=1
#repeats=5
#jitterMs=18
#jitterPx=3
#steps=5
500 5 500 0 0 0 0
620 4 32 0 0 0 0
680 4 32 0 1 0 0
780 4 69 0 0 0 0
840 4 69 0 1 0 0
```

### Header Directives

| Directive | Type | Description |
| :--- | :--- | :--- |
| `#v9m 2` | Magic | Format signature & parser version (`v2`) |
| `#name=` | string | Display name shown in the Web Studio & embedded `.v9m` library |
| `#author=` | string | Author tag embedded on save (default: `v9pv`) |
| `#speed=` | double | Playback multiplier (`0.1` to `100`, e.g., `1.5` = 1.5x faster) |
| `#repeats=` | int | Loop count (`1` to `9999`) |
| `#jitterMs=` | int | Random per-step timing variance (`+/- ms`) for humanization |
| `#jitterPx=` | int | Random pointer offset (`+/- px`) applied to mouse coordinates |
| `#steps=` | int | Total recorded/generated event lines |

---

## Event Line Schema

Each line uses 7 space-separated fields:

```text
<timeMs> <kind> <a1> <a2> <flags> <x> <y>
```

| `kind` | Event Type | Meaning of `a1` / `flags` / `x, y` |
| :--- | :--- | :--- |
| `0` | Mouse Move | Moves pointer to screen coordinates `(x, y)` |
| `1` | Mouse Button | `a1`: `1`=Left, `2`=Right, `3`=Middle · `flags`: `1`=Down, `0`=Up at `(x, y)` |
| `2` | Mouse Wheel | Vertical scroll delta in `a1` |
| `3` | HWheel | Horizontal scroll delta in `a1` |
| `4` | Keyboard | `a1`: Windows Virtual-Key code (e.g., `32`=Space, `69`=E) · `flags`: `0`=KeyDown, `1`=KeyUp |
| `5` | Wait Checkpoint | Delays `a1` ms without synthesizing input |

---

## Features in `MacroForm.cs`, `Macro.cs` & `LocalUiServer.cs`

1. **Classic Floating Macro Recorder (`MacroForm.cs`)**: Always-on-top overlay launched from the dashboard (`Open Macro Window`) or system tray (`Macro Recorder (.v9m)`). Supports global **F9** (Record / Stop) and **F10** (Play / Stop) hotkeys, `.v9m` Open/Save dialogs, Speed (`0.1x–100x`), Repeats, Pause between loops (`ms`), Continuous loop toggle, Record Mouse Moves, and Trim Leading Idle.
2. **Dashboard Hook Recording & Playback**: `POST /api/macros/record-start` and `POST /api/macros/record-stop` also work straight from the **Macros (`.v9m`) & Presets** tab with target window focus and humanization jitter (`jitterMs` / `jitterPx`).
3. **Target Window Focusing**: Optionally brings a chosen Roblox account window to the foreground (`Native.BringFront`) before playback starts.
4. **Local API Endpoints (`LocalUiServer.cs`)**:
   - `POST /api/macros/open-window`: Opens or focuses the classic `MacroForm` overlay (`F9` / `F10`)
   - `POST /api/macros/record-start`: Begins low-level hook recording (`MacroRecorder.Start`)
   - `POST /api/macros/record-stop`: Stops hook capture, trims leading idle, and writes `.v9m`
   - `POST /api/macros/save`: Writes `.v9m` metadata & steps to `%APPDATA%\MultiRLBX\macros`
   - `POST /api/macros/play`: Plays `.v9m` with custom speed, repeats, pause, jitter, and target account
   - `POST /api/macros/stop`: Immediately aborts active macro playback
   - `POST /api/macros/delete`: Removes a `.v9m` file from the local library
