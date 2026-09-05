# Changelog

Notable changes per release. The `release` job in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) reads the section
matching the tag it is building and uses it as the release body, with
GitHub's auto-generated commit list appended below it.

Format: one `## vX.Y.Z` heading per release, newest first. The heading
text must match the tag exactly — that is what the extraction keys on.

## v1.0.4

### Added

- **The setup guide now performs the whole setup, not just the creation.**
  Three steps were added around the existing ones.
  - **Check the buttons line up.** The chosen pad's live layout, with the
    Devices page's own calibration wizard driven from inside the guide
    rather than described as somewhere to go afterwards. A mis-mapped pad
    is invisible until something built on top of it behaves oddly, by
    which point the guide is long finished; pressing the buttons against a
    picture is the cheapest check there is.
  - **Name it.** With more than one virtual controller the name is the
    only thing telling them apart, and the guide previously left it as
    whatever the registry generated.
  - **See both layouts.** The physical pad and the emitted controller side
    by side, live, so the mapping can be confirmed by pressing something
    and watching both move — with a pointer to where to change it.
- The detection step now **picks** a controller rather than the creation
  silently taking whichever gamepad the catalog listed first. With two
  pads connected that was a coin toss, and nothing said which had been
  chosen — so the layout check, the name and the review could all describe
  a controller the user was not holding.

### Fixed

- **The dashboard's physical panel took far longer to draw than the
  virtual one beside it.** The virtual side has always known its own kind
  — it is whatever the slot was created to emit — so it drew immediately,
  while the physical side was left on Auto, which reads vendor and product
  off an incoming frame and so could resolve nothing until the runtime
  produced one. The visible result was a finished controller on the right
  and "no controller theme installed for this style" on the left. The
  catalog knows what the pad is the moment it is assigned, so the art is
  taken from there; Auto still applies for hardware the catalog does not
  recognise.

- **The setup guide's review step showed a motionless pad and no theme
  for the controller it had just created.** Both halves read from the new
  slot's snapshot pair, and a slot created a second earlier has not
  produced one: its rebuild is debounced, so the physical half was empty
  exactly where the user is asked to press something and watch it move,
  and the virtual half had no vendor/product for the art to be inferred
  from — reporting no theme beside a controller that had been created
  successfully. The physical half now stays on the pinned feed, which is
  already live from the previous step and owes nothing to slot startup,
  and the emitted controller's art comes from the output kind that was
  chosen rather than from frames that do not exist yet.

- **A pinned controller panel only animated while some other controller
  had an enabled slot.** The per-tick publish that feeds pinned physical
  panels sat below the coordinator's "no enabled slots, nothing to do"
  early return, so with none — a fresh install, every slot disabled, or
  the setup guide's layout check, which runs before the slot it is about
  to create exists — the panel drew the right controller and then never
  moved. Pinned panels do not belong to a slot, so they are now fed
  before that exit rather than after it.
- **The controller surface clipped its own title.** It declares a 260 px
  minimum and a fixed 30 px title row, and a shorter container does not
  shrink it — it clips it, top first, taking the title with it. The setup
  guide's boxes were shorter than that minimum.

- **The setup guide's layout check showed "No controller theme
  installed" over a blank panel.** Two causes, both invisible from the
  outside. The runtime publishes physical snapshots only for PINNED
  devices, and the guide pinned nothing — so it read back an empty
  placeholder whose vendor and product are zero and whose name is the
  catalog id. The controller art is then resolved from that frame, finds
  no match, and reports no theme. The guide now pins the pad it is
  showing (and unpins it on the way out, unless the user had pinned it
  themselves), resolves the art from the catalog's vendor/product rather
  than from the frame, and stamps that identity onto the placeholder so
  the right controller is drawn from the first tick rather than after the
  first frame arrives.

