# RitschyMirror — Architecture & Reference

Internal/technical documentation. For the user-facing overview see the main
[README](../README.md).

## Pipeline

**DXGI Desktop Duplication** (FP16, always linear scRGB) → **HLSL tonemap shader**
(`bt2390` / `reinhard` / `hable` / `aces`) → layout/crop composite → **flip-model
swap chain** (8- or 10-bit). The render device is created on the **source adapter**
(Desktop Duplication is adapter-bound); when the target display hangs off a
different GPU, the DWM performs the cross-adapter transfer in borderless mode.

**Scaling** (`scale_filter`): `bilinear` is one hardware-filtered sample per output pixel
(`PSMain`). The quality filters (`area`, `bicubic` = Mitchell, `catmull_rom`, `lanczos2`,
`lanczos3`) run two passes in linear light — `PSResampleH` filters along x from the source into an
FP16 intermediate (output width × crop rows), `PSResampleV` filters along y and grades
(`Grade()` is shared with `PSMain`). Kernels are widened by the shrink factor (area: exact texel
overlap), negative lobes are clamped before grading, taps are capped at 128 per pass. The viewport
is snapped to whole pixels; if the crop already has the viewport's size (`Renderer.IsUnscaled`,
also used by the settings window) the frame is drawn 1:1 and no filter runs.

Per-Monitor-V2 DPI awareness is set programmatically (`SetProcessDpiAwarenessContext`),
so capture/display geometry is in real pixels and not distorted by Windows scaling.

## Colour pipeline — HDR vs SDR source

Two properties of the source are easy to conflate, and conflating them is exactly what made
an SDR source come out far too dark before v1.3.2:

1. **Encoding of the captured buffer.** Both capture sources request
   `R16G16B16A16_FLOAT`, and that buffer is **always linear scRGB — also when the display is
   in SDR mode**. This is measured, not assumed: painting sRGB greys 255/192/128/64 on an SDR
   display and reading the duplicated FP16 buffer back yields `1.0000 / 0.5273 / 0.2158 /
   0.0513`, i.e. exactly `srgb_to_linear(v)`. Never sRGB-decode this buffer — that applies a
   second gamma and the output ends up at the *linear* value (mid-grey 128 → 55).
2. **Value range.** Whether anything reaches above the white point. That depends on the
   display's HDR state, which is what `ICaptureSource.InputIsHdr` reports.

The shader therefore takes three explicit inputs instead of one overloaded `InputIsHdr` flag,
computed in `Renderer.ResolveSourceLight`:

| cbuffer field | HDR source | SDR source |
|---|---|---|
| `SrcIsLinear` | `1` — no sRGB decode (both sources deliver FP16) | `1` |
| `SrcScale` — buffer value → working light, `1.0` = white point | `80 / target_paperwhite` (scRGB: `1.0` = 80 nits) | `1.0` (white already *is* the white point) |
| `SrcPeak` — source peak in white-point units | `max(source_peak_nits / target_paperwhite, 1)` | `1.0` |

Tone mapping runs only when `SrcPeak > 1`, so an SDR source is passed through 1:1 instead of
being squashed a second time. Consequence: **`source_peak_nits` and `target_paperwhite` only
affect an HDR source**; with an SDR source only exposure/saturation/contrast/gamma do anything.

`InputIsHdr` is re-read at runtime via `ICaptureSource.RefreshSourceState()` — periodically from
the render loop and after every `Recover()`, since a display mode change (HDR on/off is one) is
the most common cause of capture loss. Toggling HDR therefore no longer needs a render restart.
`WgcCapture` re-reads the monitor's colour space for monitor capture; for window capture it is a
no-op — the HDR state comes from the monitor the window sat on at start, and the window selection
is a structural parameter anyway.

## Capture sources

The thing being mirrored sits behind a small seam, `ICaptureSource` (a FP16 SRV + size +
HDR flag + cursor info + `TryAcquire`/`Recover`/`ApplyLiveConfig`/`RefreshSourceState`).
Everything downstream —
tonemap, layout, crop, present — is identical regardless of source, so adding a source type
doesn't touch the renderer or the engine loop. `capture_mode` (structural) selects which:

