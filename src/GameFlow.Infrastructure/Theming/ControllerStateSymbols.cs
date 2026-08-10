using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Theming.Flee;

namespace GameFlow.Infrastructure.Theming;

/// <summary>
/// Adapter that exposes a <see cref="ControllerSnapshot"/> through the
/// VSCView Flee variable namespace described in
/// <see href="https://github.com/Nielk1/VSCView/blob/master/THEMEENGINE.md">THEMEENGINE.md</see>.
///
/// <para>
/// Variable names are <b>colon-qualified</b> in VSCView themes:
/// </para>
/// <list type="bullet">
/// <item><c>quad_right:s</c>, <c>quad_right:e</c>, <c>quad_right:w</c>, <c>quad_right:n</c> — face buttons (A/B/X/Y for Xbox, ✕/○/□/△ for PlayStation, mapped to <see cref="ButtonId.South"/> etc.);</item>
/// <item><c>quad_left:s</c>, <c>quad_left:e</c>, <c>quad_left:w</c>, <c>quad_left:n</c> — D-pad;</item>
/// <item><c>bumpers:l</c>, <c>bumpers:r</c> — shoulder buttons (LB/RB or L1/R1);</item>
/// <item><c>bumpers2:l</c>, <c>bumpers2:r</c> — trigger-button digital state (LeftTriggerButton / RightTriggerButton);</item>
/// <item><c>triggers:l:analog</c>, <c>triggers:r:analog</c> — analog trigger pull, 0..1;</item>
/// <item><c>triggers:l:stage2</c>, <c>triggers:r:stage2</c> — Steam-controller trigger second stage (always 0 on non-SC hardware);</item>
/// <item><c>menu:l</c>, <c>menu:r</c> — Back/Select and Start/Options;</item>
/// <item><c>home:home</c> — Guide / PS / Xbox button. Also accepted bare as <c>home</c> for the legacy spelling;</item>
/// <item><c>mute:mute</c> — DualSense mute (no <see cref="ButtonId"/> entry yet, always 0);</item>
/// <item><c>stick_left:x</c>, <c>stick_left:y</c>, <c>stick_left:click</c> — left analog stick;</item>
/// <item><c>stick_right:x</c>, <c>stick_right:y</c>, <c>stick_right:click</c> — right analog stick;</item>
/// <item><c>touch_center:click</c>, <c>touch_center:N:touch</c>, <c>touch_center:N:x</c>, <c>touch_center:N:y</c> — DualShock 4 / DualSense touchpad with finger index N (0 or 1).</item>
/// </list>
///
/// <para>
/// Unknown names resolve to <c>0</c>. This matches VSCView's behaviour of
/// silently ignoring formulas that mention controls the current hardware
/// doesn't have (e.g. a DualSense-only <c>mute</c> reference on a
/// DualShock 4 theme): missing inputs render as their off-state instead
/// of crashing the whole controller surface.
/// </para>
///
/// <para>
/// The resolver is a struct so that swapping the snapshot per render
/// tick costs only a few words of stack space — no heap allocation per
/// frame. The <see cref="Snapshot"/> reference field is reassigned
/// in-place by <see cref="UpdateSnapshot"/>.
/// </para>
/// </summary>
public sealed class ControllerStateSymbols : IFleeSymbols
{
    /// <summary>
    /// The snapshot whose values the resolver reports. May be reassigned
    /// per tick via <see cref="UpdateSnapshot"/> so a single symbol-table
    /// instance can be reused across frames; no allocation on the hot path.
    /// </summary>
    public ControllerSnapshot Snapshot { get; private set; } = ControllerSnapshot.Empty();

    /// <summary>
    /// Replaces the active snapshot. Returns <see langword="this"/> to
    /// support fluent reuse: <c>symbols.UpdateSnapshot(s).Resolve("x")</c>.
    /// </summary>
    public ControllerStateSymbols UpdateSnapshot(ControllerSnapshot snapshot)
    {
        Snapshot = snapshot;
        return this;
    }

