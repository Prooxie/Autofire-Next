GameFlow 1.0.5 comes out of one user's log. A PS2 pad behind a PS2-to-PS3
converter had its right stick mapped as a trigger, and chasing that turned
up two more things that were broken in the same area — one of them silent.

## Highlights

**Sticks and triggers can now be calibrated.** Per-device calibration
covered buttons and hats, and an axis is not a button, so a pad whose
analog controls arrive on the wrong axes could not be corrected at all.
The converter presents a DualShock 3's VID/PID, so SDL applies the
DualShock 3 mapping — and where the converter's wiring differs, the right
stick lands on the axes that mapping calls the triggers. Moving the stick
pulled L2. The stick itself read dead.

There was no way around this at the profile level either: a trigger source
is clamped to 0..1, so a stick arriving on one lost its whole negative half
before any mapping rule could see it. Half the stick was gone at the source.

Six prompts, two per stick and one per trigger. Two per stick because a
stick is two independent axes on the wire, and an adapter that scrambles
the order rarely moves X and Y together. Each prompt asks for the positive
direction — right, up, or fully pulled — so the direction you push decides
the orientation and there is no separate "is it inverted?" question. Where
an axis rests decides its travel shape, which is the only thing that can
tell a trigger idling at one extreme from a stick idling at centre.

**Triggers that are just switches now report a real value.** Most PS2
converters have no analog trigger to bind. Press the button at a trigger
prompt and it binds as an on/off trigger — full pressed, zero released —
so a game reading the analog axis gets something instead of nothing. An
analog trigger closes its digital button early in the pull, so a button
press waits a moment to see whether an axis follows; the axis wins if it
does, rather than a pressure-sensitive L2 being quietly demoted to a
switch.

**A DualShock 3 drew no controller at all.** GameFlow ships no PlayStation 3
theme pack, and a style with no pack rendered an empty panel — the same was
true of the legacy generic Xbox style that older settings still carry. Both
now draw with the closest family member that has one. It only ever replaces
*nothing*: a style with its own themes never reaches a stand-in, so removing
every DualShock 4 skin still tells you to install a theme rather than quietly
showing you a DualSense.

**The setup guide's device list silently stopped updating.** Catalog changes
arrive on the SDL worker thread, and the guide was mutating UI state straight
from it. The dispatcher check threw, the worker's keep-going handler swallowed
it as a bare "SDL worker tick failed; continuing.", and every controller
plugged in or unplugged while the guide was open went unnoticed.

## Notes

Both calibration passes are also offered inside the setup guide now — a pad
whose stick lands on the wrong axis is exactly the pad being set up for the
first time.

Existing `device-button-maps.json` files load unchanged. The two passes merge
into one map, so re-running one does not drop the other's work.

GameFlow still does not hide your physical controller from games, and has no
HidHide integration. If you use HidHide, whitelist `GameFlow.App.exe` — or
GameFlow loses the pad along with everything else.
