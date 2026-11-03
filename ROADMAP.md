# Roadmap

## Done in v1.4.15 ✓

- **Exact window match** — window capture resumes on the exact title; similar titles (game build
  numbers) are an opt-in setting; full release of enumeration objects on errors.

## Done in v1.4.14 ✓

- **Same-window recovery** — a missing window is only resumed when that window is back (exe + title),
  never another window of the same app; adapter cleanup on every enumeration path.

## Done in v1.4.13 ✓

- **Wording & docs** — UI text reviewed in all three languages, README framed around the output
  monitor, PresentMon measurement of the effect on a running game.

## Done in v1.4.12 ✓

- **Always-on resilience** — DXGI waits out UAC / lock screen, missing monitors and windows are
  waited for, reinits never give up; single instance with an "already running" notice.

## Done in v1.4.11 ✓

- **Live device list in settings** — the audio section follows default-device and plug changes without
  a refresh; gains can be typed.

## Done in v1.4.10 ✓

- **Volume follow per device** — hardware volume followed, software volume (already in the loopback)
  left alone, mute always; measured on HDMI and USB audio.

## Done in v1.4.9 ✓

- **Audio follows output volume** — output-device sources follow their Windows volume and mute (switchable);
  the audio worker recovers from unexpected errors on its own.

## Done in v1.4.8 ✓

- **Stability** — no overlapping render threads after a slow stop, crash-safe audio cleanup, WGC
  stop race closed; a once-a-minute soak log (fps, memory, audio drift) for long runs.

## Done in v1.4.7 ✓

- **Audio tail fix** — the end of a short sound is played out completely; no silence padding mid-sound.

## Done in v1.4.6 ✓

- **Audio priming fix** — short sounds after silence play out instead of staying queued.

## Done in v1.4.5 ✓

- **Simpler capture setup** — one capture mode (Monitor DXGI / Monitor WGC / Window WGC) and one
  capture target list; "output monitor" wording throughout.

## Done in v1.4.4 ✓

- **Audio robustness** — retry with back-off after failed restarts, continuous drift compensation,
  channel-mask downmix, name-selected default stays visible, manual output device.
- **Monitor capture API choice** — DXGI Desktop Duplication or Windows.Graphics.Capture.

## Done in v1.4.3 ✓

- **Selectable scale filter** — Bilinear, Area, Bicubic (Mitchell / Catmull-Rom), Lanczos 2/3 as a
  two-pass linear-light resample; never applied at 1:1.

## Done in v1.4.2 ✓

- **Primary monitor excluded as target** (listed dimmed, unselectable; refused at start) — the tray
  stays reachable to stop the mirror.

## Done in v1.4.1 ✓

- **Multi-source audio mirroring** — any output devices (loopback) and input devices mixed into
  the target monitor's HDMI/DP audio, with a gain per device plus a common gain per group (dB).

## Done in v1.4.0 ✓

- **Target FPS** with high-resolution pacing and newest-frame-first capture; render/present skipped
  while nothing changes.
- **Audio mirroring** of the default playback device to the target monitor's HDMI/DP audio, with
  live pause/resume on default-device changes.
- **Capture / output FPS readout** (corner selectable) and FPS/audio status in the HTTP API.
- **Language packs** (en / ko / de, user-extendable) for settings, tray, dock and errors.

## Done in v1.3.2 ✓

- **Correct colour maths for an SDR source.** DXGI Desktop Duplication and Windows.Graphics.Capture
  return `R16G16B16A16_FLOAT` frames in **linear scRGB regardless of the display's HDR state**
  (measured, not assumed). The shader used to sRGB-decode that buffer whenever the display reported
  SDR, applying a second gamma and crushing the picture (mid-grey 128 → 55). The shader's input
  contract is now explicit — `SrcIsLinear` (buffer encoding) and `SrcScale` (buffer value → white
  point) instead of one overloaded `InputIsHdr` flag.
- **Tone mapping derives its range from the source.** New `SrcPeak`, the source peak expressed in
  white-point units: HDR → `source_peak_nits / target_paperwhite`, SDR → `1.0`, i.e. no head-room,
  so the operator is skipped instead of squashing an already-finished SDR picture.
- **Live HDR re-detection.** New `ICaptureSource.RefreshSourceState()`, called periodically from the
  render loop and after every `Recover()` — a mode change is the most common cause of capture loss,
  and HDR on/off is exactly such a change. Toggling HDR no longer needs a render restart.