    /// <inheritdoc/>
    public double Resolve(string name)
    {
        // Hot path: switch on the first colon segment. The whole switch
        // is small enough that the JIT compiles it to a hash table, and
        // we never allocate (ReadOnlySpan over the original string).
        var span = name.AsSpan();
        var firstColon = span.IndexOf(':');
        var head = firstColon < 0 ? span : span[..firstColon];
        var rest = firstColon < 0 ? ReadOnlySpan<char>.Empty : span[(firstColon + 1)..];

        // Bare "home" / "mute" are accepted as aliases per VSCView legacy.
        if (head.Equals("home", StringComparison.Ordinal) && rest.IsEmpty)
        {
            return ButtonValue(ButtonId.Guide);
        }
        if (head.Equals("mute", StringComparison.Ordinal) && rest.IsEmpty)
        {
            return 0;
        }

        if (head.Equals("quad_right", StringComparison.Ordinal))
        {
            return ResolveButton(rest, ButtonId.South, ButtonId.East, ButtonId.West, ButtonId.North);
        }
        if (head.Equals("quad_left", StringComparison.Ordinal))
        {
            return ResolveButton(rest, ButtonId.DpadDown, ButtonId.DpadRight, ButtonId.DpadLeft, ButtonId.DpadUp);
        }
        if (head.Equals("bumpers", StringComparison.Ordinal))
        {
            return ResolveLeftRightButton(rest, ButtonId.LeftShoulder, ButtonId.RightShoulder);
        }
        if (head.Equals("bumpers2", StringComparison.Ordinal))
        {
            return ResolveLeftRightButton(rest, ButtonId.LeftTriggerButton, ButtonId.RightTriggerButton);
        }
        if (head.Equals("menu", StringComparison.Ordinal))
        {
            return ResolveLeftRightButton(rest, ButtonId.Back, ButtonId.Start);
        }
        if (head.Equals("home", StringComparison.Ordinal))
        {
            return rest.Equals("home", StringComparison.Ordinal) ? ButtonValue(ButtonId.Guide) : 0;
        }
        if (head.Equals("mute", StringComparison.Ordinal))
        {
            // No ButtonId entry today; reserved for when the snapshot
            // model adds DualSense mute support.
            return 0;
        }
        if (head.Equals("triggers", StringComparison.Ordinal))
        {
            return ResolveTrigger(rest);
        }
        // key:<name> — any keyboard key by name (key:a, key:f1, key:lshift,
        // key:space...). Reads the raw pressed-key set, so a full-layout
        // keyboard theme can light every key rather than only the handful
        // that happen to map onto a ButtonId.
        if (head.Equals("key", StringComparison.Ordinal))
        {
            return ResolveKey(rest);
        }

        if (head.Equals("stick_left", StringComparison.Ordinal))
        {
            return ResolveStick(rest, Snapshot.LeftStick, ButtonId.LeftStick);
        }
        if (head.Equals("stick_right", StringComparison.Ordinal))
        {
            return ResolveStick(rest, Snapshot.RightStick, ButtonId.RightStick);
        }
        if (head.Equals("touch_center", StringComparison.Ordinal))
        {
            return ResolveTouch(rest);
        }
        if (head.Equals("touch_left", StringComparison.Ordinal) ||
            head.Equals("touch_right", StringComparison.Ordinal))
        {
            // VSCView exposes separate left/right touch surfaces for a
            // handful of older devices. GameFlow has one unified touchpad,
            // represented by touch_center; resolving the aliases as well
            // would make the compatibility terms in DS4/DualSense themes
            // count the same finger twice and displace its marker.
            return 0;
        }
        if (head.Equals("grip", StringComparison.Ordinal))
        {
            // Steam-controller paddles map to Paddle1 / Paddle2 in our snapshot.
            return ResolveLeftRightButton(rest, ButtonId.Paddle1, ButtonId.Paddle2);
        }
        if (head.Equals("motion", StringComparison.Ordinal))
        {
            // The snapshot model has no gyro/accel fields yet. Returning 0
            // keeps "motion:gyro_active" style toggles in their off state
            // until a future pass adds real IMU plumbing.
            return 0;
        }

        // Unknown name — silently return 0 so a theme written for a
        // richer controller still works on simpler hardware.
        return 0;
    }

    /// <summary>
    /// Returns 1 when <paramref name="button"/> is currently pressed,
    /// 0 otherwise. Centralised so the snapshot's
    /// <see cref="ControllerSnapshot.IsPressed"/> coercion stays in one
    /// place and the call-site reads cleanly.
    /// </summary>
    private double ButtonValue(ButtonId button) => Snapshot.IsPressed(button) ? 1 : 0;