- **A controller could come back offline after a restart, showing its raw
  id and refusing to be configured, until its virtual controller was
  killed.** A device's stable id is a hash of vendor, product and name,
  and a virtual pad impersonates the real one down to exactly those — so
  both hashed to the same id and were separated only by an ordinal
  suffix handed out in enumeration order. That order is not stable across
  restarts. When the virtual pad enumerated first it took the bare id and
  the real controller became "-2", so every slot referencing the bare id
  resolved to a device that is deliberately hidden from the catalog:
  offline, nameless, unconfigurable. Killing the virtual controller freed
  the id and the real one reclaimed it, which is why that appeared to be
  the fix. Virtual pads now hash in their own signature space, so they
  cannot collide with the hardware they imitate. Ids for real hardware
  are unchanged, so no saved assignment needs migrating.

- **Adaptive triggers were encoded in the wrong parameter layout, and
  the vibration modes produced complete silence.** Effects `0x21`, `0x25`
  and `0x26` are the DualSense firmware's EXTENDED trigger set, and they
  do not take a start byte and a strength byte: they take a ten-bit mask
  of which zones along the pull are engaged, followed by three bits of
  amplitude per zone packed across four bytes. GameFlow wrote simple
  parameters into that layout. Nothing failed — the effect id was right,
  the enable bit was right, the write was accepted — so an arbitrary
  handful of zones got engaged at arbitrary strengths, which is why the
  resistance modes felt roughly plausible and hid the problem.
  Vibration did not survive it: frequency is the ninth parameter and was
  being written as the third, landing inside the amplitude mask and
  leaving the frequency byte at zero. A buzz with no rate to buzz at is
  silence, and only a change to the effect produced the momentary tick
  that gave it away. Positions now travel as zones, strengths as the
  firmware's eight levels, and a request with no frequency or no strength
  is reported as "off" rather than arming a trigger that cannot act —
  which also fixes a zero strength wrapping to seven, i.e. full.

- **Choosing a distinct adaptive-trigger mode gave a different effect.**
  `AdaptiveTriggerMode` has seven values and the effect kind it was
  narrowed onto had four, so the Mode -> Effect -> Mode round trip in the
  middle of the pipeline was lossy: Multiple-position feedback and Slope
  feedback both reached the pad as plain Constant resistance, and
  Multiple-position vibration as plain Vibration. The UI went on showing
  the mode that had been picked and nothing was logged, so the pad was
  running a different effect from the one on screen. The DualSense
  encoder already writes distinct effect ids for all seven, so the detail
  was discarded for nothing. The effect kinds now mirror the mode set
  one-to-one.

### Known limitations

- **Battery, motion and touch cannot pass through a USB Sony output
  profile.** They travel in a Sony report's extended section, and
  HIDMaestro runs that section's codec on the input direction only for a
  profile that arms it. Checked against the shipped catalogue:
  `dualshock-4-v1-full` and `dualsense` set neither `armOn` nor
  `alwaysArmed`, while `dualsense-bt` arms on a feature read. So the
  battery byte is never written and whatever GameFlow submits goes
  nowhere — the submit succeeds, the SDK accepts the field, and the value
  simply does not reach the wire. Diagnosed by changing the submitted
  charge from 3 to 25 and watching the reported value not move. GameFlow
  now warns when such a profile is deployed and names the Bluetooth
  alternative; it cannot work around it.

- **The battery a virtual pad reported was a tenth of the real charge.**
  The SDK documents the underlying report field as "0..10 (Sony firmware
  convention)", and that wording was taken to describe the SDK's own
  property as well. It does not — the property takes a percentage and the
  codec down-scales into the Sony field itself, so scaling here divided
  the charge a second time. Measured on a live pad: a source at 25% was
  submitted as 3 and read back as 3%, one at 65% as 7 and read back as
  about 7%. Both match the raw number rather than a tenth of it, which
  the 0..10 reading cannot account for. Charge now travels as a
  percentage.
- **The first hover of a control flashed the wrong highlight shape.**
  Theme bitmaps decode in the background and the first request for one
  returns nothing, so the click and active overlays — which an idle
  render never draws, and whose first request is therefore the user's
  first hover — had no silhouette for that frame and fell back to a
  rectangle before correcting themselves. Every image a theme references
  is now decoded when the theme is set, at a moment when nothing is
  waiting on it.
