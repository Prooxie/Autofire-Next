using GameFlow.Infrastructure.Runtime.Input;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input;

/// <summary>
/// Covers the suppression of the phantom Left Control that Windows
/// injects ahead of every AltGr press on layouts that have an AltGr key.
/// </summary>
public sealed class AltGrGhostFilterTests
{
    private const int LeftControl = AltGrGhostFilter.LeftControl;
    private const int RightAlt = AltGrGhostFilter.RightAlt;
    private const int KeyA = 0x41;

    /// <summary>A clock the test advances by hand, so the pairing window is exact rather than timing-dependent.</summary>
    private sealed class FakeClock
    {
        public long Ticks;
        public long Read() => Ticks;
        public void Advance(TimeSpan by) => Ticks += by.Ticks;
    }

    [Fact]
    public void AltGr_press_reports_only_right_alt()
    {
        var filter = new AltGrGhostFilter();

        // Exactly what Raw Input delivers for one AltGr press.
        filter.Apply(LeftControl, keyUp: false);
        filter.Apply(RightAlt, keyUp: false);

        Assert.Equal([RightAlt], filter.Snapshot());
    }

    [Fact]
    public void AltGr_release_clears_everything()
    {
        var filter = new AltGrGhostFilter();

        filter.Apply(LeftControl, keyUp: false);
        filter.Apply(RightAlt, keyUp: false);
        filter.Apply(LeftControl, keyUp: true);
        filter.Apply(RightAlt, keyUp: true);

        Assert.Empty(filter.Snapshot());
    }

    [Fact]
    public void Auto_repeat_while_alt_gr_is_held_does_not_resurrect_control()
    {
        var clock = new FakeClock();
        var filter = new AltGrGhostFilter(clock.Read);

        filter.Apply(LeftControl, keyUp: false);
        filter.Apply(RightAlt, keyUp: false);

        // Held down: Windows repeats the whole injected pair, and the
        // repeats arrive far apart enough to fall outside the pairing
        // window, so they must be suppressed by the latched flag instead.
        for (var repeat = 0; repeat < 5; repeat++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(33));
            filter.Apply(LeftControl, keyUp: false);
            filter.Apply(RightAlt, keyUp: false);
        }

        Assert.Equal([RightAlt], filter.Snapshot());
    }

    [Fact]
    public void Real_control_held_before_alt_gr_survives()
    {
        var clock = new FakeClock();
        var filter = new AltGrGhostFilter(clock.Read);

        filter.Apply(LeftControl, keyUp: false);

        // A person reaching for Alt after Ctrl takes much longer than the
        // burst Windows injects, so this is a genuine Ctrl+AltGr chord.
        clock.Advance(AltGrGhostFilter.PairingWindow + TimeSpan.FromMilliseconds(1));
        filter.Apply(RightAlt, keyUp: false);

        Assert.Equal([LeftControl, RightAlt], filter.Snapshot().OrderBy(k => k));
    }

    [Fact]
    public void Control_alone_is_untouched()
    {
        var filter = new AltGrGhostFilter();

        filter.Apply(LeftControl, keyUp: false);
        filter.Apply(KeyA, keyUp: false);

        Assert.Equal([KeyA, LeftControl], filter.Snapshot().OrderBy(k => k));
    }

    [Fact]
    public void Control_works_again_after_alt_gr_is_released()
    {
        var filter = new AltGrGhostFilter();

        filter.Apply(LeftControl, keyUp: false);
        filter.Apply(RightAlt, keyUp: false);
        filter.Apply(LeftControl, keyUp: true);
        filter.Apply(RightAlt, keyUp: true);

        // A deliberate Ctrl press after the AltGr episode must not be
        // eaten by a stale suppression flag.
        filter.Apply(LeftControl, keyUp: false);

        Assert.Equal([LeftControl], filter.Snapshot());
    }

    [Fact]
    public void Right_alt_without_a_preceding_control_is_reported_normally()
    {
        var filter = new AltGrGhostFilter();

        // US layouts have a plain Right Alt and inject nothing.
        filter.Apply(RightAlt, keyUp: false);

        Assert.Equal([RightAlt], filter.Snapshot());
    }
}
