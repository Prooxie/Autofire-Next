GameFlow 1.0.4 is a correctness release. Several features that looked
implemented turned out never to have reached the controller — adaptive
triggers, battery, and half the on-screen highlights — and this release
fixes the paths rather than the symptoms.

## Highlights

**Adaptive triggers now actually work.** The DualSense's extended trigger
effects (`0x21`, `0x25`, `0x26`) take a ten-bit mask of engaged zones plus
three bits of amplitude per zone, not a start byte and a strength byte.
GameFlow was writing simple parameters into that layout. Nothing failed —
the effect id, the enable bit and the write were all accepted — so the
resistance modes engaged an arbitrary handful of zones and felt roughly
plausible, which hid the problem. Vibration did not survive it: frequency
is the ninth parameter and was being written as the third, so the pad was
handed a buzz with no rate to buzz at and performed silence. Choosing
Multiple-position feedback, Slope feedback or Multiple-position vibration
also silently produced a different effect, because seven modes were being
narrowed onto four kinds and widened back again.

**Virtual controllers stay out of the way.** They enumerate under
`HID\HIDCLASS\…`, which matched none of the three signals GameFlow used to
recognise its own output, so they were published as physical hardware and
only withdrawn a beat later — the appear-then-vanish flicker. A related
bug could leave a real controller offline after a restart: a virtual pad
impersonates the real one down to vendor, product and name, so both hashed
to the same device id and the winner was decided by enumeration order.

**The dashboard is no longer stuck at 10 Hz.** Recovery from a throttle
was mathematically unreachable — the step always overshot the ceiling it
was tested against — so one stall while themes were still decoding pinned
the refresh rate for the rest of the session.

**Highlights take the shape of the control again.** Theme art decodes in
the background and the first request returns nothing; that nothing was
cached permanently. It landed on exactly the art an idle screen never
draws — the click overlays — so L1, R1, the touchpad, Options/Share and
the PS button fell back to a rectangle while everything else was correct.
The PS/Guide button was also unmappable outright: the hit-tester required
a colon in a binding name, and every Sony pack calls that button `home`.

**The setup guide performs the setup.** It now checks the pad's layout
against a live picture (with calibration available in place), asks for a
name, and finishes by showing your controller and the emitted one side by
side so the mapping can be confirmed by pressing something.

**Fewer places to look.** Preview, Devices & profiles and Mappings were
three tabs describing one controller — the mappings tab literally opened
by telling you to go back to Preview — and are now one page. Tuning moved
in with the virtual controller it tunes, instead of carrying a second slot
picker that could disagree with the first.

## Known limitation

Battery, motion and touch **cannot** pass through a USB Sony output
profile. They travel in a Sony report's extended section, and HIDMaestro
runs that codec on the input direction only for a profile that arms it —
`dualshock-4-v1-full` and `dualsense` arm nothing, while `dualsense-bt`
does. Whatever GameFlow submits is accepted and then never reaches the
wire. Choose a Bluetooth Sony profile if you need those to pass through;
GameFlow now warns when a profile that cannot carry them is deployed.

## Virtual controller support

Virtual controllers are provided by
[HIDMaestro](https://github.com/hifihedgehog/HIDMaestro)
([hidmaestro.org](https://hidmaestro.org)), a user-mode driver with a
catalogue of over 200 controller profiles. `HIDMaestro.Core.dll` must sit
beside the executable; without it GameFlow runs with no output backend and
says so.

## Install

Download the archive for your platform below and extract it. The builds
are self-contained — no .NET install required. On Windows, creating
virtual controllers requires running as Administrator.