- **`monitor`** (default) — a whole monitor, through the API chosen by `monitor_api` (structural):
  - `dxgi` (default) — `DuplicationCapture`: **DXGI Desktop Duplication**, as above. The render
    device is created on the *source* adapter (Duplication is adapter-bound). Lowest overhead; the
    cursor is composited by the renderer.
  - `wgc` — `WgcCapture.ForMonitor`: **Windows.Graphics.Capture** of the monitor (HMONITOR from the
    DXGI output). Device on the *target* adapter, the DWM bridges GPUs; cursor composited by WGC;
    HDR re-read live via a probe of the output's colour space. Same frame handling as below.
- **`window`** — `WgcCapture.ForWindow`: a single window / fullscreen app via **Windows.Graphics.Capture
  (WGC)** — the same OS API behind OBS' "Windows 10 (1903+)" window capture, *not* injection.
  Frames arrive as `R16G16B16A16_Float` D3D11 textures (free-threaded frame pool, polled with
  `TryGetNextFrame` from the render thread) → copied into the same shader-readable SRV the
  Duplication path produces. The render device is created on the *target* adapter; WGC delivers
  the window content across adapters via the DWM, which is why it's robust on multi-GPU rigs where
  injection-based capture goes black. The cursor (if enabled) is composited by WGC into the frame
  (`IsCursorCaptureEnabled`), so the renderer's separate cursor pass is unused in this mode; the
  yellow capture border is disabled where the API exists (Win11 22000+). The window is selected by
  **stable identity** (process exe + title, see `WindowEnum`), so it survives the non-stable HWNDs
  across app launches — and only that window is ever shown, so alt-tabbing never reveals the
  desktop. HDR is inferred from the monitor the window currently sits on.

## Frame pacing — VSync, `target_fps`, skipped frames

The render loop (`MirrorEngine.RunSession`) has two pacing modes:

| Mode | When | Loop |
|---|---|---|
| **Source-paced** | `vsync = true`, or `target_fps = 0` | `TryAcquire(timeout 16 ms)` blocks until the source delivers a frame → render → `Present(vsync)` |
| **Fixed rate** | `vsync = false` and `target_fps > 0` | `FramePacer` sleeps until the next slot → `TryAcquire(timeout 0)` takes the newest frame → render → `Present(0)` |

- **Nothing new → nothing drawn.** Render + Present run only for a new source frame or when
  something else changed (live config, HDR state, FPS readout). The flip-model swap chain keeps
  showing the last frame, so a static desktop costs almost no GPU time.
- **Newest frame first.** Desktop Duplication always returns the accumulated latest image. WGC
  queues frames oldest-first, so `WgcCapture.TakeNewestFrame` drains the pool and keeps only the
  last one; it waits on the pool's `FrameArrived` event instead of spinning.