## Done in v1.3.1 ✓

- **Fixed a rare mid-stream `FailFast` crash in the window message pump.** The window procedure's
  delegate could be GC-collected while Windows was still dispatching to it (window and delegate
  formed a self-referential cycle), and a class-registration quirk made later windows reuse the
  first window's delegate. Replaced with a **process-wide static window procedure** (registered
  once, dispatch by HWND), eliminating both the collectible cycle and the dangling-pointer path.
  Distinct from the v1.2.2 capture-loss crash.

## Done in v1.3.0 ✓

- **Window / fullscreen-app capture** — a second source mode next to whole-monitor capture, built
  on **Windows.Graphics.Capture** (`WindowCapture` behind a new `ICaptureSource` seam, so the
  tonemap/layout/present pipeline is unchanged). Mirrors a single window picked by stable identity
  (exe + title, replug-proof like the monitor selection). Wired through Settings, the OBS dock,
  the HTTP agent (`/windows`), and the Cockpit. No injection → anti-cheat-safe and cross-GPU
  robust. Doesn't expose the desktop on alt-tab.

## Done in v1.2.2 ✓

- **Self-healing capture** — when DXGI Desktop Duplication is lost (`DXGI_ERROR_ACCESS_LOST`:
  source mode change, fullscreen app, UAC secure desktop, cross-GPU hiccup), the engine first
  retries the duplication a few times; if that fails it tears down and **rebuilds the entire
  render chain** (device + capture) under a supervisor loop with exponential back-off and a retry
  cap. Fixes the rare mid-stream **crash** where a failed re-grab left a dangling capture, the
  next native D3D call hit freed memory (an access violation managed `try/catch` can't intercept),
  and the whole process fast-failed and vanished.

## Done in v1.2.1 ✓

- **Smooth cursor in `fullscreen_block`** — the mouse-lock's low-level hook moved off the
  vsync-bound render thread onto its own dedicated, fast-pumping thread. The cursor stays
  smooth at full polling rate (no more render-cadence throttling) while the mouse is still
  locked out of the target display.

## Done in v1.2.0 ✓

- **Keep the PC awake while mirroring** — the mirror now holds off display power-off and system
  sleep while it's running (`SetThreadExecutionState`, like a video player), so the capture-card
  display can't drop out because Windows blanked the screen. On by default, live-toggleable from
  Settings and the OBS dock, and released automatically when mirroring stops.

## Done in v1.0.2 ✓

- **Single-instance guard** — a second launch (or a stray double autostart) no longer creates a
  second tray icon + HTTP agent; it brings the running instance's settings to the front and exits.
- **Autostart unified on one mechanism** — installer and the Settings checkbox now both manage the
  same `HKCU\…\Run` value, so they can't create a double autostart and the checkbox always reflects
  reality. (Previously: installer = Startup-folder shortcut, Settings = Run value → could double up.)
- Tray icon shows status (green = mirroring, red = stopped); version in the settings title bar;
  About section (version / copyright / MIT / GitHub link); GitHub update check (notify only).

## Done in v1.1.0 ✓

- **OBS browser dock** — the HTTP agent serves a small control page (status, start/stop, monitor
  pick, live crop & image sliders) at `/` and `/dock` that you can add as an **OBS custom browser
  dock** or open in any browser. Same-origin → no CORS, no extra hosting. Reaches everyone who runs
  OBS, no separate app needed.

## Later / nice-to-have

- **Stream Deck integration** — the HTTP API already lets you bind buttons to `start` / `stop` /
  `config` (on-the-fly crops!) today via a generic web-request plugin; a dedicated Stream Deck
  plugin with status feedback + dial controls (live crop / saturation) would be the premium step.
- **Code signing** — sign the installer + exe (Authenticode) to remove the Windows SmartScreen
  "Windows protected your PC" warning on first run.
- **DisplayFusion profile hook** — optionally load a monitor profile when the mirror starts, so
  the capture-card display is activated automatically before mirroring.
- **Wake-from-off (advanced)** — document a Wake-on-LAN + auto-login path so a controller (e.g.
  the RitschyBot Cockpit) can bring the machine up and have the agent auto-start, enabling
  control even when the PC was fully off.
- **In-app auto-update (optional)** — beyond the current notify-only check, optionally download &
  launch the new installer from within the app.
