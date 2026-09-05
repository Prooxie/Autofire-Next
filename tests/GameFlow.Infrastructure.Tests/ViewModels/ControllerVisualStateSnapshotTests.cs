using GameFlow.App.ViewModels;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.ViewModels;

/// <summary>
/// What <c>ControllerVisualStateViewModel.Update</c> lets THROUGH.
///
/// <para>
/// The view-model is a gate, not just a store: when it decides a tick
/// changed nothing it returns without adopting the new snapshot, and
/// <c>RawSnapshot</c> keeps handing <c>ControllerSurface</c> the last one
/// it accepted. So a field the gate does not look at is a field the
/// artwork can never show moving, no matter how correct every layer
/// behind it is — the surface downstream compares a stale snapshot with
/// itself, finds them identical, and declines to repaint.
/// </para>
///
/// <para>
/// That is one bug wearing two faces. It was fixed once in the surface's
/// own dirty check (V5, the touchpad dot freezing mid-drag) while this
/// copy in front of it still counted fingers and nothing else, so the dot
/// froze again for the same reason one layer up. These tests assert on
/// <c>RawSnapshot</c> — what the surface actually receives — rather than
/// on any comparison helper, because that is the property the bug was
/// visible in.
/// </para>
/// </summary>
public sealed class ControllerVisualStateSnapshotTests
{
    private static ControllerVisualStateViewModel CreateViewModel() => new(_ => { });

    /// <summary>
    /// Pushes a tick the way <c>ShellViewModel</c> does. Panel id, title
    /// and style are held constant so only the snapshot can be
    /// responsible for a change being noticed — <c>identityChanged</c>
    /// and <c>styleChanged</c> would otherwise mask a gate that ignores
    /// the snapshot entirely.
    /// </summary>
    private static void Push(ControllerVisualStateViewModel vm, ControllerSnapshot snapshot) =>
        vm.Update("physical", "Physical", snapshot, ControllerVisualStyle.SimpleGamepad);

    private static ControllerSnapshot Touching(params TouchContact[] contacts) =>
        ControllerSnapshot.Empty("Pad").WithTouchContacts(contacts);

    [Fact]
    public void ADraggedFingerReachesTheSurface()
    {
        var vm = CreateViewModel();
        Push(vm, Touching(new TouchContact(0, 0.20f, 0.50f)));

        Push(vm, Touching(new TouchContact(0, 0.70f, 0.50f)));

        // One finger throughout, so the contact COUNT never changes —
        // this is exactly the drag that used to be swallowed.
        Assert.Equal(0.70f, vm.RawSnapshot.TouchX, 3);
    }

    [Fact]
    public void TheSecondFingerOfATwoFingerDragReachesTheSurface()
    {
        var vm = CreateViewModel();
        Push(vm, Touching(
            new TouchContact(0, 0.20f, 0.50f),
            new TouchContact(1, 0.60f, 0.50f)));

        // Only the non-primary finger moves. The primary contact — and
        // therefore TouchX/TouchY — is byte-identical, so a check that
        // looks no further than the primary point still freezes the
        // second dot.
        Push(vm, Touching(
            new TouchContact(0, 0.20f, 0.50f),
            new TouchContact(1, 0.60f, 0.90f)));

        Assert.Equal(0.90f, vm.RawSnapshot.TouchContacts[1].Y, 3);
    }

    [Fact]
    public void AFingerPressingHarderWithoutMovingReachesTheSurface()
    {
        var vm = CreateViewModel();
        Push(vm, Touching(new TouchContact(0, 0.40f, 0.40f, 0.20f)));

        // Contact dots scale their radius with pressure, so this is a
        // visible change with no positional one behind it.
        Push(vm, Touching(new TouchContact(0, 0.40f, 0.40f, 0.90f)));

        Assert.Equal(0.90f, vm.RawSnapshot.TouchContacts[0].Pressure, 3);
    }

    [Fact]
    public void AnIdenticalTickIsStillRejected()
    {
        var vm = CreateViewModel();
        var landed = Touching(new TouchContact(0, 0.33f, 0.66f));
        Push(vm, landed);

        // The gate's whole purpose. A resting finger streams identical
        // frames at the display rate, and adopting each one would
        // repaint the entire theme tree for nothing — the failure the
        // count-only check was over-correcting for. Timestamp differs
        // (Empty stamps UtcNow), which is precisely what must NOT count.
        var restated = Touching(new TouchContact(0, 0.33f, 0.66f));
        Push(vm, restated);

        Assert.Same(landed, vm.RawSnapshot);
    }

    [Fact]
    public void LiftingTheLastFingerReachesTheSurface()
    {
        var vm = CreateViewModel();
        Push(vm, Touching(new TouchContact(0, 0.50f, 0.50f)));

        Push(vm, ControllerSnapshot.Empty("Pad").WithTouchContacts([]));

        Assert.False(vm.RawSnapshot.TouchDown);
        Assert.Empty(vm.RawSnapshot.TouchContacts);
    }

    [Fact]
    public void AMovedStickStillReachesTheSurface()
    {
        var vm = CreateViewModel();
        Push(vm, ControllerSnapshot.Empty("Pad"));

        Push(vm, ControllerSnapshot.Empty("Pad").WithStick(StickId.Left, new StickVector(0.75f, -0.25f)));

        Assert.Equal(0.75f, vm.RawSnapshot.LeftStick.X, 3);
    }

    [Fact]
    public void APressedButtonStillReachesTheSurface()
    {
        var vm = CreateViewModel();
        Push(vm, ControllerSnapshot.Empty("Pad"));

        var buttons = ButtonMask.Empty;
        buttons[ButtonId.South] = true;
        Push(vm, ControllerSnapshot.Empty("Pad").WithButtons(buttons));

        Assert.True(vm.RawSnapshot.IsPressed(ButtonId.South));
    }
}
