# Changelog

## Unreleased

- **UI wording pass (en / ko / de).** "Capture card" is no longer assumed in the UI (the mirror goes
  to any output monitor); clearer names for the restart button, tone-map curve, paper white, source
  peak, scale filter, agent bind address; the tray says "stopped" instead of an "idle" that read
  like "waiting"; German menu entries say "Spiegelung" instead of "Mirror"; tidier filter
  descriptions and audio notes.
- Changing the mix output device or the output monitor updates the audio source list right away
  (the device that becomes the output drops out as a source) — no more "Refresh devices" needed.
- Gain boxes show their unit (dB).
- Docs: new README section **Performance & game impact** — PresentMon measurement showing that DXGI
  and WGC capture keep a borderless game in Independent Flip (no added latency), how the mirror's
  cost scales, and how to check your own setup.

## v1.4.12 — mirroring never gives up: survives UAC, waits for missing monitors

- **Mirroring never gives up.** It used to stop after 8 failed reinits; now it keeps retrying
  (fast at first, then every 10 s, with a quiet log) until it works or you stop it.
- **DXGI survives UAC prompts and the lock screen.** These switch to the secure desktop, which
  DXGI can't capture — after ~25 s the mirror used to stop. It now keeps the last frame on screen,
  re-tries once a second and continues within a second of the desktop returning. (Tested: a UAC
  prompt left open for 39 s — capture resumed in the same second it closed, no reinit.)
- **Waits for a missing monitor or window** instead of stopping: "not connected" / "window not
  found" are re-checked every second, and mirroring continues as soon as that exact monitor or
  window is back (status: "waiting"). Starting while it is missing starts anyway, with a note.
  Real setup errors (nothing selected, same monitor, primary as output) still stop with a message.