    /// <summary>
    /// Resolves a single-letter direction inside a <c>quad_*</c> bag.
    /// VSCView convention: <c>s/e/w/n</c> = south/east/west/north, where
    /// "south" is the bottom and "north" is the top (visual convention,
    /// independent of the controller layout).
    /// </summary>
    private double ResolveButton(ReadOnlySpan<char> tail, ButtonId south, ButtonId east, ButtonId west, ButtonId north)
    {
        if (tail.Equals("s", StringComparison.Ordinal)) { return ButtonValue(south); }
        if (tail.Equals("e", StringComparison.Ordinal)) { return ButtonValue(east); }
        if (tail.Equals("w", StringComparison.Ordinal)) { return ButtonValue(west); }
        if (tail.Equals("n", StringComparison.Ordinal)) { return ButtonValue(north); }
        return 0;
    }

    /// <summary>
    /// Resolves the <c>l</c>/<c>r</c> suffix of a paired-button bag.
    /// Drops the optional "click" suffix that some themes append for
    /// readability — VSCView accepts both <c>bumpers:l</c> and
    /// <c>bumpers:l:click</c> as the same press signal.
    /// </summary>
    private double ResolveLeftRightButton(ReadOnlySpan<char> tail, ButtonId left, ButtonId right)
    {
        // Strip a trailing ":click" / ":press" before comparing so themes
        // that name the access explicitly still resolve to the same data.
        var dropClick = tail.LastIndexOf(':');
        var key = dropClick < 0 ? tail : tail[..dropClick];

        if (key.Equals("l", StringComparison.Ordinal)) { return ButtonValue(left); }
        if (key.Equals("r", StringComparison.Ordinal)) { return ButtonValue(right); }
        return 0;
    }

    private double ResolveTrigger(ReadOnlySpan<char> tail)
    {
        // Format is "l:analog", "r:stage2", etc. Split on the colon.
        var sep = tail.IndexOf(':');
        if (sep < 0) { return 0; }
        var side = tail[..sep];
        var axis = tail[(sep + 1)..];

        if (axis.Equals("analog", StringComparison.Ordinal))
        {
            if (side.Equals("l", StringComparison.Ordinal)) { return Snapshot.LeftTrigger; }
            if (side.Equals("r", StringComparison.Ordinal)) { return Snapshot.RightTrigger; }
        }
        if (axis.Equals("stage2", StringComparison.Ordinal))
        {
            // Stage-2 is a Steam Controller concept (the hard click past
            // the analog pull). Approximated as "trigger > 0.95" so themes
            // using stage2 still light up when the user pulls the trigger
            // all the way on non-SC hardware.
            if (side.Equals("l", StringComparison.Ordinal)) { return Snapshot.LeftTrigger  > 0.95f ? 1 : 0; }
            if (side.Equals("r", StringComparison.Ordinal)) { return Snapshot.RightTrigger > 0.95f ? 1 : 0; }
        }
        return 0;
    }

    /// <summary>
    /// Maps a theme's key name to its Windows virtual-key code. Names are
    /// the lowercase forms a theme author would reasonably write; the
    /// table is the single place a full-layout keyboard theme and the
    /// renderer agree on spelling.
    /// </summary>
    private static readonly Dictionary<string, int> KeyNameToVirtualKey = BuildKeyNames();

