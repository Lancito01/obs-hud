# OBS HUD

See at a glance whether OBS is recording or buffering — with a pop-up and a sound every time something happens.

![OBS HUD over a game: a REC and REPLAY pill with a "Clip saved" notification below it](docs/hero.png)

- Click-through and never steals focus, so it's safe over games
- Hidden from OBS captures, so it never shows up in your clips
- Uses no CPU while idle (it listens to OBS instead of polling)

## Get started

1. In OBS 28+, open **Tools > WebSocket Server Settings** and tick **Enable WebSocket server**.
2. Download **`obs-hud.exe`** from the [latest release](../../releases/latest), put it in its own folder and run it.

That's it. Right-click the tray icon to move it, test it, or edit settings.

> Windows may warn that the app is unsigned the first time: **More info > Run anyway**.

## What you'll see

A small pill while something is running:

![Status pills: recording with replay buffer, paused, and replay buffer only](docs/pills.png)

And a pop-up for each event:

![Notifications: recording started, clip saved, recording paused, recording saved, replay buffer on, lost connection](docs/notifications.png)

## Settings

`obs-hud.ini` appears next to the exe on first run. Changes apply as soon as you save. The main ones:

- `Position`: `TopLeft`, `TopCenter`, `TopRight`, `BottomLeft`, `BottomCenter` or `BottomRight`
- `PeekHotkey`: a hotkey that flashes the current status, e.g. `Ctrl+Alt+S`
- `Device`: play sounds on a device OBS doesn't record, so beeps stay out of your clips

Custom sounds: drop `clip.wav`, `rec_start.wav`, `rec_stop.wav` (and so on) in a `sounds` folder next to the exe.

## Build from source

Run `build.cmd`. It uses the C# compiler built into Windows, so there's nothing to install.
