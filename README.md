<p align="center">
  <img src="docs/icon.png" width="112" alt="RitschyMirror logo">
</p>

<h1 align="center">RitschyMirror</h1>

<p align="center">
  <b>Mirror any monitor to your capture card — with proper HDR&nbsp;→&nbsp;SDR tonemapping.</b>
</p>

<p align="center">English · <a href="README.ko.md">한국어</a></p>

<p align="center">
  A lightweight Windows tool for <b>dual-PC / capture-card streaming setups</b>.<br>
  Pick a source monitor, fit it cleanly onto your capture display, lock the mouse out — done.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.4.3-2ea0c9">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20(x64)-2ea0c9">
  <img src="https://img.shields.io/badge/license-MIT-3aa757">
  <img src="https://img.shields.io/badge/.NET-9-512bd4">
</p>

---

## What is this for?

If you stream from a **two-PC setup** (a gaming PC feeding a capture card on a second
streaming PC), you usually need to send **one specific monitor** into the capture card.
The common trick is OBS's *Fullscreen Projector* — but it has real problems:

- 🌫️ **HDR looks washed-out / grey** when a capture card grabs it as SDR.
- 📐 **Ultrawide / mismatched resolutions** don't fit a 16:9 capture cleanly.
- 🖱️ The projector eats a whole display and **your mouse keeps wandering into it**.
- 🧩 You're locked into whatever OBS decides to do with the image.

**RitschyMirror** is a small, dedicated app that does exactly this one job — and does it
better. It grabs a monitor, **tonemaps HDR → SDR** on the GPU, **fits / letterboxes / crops**
it to your capture resolution, optionally **locks the mouse out of the target display**, and
sends the result to the display your capture card is plugged into.

> Originally built to replace the OBS projector in my own dual-stream rig. It runs quietly in
> the system tray and just works.

<p align="center">
  <img src="docs/screenshot-settings.png" width="430" alt="RitschyMirror settings window">
</p>

---

## Features

- 🎯 **Pick source & target monitor** from dropdowns — real monitor names (e.g. *AORUS FO32U2P*),
  HDR-aware, multi-GPU aware (source and target may even hang off **different graphics cards**).
- 🧭 **Choose the monitor capture API** — DXGI Desktop Duplication (default, lowest overhead) or
  Windows.Graphics.Capture (works across GPUs, cursor drawn by Windows).
- 🪟 **Or mirror a single window / fullscreen app** instead of a whole monitor — pick one app
  (e.g. just your game). Only that window is shown, so **alt-tabbing never flashes your desktop**
  into the capture card. Built on Windows.Graphics.Capture (the modern OS API, *not* injection →
  no anti-cheat risk, robust even when capture and game live on different GPUs).
- 🌈 **HDR → SDR tonemapping** on the GPU (`bt2390`, `reinhard`, `hable`, `aces`) with **live**
  exposure / saturation / contrast / gamma / paper-white controls. Applied **only when the source
  really is HDR** — an SDR source is passed through 1:1, and turning HDR on or off is picked up
  while the mirror is running.
- 🖼️ **Layout modes** — how the source fills the output:
  - `fit` — whole source, aspect-preserved, nothing cut off (true 1:1 mirror)
  - `stretch` — fill the frame (ignore aspect)
  - `top_strip` — source on top, black bar below (e.g. fit 21:9 content into a 16:9 frame)
  - `crop_region` — mirror a freely defined rectangle of the source
- 🔍 **Scale filter** — Bilinear, Area, Bicubic (Mitchell / Catmull-Rom) or Lanczos 2/3 when the
  source has to be shrunk (or enlarged): Area and Lanczos keep thin lines and small text clean
  instead of shimmering. At 1:1 nothing is filtered.
- 🖥️ **Output modes**:
  - `windowed` — a normal test window
  - `borderless` — borderless fullscreen on the target display
  - `fullscreen_block` — borderless fullscreen **+ mouse-lock** (the cursor is kept out of the
    target display; works on any setup, including cross-GPU). The mouse-lock runs on its own
    thread, so the cursor stays smooth at full polling rate (v1.2.1).
  - `exclusive` — true DXGI exclusive fullscreen (best when source & target share one GPU; falls
    back automatically to `fullscreen_block` otherwise)
- 🪟 **Lives in the system tray** — start/stop mirroring without a console window; nothing to
  babysit. Optional **auto-start at login**.
- 🎚️ **Live config** — most settings (image, layout, crop) apply **instantly** while mirroring.
- 🛡️ **Keeps your PC awake while mirroring** — holds off display power-off and sleep so the
  capture-card display can't drop out mid-stream (like a video player). On by default,
  toggleable in Settings and the dock.
- 🔌 **OBS / browser control dock** — the app serves a small control page you can add to OBS as a
  **Custom Browser Dock** (or open in any browser): status light, start/stop, monitor pick, and
  **live image + crop sliders**. Control the mirror without alt-tabbing. See below.
- 🎛️ **HTTP API** — `start` / `stop` / `config` / `displays` over LAN, so Stream Deck (via a
  web-request plugin) and custom dashboards can drive it too.
- ⏱️ **Target FPS** *(optional, VSync off only)* — cap the mirror at e.g. 30 / 60 / 120 fps.
  Frames are paced with a high-resolution timer, nothing is rendered or presented while the source
  hasn't changed, and every output slot uses the **newest** source frame (no queued, stale frames →
  no extra latency). With VSync on, the display refresh paces the output and the limit is ignored.