- **`FramePacer`** uses a high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`,
  Win10 1803+; plain waitable timer before that), advances deadlines on a fixed grid and re-anchors
  instead of bursting when it falls behind. Waits are sliced (≤ 50 ms) so `Stop()` stays responsive.
- Config polling (250 ms) and HDR re-detection (4 s) are wall-clock based, independent of the
  frame rate. The supervisor's "healthy session" test is time-based (5 s) for the same reason.
- `target_fps` is clamped to 1–1000 and ignored while VSync is on (`MirrorConfig.EffectiveTargetFps`).

### FPS readout

`FpsCounter` counts acquired frames (capture FPS) and presents (output FPS) and publishes a rate
once per second. With `fps_overlay` on, `FpsOverlay` rasterises the label with GDI+ into a small
immutable BGRA texture (only when the text changes) and `Renderer.DrawOverlay` blends it into the
chosen corner with the cursor's straight-alpha shader — independent of the 8/10-bit swap-chain
format. The rates are also in `GET /status` (`capture_fps`, `output_fps`).

## Audio mirroring

`AudioMirror` (NAudio 3, WASAPI) mixes the selected **sources** and plays the mix on the
**target monitor's HDMI/DP audio endpoint**:

- **Sources** (`audio_sources`) — output devices are captured in loopback (what they play), input
  devices directly. `id: "default"` follows the Windows default (role Multimedia) of that kind.
  Each source becomes an `AudioSourceChannel`: `WasapiRecorder` → `BufferedWaveProvider` → float →
  stereo (`ToStereo` / `DownmixToStereoSampleProvider` by channel mask) → `DriftCompensator` →
  48 kHz (`WdlResamplingSampleProvider`) → `VolumeSampleProvider` → `MixingSampleProvider` →
  `WasapiPlayer`.
- **Drift** — `DriftCompensator` holds the queue at ~40 ms with a smoothed proportional rate
  (±0.5 % max, 4-point Hermite). When a source stops (loopback sends nothing during silence) the
  rest of its queue is played out, then it re-primes to the target — waiting at most 40 ms after
  data arrives, so a short click that never fills the target still plays. Only a runaway queue (≥ 300 ms, playback stalled) is cut back on the capture thread.
- **Soak figures** — `DriftCompensator.TakeStats()` reports the smoothed queue latency, its range
  and the ratio range since the previous call (the tail of a sound is left out), plus restarts after
  silence; `AudioSourceChannel` adds its emergency trims. The engine logs them once a minute.
- **Cleanup** — `StopPipeline` releases every source, the player and the endpoint independently
  (errors logged, never thrown: it runs inside the worker's catch/finally). Any other exception in
  a worker iteration is handled like a failed start — pipeline dropped, retry on the back-off
  (at least 1 s, so a failing wait can't spin) — and the worker stays alive until `Dispose()`.
- **Volume follow** (`audio_follow_volume`, default on) — loopback delivers an output device's mix
  before its endpoint volume and mute (measured: the mirror kept the same level at 14 % and muted).
  An output-device channel therefore multiplies its gain by the device's level in dB relative to
  its maximum (never a boost) and by 0 when muted or at 0 %, updated from
  `AudioEndpointVolume.OnVolumeNotification`. Input devices are left alone — Windows applies a
  mic's input level and mute before capture clients get the signal (not measured here: no mic).
- **Output** — `audio_output_id` if set, otherwise the target monitor's endpoint by EDID name.
- **Retry** — a (re)start that throws (device not ready, exclusive use by another app) is retried
  by the worker on a 1 s → 30 s back-off; a success resets it. Gain = group dB (`audio_render_gain_db` / `audio_capture_gain_db`) + source dB,
  applied in place; only a change of the *set* of sources rebuilds the pipeline.
- **Skipping** — an output source that is the target endpoint is skipped (feedback loop), a device
  selected twice ("default" and by id) is captured once (the "default" entry and its gain win),
  unplugged devices are reported in the status line.

- **Target matching** — Windows names HDMI/DP audio endpoints after the monitor's EDID, so the
  endpoint whose device description (`PKEY_Device_DeviceDesc`) equals the target monitor's EDID name
  is the target (`AudioEndpointMatcher`). No name, no match or several matches → **unavailable**,
  with the reason shown in Settings, the dock and `GET /status → audio`.
- **Default == target** → if that leaves no source, **paused** (the sound already plays there;
  mirroring it would loop it back onto itself); other sources keep being mixed.
- **Live device changes** — an `MMDeviceNotificationClient` signals a dedicated worker thread on
  default-device change / add / remove / state change; the worker debounces (300 ms) and re-runs the
  decision, so switching the default from the HDMI monitor to a headset starts the mirror and back
  pauses it. Callbacks never touch audio objects (Core Audio holds a lock while dispatching).
- **Pipeline** — `WasapiRecorder` (loopback, shared) → `BufferedWaveProvider` → `WasapiPlayer`
  (shared mode with `AUTOCONVERTPCM`, so Windows converts sample rate / channel layout). The two
  devices run on independent clocks: when the queue exceeds 120 ms it is trimmed back to 40 ms,
  underruns are padded with silence.
- Lifetime: enabled at render start and on live config reload (`audio_mirror`), kept across a
  capture reinit, switched off when the render thread ends.

## Language packs

All user-visible text lives in flat JSON packs (`lang/en.json`, `ko.json`, `de.json`), embedded
into the exe as `lang.<code>.json`. `L.Init` merges English first and the selected language on top,
then lets a `lang\<code>.json` next to the exe override single keys or add a whole new language —
missing keys always fall back to English. `app_settings.json → language` is `auto` (Windows UI
language) or a code.

- Errors meant for the user (`LastError`, preflight) are `LocalizedText` (key + args): shown in the
  UI language, logged in English via `L.En` — the log stays English regardless of the UI.
- The OBS dock fetches the active pack from `GET /lang` and fills `data-i18n` / `data-i18n-title`
  elements; the built-in English text is the fallback when the agent is unreachable.
- Switching the language in Settings re-initialises the packs, relabels the tray and rebuilds the
  settings window.

## Resilience — capture-loss recovery

DXGI Desktop Duplication can lose access at runtime (`DXGI_ERROR_ACCESS_LOST`): a source-side
mode/refresh change, a game going fullscreen-exclusive, the UAC secure desktop, or a cross-GPU
device hiccup. Recovery is two-tiered so a transient loss never freezes the picture:

1. **Fast path (`MirrorEngine.TryRecoverCapture`)** — re-create just the duplication a few times
   with a short delay (`DuplicationCapture.Recreate`). Handles the common transient case without
   tearing the window down. On failure, `Recreate` leaves the capture in a clean *dead* state
   (`_dup == null`) — so `TryAcquire` returns `false` instead of touching a disposed COM object.
   The old code's silent `catch {}` left `_dup` dangling; subsequent calls first threw managed
   `NullReferenceException`s and then hit a **native access violation** in the D3D layer (freed
   memory) — a corrupted-state exception that managed `try/catch` can't intercept, so the whole
   process fast-failed and disappeared. That was the rare mid-stream crash this release fixes.
2. **Full reinit (supervisor loop in `RenderThreadMain`)** — if the fast path can't recover (e.g.
   the device itself was lost), the session is abandoned and the **whole render chain is rebuilt**
   (DXGI factory → adapter/output enumeration → device → window → renderer → capture) on a fresh
   `RunSession()`. Reinit uses **exponential back-off** (250 ms → 5 s, interruptible by `Stop()`)
   and a **retry cap** (`MaxConsecutiveReinit`); a session that ran healthy for a while
   (`HealthyFrameThreshold` frames) resets the budget, so only tight failure chains count toward
   the cap. If recovery genuinely fails, the engine stops cleanly with `LastError` set rather than
   hanging. Config-/selection errors (no displays, configured monitor unplugged) are terminal
   (`SessionResult.Fatal`) and are *not* retried — they surface their exact message instead.
3. **Window-procedure lifetime (`Win32Window`)** — the window class is registered **once per
   process** against a **static** window procedure held in a `static` field (rooted for the whole
   process), dispatching to the live instance by `HWND`. This closes a separate rare crash: an
   *instance* delegate handed to Win32 as a native callback formed a self-referential window↔delegate
   cycle the GC could collect mid-`DispatchMessage`, so Windows would invoke a freed callback and the
   CLR `FailFast`ed (`callback on a garbage collected delegate`) — a hard, log-less process death,
   made worse by every window after the first silently reusing the first window's delegate (a failed
   re-`RegisterClassEx`). Unrelated to the capture-loss reinit above.
4. **One render thread at a time** — `Stop()` waits up to 4 s for the thread. If it is still
   tearing down after that, the engine keeps the reference (`IsRunning` stays true) and `Start()`
   refuses with `error.still_stopping` instead of starting a second thread on the same stop flag,
   output window and audio mirror.
5. **WGC shutdown** — `WgcCapture` only accepts a frame into its newest-frame slot when the
   callback's pool is the active one (`_activePool`, under `_frameLock`); `StopSession` clears it
   before releasing the slot, so a callback still running during teardown drops its frame.

## Source files

| File | Purpose |
|---|---|
| `Program.cs` | Entry point → tray app |
| `TrayContext.cs` | NotifyIcon + menu, owns the engine + HTTP agent |
| `SettingsForm.cs` | Local settings GUI (all parameters), one collapsible section per feature |
| `CollapsibleSection.cs` | Section control: header (on/off toggle + live summary) + collapsible body |
| `LogView.cs` | Live log pane (current run's log file) at the bottom of the settings window |
| `AppPaths.cs` | Folder layout (bin/config/lang/log), legacy migration, log naming + retention |
| `MirrorEngine.cs` | Render loop (start/stop), display enumeration, live config reload |
| `Renderer.cs` | Swap chain + layout/crop geometry + shader pipeline (consumes `ICaptureSource`) |
| `ICaptureSource.cs` | Capture-source seam (FP16 SRV + size/HDR/cursor + acquire/recover/refresh) |
| `Capture.cs` | `DuplicationCapture` — DXGI Desktop Duplication (incl. optional cursor compositing) |
| `WgcCapture.cs` | `WgcCapture` — window / fullscreen app or whole monitor via Windows.Graphics.Capture |
| `WindowEnum.cs` | Enumerate capturable windows + resolve a saved window by stable identity (exe+title) |
| `Shaders.cs` | HLSL (tonemap + crop UV + cursor blend) |
| `FramePacer.cs` | High-resolution waitable-timer pacing for `target_fps` |
| `FpsCounter.cs` | Events-per-second meter (capture / output FPS) |
| `FpsOverlay.cs` | FPS readout texture (GDI+ → immutable BGRA texture) |
| `AudioMirror.cs` | Audio sources → mixer → target monitor HDMI/DP audio (NAudio WASAPI) |
| `AudioSourceChannel.cs` | One captured device: queue, drift trim, stereo/48 kHz conversion, gain |
| `AudioSourceConfig.cs` | `audio_sources` entry (kind, id, name, enabled, gain_db) |
| `AudioDevices.cs` | Active input/output endpoints for the settings list |
| `DownmixToStereoSampleProvider.cs` | Multichannel → stereo fold-down by speaker mask |
| `DriftCompensator.cs` | Per-source latency control by tiny resampling-rate changes |
| `SettingsForm.Source.cs` | Capture mode + capture target rows (list follows the mode) |
| `SettingsForm.Scale.cs` | Scale-filter row: drop-down with per-filter notes, disabled at 1:1 |
| `SettingsForm.Audio.cs` | Audio section of the settings window (device checkboxes + gains) |
| `Localization.cs` | Language packs (`L`), `LocalizedText` for user-facing errors |
| `Win32Window.cs` | Window + mouse-lock hook (`WH_MOUSE_LL`, on its own dedicated thread so the cursor stays smooth — the hook is never serviced on the vsync-bound render thread) |
| `MonitorNames.cs` | Friendly EDID monitor names (CCD `QueryDisplayConfig`) |
| `ControlAgent.cs` | HTTP control agent (`HttpListener`) |
| `MirrorConfig.cs` | Render config (`mirror_config.json`) |
| `AppSettings.cs` | App config (`app_settings.json`: agent port/bind, autostart) |
| `AppIcon.cs` | Loads the embedded app icon for tray + window |

## Configuration — `mirror_config.json`

Read live (mtime polling — `FileSystemWatcher` misses atomic writes). The file is the
**single source of truth**: the local GUI, the HTTP agent and (in the RitschyBot setup) the
remote controller all write the same file; the render loop reads it live.

**Live keys** (apply immediately): `tonemap_enabled`, `operator`, `source_peak_nits`,
`target_paperwhite`, `exposure`, `saturation`, `contrast`, `gamma`, `content_offset_y`,
`vsync`, `layout_mode`, `crop_x`, `crop_y`, `crop_w`, `crop_h`, `show_cursor`, `keep_awake`,
`target_fps`, `fps_overlay`, `fps_overlay_position`, `audio_mirror`, `audio_sources`,
`audio_render_gain_db`, `audio_capture_gain_db`, `audio_output_id`, `audio_output_name`,
`audio_follow_volume`, `scale_filter`.

`tonemap_enabled`, `operator`, `source_peak_nits` and `target_paperwhite` are only in effect for
an **HDR** source — see [Colour pipeline](#colour-pipeline--hdr-vs-sdr-source).

`keep_awake` (default `true`) holds off display power-off + system sleep while the mirror is
running, via `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`
on the render thread. It's set once the render loop actually starts (after source/target
resolve), toggled live on config reload, and cleared on stop — and because the hold is bound to
the render thread, Windows releases it automatically if that thread ever exits.

**Structural keys** (need a render restart): `output_bit_depth`, `capture_mode`, `monitor_api`, `window_exe`,
`window_title`, `source_display`, `target_display`, `output_mode`, `windowed`, `window_width`,
`window_height`, `output_width`, `output_height`.

`capture_mode` = `monitor` (default) or `window`; in window mode the source is the window matched
by `window_exe` + `window_title` (stable identity) instead of `source_display`. The settings window
and the dock present `capture_mode` + `monitor_api` as one "capture mode" choice (Monitor DXGI /
Monitor WGC / Window WGC) with one "capture target" list; changing it clears the capture target
(`source_*` / `window_*`).

Source and target must differ (monitor mode), and the **primary monitor** (desktop origin 0,0)
can't be the target outside the windowed test mode — the GUI and dock list it dimmed and
unselectable, preflight and session start refuse it. The mirror thus never covers the taskbar.

`crop_*` are in 0..1 of the source and only used when `layout_mode = crop_region`.
`output_mode` empty falls back to the legacy `windowed` flag for backward compatibility.

## HTTP control agent

Listens on `http://<bind>:<port>/` — default port **8788**. `bind` = `"+"` makes it reachable
from the network (requires a URL-ACL / admin; otherwise it falls back to `localhost`).
Configured via `app_settings.json` (`agent_bind`, `agent_port`, `agent_enabled`); the same file
holds `autostart_mirror`, the UI `language` and the settings-window state (`ui_expanded` — the
expanded sections, `ui_show_log` — log pane visible).

