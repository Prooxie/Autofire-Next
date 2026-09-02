# Changelog

Notable changes per release. The `release` job in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) reads the section
matching the tag it is building and uses it as the release body, with
GitHub's auto-generated commit list appended below it.

Format: one `## vX.Y.Z` heading per release, newest first. The heading
text must match the tag exactly — that is what the extraction keys on.

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