- **Startup contention set the dashboard's refresh rate for the whole
  session.** Warm-up ends after four seconds, but a multi-slot dashboard
  is still faulting in the better part of three hundred theme bitmaps
  well past that, and every completed decode posts a repaint. The
  adaptive rate read that as a verdict on the hardware and stepped 165 Hz
  down to its 10 Hz floor in a second and a half. Measurement shows the
  refresh itself costs 0.2 ms against a 115 ms gap, so it was never the
  work being timed. No throttling decision is now taken while theme art
  is still decoding, and a ceiling relaxes after ten quiet seconds rather
  than thirty.

- **The dashboard pinned itself at 10 Hz for the rest of the session.**
  The adaptive tick rate remembers a rate the machine failed to hold and
  keeps recovery below it, which is right; testing the proposed step
  against that ceiling was not. Recovery halves the interval, and halving
  overshoots the ceiling in one jump every time — from the 10 Hz floor
  the step is to 20 Hz, past any ceiling below that — so every recovery
  step that could ever be proposed was rejected and the rate could only
  travel downwards. A single stall while themes were still decoding at
  startup therefore fixed the refresh rate at its floor for the whole
  session, which is the permanent choppiness the recovery path exists
  to prevent. Recovery now approaches a failed rate asymptotically
  instead of being refused, so it still cannot oscillate into one, and a
  ceiling is relaxed after thirty seconds without an overrun — startup
  contention no longer decides how the dashboard runs an hour later.

- **Hover and press highlighted the wrong shape on half the controls.**
  L1, R1, the touchpad, Options/Share and the PS button drew a plain
  rounded rectangle (or, for the PS button, a stretched ellipse) instead
  of the control's own silhouette, while the D-pad, face buttons, sticks
  and triggers were correct. The split was not arbitrary: theme bitmaps
  decode on a background thread, so the first request for any image
  returns nothing and schedules a repaint for when it lands — and the
  highlight cache stored that nothing permanently. Art the ordinary
  render already draws was decoded long before anyone could hover it, so
  it silhouetted correctly. The click/active overlays are drawn only
  while a control is held, so a hover was always the very first request
  for them, always got nothing back, and cached that for the rest of the
  session. A not-yet-decoded mask is no longer cached, so the highlight
  picks it up on the repaint the decode already posts.

- **The PS / Guide button could not be hovered, clicked or mapped.**
  Theme bindings are almost all `group:member` — `quad_right:s`,
  `bumpers:l` — and the hit-tester's variable extractor required that
  colon before it would report a binding at all. Every Sony pack names
  the PS button plainly `home`, which has no colon, so it returned
  nothing and the button produced no hit result: no highlight, no
  click-to-map, no mapping. The mapper's own `"home" => "Guide"` case
  had been present and unreachable the entire time. Any variable name is
  now accepted; deciding what is mappable belongs to the mapper, which
  already returns nothing for names it does not recognise.

- **Virtual controllers appeared as real hardware before disappearing
  again.** GameFlow recognises its own emitted pads by three signals, and
  a live capture showed the pads matching none of them: HIDMaestro
  enumerates them under `HID\HIDCLASS\...`, which carries neither the
  `hidmaestro` name nor the `ROOT` enumerator the other two look for. So
  every poll classified them as physical hardware, and they were only
  withdrawn later by the slower vendor/product filter — the visible
  "appears as a real controller, then vanishes" sequence. A HID device
  parented by the HID stack rather than by a bus is now recognised
  directly: real pads always name their bus in that path position
  (`HID\VID_054C&PID_0CE6&...` over USB, `HID\{00001124-...}_VID&...`
  over Bluetooth), so nothing physical matches. Two consequences beyond
  the flicker: the pad is no longer offered back as an input source, and
  GameFlow stops battery-polling a device it created itself.
- **The window in which a new virtual pad was unclaimed.** The owning
  slot published its vendor/product signature only *after* the device had
  been created, leaving a gap in which the device existed in Windows —
  and could therefore be enumerated — with nothing yet claiming it. The
  signature is now reserved before the creation call and released if it
  fails, so there is no moment at which the device is visible and
  unowned. A real pad of the same model that was already connected is
  still unaffected, because the catalog compares against when each device
  was *first* seen.
