using GameFlow.Infrastructure.Runtime.Input;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input;

/// <summary>
/// Rising-edge capture for the calibration wizard.
///
/// <para>
/// The hat cases are the reason this exists. A D-pad is a hat on nearly
/// every gamepad, the wizard only watched buttons, and pressing the D-pad
/// therefore did nothing at all — the wizard just never advanced, which
/// reads as the D-pad being unrecognized rather than as the wizard not
/// listening.
/// </para>
/// </summary>
public sealed class ButtonCaptureTests
{
    private const byte Up = 0x01;
    private const byte Right = 0x02;
    private const byte Down = 0x04;
    private const byte Left = 0x08;

    private static HashSet<int> Buttons(params int[] indices) => [.. indices];

    [Fact]
    public void NothingPressedCapturesNothing()
    {
        Assert.Null(ButtonCapture.Detect(Buttons(), Buttons(), [0], [0]));
    }

    [Fact]
    public void ANewlyPressedButtonIsCaptured()
    {
        var captured = ButtonCapture.Detect(Buttons(), Buttons(3), [], []);

        Assert.Equal(3, captured?.ButtonIndex);
    }

    [Fact]
    public void AButtonAlreadyHeldIsNotCaptured()
    {
        // Otherwise whatever the user was resting a finger on is captured
        // for the first prompt the instant the wizard starts.
        Assert.Null(ButtonCapture.Detect(Buttons(3), Buttons(3), [], []));
    }

    [Fact]
    public void APressedHatDirectionIsCaptured()
    {
        var captured = ButtonCapture.Detect(Buttons(), Buttons(), [0], [Up]);

        Assert.Equal(new HatDirectionBinding(0, Up), captured?.Hat);
        Assert.Null(captured?.ButtonIndex);
    }

    [Fact]
    public void EachHatDirectionIsCapturedSeparately()
    {
        Assert.Equal(new HatDirectionBinding(0, Right), ButtonCapture.Detect(Buttons(), Buttons(), [0], [Right])?.Hat);
        Assert.Equal(new HatDirectionBinding(0, Down), ButtonCapture.Detect(Buttons(), Buttons(), [0], [Down])?.Hat);
        Assert.Equal(new HatDirectionBinding(0, Left), ButtonCapture.Detect(Buttons(), Buttons(), [0], [Left])?.Hat);
    }

    [Fact]
    public void ADiagonalCapturesOneDirectionOnly()
    {
        // Rolling a thumb onto the D-pad can set two bits in one tick.
        // Binding "D-pad Up" to up+right would then never match a clean
        // press of up on its own.
        var captured = ButtonCapture.Detect(Buttons(), Buttons(), [0], [(byte)(Up | Right)]);

        Assert.Equal(new HatDirectionBinding(0, Up), captured?.Hat);
    }

    [Fact]
    public void AHatReturningToCentreCapturesNothing()
    {
        Assert.Null(ButtonCapture.Detect(Buttons(), Buttons(), [Up], [0]));
    }

    [Fact]
    public void MovingFromOneDirectionToAnotherCapturesTheNewOne()
    {
        var captured = ButtonCapture.Detect(Buttons(), Buttons(), [Up], [Down]);

        Assert.Equal(new HatDirectionBinding(0, Down), captured?.Hat);
    }

    [Fact]
    public void TheCorrectHatIndexIsReported()
    {
        var captured = ButtonCapture.Detect(Buttons(), Buttons(), [0, 0], [0, Left]);

        Assert.Equal(new HatDirectionBinding(1, Left), captured?.Hat);
    }

    [Fact]
    public void AHatAppearingMidCalibrationIsTreatedAsCentredBefore()
    {
        // Hot-plugged, or simply a first sample taken before the hat list
        // was populated. Indexing past the old list must not throw.
        var captured = ButtonCapture.Detect(Buttons(), Buttons(), [], [Up]);

        Assert.Equal(new HatDirectionBinding(0, Up), captured?.Hat);
    }

    [Fact]
    public void IsPressedMatchesTheBoundDirection()
    {
        var binding = new HatDirectionBinding(0, Down);

        Assert.True(ButtonCapture.IsPressed(binding, [Down]));
        Assert.True(ButtonCapture.IsPressed(binding, [(byte)(Down | Right)]));   // diagonal still counts
        Assert.False(ButtonCapture.IsPressed(binding, [Up]));
        Assert.False(ButtonCapture.IsPressed(binding, [0]));
    }

    [Fact]
    public void IsPressedIsSafeWhenTheHatIsGone()
    {
        // A saved map can outlive the device layout it was captured on.
        Assert.False(ButtonCapture.IsPressed(new HatDirectionBinding(4, Up), [0]));
        Assert.False(ButtonCapture.IsPressed(new HatDirectionBinding(0, Up), []));
        Assert.False(ButtonCapture.IsPressed(new HatDirectionBinding(-1, Up), [Up]));
    }

    [Fact]
    public void AnEmptyMapIsReportedEmptySoTheReadPathSkipsIt()
    {
        Assert.True(new DeviceButtonMap().IsEmpty);
        Assert.False(new DeviceButtonMap { Hats = { [Core.Enums.ButtonId.DpadUp] = new(0, Up) } }.IsEmpty);
    }

    [Fact]
    public void CloneCarriesTheHatBindings()
    {
        var map = new DeviceButtonMap { Hats = { [Core.Enums.ButtonId.DpadLeft] = new(0, Left) } };

        var clone = map.Clone();
        clone.Hats[Core.Enums.ButtonId.DpadRight] = new(0, Right);

        Assert.Single(map.Hats);
        Assert.Equal(2, clone.Hats.Count);
    }
}
