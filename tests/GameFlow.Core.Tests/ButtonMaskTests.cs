using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using Xunit;

namespace GameFlow.Core.Tests;

/// <summary>
/// <see cref="ButtonMask"/> replaced a <c>Dictionary&lt;ButtonId, bool&gt;</c>
/// that the mapping pipeline rebuilt on every frame of every slot. These
/// tests pin the behaviour the dictionary provided, so the swap is a
/// representation change and nothing more.
/// </summary>
public sealed class ButtonMaskTests
{
    [Fact]
    public void Every_button_id_fits_in_the_mask()
    {
        // ButtonMask packs one bit per ButtonId into a uint. A 33rd button
        // would wrap and alias onto an existing one — "pressing Misc1 also
        // presses South" — so this is the check that has to fail in CI
        // rather than in someone's game.
        Assert.True(
            ButtonState.Count <= 32,
            $"ButtonId now has {ButtonState.Count} members; ButtonMask's uint backing field holds 32. " +
            "Widen it to ulong before adding more buttons.");
    }

    [Fact]
    public void A_default_mask_has_nothing_pressed()
    {
        var mask = default(ButtonMask);

        Assert.True(mask.IsEmpty);
        Assert.Equal(0, mask.PressedCount);
        Assert.Equal(ButtonMask.Empty, mask);

        foreach (var button in ButtonState.All)
        {
            Assert.False(mask[button]);
        }
    }

    [Fact]
    public void Every_button_round_trips_independently()
    {
        // Catches an aliasing bug in the bit indexing: setting one button
        // must never light up another.
        foreach (var button in ButtonState.All)
        {
            var mask = ButtonMask.Empty;
            mask[button] = true;

            Assert.True(mask[button]);
            Assert.Equal(1, mask.PressedCount);

            foreach (var other in ButtonState.All)
            {
                if (other != button)
                {
                    Assert.False(mask[other]);
                }
            }
        }
    }

    [Fact]
    public void Clearing_a_button_leaves_the_others_alone()
    {
        var mask = ButtonMask.Empty;
        mask[ButtonId.South] = true;
        mask[ButtonId.North] = true;

        mask[ButtonId.South] = false;

        Assert.False(mask[ButtonId.South]);
        Assert.True(mask[ButtonId.North]);
        Assert.Equal(1, mask.PressedCount);
    }

    [Fact]
    public void Copying_a_mask_copies_the_state()
    {
        // The dictionary this replaced was shared by reference, so a
        // snapshot could be mutated by whoever built it after the fact.
        // Value semantics are the point, and the pipeline relies on them:
        // it clones the physical buttons and writes the mapped result into
        // the clone.
        var original = ButtonMask.Empty;
        original[ButtonId.South] = true;

        var copy = original;
        copy[ButtonId.North] = true;

        Assert.False(original[ButtonId.North]);
        Assert.True(copy[ButtonId.North]);
    }

    [Fact]
    public void Or_is_the_union_used_when_merging_devices()
    {
        var stick = ButtonMask.Empty;
        stick[ButtonId.South] = true;

        var throttle = ButtonMask.Empty;
        throttle[ButtonId.LeftShoulder] = true;

        var merged = stick | throttle;

        Assert.True(merged[ButtonId.South]);
        Assert.True(merged[ButtonId.LeftShoulder]);
        Assert.Equal(2, merged.PressedCount);
    }

    [Fact]
    public void Xor_reports_what_changed_and_and_narrows_it_to_rises()
    {
        // This is how the shift-layer resolver detects activity: XOR gives
        // every transition, ANDing with the current state keeps only the
        // presses.
        var before = ButtonMask.Empty;
        before[ButtonId.South] = true;
        before[ButtonId.North] = true;

        var after = ButtonMask.Empty;
        after[ButtonId.North] = true;   // held
        after[ButtonId.West] = true;    // newly pressed
                                        // South released

        var changed = before ^ after;
        Assert.True(changed[ButtonId.South]);
        Assert.True(changed[ButtonId.West]);
        Assert.False(changed[ButtonId.North]);

        var rose = changed & after;
        Assert.True(rose[ButtonId.West]);
        Assert.False(rose[ButtonId.South]);
    }

    [Fact]
    public void Enumeration_yields_only_pressed_buttons_ascending()
    {
        var mask = ButtonMask.Empty;
        mask[ButtonId.North] = true;
        mask[ButtonId.South] = true;
        mask[ButtonId.Misc1] = true;

        var seen = new List<ButtonId>();
        foreach (var button in mask)
        {
            seen.Add(button);
        }

        Assert.Equal([ButtonId.South, ButtonId.North, ButtonId.Misc1], seen);
    }

    [Fact]
    public void An_empty_mask_enumerates_nothing()
    {
        foreach (var _ in ButtonMask.Empty)
        {
            Assert.Fail("An empty mask must yield no buttons.");
        }
    }

    [Fact]
    public void Equality_compares_the_pressed_set()
    {
        var a = ButtonMask.Empty;
        a[ButtonId.South] = true;

        var b = ButtonMask.Empty;
        b[ButtonId.South] = true;

        var c = ButtonMask.Empty;
        c[ButtonId.North] = true;

        Assert.True(a == b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != c);

        // Writing a button as false is not a change — the dictionary
        // version had to special-case "key present but false" against
        // "key absent"; here they are the same bit.
        var released = ButtonMask.Empty;
        released[ButtonId.North] = false;
        Assert.Equal(ButtonMask.Empty, released);
    }

    [Fact]
    public void None_is_carried_like_any_other_id()
    {
        // ButtonId.None is bit 0. The dictionary kept a None entry too, so
        // this stays representable rather than being silently dropped —
        // rules that leave a button unset use None as their sentinel and
        // must not accidentally toggle a real button.
        var mask = ButtonMask.Empty;
        mask[ButtonId.None] = true;

        Assert.True(mask[ButtonId.None]);
        Assert.False(mask[ButtonId.South]);
    }

    [Fact]
    public void ToString_names_the_pressed_buttons()
    {
        Assert.Equal("(none)", ButtonMask.Empty.ToString());

        var mask = ButtonMask.Empty;
        mask[ButtonId.South] = true;
        mask[ButtonId.Start] = true;

        Assert.Equal("South, Start", mask.ToString());
    }
}