- **The touchpad click never reached a virtual DualSense or DualShock 4.**
  The sink submitted it as HIDMaestro's `Share` button, and every Sony
  profile marks `Share` with the SDK's explicit "this pad does not carry
  this button" sentinel — so the press was dropped on the floor rather than
  landing on the touchpad. It is now submitted as `Touchpad`, which those
  profiles map to the real descriptor button.
- **Paddles and the mic-mute button reached nothing.** `Paddle 1-4` and
  `Misc1` were absent from the submit list entirely, so mapping a paddle
  onto a virtual controller did nothing at all. They now pair by side with
  HIDMaestro's own paddle buttons, matching how the SDL reader fills them.
- **Virtual controllers reported no motion and no touch.** The DualSense and
  DualShock 4 report layouts declare gyro, accelerometer and two touch
  fingers, and the sink never wrote any of them — so every frame went out
  with those fields zeroed, which a game reads as a controller held
  perfectly still with nothing on its touch surface. This is the same shape
  as the battery bug fixed in 1.0.3: an unwritten field is not an absent
  one. Motion is now converted from the snapshot's SDL units (rad/s, m/s²)
  into the SDK's (deg/s, g) and touch from normalized coordinates into the
  Sony surface range. A source with no gyro still reports *no motion*
  rather than zeroes, so "held still" and "no sensor here" stay
  distinguishable.
- **The first virtual controller could end up mis-named in a multi-slot
  session.** Creating a second controller re-triggers Windows PnP
  driver-bind activity that overwrites the first one's friendly name. The
  SDK exposes a settling call for exactly this race and GameFlow never made
  it; it now runs once per rebuild, after every slot's device exists.
- **A missing HIDMaestro DLL flooded the log at the polling rate.** Every
  path that marks the output backend unavailable is supposed to latch the
  verdict and arm a retry cooldown, but the two most likely ones — the DLL
  not being found, and running on a non-Windows platform — latched without
  arming. The cooldown is the gate, so every tick fell straight through it,
  re-probed and warned again: roughly a thousand identical lines a second
  per slot, to the console and the rolling file sink, with that file I/O
  landing on the runtime tick thread. The verdict is now held for 45
  seconds, so dropping `HIDMaestro.Core.dll` in beside a running app still
  recovers on its own, quietly.
- **Per-device tuning was saved outside the application data folder.** It
  resolved `%LocalAppData%\AutofireNext` directly while everything else had
  moved to `%LocalAppData%\GAMEFLOW`, so slider settings, rumble, lighting
  and adaptive-trigger configuration were the only state left behind by the
  rename's migration — invisible to a folder override and to anyone backing
  up their data folder. It now goes through the same path resolver as
  profiles, slots and settings, which also means the one-time copy from the
  legacy folder picks it up.
- **The exit watchdog killed the process before shutdown could finish.** It
  fired after four seconds while the graceful stop was given five, so a host
  that took between four and five seconds to release native handles — the
  case the five seconds exists for — was hard-killed mid-teardown instead.
  The watchdog now waits out the full graceful budget and only acts on a
  genuine hang.
- **`StartRuntimeOnLaunch` did nothing.** The setting has been documented
  since the options type was introduced, but nothing read it: setting it to
  `false` started the runtime anyway. It is now honoured — no input source,
  output sink or slot pipeline is created, which is the diagnostic mode it
  always described.
- **Two view models leaked their localization subscriptions.** The touchpad
  and output-template editors subscribed to the culture service with
  anonymous handlers that could never be detached, so they and everything
  they held stayed alive for the life of the process and kept reacting to
  language changes after their panel was gone.

### Changed

- **Preview, Devices & profiles and Mappings are one page.** Three
  sibling tabs all described the same controller, and the split forced a
  round trip to do anything useful — the mappings tab opened by telling
  you to "return to Preview and click the control you want", which is a
  tab admitting it is in the wrong place. Setting a controller up now
  reads top to bottom: see it, say what drives it, map it.
- **Tuning moved in with the virtual controller it tunes.** It was a
  sibling tab with a slot picker of its own, restating a choice already
  made one tab over — and the two could disagree, so the tuning panel
  could be editing a different controller from the one on screen. It is
  now a tab of the selected controller and follows that selection.