| Route | Description |
|---|---|
| `GET /health` | Ping → `{ok, running}` |
| `GET /status` | `{running, state, lang, config, error, capture_fps, output_fps, audio: {state, message}}` |
| `GET /lang` | Active language pack `{lang, strings}` (used by the dock) |
| `GET /config` · `POST /config` | Read config / merge a whitelisted patch |
| `POST /start` · `/stop` · `/restart` | Control rendering |
| `GET /displays` | Enumerated displays (index, name, friendly, key, resolution, hdr, adapter, primary) |
| `GET /windows` | Capturable windows for the window-source picker (title, exe, pid) |

### RitschyBot Cockpit integration (optional)

The [RitschyBot](https://github.com/RitschyRigz) Cockpit can drive this agent via
`cockpit/services/mirror_control.py`: connector `gaming_pc` → field **`agent_port`** set ⇒
control over HTTP; empty ⇒ WinRM fallback. Response shapes match the WinRM contract so the
two transports are interchangeable.

## Folder layout & log

`AppPaths` owns the layout next to the exe: `bin\` (dependency assemblies), `config\`
(`mirror_config.json`, `app_settings.json`), `lang\` (pack overrides), `log\`.

- **Dependency DLLs in `bin\`** — normal builds set `GenerateDependencyFile=false` and an
  `AfterTargets="Build"` step moves every DLL except `RitschyMirror.dll` into `bin\`;
  `Program.ResolveFromBin` (an `AssemblyLoadContext.Default.Resolving` handler registered first
  thing in `Main`) loads them from there. Without a deps.json the host would otherwise insist on
  finding them next to the exe. The single-file publish bundles everything and is unaffected.
- **One log per run** — `log\mirror_yyyyMMdd_HHmmss.log`, created at start; files older than
  30 days (`LogRetentionDays`, by last write time) are deleted at start. The settings log pane and
  the tray's "Open log" show only the current run's file. Every render start lists all outputs
  (index, name, resolution, color space, adapter) — the basis for display selection and HDR
  detection. The log pane keeps the last 500 lines.
- **Soak log** — once a minute while mirroring: `Soak video 60 s: capture … fps, output … fps;
  private … MB, GC heap … MB, … handles.` and per audio source `Soak audio 60 s "<device>": queue
  40.1 ms (34.7..42.7), ratio 1.000009 (0.999749..1.000034), N restart(s) after silence, N emergency
  trim(s).` ("silent" when nothing played). A stable ratio = locked clocks (1.000137 ≈ 137 ppm);
  a latency range that walks away from 40 ms, emergency trims, or rising memory/handles point to
  drift or a leak.
- **Migration** — config files and `mirror.log` left next to the exe by older versions are moved
  into `config\` / `log\mirror_legacy.log` on first start (a legacy config wins over the default
  that a build drops into `config\`).

## Packaging

- `publish.ps1` — `dotnet publish` self-contained single-file (win-x64).
- `installer/RitschyMirror.iss` + `installer/build_installer.ps1` — Inno Setup, per-user
  install to `%LOCALAPPDATA%\Programs\RitschyMirror`, no admin required.
- `install.ps1` — minimal alternative: copy + shortcuts (and, as admin, URL-ACL + firewall for
  network reachability of the agent).