- **One instance only, and it says so:** starting the app a second time shows "already running"
  and exits (the "Settings" shortcut still just opens the running copy's settings).

## v1.4.11 — settings follow audio device changes live, typed gain values

- **The settings window follows device changes live.** When Windows switches the default output or
  input device (or one is plugged in or removed), the audio section updates by itself: only the
  side that changed (output or input) is rebuilt, in one repaint, and the scroll position stays.
  "↻ Refresh devices" still forces a full refresh. (The mirror itself already switched to the new
  default device; only the window showed the old name.)
- **Gain values can be typed.** Each gain slider has a number box next to it (dB, arrows step
  0.5 dB); type 0 to reset.

## v1.4.10 — volume follow fixed for software-volume devices

- **Fixed: volume follow applied the volume twice on some devices.** Measured: loopback already
  contains a volume that Windows applies in software (e.g. HDMI audio, many speakers) and lacks
  only a volume or mute done in hardware (e.g. this USB headset). Following is now per device:
  hardware volume → followed in dB, software volume → left as is (already in the signal), mute →
  always followed. The source log says which applies.
- Corrected: microphones are left as Windows delivers them; v1.4.9's note that Windows always
  applies their input level and mute first was not verified (no mic to measure here).

## v1.4.9 — audio mirror follows output volume and mute, self-recovering audio worker

- **The mirror follows the Windows volume and mute of output devices** (new setting, on by
  default). Loopback captures a device's sound before its volume, so turning the headset down or
  muting it used to leave the HDMI audio unchanged. Now the mirror changes the same way, in dB as
  you hear it. Switch it off to always send the full signal, e.g. for recording. Microphones need
  nothing: Windows applies their input level and mute before any app receives them.
- **Audio recovers by itself after an unexpected error.** The audio worker's last-resort handler
  no longer ends the worker (which left audio off until a config change or a mirror restart); it
  releases the pipeline and retries on the normal 1 s → 30 s back-off.
- Soak-tested (v1.4.8 build, 33 min, 60 fps, audio mirrored for 21 min): memory 134–137 MB and
  ~1,100 handles flat, audio queue held at 40 ms (minute averages 38.8–40.2 ms) with a ~10 ppm
  clock offset compensated, no emergency trims, no errors.

## v1.4.8 — stability: no overlapping render threads, crash-safe audio cleanup, soak log

- **Fixed: two render threads could run at once.** If a stop took longer than 4 s, the engine
  forgot the old thread and a new start shared its stop flag, so the old session could resume next
  to the new one (and switch the new session's audio off when it finally ended). The engine now
  keeps a thread that is still shutting down and refuses to start until it has finished, with a
  message in the UI.
- **Fixed: a failing audio device release could crash the app.** Every audio resource (sources,
  output, endpoints) is now released on its own with its error logged, and the audio worker has a
  last-resort handler — an exception there no longer ends the process; the next change starts a
  fresh worker.
- **Fixed: WGC could keep a frame from an already stopped capture session.** A frame arriving
  while the session was being stopped is now dropped instead of being parked for the next one.
- **Soak log:** while mirroring, the log gets one line a minute with capture/output fps, memory
  and handle count, plus one line per audio source with queue latency (average and range),
  playback ratio, restarts after silence and emergency trims — so a long run shows leaks or clock
  drift.
- The settings log pane keeps the last **500 lines** (was: about 200,000 characters).

## v1.4.7 — no more cut-off sound endings in the audio mirror

- **Fixed: the end of a short sound could be cut off** (e.g. the last millisecond of a 31–41 ms
  sound). The compensator decided a source had stopped by looking only at the capture queue, not
  at audio it had already read ahead, and dropped that rest. It now tracks how much real audio it
  holds, plays all of it before re-priming, and no longer pads a short read with silence in the
  middle of a sound.

## v1.4.6 — short sounds no longer stuck in the audio mirror

- **Fixed: short sounds could get stuck in the audio mirror.** After silence a source waited for
  40 ms of audio before playing; a short sound (a click) followed by silence never reached that, so
  it stayed queued and could surface late in front of the next sound. Priming now waits at most
  40 ms once data has arrived, and when a source stops, the rest of its queue is played out.

## v1.4.5 — one capture mode + capture target

- **Simpler capture setup:** one **capture mode** list — Monitor (DXGI), Monitor (WGC), Window (WGC)
  — and one **capture target** list that shows monitors or windows to match the mode (settings and
  dock). Changing the mode clears the capture target. The config keeps `capture_mode` +
  `monitor_api`, so older files and the HTTP API are unaffected.
- The display the mirror is shown on is now called the **output monitor** throughout the UI.
- Section headers keep their live summary on one line (cut with "…") instead of wrapping a long
  window title out of view.

## v1.4.4 — robust audio mirroring, DXGI/WGC monitor capture

- **Monitor capture: choose DXGI or WGC** (`monitor_api`). DXGI Desktop Duplication stays the
  default (lowest overhead); Windows.Graphics.Capture is now available for whole monitors too —
  it works across GPUs and lets Windows draw the cursor. Selectable in Settings and the dock.

- **Audio recovers on its own after a failed restart.** If the output can't be reopened — not ready
  yet after an error, or held in exclusive mode by another app — the audio mirror now retries with
  back-off (1 s → 30 s) instead of waiting for a device notification that may never come.
- **No more skips from clock drift.** Each source's latency is held at ~40 ms by resampling it
  imperceptibly faster or slower (±0.5 % max, Hermite interpolation) instead of discarding chunks of
  audio when the queue grows. After silence a source re-primes cleanly instead of crackling.
- **Correct multichannel downmix.** 3–8 channel sources are folded to stereo by the real speaker
  layout (WAVEFORMATEXTENSIBLE channel mask, Windows default layout as fallback) — e.g. a quad
  source keeps both rear channels instead of mixing one into the centre and dropping the other.
- **A device ticked by name stays visible when it becomes the Windows default** (marked
  "default"), so un-ticking "Default output" no longer leaves a hidden selection still capturing.
- **Choose the audio output device manually** (`audio_output_id`) when the automatic match to the
  target monitor's HDMI/DP audio fails (different name, identical monitors).

## v1.4.3 — selectable scale filter (area, bicubic, Lanczos)

- **Selectable scale filter** (`scale_filter`, live): Bilinear (default, as before), Area (box
  average), Bicubic (Mitchell), Bicubic sharp (Catmull-Rom), Lanczos 2 and Lanczos 3. Quality
  filters run as a separable two-pass resample in linear light, with the kernel widened by the
  shrink factor — 1px lines and small text no longer turn into moiré when a 4K source is shrunk.
  The settings drop-down lists each filter's character, typical use and cost.
- **No filtering at 1:1:** when the source crop and the output have the same size the image is
  drawn texel-to-pixel and no filter runs; the settings (and the dock while mirroring) show the
  filter choice disabled with a note.
- Fixed: the settings window's log button said "Show log" while the log was already shown.

## v1.4.2 — primary monitor can't be the target

- **The primary monitor is no longer a selectable target.** It is still listed (settings and dock)
  but dimmed, marked "primary monitor" and can't be picked; a saved primary target is cleared, and
  starting with it is refused (the windowed test mode excepted). The mirror can therefore never
  cover your own desktop and taskbar — stopping it from the tray is always one right-click away.
- Clearer HTTP agent log: a refused network bind (no URL reservation) is now logged as an expected
  fallback to local-only listening instead of looking like a failure.

## v1.4.1 — multi-source audio mirroring with per-device gain

- **Audio mirroring: pick any number of sources.** A checkbox list in Settings (audio section) offers
  every output device — captured as it sounds, e.g. speakers *plus* the headset Discord plays to —
  and every input device (e.g. a microphone). "Default output/input" entries follow the Windows
  default; the default device isn't listed twice, and a device picked both ways is captured once.
  The target monitor's own HDMI/DP output is never offered (it would loop the mix back).
- **Gain per source plus a common gain per group** (outputs / inputs), in dB and additive
  (common +10 and device −5 → +5 dB). Gain changes apply instantly, without restarting the stream.
- All sources are mixed at 48 kHz stereo (mono/multichannel and other sample rates are converted);
  each source has its own drift control. The status line lists what is mixed and what was skipped
  (not connected, same device as the target, no default device).
- New config keys (live): `audio_sources` (list of `{kind, id, name, enabled, gain_db}`),
  `audio_render_gain_db`, `audio_capture_gain_db`. Older configs keep mirroring the default output.

## v1.4.0 — frame limit, audio mirroring, FPS readout, language packs

- **New: Target FPS** (`target_fps`, live). With VSync off, the mirror can be capped at a fixed
  frame rate. Frames are paced with a high-resolution timer and every slot shows the **newest**
  source frame, so a lower rate never adds queued, stale frames. With VSync on the setting is
  ignored (and greyed out) — the display refresh paces the output.
- **Less work when nothing changes.** Rendering and presenting now only happen for a new source
  frame (or a settings change). A static desktop, or a source slower than the output, no longer
  burns GPU time. In window-capture mode the loop now waits for the next frame instead of spinning,
  and always skips ahead to the newest queued frame.
- **New: audio mirroring** (`audio_mirror`, live). Copies the Windows default playback device to the
  target monitor's HDMI/DP audio, so the capture card receives the sound too. It follows device
  changes live: while the default output already is the target monitor it pauses, switching the
  default to e.g. a headset starts it automatically. If the target's audio device can't be found
  (no EDID name, no match, several identical monitors) it stays off and shows why. Built on NAudio.
- **New: capture / output FPS readout** (`fps_overlay`, `fps_overlay_position`, live) — a small
  label in the corner of your choice, for verifying the pipeline. It is part of the mirrored
  picture, so the capture card sees it too. The rates are also reported by `GET /status`.
- **New: language packs.** The settings window, tray menu, OBS dock and user-facing errors are now
  available in English, Korean and German, following the Windows display language or the choice in
  Settings. A `lang\<code>.json` next to the exe overrides a translation or adds a language.
- **Fixed: mirroring never started on Windows with a double-byte system locale** (e.g. Korean).
  The shader source contained non-ASCII comment characters; under such a code page the source
  reached the compiler truncated, the cursor shader's entry point was missing and the render
  session failed. The source is now pure ASCII.
- **Fixed: a failed render start leaked the screen capture**, so every automatic retry failed with
  "invalid parameter" until the app was restarted. A session now always releases everything it
  created, even when a setup step throws.
- Code comments and `mirror.log` messages are now English.
- The dock shows the last error, the FPS rates and the audio-mirror status.
- **Reworked settings window.** Every main feature (mirroring, tone mapping, picture adjustments,
  frame rate, FPS readout, audio mirroring, crop, connection, general) is a collapsible section:
  the header carries the on/off switch and a live summary (e.g. running state and source → target,
  `60 fps limit · capture 59.9 / output 60.0 fps`, the audio-mirror status), expanding it shows the
  sub-settings. Expanded sections are remembered.
- **Source and target can no longer be the same monitor.** The target list (settings and dock)
  leaves out the current source monitor; changing the source to the current target clears the
  target (nothing selected), and starting is refused with a clear message in that state.
- **Tidier install folder:** `config\` (settings), `lang\` (language-pack overrides), `log\` and —
  in normal builds — `bin\` for the dependency DLLs; only the exe stays at the top. Existing config
  and `mirror.log` are moved over automatically on first start.
- **One log file per run** (`log\mirror_<date>_<time>.log`); logs older than 30 days are deleted at
  start. The settings log pane and "Open log" show the current run only.
- **Log pane** at the bottom of the settings window: the tail of `mirror.log` plus every new line
  live; resizable, hidden with one click (remembered).
- **Fixed: the settings window at 125/150 % display scaling** — controls no longer overlap and
  sliders render correctly.

## v1.3.2 — fix a badly darkened picture when the source monitor is in SDR mode

- **Fixed: with HDR turned off on the source monitor, the mirrored picture came out far too dark.**
  Highlights survived, but midtones and shadows were crushed — a mid-grey left the mirror at roughly
  40 % of its real brightness, a dark grey at 20 %. Games looked murky and washed out. Turning the
  tone mapper off did **not** help, because the darkening happened before tone mapping.
- **Root cause: one gamma too many.** Screen capture (both the monitor and the window source) hands
  us 16-bit float frames, and those are **always in a linear colour space — including when the
  display is in SDR mode**. The renderer assumed an SDR display meant gamma-encoded pixels and
  decoded them a second time, so every value was effectively raised to the power of 2.2.
- **Fixed: the tone mapper no longer squashes an SDR source.** It used to roll highlights against
  the configured HDR source peak even when the source had no HDR range at all, dimming pure white
  to about 75 %. It now derives its range from the source that is actually connected: HDR source →
  roll off as before, SDR source → pass the picture straight through, untouched.
- **Fixed: switching HDR on or off is now noticed while the mirror is running.** Previously the
  source's HDR state was only read when the mirror started, so toggling HDR mid-session kept the
  wrong colour maths running until you restarted the mirror. It is now re-checked continuously and
  after every capture recovery, and the change is written to the log.
- **Nothing changes for an HDR source** — that path is mathematically identical to v1.3.1. The one
  exception: if you set the source peak at or below the white point, tone mapping is now skipped
  instead of applied at a neutral setting (with the `hable` and `aces` operators that was never
  truly neutral).
- **Note:** `source peak` and `white point` only affect an HDR source. With an SDR source the
  picture is passed through 1:1 and those two sliders do nothing — use exposure, contrast, gamma
  and saturation to grade it.

## v1.3.1 — fix a rare mid-stream crash (window message pump)

- **Fixed a rare hard crash that could kill the mirror mid-stream.** On long sessions the app could
  vanish instantly (no error dialog, you had to relaunch it), most often after the render pipeline
  had been rebuilt one or more times. This is *not* the capture-loss crash fixed in v1.2.2 — it was
  a separate issue in the window message loop.
- **Root cause.** The window procedure is handed to Windows as a native callback, but the delegate
  backing it could be garbage-collected while Windows was still dispatching messages to the window
  (window and delegate referenced only each other — a cycle the GC is free to collect). When that
  happened, the next window message invoked a freed callback and .NET terminated the process
  immediately (`FailFast: callback on a garbage collected delegate`). A second, related flaw meant
  every window after the first silently reused the first window's callback, so once that first
  window's session ended the pointer could dangle for all later windows.
- **The fix.** The window class is now registered **once per process** against a **static** window
  procedure that lives for the entire process and dispatches to the right window by its handle. The
  collectible cycle is gone, so this class of crash can no longer occur — no matter how many times
  the pipeline rebuilds during a stream.

## v1.3.0 — mirror a single window or fullscreen app (not just the whole screen)

- **New "window" source mode.** Instead of mirroring a whole monitor, you can now mirror **one
  specific window or fullscreen app** — e.g. just your game, not your whole desktop. Pick it from
  a dropdown in **Settings**, the **OBS dock**, or the RitschyBot Cockpit. The old whole-monitor
  mode is unchanged and stays the default.
- **Safer for streaming.** A window source shows *only that window* — when you alt-tab away, the
  mirror keeps showing the app, **never your desktop**. No more accidentally flashing something
  private into the capture card.
- **Built on Windows.Graphics.Capture** — the same modern API OBS uses for its "Windows 10 (1903+)"
  window capture, *not* injection. So: no anti-cheat risk, no admin/elevation mismatch, and it's
  robust across two GPUs (the classic reason OBS' Game Capture goes black on a multi-GPU rig).
- The chosen window is remembered by **app + title**, so it's re-selected automatically next time
  even though Windows hands out fresh window handles on each launch — the same replug-proof
  approach as the monitor selection. If the app isn't running, the mirror refuses to start with a
  clear message instead of mirroring the wrong thing.

## v1.2.2 — self-healing capture (no more crash when the capture drops)

- **The mirror now recovers on its own when the screen capture is lost.** Certain events on the
  source PC (a resolution/refresh change, a game switching to fullscreen, a UAC prompt, or a
  cross-GPU hiccup) make Windows drop the capture session. This is rare — it doesn't happen every
  frame, only when one of those events fires. Previously, when it did, a failed re-grab left the
  app holding a dead capture; the next call into the graphics driver touched freed memory and the
  **whole app crashed and disappeared** — you had to relaunch it mid-stream. Now it retries the
  capture a few times and, if that's not enough, **fully rebuilds the whole render pipeline**
  automatically (with a short, growing back-off), so the picture comes back on its own.
- **No more crash, no silent hang.** If recovery genuinely can't succeed after several attempts,
  the mirror stops cleanly with a clear status instead of crashing — and it never blocks your PC
  from sleeping in that state.

## v1.2.1 — smooth cursor in `fullscreen_block` (mouse-lock)

- **Fixed the laggy/stuttery cursor in `fullscreen_block` mode.** The mouse-lock used a global
  low-level mouse hook that ran on the render thread — which spends most of its time waiting on
  vsync, so every mouse movement system-wide was throttled to the render cadence and felt heavy
  (even though the display stayed at full refresh / G-Sync). The hook now runs on its own
  dedicated, fast-pumping thread, so the cursor stays smooth at full polling rate while the
  display is still mirrored. The mouse is still locked out of the target display — just fluffy
  again.
- No effect on controller/gamepad input (it never went through the hook). Mouse-driven games
  (e.g. shooters) no longer inherit the added input latency while the mirror is running.

## v1.2.0 — keep the PC awake while mirroring

- **Prevent sleep / monitor power-off while mirroring.** As long as the mirror is running, the
  PC no longer dims, blanks, or goes to sleep — just like a video player does during playback.
  No more capture-card display dropping out because Windows turned the screen off mid-stream.
- **On by default**, and you can turn it off: a *„Schlafmodus / Monitor-Abschaltung verhindern"*
  checkbox in **Settings** (Bild / Tonemap) and in the **OBS browser dock**. It's a **live**
  setting — toggling it takes effect immediately, without restarting the mirror.
- The keep-awake hold is released automatically the moment mirroring stops (or if the render
  thread ever exits), so it can never leave your PC stuck awake.

## v1.1.0 — OBS browser dock (remote control for everyone)

- **Built-in OBS / browser control dock.** The app's HTTP agent now serves a small control page at
  `http://<host>:8788/` (and `/dock`). Add it to OBS as a **Custom Browser Dock** (or open it in any
  browser) to control the mirror remotely — no extra app needed:
  - status light (green = mirroring, red = stopped) + one-click **Start / Stop**
  - source / target monitor pickers (identity-stable, like the app)
  - layout & output mode
  - **live image sliders** (saturation / contrast / gamma / exposure / paper-white)
  - **live crop sliders** (on-the-fly crop region)
- Same-origin (served by the agent itself) → no CORS, no extra hosting. Works single-PC
  (`localhost:8788`) or two-PC (`<mirror-pc-ip>:8788`).

## v1.0.2 — status icon, single-instance, About, update check

- **Tray icon now shows status** — green when the mirror is running, red when it's stopped.
- **Single-instance guard** — launching a second copy (or a stray double autostart) no longer
  creates a second tray icon; it just brings the running instance's settings to the front.
- **Version in the settings title bar** (`… — Einstellungen vX.Y.Z`).
- **About section** at the bottom of Settings — version, copyright, MIT license, GitHub link.
- **Update check** — the app checks GitHub for a newer release on start (best-effort) and shows a
  clickable tray notification; you can also check on demand from the About section. (Check only —
  it never downloads or installs anything by itself.)

## v1.0.1 — stable monitor selection (replug-proof)

Monitor selection now survives unplugging/replugging and display-reordering — important for
setups where the capture card isn't always connected.

- Source/target monitors are now remembered by a **stable identity** (EDID device path + name),
  not just the enumeration index. When your monitors come back, the right ones are picked again
  automatically, even if Windows reordered them.
- If a configured monitor is **not connected**, the app now **refuses to start with a clear
  message** (tray balloon / dialog) instead of silently mirroring the wrong screen.
- Settings window flags a saved source/target that's currently disconnected (the selection is
  kept, not lost).
- Fully backward compatible with v1.0.0 configs (falls back to the index until you re-pick once).

## v1.0.0 — first public release

First standalone release of RitschyMirror — a Windows tray app that mirrors any monitor to a
capture card / target display with proper HDR → SDR tonemapping. Built for dual-PC streaming
setups.

**Highlights**
- Source/target monitor pickers with friendly EDID names; multi-GPU & HDR aware.
- GPU HDR → SDR tonemapping (`bt2390` / `reinhard` / `hable` / `aces`) with live image controls.
- Layout modes: `fit`, `stretch`, `top_strip`, `crop_region`.
- Output modes: `windowed`, `borderless`, `fullscreen_block` (mouse-lock), `exclusive`
  (with automatic fallback when source & target are on different GPUs).
- Lives in the system tray; optional auto-start at login; live config reload.
- Optional HTTP control agent (start/stop, config, displays) for remote/dashboard control.
- **Per-user installer** (`RitschyMirror-Setup-1.0.0.exe`) — no admin/UAC, clean uninstall.