- **Adaptive-trigger writes are logged once, like rumble.** A trigger
  mode that "does not work" can fail at the encoder, in the plan, or at
  the pad, and from outside the three are indistinguishable. The line
  names the effect asked for, its parameters, and whether the pad
  accepted it.

- **Dashboard tick telemetry is aggregated rather than sampled.** The
  old line reported one overrun per five seconds, which cannot tell "one
  hitch every few seconds" from "every tick is late" — and those need
  different fixes. It now reports tick count, mean and worst gap, overrun
  count, and the time spent inside the refresh itself, so the share lost
  elsewhere on the dispatcher is visible rather than inferred.

- **The hover/press highlight says once per element which path it took**
  — the element's own silhouette art, or the fallback shape — together
  with the art path, whether the mask actually loaded, and the bounds. A
  highlight that comes out as a plain rectangle has two causes that look
  identical on screen, and nothing in the log told them apart.

- **An XInput output packet in an unrecognised shape no longer reports
  itself as broken rumble.** XUSB carries more than vibration on that
  channel — Windows sends an LED/index packet the moment it binds a pad,
  before any game is running — and the warning claimed "rumble will not
  reach the pad", which is not what an unknown shape means and is not
  what was happening. It is now an informational line that says the
  packet was ignored, notes that recognised packets are still decoded and
  delivered, and is emitted once per distinct payload length rather than
  once per pad, so a shape that only shows up during actual vibration
  stays visible against the startup chatter.
- **Battery submission is logged once per virtual pad**, with the source
  pad's charge and the value written side by side. A pad reading "5%" has
  two indistinguishable causes — the source reporting no charge at all,
  so the no-battery fallback is what gets written, or the value being
  read back on a different scale — and the existing logs could not tell
  them apart.
- **The runtime tick no longer allocates on its steady path.** Several
  per-frame costs that scaled with the polling rate were removed: the slot
  registry deep-cloned every slot on every read (once per tick, plus once
  per frame from the effects producer and the DSU server); the coordinator
  rebuilt and re-sorted its virtual-output filter every tick to hand the
  device catalog a list that had not changed; the mapping pipeline allocated
  a diagnostics list and a two-entry dictionary per frame and re-formatted
  the same remap notes thousands of times a second; and the HIDMaestro sink
  built a fresh button list, boxed a button mask and re-parsed the D-pad
  direction from a string on every submit. Behaviour is unchanged — this is
  garbage-collection pressure removed from the path whose pauses are felt as
  input lag.
- **Button state is a bit mask rather than a dictionary.** Every controller
  snapshot carried a 24-entry `Dictionary<ButtonId, bool>`, and the mapping
  pipeline cloned it on every frame of every slot — roughly ten megabytes a
  second of short-lived garbage at full polling rate across sixteen slots,
  to hold twenty-four booleans, on the path whose collection pauses are felt
  as input lag. It is now four bytes with no allocation, and the whole-map
  operations (merging several devices into one slot, deciding whether a
  repaint is owed, detecting which buttons changed this tick) became single
  instructions. Behaviour is unchanged, with one exception that is a fix:
  a source reporting a sparse button map could previously leave the shift-
  layer resolver holding a stale "still pressed" for a button it had
  stopped reporting.
- **Environment overrides accept a `GAMEFLOW_` prefix** — for example
  `GAMEFLOW_Runtime__DashboardRefreshHz=120`. The pre-rename `AUTOFIRE_`
  prefix is still read, and `GAMEFLOW_` wins when both are set.

### Removed

- **The legacy localhost overlay server.** Disabled by default and
  superseded by the OBS browser overlay, it still started a hosted service
  and carried its own copy of an overlay page on every launch. The OBS
  overlay under **Settings → Stream overlay** is unaffected.
- Dead scaffolding with no remaining callers: the placeholder output-sink
  base class that had no subclasses, and the button-prompt asset loader
  (whose asset folder never contained any assets).