- 🔊 **Audio mirroring** *(optional)* — mixes the sources you tick into the HDMI/DP audio of the
  target monitor, so the capture card gets the sound too: any output device as it sounds (e.g.
  speakers **and** the headset Discord plays to) and input devices such as a mic, each with its own
  gain plus a common gain per group (dB). It reacts to device changes live, never loops the target
  back into itself, and shows what it mixes and what it skipped (and why).
- 📊 **Capture / output FPS readout** *(optional, for verification)* — shows the capture and output
  frame rates in a corner of your choice (top/bottom, left/right). Note that it is drawn into the
  mirrored picture, so the capture card sees it as well.
- 🗂️ **Tidy settings window** — one collapsible section per feature, each with its on/off switch
  and a live summary in the header (running state, frame rates, audio status …), plus a
  **live log pane** at the bottom that you can resize or hide.
- 🌐 **Language packs** — UI in **English, 한국어 and Deutsch**, following the Windows display
  language (or picked in Settings). Drop a `lang\<code>.json` next to the exe to tweak a
  translation or add a new language — the OBS dock uses the same packs.

---

## Download & Install

1. Go to the [**Releases**](https://github.com/gloomycoder/ritschy-mirror/releases) page and
   download **`RitschyMirror-1.4.3-portable.zip`**.
2. Extract it anywhere you can write to (e.g. `D:\Tools\RitschyMirror`). It contains a single,
   self-contained `RitschyMirror.exe` (no .NET install needed) and `config\` with the defaults.
3. Run `RitschyMirror.exe` — the app starts in your tray; no admin / UAC prompt. `log\` is created
   next to it on first start. Use **Settings → Connection / autostart** to start it at login.

> **Windows SmartScreen note:** the exe isn't code-signed yet, so Windows may show
> *"Windows protected your PC"*. Click **More info → Run anyway**.

To remove it: quit it from the tray, untick login autostart first if you enabled it, then delete
the folder.

> Building the Inno Setup installer yourself is still supported — see *Build from source*.

---

## Quick start

1. Click the tray icon → **Settings**.
2. **Source monitor** = the monitor you want to capture. **Target monitor** = the display your
   capture card is plugged into.
3. Pick a **Layout mode** (`fit` is the safe default) and **Output mode**
   (`fullscreen_block` if you want the mouse locked out of the capture display).
4. Hit **Start** in the tray menu (or in the settings window).
5. Tune the image live with the **Tonemap / sliders** if your source is HDR.

---

## Remote control — OBS browser dock

The mirror runs on the PC whose monitor you're capturing; the **control dock** lets you start/stop
and tweak it from anywhere — most usefully right inside OBS on your streaming PC.

1. In OBS: **Docks → Custom Browser Docks…**
2. Dock Name: `RitschyMirror`, URL:
   - same PC as OBS → `http://localhost:8788/`
   - mirror on another PC (two-PC setup) → `http://<mirror-pc-ip>:8788/`
3. **Apply** → the dock appears; drag it where you like.

> For the dock to be reachable from another PC, the mirror PC must allow the agent on the network
> (the installer/app uses port `8788`; allow it through the firewall). On a single PC, `localhost`
> works out of the box.

The dock gives you a status light, one-click Start/Stop, source/target monitor pickers, layout &
output mode, and **live image + crop sliders** — no extra software, it's served by the app itself.

---

## Configuration

Folder layout next to the executable:

```
RitschyMirror.exe
bin\      dependency DLLs (only in a normal build — the installer's single-file exe bundles them)
config\   mirror_config.json, app_settings.json
lang\     your own language packs / translation overrides (optional)
log\      mirror_<date>_<time>.log — one file per app start, deleted after 30 days
```

Files from older versions (config and `mirror.log` next to the exe) are moved into these folders
automatically on first start.

Settings live in two JSON files in `config\` (you normally never touch these — the
GUI writes them):

- **`mirror_config.json`** — render parameters. Image & layout keys apply **live**; structural
  keys (`source_display`, `target_display`, `output_mode`, `output_bit_depth`, window size) take
  effect on the next **render restart**. New in 1.4 (all live): `target_fps` (0 = unlimited),
  `fps_overlay`, `fps_overlay_position` (`top_left` / `top_right` / `bottom_left` /
  `bottom_right`), `audio_mirror`.
- **`app_settings.json`** — the HTTP agent (port / bind / enabled), auto-start and `language`
  (`auto`, `en`, `ko`, `de`, or any pack you add).

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full key reference, the HTTP agent
API, and how the rendering pipeline works internally.

---

## Build from source

Requires the **.NET 9 SDK** (and, for the installer, **[Inno Setup 6](https://jrsoftware.org/isinfo.php)**).

```powershell
# 1) Build a self-contained single-file .exe (no .NET needed on the target PC):
powershell -ExecutionPolicy Bypass -File publish.ps1

# 2a) Build the polished installer (RitschyMirror-Setup-x.y.z.exe):
powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1

# 2b) …or just drop the app into place without an installer:
powershell -ExecutionPolicy Bypass -File install.ps1
```

---

## Tech

C# / .NET 9 + [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D 11 /
DXGI) + [NAudio](https://github.com/naudio/NAudio) (WASAPI, audio mirroring). It uses **DXGI Desktop Duplication** (FP16, HDR-capable) → an **HLSL tonemap shader** →
a flip-model swap chain. Full internals in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## License

[MIT](LICENSE) © RitschyRigz