    private static Dictionary<string, int> BuildKeyNames()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var c = 'a'; c <= 'z'; c++)
        {
            map[c.ToString()] = 0x41 + (c - 'a');       // VK_A..VK_Z
        }
        for (var d = 0; d <= 9; d++)
        {
            map[d.ToString()] = 0x30 + d;               // VK_0..VK_9
        }
        for (var f = 1; f <= 12; f++)
        {
            map["f" + f] = 0x70 + (f - 1);              // VK_F1..VK_F12
        }

        map["escape"] = 0x1B; map["esc"] = 0x1B;
        map["tab"] = 0x09; map["capslock"] = 0x14;
        map["lshift"] = 0xA0; map["rshift"] = 0xA1;
        map["lctrl"] = 0xA2; map["rctrl"] = 0xA3;
        map["lalt"] = 0xA4; map["ralt"] = 0xA5;
        map["lwin"] = 0x5B; map["rwin"] = 0x5C; map["menu"] = 0x5D;
        map["space"] = 0x20; map["enter"] = 0x0D; map["backspace"] = 0x08;
        map["insert"] = 0x2D; map["delete"] = 0x2E;
        map["home"] = 0x24; map["end"] = 0x23;
        map["pageup"] = 0x21; map["pagedown"] = 0x22;
        map["up"] = 0x26; map["down"] = 0x28; map["left"] = 0x25; map["right"] = 0x27;
        map["printscreen"] = 0x2C; map["scrolllock"] = 0x91; map["pause"] = 0x13;
        map["numlock"] = 0x90;

        map["minus"] = 0xBD; map["equals"] = 0xBB;
        map["lbracket"] = 0xDB; map["rbracket"] = 0xDD;
        map["semicolon"] = 0xBA; map["apostrophe"] = 0xDE;
        map["grave"] = 0xC0; map["backslash"] = 0xDC;
        map["comma"] = 0xBC; map["period"] = 0xBE; map["slash"] = 0xBF;

        for (var n = 0; n <= 9; n++)
        {
            map["num" + n] = 0x60 + n;                  // VK_NUMPAD0..9
        }
        map["nummultiply"] = 0x6A; map["numadd"] = 0x6B;
        map["numsubtract"] = 0x6D; map["numdecimal"] = 0x6E;
        map["numdivide"] = 0x6F; map["numenter"] = 0x0D;

        return map;
    }

    private double ResolveKey(ReadOnlySpan<char> tail)
    {
        if (tail.IsEmpty || Snapshot.PressedKeys.Count == 0)
        {
            return 0;
        }

        // Numeric form (key:65) is accepted too, so a theme can address a
        // key this table hasn't named.
        if (int.TryParse(tail, out var directVk))
        {
            return Snapshot.PressedKeys.Contains(directVk) ? 1 : 0;
        }

        return KeyNameToVirtualKey.TryGetValue(tail.ToString(), out var vk)
            && Snapshot.PressedKeys.Contains(vk) ? 1 : 0;
    }

    private double ResolveStick(ReadOnlySpan<char> tail, StickVector stick, ButtonId clickButton)
    {
        if (tail.Equals("x", StringComparison.Ordinal))     { return stick.X; }
        if (tail.Equals("y", StringComparison.Ordinal))
        {
            // VSCView's Y axis is positive-down (screen coordinates) but
            // our StickVector follows the Steam Input convention where
            // positive Y is up. Flip the sign here so themes drawn for
            // VSCView don't render their stick "upside down".
            return -stick.Y;
        }
        if (tail.Equals("click", StringComparison.Ordinal)) { return ButtonValue(clickButton); }
        return 0;
    }

    private double ResolveTouch(ReadOnlySpan<char> tail)
    {
        if (tail.Equals("click", StringComparison.Ordinal))
        {
            return Snapshot.IsPressed(ButtonId.Touchpad) ? 1 : 0;
        }

        // Finger-index axes — "0:touch", "0:x", "0:y", "1:touch", ...
        var sep = tail.IndexOf(':');
        if (sep < 0) { return 0; }
        var index = tail[..sep];
        var axis  = tail[(sep + 1)..];

        if (!int.TryParse(index, out var fingerIndex)) { return 0; }

        // VSCView exposes ordinal display slots (first/second contact),
        // while browser touch identifiers can be arbitrary values such as
        // 2 and 7. The snapshot list is already stable-sorted, so indexing
        // it by ordinal renders every source without changing the original
        // identifiers used by gesture recognition.
        TouchContact? contact = fingerIndex >= 0 && fingerIndex < Snapshot.TouchContacts.Count
            ? Snapshot.TouchContacts[fingerIndex]
            : null;

        if (axis.Equals("touch", StringComparison.Ordinal))
        {
            // Some sources can report only a contact count. Preserve that
            // useful fallback when no per-finger list is available.
            return Snapshot.TouchContacts.Count > 0
                ? contact.HasValue ? 1 : 0
                : Snapshot.TouchContactCount > fingerIndex ? 1 : 0;
        }

        if (axis.Equals("x", StringComparison.Ordinal) || axis.Equals("y", StringComparison.Ordinal))
        {
            float? coordinate = contact.HasValue
                ? axis.Equals("x", StringComparison.Ordinal) ? contact.Value.X : contact.Value.Y
                : fingerIndex == 0 && Snapshot.TouchDown
                    ? axis.Equals("x", StringComparison.Ordinal) ? Snapshot.TouchX : Snapshot.TouchY
                    : null;

            // Snapshot coordinates follow SDL (0..1); VSCView themes
            // position touch markers around their centre (-1..1).
            return coordinate.HasValue
                ? Math.Clamp(coordinate.Value, 0f, 1f) * 2d - 1d
                : 0;
        }
        return 0;
    }
}