- **The compile-time HIDMaestro tier.** A second copy of the output sink
  lived behind `#if HIDMAESTRO_SDK`, but nothing defined the symbol and no
  project referenced the SDK assembly, so it could not be built — or
  compiler-checked — without editing the build first. It duplicated the
  runtime bridge and had already drifted from it. The bridge, which loads
  `HIDMaestro.Core.dll` from beside the executable with no rebuild, is
  unchanged and remains the only path.

## v1.0.3

### Fixed

- **Phones could not reach the web controller even with a firewall rule.**
  A Windows Firewall rule names the network categories it covers, and a
  machine with several adapters up is on several categories at once. A
  rule covering Private looked like it applied while a home Ethernet
  filed as Public — the network a phone actually connects over — stayed
  blocked. GameFlow now checks that the rule covers *every* active
  category, names the one it misses, and offers both fixes: re-categorise
  the network in Windows (safer) or widen the rule to Public.
- **Virtual controllers always reported ~10% battery.** The battery
  fields on the emitted HID report were never written, and unwritten is
  not the same as absent — a DualSense or DualShock 4 report always
  carries a battery, so zero decoded as nearly flat, forever, whatever
  was driving the slot. Charge now travels from the source pad through to
  the virtual one; a source with no battery reports full, which is what a
  wired controller says about itself.
- **Holding AltGr registered Control as well.** On every layout with an
  AltGr key — Czech, German, Polish and most other non-US layouts —
  Windows injects a phantom Left Control press ahead of Right Alt. It was
  lighting up the on-screen keyboard and firing any mapping bound to
  Control whenever the user typed a character needing AltGr. The injected
  press is now identified and suppressed for as long as AltGr is held.
- **The dashboard was unusable for the first ten seconds after launch.**
  It ticked at the display's full refresh rate straight into startup
  contention, then read that contention as a slow machine and throttled
  itself to 10 Hz, taking a further eight seconds to recover. Startup now
  runs at a fixed modest rate and no throttling decisions are taken from
  measurements made during it.
- **Adding a controller opened the Tuning tab** instead of Virtual
  controllers, after Tuning was inserted between them.
- **Tab headers ran into each other** in the tuning editor and every
  other ordinary tab strip.
- **VIRTUAL and LIVE overlapped** on each dashboard panel.
- **The bottom of the Options dialog could not be scrolled to**, leaving
  the last control permanently behind the button bar.
- Tuning sliders had no track to travel along, and its numeric fields
  stretched the full width of the window.
- Mapping summaries and rule cards were clipped through the middle of a
  line of text rather than ellipsised.

### Added

- **A setup walkthrough.** Four steps that *perform* the setup rather
  than describing it: it explains what a virtual controller is, waits
  live for a pad to be connected, asks what games should see, and creates
  and assigns the slot. Shows once on first run, and is available any
  time from **Setup guide** in the sidebar.
- **Phone and OBS on the dashboard.** "Use a phone" opens a sheet with
  the address, an Open button and the reachability diagnosis; "Copy OBS
  layout" puts the browser-source URL straight on the clipboard. Both
  previously existed only inside Options.
- **Open / Preview buttons** beside the Copy buttons for the phone
  controller and stream overlay.
- **Plain-language help throughout controller tuning**: an orientation
  line per tab, a named preset row, and a tooltip on every term of art
  written in terms of what you would observe rather than what the number
  does to the signal.
- A translation-catalogue test that fails the build when a language is
  missing a key, carries a stale one, or leaves a translation empty.

### Changed

- Every stock control — combo popups, scroll bars, check boxes, sliders,
  progress bars, tool tips — now follows the selected GameFlow theme.
  Several of the rules meant to do this had been silently matching
  nothing since the move to Avalonia 12.
- Disabled buttons now look disabled.
- The window's minimum size no longer exceeds a 1366x768 laptop panel in
  both axes, which used to open the shell partly off-screen with no way
  to drag it back.
- Filled in eight keys the code asked for that no language defined, and
  six keys missing from German, Spanish, French, Italian, Polish and
  Russian. These translations are unreviewed by native speakers.

### Known limitations

- The battery fix is verified against the HIDMaestro SDK's state surface
  but has not been confirmed against a game reading the emitted value.
- The firewall rule is registered for the executable's current path, so a
  build that moves needs the rule re-created from Options.
