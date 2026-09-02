<div align="center">

# ⊕ GameFlow

**Cross-platform gamepad tooling for anyone.**



**Ultimate multi-platform, multi-language controller tool for remapping and compability layering, Any input device in, any virtual controller out: Xbox, PlayStation or Nintendo. third-party gamepad, keyboard or/and mouse.**


**v1.0.1 Beta** · Built with **.NET 10** · **Avalonia UI** · **SDL3** · **HIDMaestro** · **ViGemBus**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-informational)](#requirements)
[![Made by Proxy Darkness](https://img.shields.io/badge/made%20by-Proxy%20Darkness-blueviolet)](https://buymeacoffee.com/ProxyDarkness)

---

[![Buy Me a Coffee](https://user-images.githubusercontent.com/1286821/181085373-12eee197-187a-4438-90fe-571ac6d68900.png)](https://buymeacoffee.com/ProxyDarkness)
*No pressure — using and sharing the project already helps a lot. Thank you!*

</div>

---

## What is GameFlow?

GameFlow sits between your physical input — a gamepad, keyboard, mouse, or a phone on your Wi-Fi — and any game or application. It reads that input, transforms it through a saved **profile** (a stack of mapping rules), and writes the result to one or more **virtual controllers** the game sees.

This project started as a script to spare players from repetitive inputs that were damaging both their controllers and their hands. It was later rebuilt in Python as **Autofire**, then in C#/.NET as the architecture you're looking at now, since renamed GameFlow to separate the *product* from *autofire* the individual *feature*.

For the full feature reference, per-rule mechanics, and platform-by-platform verification notes, see [**WIKI.md**](WIKI.md).

---

## Features at a Glance

|Category|Details|
|-|-|
|**Autofire**|Button and stick autofire, hysteresis-shaped so it doesn't jitter at the trigger threshold|
|**Remapping**|Button-to-button remap with optional source suppression; toggle rules to flip other rules on/off at runtime|
|**Shift layers**|"Caps Lock for your controller" — 6 activation modes (Hold, Toggle, Latch, Cycle, Sticky, No Button), long-press-to-engage, idle auto-cancel|
|**SOCD cleaning**|Opposite-direction key resolution — Last-wins (Snap Tap), First-wins, or Neutral, per button pair|
|**Stick Trim**|Hold a digital button, a stick modulates it into an analog trigger press — deadzone, ramp rate, and reset-on-release all configurable|
|**Multi-source rows**|Many inputs → one output. Six combine modes (Maximum, Minimum, Sum, Average, Multiply, FirstActive) or a **formula** — a small dependency-free expression compiler (`s1 - s2`, `if(s1>0.5, s2, 0)`, `clamp(...)`), with 10 starter recipes|
|**Gyro aiming**|Local / Player / World reference frames, dual-threshold smoothing (kills tremor, keeps flicks sharp), 4 engage modes including a raw-stick gate, drift bias calibration, output to a stick or the mouse|
|**Touchpad mapping**|Finger-anchored virtual stick, 8-way wedge D-pad, frame-to-frame mouse mode — works on any pad with a touch surface|
|**Custom scripting**|Sandboxed Lua per control (MoonSharp), for logic the built-in rule types don't cover|
|**Button combos**|One press → a timed sequence of virtual presses|
|**Freeze macro**|Captures the stick vector on the rising edge of a button; optional pulse-while-frozen|
|**Per-device tuning**|Deadzone, anti-deadzone, full-at, sensitivity, response curve, and invert — per stick, per trigger, **per slot AND per device**, so the same pad feels different on two different slots. Offline slots remain editable through inheritable defaults; rumble, lighting, and adaptive-trigger settings reach assigned supported hardware through the effects thread.|
|**Rumble-linked adaptive triggers**|A DualSense trigger can stiffen with the game's rumble, or buzz along with it — the impulse-trigger feel, without the game knowing. Per trigger, with its own amount|
|**Keyboard \& mouse as gamepad**|Full keyboard state (not just a handful of buttons) synthesized into a gamepad snapshot — works on **Windows, Linux, and macOS**|
|**Motion server**|DSU / Cemuhook over UDP (26760 by default) — Cemu, Dolphin, Yuzu and Ryujinx read gyro and accelerometer straight from any slot with a motion-capable pad. Off by default; enable it in the Dashboard's Motion Server section|
|**Phone as controller**|No app install — open a URL in any phone browser on the LAN. Dual anchored sticks, 8-way D-pad, analog triggers, rumble via the Vibration API, and the phone's own **gyroscope/accelerometer** feed the same gyro pipeline a DualSense uses. Up to 16 phones at once|
|**Multiple virtual controllers**|Each "slot" is an independent input → profile → output pipeline, with its own device assignment, output kind, and output provider|
|**Profile system**|Create, duplicate, rename, import, export — JSON, human-editable|
|**Live dashboard**|Physical and virtual controllers rendered side by side, per slot|

---

## Requirements

### All platforms

|Requirement|Version|
|-|-|
|[.NET SDK](https://dotnet.microsoft.com/download)|10.0 or later|
|Git|Any recent version|

### SDL3 native library (required everywhere)

Gamepad/joystick input goes through SDL3 on every platform:

|Platform|File|Location|
|-|-|-|
|Windows|`SDL3.dll`|Next to `GameFlow.App.exe`|
|Linux|`libSDL3.so`|System library path, or the app directory|
|macOS|`libSDL3.dylib`|System library path (e.g. `/usr/local/lib`), or the app directory|

An optional `gamecontrollerdb.txt` ([SDL\_GameControllerDB](https://github.com/mdqinc/SDL_GameControllerDB)) in the application directory extends the built-in mapping database.

### Windows — virtual controller output

GameFlow uses [**HIDMaestro**](https://github.com/hifihedgehog/HIDMaestro) exclusively: user-mode, no kernel driver, no reboot. Drop **`HIDMaestro.Core.dll`** next to `GameFlow.App.exe` and it activates on next launch. If it isn't available, that slot has no output — GameFlow won't silently substitute something else, and the log and the slot's displayed name both say why. The app runs fine either way; the slot falls back to **Preview** (dashboard-only, no real device).

Windows also has the fullest platform support: keyboard/mouse-as-gamepad, mouse output (`SendInput`), and the only real virtual-**gamepad** output backend.

### Linux

* Gamepad input: SDL3, works out of the box.
* Keyboard/mouse-as-gamepad: real, via direct `evdev` reads (`/dev/input/eventN`) — not a Windows-only feature anymore.
* Mouse output: real, via `uinput` (creates an actual virtual input device).
* Virtual **gamepad** output: not built yet — Preview only (see [Known Limitations](#known-limitations)).

Needs the `input` group for device access without root:

```bash
sudo usermod -aG input $USER
# then log out and back in
```

### macOS

* Gamepad input: SDL3, works out of the box.
* Keyboard/mouse-as-gamepad: real, via `IOHIDManager` — per physical device, same as Windows and Linux.
* Mouse output: real, via `CGEventPost`.
* Virtual **gamepad** output: not built yet — Preview only.

Keyboard/mouse capture needs **Input Monitoring** permission (System Settings → Privacy \& Security → Input Monitoring). GameFlow asks for it on first run and writes the answer to the log either way — if the permission is refused, keyboards and mice still appear on the Devices page and simply never register a press, so the log line is the only thing that tells a refusal apart from a bug. macOS shows that prompt once and once only; after a refusal, System Settings is the only way back.

\---

## Quick Start

```bash
git clone https://github.com/Prooxie/GameFlow.git
cd GameFlow
dotnet build GameFlow.sln
dotnet run --project src/GameFlow.App
```

First launch creates a default profile under:

|Platform|Location|
|-|-|
|Windows|`%LOCALAPPDATA%/AutofireNext/`|
|Linux|`\~/.local/share/AutofireNext/`|
|macOS|`\~/Library/Application Support/AutofireNext/`|

> Still named `AutofireNext` on purpose — it predates the rename, and changing it would orphan existing installs. Nobody types or sees this path; the app itself always says GameFlow.

\---

## How to Use

### Basics

1. **Profiles** tab → create a profile, or start from the default.
2. **Devices** tab → confirm your physical device is listed.
3. **Virtual controllers** panel → **Add controller** to create a slot, assign a device, pick an output kind and provider, enable it.
4. **Dashboard** → confirm the virtual side (marked with a **VIRTUAL** badge) mirrors your input through the mapping.

A slot's own virtual output is hidden from every input picker — you can't feed a virtual controller back in as a source.

### Tune a device (deadzones, curves, rumble, lighting, adaptive triggers)

**Click any virtual controller panel on the Dashboard.** The panel outlines on hover, and clicking opens that slot's tuning editor — five tabs: Sticks, Triggers, Rumble, Lighting, Adaptive. The editor remains available with no physical controller connected: an offline assigned device keeps its own settings, while a slot with no assignment edits defaults inherited by devices added later. Device-specific settings override those defaults. Changes are live and effects are delivered to an assigned supported controller whenever it is connected.

On the Adaptive tab, **React to rumble** ties a trigger to what the game is actually doing instead of holding one configured feel. *Resistance* makes the trigger stiffen as the game rumbles — free at rest, reaching the strength you set at full rumble. *Vibration* makes the trigger buzz along with the rumble, which is how an Xbox title's impulse triggers land on a DualSense; between events the effect you configured applies unchanged. **Amount** scales how far the game is allowed to move it. Both read the rumble level *after* the Rumble tab's gain, so silencing a pad's motors silences its triggers too.

### Phone as a controller

1. Open **Settings → Phone controller** and copy the shown address. The local server starts automatically with GameFlow.
2. Open that address (for example, `http://10.0.0.5:8080`) on a phone on the same Wi-Fi.
3. Pick a layout, tap **Enable motion** for gyro aiming.
4. The phone appears under **Physical devices** as an ordinary gamepad — assign it to a slot like any other device.

Up to 16 phones can connect at once. Each browser tab keeps a stable pad number across automatic WebSocket reconnects, and stale sockets cannot overwrite a replacement connection. Game rumble returns through the browser Vibration API when the phone supports it; GameFlow sends an explicit stop when rumble ends or the connection closes.

> \*\*Windows firewall note:\*\* binding to all interfaces needs an admin URL ACL, or the server falls back to localhost-only and phones can't reach it (the log says which mode it's in):
> ```
> netsh http add urlacl url=http://+:8080/ user=Everyone
> ```

### Add a controller overlay to OBS

1. Open **Settings → Stream overlay**.
2. Choose the controller slot and skin. Leave **Match the controller** selected to follow that slot's output type automatically.
3. Enable **Show the physical pad** only when the stream should display raw hardware input rather than the mapped virtual output the game receives.
4. Copy the generated URL.
5. In OBS, add a **Browser** source, paste the URL, and set its width/height for the scene.

The page has a transparent background, reconnects automatically, and renders directly from the same slot snapshots as the dashboard. The controller, side, and optional skin are encoded in the URL, so OBS can reuse it without QR scanning or an always-on-top GameFlow window. GameFlow must be running while the Browser source is active.

### Add mapping rules

**Profiles** tab → **+ Add rule** against any control. Rules apply in declared order, every polling tick, live — no separate "apply" step.

---

## Configuration

`src/GameFlow.App/appsettings.json`:

```json
{
  "Runtime": {
    "DashboardRefreshHz": 165,
    "StartRuntimeOnLaunch": true,
    "DefaultCulture": "en",
    "Updates": {
      "RepoOwner": "Prooxie",
      "RepoName": "GameFlow",
      "AssetNamePattern": "GameFlow-{tag}-{rid}.zip",
      "UserAgent": "GameFlow-UpdateChecker"
    }
  }
}
```

|Key|Default|Description|
|-|-|-|
|`DashboardRefreshHz`|`60`|UI refresh rate for the live dashboard (30–1000). This drives a full redraw of every controller surface on screen, so it is the dominant UI cost — lower it on a weak machine. Independent of the mapping tick, which polls at its own rate and is unaffected|
|`StartRuntimeOnLaunch`|`true`|Whether the input/output runtime starts automatically|
|`DefaultCulture`|`en`|Fallback UI language|
|`Updates:RepoOwner` / `RepoName`|`Prooxie` / `GameFlow`|GitHub-releases update checker|

Profiles are JSON in the application data directory (see Quick Start), editable by hand or entirely through the **Profiles** tab.

---

## Providers

**Input:** `sdl` (all platforms — gamepads/joysticks everywhere, plus keyboard/mouse synthesis via platform-native raw input), `web` (phone browsers over the LAN), `demo` (animated preview, no hardware), `none`.

**Output:** `hidmaestro` (Windows, real virtual gamepad), `preview` (all platforms, dashboard-only). Mouse-cursor output (separate from gamepad output) is real on all three platforms via `SendInput` / `uinput` / `CGEventPost`.

Older profiles referencing retired providers (XInput, GameInput, x360ce, ViGEm, PS3, and similar) migrate to `sdl`/`hidmaestro` automatically.

\---

## Project Structure

```
GameFlow/
├── src/
│   ├── GameFlow.App              # Avalonia UI, ViewModels, Views, Localization
│   ├── GameFlow.Core              # Domain models, mapping pipeline, rule types, formula engine
│   └── GameFlow.Infrastructure    # SDL3, HIDMaestro, platform input/output, web controller, profiles
├── tests/
│   ├── GameFlow.Core.Tests        # Pipeline, rules, formula compiler, gyro maths
│   └── GameFlow.Infrastructure.Tests  # Platform interop, protocol, device catalog
└── .github/workflows/             # CI: build, test, and tag-triggered release packaging
```

---

## Architecture Overview

```
Physical input                 Keyboard/mouse            Phone browser
  (SDL3 gamepad/joystick)     (Win Raw Input /              (WebSocket)
        │                      Linux evdev /                    │
        │                      macOS IOHIDManager)              │
        ▼                            ▼                          ▼
     InputSource.ReadAsync() / ReadDevice() ─────────────────────┘
                    │
                    ▼
     DeviceSettingsProcessor      (per-device deadzone/curve, before merge)
                    │
                    ▼
     ControllerMappingPipeline    (one per active slot, isolated)
        ├── RuleToggleRule             ├── StickTrimRule
        ├── SocdCleanRule              ├── GyroMapRule
        ├── ButtonRemapRule            ├── TouchpadMapRule
        ├── ButtonAutofireRule         ├── MultiSourceMapRule (+ Formula)
        ├── MultiButtonAutofireRule    ├── ControlScriptRule (Lua)
        ├── ButtonComboRule            └── StickThresholdRule
        ├── StickAutofireRule
        └── FreezeLastDirectionRule
                    │
                    ▼
     OutputSink.WriteAsync()      (HIDMaestro · Preview)
     + IMouseOutputWriter          (SendInput · uinput · CGEventPost, independent of gamepad output)
                    │
                    ▼
     Virtual controller seen by games — hidden from GameFlow's own
     input list, so it can never be selected back in as a source.
```

Shift layers resolve first each tick, gating which rules are active. Every slot's rules live in its profile's JSON, applied in order, live. The dashboard reads from lock-free snapshot stores on its own timer, independent of the mapping pipeline's tick rate.

---

## Known Limitations

Documented here rather than discovered by surprise:

* **No virtual *gamepad* output on Linux or macOS.** Mouse output is real on both; a real virtual controller (via `uinput`'s gamepad mode, or DriverKit on macOS) is future work.
* **Bundled controller theme placement is known-imperfect** on some skins — several were generated from asset-pack sprites without authoritative layout data. Fixing this properly needs template-matching each sprite against its base image; tracked, not yet done.
* **The macOS input path has never run on a Mac.** It is written against IOKit's documented HID API, but there is no macOS toolchain in the build environment, so nothing about it has been exercised on hardware — unlike the Linux interop, which was checked against real kernel headers. Absolute-mode pointers (some tablets) are also unsupported by it, and the volume/mute keys are not mappable there because they are Consumer-page usages the reader does not claim.
* **Remote Link** — on the roadmap, not started.
* **The DSU/Cemuhook motion server addresses 4 pads, not 16.** That is the protocol's own limit, not an implementation shortcut — DSU has no way to describe a fifth pad. The first four *enabled* slots are the ones emulators see.

---

## Localization

🇬🇧 English · 🇨🇿 Czech · 🇩🇪 German · 🇪🇸 Spanish · 🇫🇷 French · 🇮🇹 Italian · 🇵🇱 Polish · 🇷🇺 Russian — switchable live from Settings.

---

## Contributing

1. Fork the repository
2. `git checkout -b feature/my-feature`
3. Commit your changes
4. Open a Pull Request

Keep commits focused with a short description of what changed and why. See [**WIKI.md**](WIKI.md) for how the mapping pipeline and rule system fit together before adding a new rule type.

---

## License

MIT — see [**LICENSE**](LICENSE).

---

## Acknowledgments

* [**HIDMaestro**](https://github.com/hifihedgehog/HIDMaestro) by hifihedgehog — driver-less, user-mode virtual controller platform.
* [**SDL\_GameControllerDB**](https://github.com/mdqinc/SDL_GameControllerDB) — the community gamepad mapping database SDL3 input builds on.
* [**VSCView THEMEENGINE**](https://github.com/Nielk1/VSCView/blob/master/THEMEENGINE.md) by Nielk1 — the controller theme format GameFlow's controller surfaces are compatible with.
* [**AL2009man's Gamepad Asset Pack \& Prompt Asset Pack**](https://github.com/AL2009man/Gamepad-Asset-Pack) — controller and button-prompt art.
* [**MoonSharp**](https://www.moonsharp.org/) — the sandboxed Lua interpreter behind custom scripting.

Check out my other projects on GitHub, [YouTube](https://www.youtube.com/@ProxyDarkness), and [Twitch](https://www.twitch.tv/ProxyDarkness).

---

## Thanks to

**Nazzareno96** — keeping me sane during development, beta testing
[twitch.tv/nazzareno96](https://www.twitch.tv/nazzareno96)

**NoobKillerRoof** — voicing real hardware/software pain points that inspired this project, beta testing
[twitch.tv/noobkillerroof](https://www.twitch.tv/noobkillerroof)

