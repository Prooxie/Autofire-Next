# GameFlow Wiki

Deep reference for every system in GameFlow. For install/quick-start, see [README.md](README.md).

## Contents

1. [Core Concepts](#core-concepts)
2. [Rule Reference](#rule-reference)
3. [Formula Language](#formula-language)
4. [Shift Layers](#shift-layers)
5. [Gyro Aiming](#gyro-aiming)
6. [Touchpad Mapping](#touchpad-mapping)
7. [Per-Device Tuning](#per-device-tuning)
8. [Cross-Platform Input & Output](#cross-platform-input--output)
9. [Phone as a Controller](#phone-as-a-controller)
10. [Theme System](#theme-system)
11. [Troubleshooting](#troubleshooting)
12. [For Contributors](#for-contributors)

---

## Core Concepts

**Profile** — a JSON document holding a slot's polling rate, input provider, and its ordered list of mapping rules.

**Slot** — one independent input → profile → output pipeline. You can run several at once (local co-op, or one device split across several transformed outputs), each with its own device assignment, output kind, and output provider.

**Tick** — one pass through a slot's `ControllerMappingPipeline`. At the configured polling rate (30–1000 Hz), the pipeline: reads the physical snapshot → applies [per-device tuning](#per-device-tuning) → merges multiple devices if the slot has more than one assigned → resolves the active [shift layer](#shift-layers) → runs every rule type's pass in a fixed order → writes the result to the output sink.

**Rule ordering / last-write-wins** — within one rule-type's pass, rules run in the order they appear in the profile. If two rules of the same type target the same output, the later one wins. This is the same convention every rule type in GameFlow uses, so once you understand it for one, you understand it for all.

**Base vs. layers** — a rule with no `LayerId` (empty string) is a **Base** rule and is always active. A rule tagged with a layer id is only active while that [shift layer](#shift-layers) is engaged. Layer rules run *after* Base rules in the same pass, so a layer's remap for a control naturally overrides Base's remap for the same control — you don't need to do anything special, just order your rules.

---

## Rule Reference

All 14 rule types, in the order their passes run each tick.

| # | Rule | What it does |
|---|---|---|
| 1 | `RuleToggleRule` | On button press, flips another rule's `Enabled` flag on/off. Runs first since everything downstream reads `Enabled`. |
| 2 | `SocdCleanRule` | Resolves one opposite-direction button pair. Runs early — it's input hygiene, not a creative remap, so everything else should see the cleaned result. |
| 3 | `ButtonRemapRule` | Source button → target button, with optional source suppression. |
| 4 | `ButtonAutofireRule` | Pulses one button at a configurable rate while held. |
| 5 | `MultiButtonAutofireRule` | Same, but any-of-several source buttons arms it. |
| 6 | `ButtonComboRule` | One press → a timed sequence of virtual button presses (with per-step delay/hold). |
| 7 | `StickAutofireRule` | Pulses a stick direction, with jitter-resistant hysteresis at the threshold. |
| 8 | `FreezeLastDirectionRule` | Captures the stick vector on the rising edge of an activation button; optionally keeps pulsing it while frozen. |
| 9 | `StickTrimRule` | A held digital button arms it; a stick then modulates an *analog trigger* press. |
| 10 | `GyroMapRule` | See [Gyro Aiming](#gyro-aiming). |
| 11 | `TouchpadMapRule` | See [Touchpad Mapping](#touchpad-mapping). |
| 12 | `MultiSourceMapRule` | Many sources → one target, via a combine mode or formula. See below. |
| 13 | `ControlScriptRule` | Sandboxed Lua (MoonSharp) per control, for anything the built-in types don't cover. |
| 14 | `StickThresholdRule` | Deadzone / full-at shaping, applied directly to the *virtual* output (distinct from the *physical*-side shaping in [Per-Device Tuning](#per-device-tuning)). |

### Multi-Source Rows — combine modes

A `MultiSourceMapRule` has a list of **sources** (button, trigger, stick axis X/Y, stick magnitude, or gyro pitch/yaw/roll — each optionally inverted) and one **target** (a button via a press threshold, a stick axis, or a trigger). The sources fold into one value via a **combine mode**:

| Mode | Behavior |
|---|---|
| `Maximum` | Largest value wins — "any of these" for buttons, strongest push for analog. |
| `Minimum` | Smallest value wins — "all of these" for buttons. |
| `Sum` | Values add (clamped on write). |
| `Average` | Arithmetic mean. |
| `Multiply` | Values multiply — a natural gate: a released button (0) zeroes the product regardless of the other sources. |
| `FirstActive` | The first source in list order past a small activity threshold wins outright — priority ordering. |
| `Formula` | See [Formula Language](#formula-language). |

A row **owns its target for the tick**: if the combined value is below the press threshold, the target releases even if the physical button is still held. `SuppressSources` optionally zeroes each source's own contribution to the virtual output, so only the combined target carries the input.

> These six modes plus Formula are this implementation's own selection for the domain — worth knowing if you're comparing against another tool's exact mode names.

---

## Formula Language

A small, dependency-free expression compiler — not a scripting language, deliberately: for arithmetic over a handful of sources, a purpose-built parser is a smaller, more predictable trust boundary than routing through a full interpreter.

**Sources:** `s1`, `s2`, ... `sN` (1-indexed, matching the row's source list). Referencing `s3` on a two-source row is a **compile error**, not a silent zero — a typo should be caught, not become a dead input.

**Operators:** `+ - * /` (unary `-` too), parentheses, comparisons `< > <= >= == !=` (return `1`/`0`), logic `and or not` (also `&& || !`).

**Functions:** `if(cond, a, b)`, `min(a, b, ...)`, `max(a, b, ...)`, `abs(x)`, `clamp(x, lo, hi)`.

**Safety:** division by zero yields `0`, not infinity or an exception.

### Starter recipes

| Name | Formula | Use |
|---|---|---|
| Two buttons → one axis | `s1 - s2` | A D-pad pair or two keys become an analog axis |
| Strongest input wins | `max(s1, s2)` | Whichever source is pushed hardest drives the output |
| Both required (gate) | `if(s1 > 0.5, s2, 0)` | s2 passes through only while s1 is held |
| Blend 50/50 | `(s1 + s2) / 2` | Two people steering one wheel |
| Weighted blend | `s1 * 0.7 + s2 * 0.3` | A main input with a trim input |
| Boost while held | `if(s2 > 0.5, s1, s1 * 0.5)` | Walk/sprint |
| Invert | `-s1` | Flip a source's direction |
| Threshold to digital | `s1 > 0.4` | A trigger becomes a button at 40% pull |
| Sum, capped | `clamp(s1 + s2, 0, 1)` | Two sources stack, never past full press |
| Deadzone re-map | `clamp((abs(s1) - 0.15) / 0.85, 0, 1)` | Ignore the first 15% of travel, rescale the rest to 0..1 |

---

## Shift Layers

"Caps Lock for your controller" — extra rule tables that turn on while a button, chord, or axis fires. At most **one** non-Base layer is active at a time; engaging a new one always replaces whatever was active, including a different latched or cycled layer.

| Mode | Behavior |
|---|---|
| `Hold` | Active only while the activator is physically held. Released → Base immediately. |
| `Toggle` | Press to turn on, press again to turn off. Supports **hold-to-fire** (a quick tap does its normal job; a hold flips the layer) and **auto-cancel** (idle timeout drops back to Base so you're never stranded). |
| `Latch` | Press to turn on and stay on. Turns off by pressing the *same* activator again, or *switches directly* to a different Latch layer without detouring through Base. |
| `Cycle` | Steps forward through an ordered queue of other layers; a second button steps back. Wrap-around and whether Base is one of the stops are both configurable. A Cycle entry doesn't gate any rule itself — it only orchestrates the queue. |
| `Sticky` | Press once to engage; stays active through exactly the *next* button press elsewhere, then reverts automatically — classic "sticky keys" behavior. |
| `NoButton` | Has no activator of its own; can only become active by being a stop in a Cycle queue. |

A **stick gate** (used by Gyro's engage modes, and available generally) reads the *raw* physical stick — before any deadzone shaping — so a nudge too small for the game to act on can still arm something.

---

## Gyro Aiming

Turns a pad's angular velocity into aim. Raw input is radians/second, following SDL's own convention (positive = counter-clockwise, axes X-right/Y-up/Z-toward-you).

### Reference frames

| Frame | Behavior |
|---|---|
| `Local` | Raw axes, no correction. Yaw turns you horizontally, pitch aims vertically. Exact and predictable — but "horizontal" tilts with the pad if you hold it leaned. |
| `Player` | Combines yaw and roll using the accelerometer as a gravity reference, so twisting the pad about the world's vertical always reads as horizontal aim, even leaned. Ignores pitch's contribution to horizontal. |
| `World` | Full projection onto the world's vertical axis — horizontal aim is correct at *any* orientation, including holding the pad sideways. |

Both `Player` and `World` fall back to `Local` when there's no accelerometer reading, rather than producing garbage from a zero gravity vector.

### Engage modes

`AlwaysOn`, `HoldToEngage`, `HoldToDisable` (inverse — gyro is live by default, holding silences it), `Toggle`. Any mode can additionally be armed by the stick gate.

### Smoothing

Dual-threshold: movement below the lower threshold is fully smoothed (kills hand tremor and sensor noise while holding still); movement above the upper threshold passes through completely raw (keeps fast flicks sharp); it blends linearly between the two.

### Calibration

Per-axis bias values subtract out steady drift (a gyro at rest still reports a small non-zero rate). A deadzone on the *combined* magnitude catches residual creep after bias correction. Bias capture currently has no in-app UI — values are set by hand in the profile JSON.

### Output

Drives a stick (clamped at a documented reference angular rate) or the mouse (via the same delta channel the touchpad's mouse mode uses — angular velocity is a *rate*, so this scales by elapsed time between ticks, unlike the touchpad's already-per-frame deltas).

---

## Touchpad Mapping

For any pad with a touch surface (DualSense, DS4, and others SDL exposes touchpad data for).

- **Stick anchor** — the moment a finger touches down, that position becomes the anchor (like a phone game's virtual joystick appearing wherever you tap). Movement away from the anchor drives stick deflection.
- **Wedge D-pad** — anchor-relative direction bucketed into 4 or 8 wedges; 8-way diagonals hold two adjacent buttons at once, the standard way to represent 8 directions on 4 buttons.
- **Mouse mode** — genuinely different from the stick/D-pad modes: it's **frame-to-frame**, not anchor-relative, matching how a real laptop touchpad works (it doesn't matter where your finger first landed). Touch Y and screen Y both already grow downward, so — unlike the stick mode — mouse mode does **not** negate Y.

All three modes can be enabled simultaneously on one rule.

---

## Per-Device Tuning

Click any virtual controller panel on the Dashboard to open a slot's device editor. Settings are keyed by **slot AND device**, so the same physical pad can be tuned two different ways on two different slots without them fighting each other.

**Sticks:** deadzone, anti-deadzone (lifts the floor past a game's own internal deadzone), full-at (lets a worn stick that no longer reaches its corners still hit 100%), sensitivity, response curve (Linear / Precision-squared / Aggressive-sqrt), per-axis invert.

Shaping is **radial**, not per-axis: deadzone and saturation apply to the stick's magnitude with direction preserved. A per-axis approach produces a square dead region, so a diagonal push escapes the deadzone at a different physical distance than a straight one — the classic "diagonals feel wrong" bug. This is applied before any mapping rule sees the input, and before multi-device merging (each device in a multi-device slot is conditioned with its *own* settings, then merged).

**Triggers:** deadzone, full-at, sensitivity, invert.

**Rumble, Lighting, Adaptive Triggers:** the settings model, persistence, and UI are all real and tested. **They don't reach physical hardware yet** — see [Known Limitations](README.md#known-limitations) in the README.

---

## Cross-Platform Input & Output

| Capability | Windows | Linux | macOS |
|---|---|---|---|
| Gamepad/joystick input | SDL3 | SDL3 | SDL3 |
| Keyboard/mouse as source | Raw Input | `evdev` direct reads | `CGEventTap` |
| Mouse cursor output | `SendInput` | `uinput` | `CGEventPost` |
| Virtual gamepad output | HIDMaestro | — | — |

### Verification notes

The Linux `evdev`/`uinput` interop (struct layouts, ioctl numbers) was verified by compiling small C programs against the actual kernel headers and cross-checking the output — not derived from memory. The macOS `CGEventTap`/`CGEventPost` interop is written against Apple's documented, stable API surface, but **could not be verified against real headers or hardware** during development (no macOS toolchain was available) — treat it as a good-faith implementation that hasn't had a hardware pass yet.

**macOS specifically:** `CGEventTap` has no per-device concept — one aggregate stream for every keyboard/mouse system-wide, unlike evdev's one-file-per-device or Raw Input's per-handle model. Per-device selection in the UI on macOS falls back to that aggregate.

**Linux permissions:** `/dev/input/eventN` needs the `input` group (see [README](README.md#linux)). Without it, GameFlow runs fine — that input source just reads as empty, logged once.

**macOS permissions:** keyboard/mouse capture needs Input Monitoring (System Settings → Privacy & Security). Same graceful-empty behavior without it.

---

## Phone as a Controller

A tiny embedded HTTP + WebSocket server (`.NET`'s built-in `HttpListener` — no ASP.NET Core dependency for one page and one socket). The page is a single self-contained HTML document with **zero external requests**, since a phone on a LAN may have no real internet route at all.

**Wire protocol:** a fixed bit-order button mask (documented in `WebControllerProtocol.cs`, deliberately *not* derived from any enum's declaration order, so a refactor elsewhere can't silently break it), plus stick/trigger axes, plus optional motion fields.

**Motion:** the phone's gyroscope/accelerometer, converted from the browser's degrees/second to SDL's radians/second before sending, so a phone arrives in the exact same units a DualSense does and drives `GyroMapRule` with no phone-specific code downstream. iOS requires an explicit permission tap (`DeviceMotionEvent.requestPermission()`); Android doesn't gate it.

**Capacity:** up to 16 phones, each an independent virtual pad, each claiming the lowest free slot index. A disconnected or stale (no traffic for 5 seconds) pad reads as fully neutral rather than its last input — so a phone that dies mid-press can't leave a button stuck down in a live game.

**Rumble:** queued back to the phone and played via the browser's Vibration API.

---

## Theme System

Controller visuals use the [VSCView THEMEENGINE](https://github.com/Nielk1/VSCView/blob/master/THEMEENGINE.md) format: a JSON tree of image/slider/showhide nodes, each with an input expression evaluated against a symbol table (`stick_left:x`, `key:f7`, `triggers:l:analog`, etc.) built from the current controller snapshot.

**Keyboard themes** bake keys and legends directly into the body image (matching how the bundled default theme works) — a theme with only geometry and no rendered artwork will show nothing. `keyboard-100-default` is a full ANSI-104 layout with all keys individually addressable via `key:<name>` symbols.

**Known issue:** several bundled gamepad themes have imperfect button/stick placement — they were generated from an asset pack's individual sprite crops without authoritative layout coordinates. The correct fix is template-matching each sprite against its full-canvas base image to derive true pixel positions; this is scoped but not yet done.

---

## Troubleshooting

**A slot's dashboard panel shows animated input I never gave it.** No device is assigned to that slot — it's falling back to the Demo preview source, which intentionally animates. Assign a real device in the Devices tab.

**Web controller says "connection lost" from a phone.** On Windows, binding to all network interfaces needs an admin URL ACL; without it the server falls back to localhost-only (check the log — it states which mode it's in):
```
netsh http add urlacl url=http://+:8080/ user=Everyone
```

**Clicking a virtual panel does nothing.** That slot has no device assigned — there's nothing to tune. The status bar says so; assign a device first.

**HIDMaestro output isn't appearing.** Confirm `HIDMaestro.Core.dll` is next to `GameFlow.App.exe`. If it's genuinely missing, GameFlow says so explicitly in the log and the slot's display name — it will not silently fall back to a different backend without telling you.

---

## For Contributors

- **Adding a rule type:** follow the existing pattern in `src/GameFlow.Core/Models/Rules/` — a record deriving `MappingRule`, registered in `MappingRule`'s `[JsonDerivedType]` list, with its pass added to `ControllerMappingPipeline.Process()`. Keep state that needs to persist across ticks (schedulers, latches) as a small dictionary field on the pipeline, matching every other stateful rule.
- **Platform interop:** if you're touching `EvdevInterop.cs`/`UinputInterop.cs`, verify struct layouts and ioctl numbers by compiling a small C program against real headers rather than trusting memory — that's how the existing Linux interop was grounded, and it caught a real ABI mismatch during development (`ioctl`'s request parameter needing 8 bytes, not 4, on x86_64).
- **Tests:** `tests/GameFlow.Core.Tests` covers the pipeline and pure logic (no OS dependency); `tests/GameFlow.Infrastructure.Tests` covers platform interop and protocol correctness. 122 tests as of this writing.
- **Versioning:** the single source of truth is `Directory.Build.props`'s `<Version>`, used as the fallback for local builds. Tagged releases (`v*`) override it via `-p:Version=${GITHUB_REF_NAME#v}` in `.github/workflows/ci.yml` — tag `v1.0.1` and CI picks it up with no workflow changes needed.
