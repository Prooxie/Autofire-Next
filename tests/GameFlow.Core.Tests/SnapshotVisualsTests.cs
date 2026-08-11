using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using Xunit;

namespace GameFlow.Core.Tests;

/// <summary>
/// The repaint dirty check. Both directions matter: a false "changed"
/// repaints the whole theme tree for nothing many times a second, and a
/// false "identical" freezes live feedback in a way that looks exactly
/// like the input pipeline being broken.
/// </summary>
public sealed class SnapshotVisualsTests
{
    private static ControllerSnapshot Touching(params TouchContact[] contacts)
    {
        var first = contacts.Length > 0 ? contacts[0] : default;
        return ControllerSnapshot.Empty() with
        {
            TouchContacts = contacts,
            TouchContactCount = contacts.Length,
            TouchDown = contacts.Length > 0,
            TouchX = first.X,
            TouchY = first.Y,
        };
    }

    [Fact]
    public void AnUnchangedPadIsEquivalentSoItDoesNotRepaint()
    {
        Assert.True(SnapshotVisuals.AreEquivalent(
            ControllerSnapshot.Empty(), ControllerSnapshot.Empty()));
    }

    [Fact]
    public void ADraggedFingerIsNotEquivalent()
    {
        // The reported bug: putting a finger down painted once, then the
        // dot froze until it was lifted. Everything except the position
        // stays the same while dragging, so comparing counts alone said
        // "identical" for the whole gesture.
        var down = Touching(new TouchContact(0, 0.20f, 0.50f));
        var dragged = Touching(new TouchContact(0, 0.60f, 0.50f));

        Assert.False(SnapshotVisuals.AreEquivalent(down, dragged));
    }

    [Fact]
    public void ASecondFingerDraggingIsNotEquivalent()
    {
        // Same trap one level in: the count is stable at two, and only a
        // contact past the first moved.
        var a = Touching(new TouchContact(0, 0.2f, 0.5f), new TouchContact(1, 0.7f, 0.5f));
        var b = Touching(new TouchContact(0, 0.2f, 0.5f), new TouchContact(1, 0.7f, 0.9f));

        Assert.False(SnapshotVisuals.AreEquivalent(a, b));
    }

    [Fact]
    public void AFingerHeldPerfectlyStillIsEquivalent()
    {
        var a = Touching(new TouchContact(0, 0.35f, 0.65f));
        var b = Touching(new TouchContact(0, 0.35f, 0.65f));

        Assert.True(SnapshotVisuals.AreEquivalent(a, b));
    }

    [Fact]
    public void PressingHarderInPlaceIsNotEquivalent()
    {
        // Contact dots scale their radius with pressure.
        var light = Touching(new TouchContact(0, 0.5f, 0.5f, Pressure: 0.2f));
        var firm = Touching(new TouchContact(0, 0.5f, 0.5f, Pressure: 0.9f));

        Assert.False(SnapshotVisuals.AreEquivalent(light, firm));
    }

    [Fact]
    public void AFingerMovingToAnotherSlotIsNotEquivalent()
    {
        var a = Touching(new TouchContact(0, 0.5f, 0.5f));
        var b = Touching(new TouchContact(1, 0.5f, 0.5f));

        Assert.False(SnapshotVisuals.AreEquivalent(a, b));
    }

    [Fact]
    public void LiftingTheLastFingerIsNotEquivalent()
    {
        Assert.False(SnapshotVisuals.AreEquivalent(
            Touching(new TouchContact(0, 0.5f, 0.5f)), Touching()));
    }

    [Fact]
    public void MovementFinerThanTheSurfaceCanShowIsEquivalent()
    {
        // Otherwise sensor jitter alone repaints every tick, which is the
        // cost this check exists to avoid.
        var a = Touching(new TouchContact(0, 0.5000f, 0.5f));
        var b = Touching(new TouchContact(0, 0.5001f, 0.5f));

        Assert.True(SnapshotVisuals.AreEquivalent(a, b));
    }

    [Fact]
    public void AMovedStickIsNotEquivalent()
    {
        var still = ControllerSnapshot.Empty();
        var moved = still with { LeftStick = new StickVector(0.4f, 0f) };

        Assert.False(SnapshotVisuals.AreEquivalent(still, moved));
    }

    [Fact]
    public void APressedButtonIsNotEquivalent()
    {
        var up = ControllerSnapshot.Empty();
        var down = up with { Buttons = new Dictionary<ButtonId, bool> { [ButtonId.South] = true } };

        Assert.False(SnapshotVisuals.AreEquivalent(up, down));
    }

    [Fact]
    public void AnExplicitlyReleasedButtonMatchesAnAbsentOne()
    {
        // Producers disagree about whether to carry released keys at all,
        // and both draw the same thing.
        var absent = ControllerSnapshot.Empty() with { Buttons = new Dictionary<ButtonId, bool>() };
        var explicitFalse = ControllerSnapshot.Empty() with
        {
            Buttons = new Dictionary<ButtonId, bool> { [ButtonId.South] = false },
        };

        Assert.True(SnapshotVisuals.AreEquivalent(absent, explicitFalse));
    }

    [Fact]
    public void TimestampAloneIsNotAChange()
    {
        // The runtime allocates a fresh snapshot every tick; if this
        // returned false the dirty check would never save a single frame.
        var a = ControllerSnapshot.Empty();
        var b = a with { Timestamp = a.Timestamp.AddSeconds(5) };

        Assert.True(SnapshotVisuals.AreEquivalent(a, b));
    }
}
