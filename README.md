# OBS HUD

A tiny always-on-top overlay for OBS on Windows. It shows a status pill while you're recording or the replay buffer is on, pops up a short notice when something happens (clip saved, recording started/stopped/paused, replay buffer on/off, OBS disconnected), and plays a different sound for each.

It listens to OBS's built-in WebSocket events, so it doesn't poll and uses no CPU while nothing is happening. The overlay is click-through, never steals focus, stays out of the taskbar's way, and by default is hidden from OBS's own captures so it never ends up in your clips.

## Install

1. Download `obs-hud.exe` from the [latest release](../../releases/latest) and put it in its own folder (it writes its settings and a log next to itself).
2. In OBS (28 or newer), open **Tools > WebSocket Server Settings** and tick **Enable WebSocket server**.
3. Run `obs-hud.exe`. A tray icon appears; right-click it to save a replay, open your recordings, change the position, run a test of every notification, or edit the settings.

To start it with Windows, put a shortcut to `obs-hud.exe` in `shell:startup`.

Windows SmartScreen may warn about an unsigned app the first time. Click **More info > Run anyway**, or build it yourself (below).

## Settings

`obs-hud.ini` is created next to the exe on first run, and saving it applies changes right away:

| Setting | What it does |
| --- | --- |
| `Position` | `TopLeft`, `TopCenter`, `TopRight`, `BottomLeft`, `BottomCenter` or `BottomRight` |
| `Pill` | `1` keeps a small status pill up while recording or buffering, `0` shows notices only |
| `ToastSeconds` | How long a notice stays up |
| `PeekHotkey` | Hotkey that flashes the current status, e.g. `Ctrl+Alt+S`; empty turns it off |
| `Scale` | Size multiplier on top of Windows display scaling |
| `HideFromCapture` | `1` keeps the HUD out of OBS display captures and screenshots |
| `Sounds`, `Volume` | Turn sounds on/off and set their volume (0-100) |
| `Device` | Part of a playback device name to play sounds on; empty = Windows default. Pick one OBS doesn't capture so the beeps stay out of your clips |
| `Host`, `Port`, `Password` | Leave `Port` and `Password` empty and they're read from OBS's own WebSocket settings on this PC. Only fill them in to connect to OBS on another machine |

### Custom sounds

Put any of `clip.wav`, `rec_start.wav`, `rec_stop.wav`, `replay_on.wav`, `replay_off.wav`, `pause.wav`, `resume.wav` or `warn.wav` (PCM WAV) in a `sounds` folder next to the exe.

## Build it yourself

Run `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4.x), so there's nothing to install.
