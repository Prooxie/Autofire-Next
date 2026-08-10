using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Motion;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Motion;

public sealed class DsuSnapshotMapperMotionTests
{
    private static ControllerSnapshot WithMotion(
        float gyroPitch = 0f, float gyroYaw = 0f, float gyroRoll = 0f,
        float accelX = 0f, float accelY = 0f, float accelZ = 0f,
        bool hasGyro = true)
    {
        return new ControllerSnapshot
        {
            GyroPitch = gyroPitch,
            GyroYaw = gyroYaw,
            GyroRoll = gyroRoll,
            AccelX = accelX,
            AccelY = accelY,
            AccelZ = accelZ,
            HasGyro = hasGyro
        };
    }

    private static DsuControllerData Map(ControllerSnapshot snapshot) =>
        DsuSnapshotMapper.ToControllerData(snapshot, slot: 0, packetNumber: 1, timestampMicroseconds: 0);

    [Fact]
    public void GyroIsConvertedFromRadiansToDegrees()
    {
        // The snapshot is in rad/s (SDL's contract) and the wire format is
        // deg/s. Getting this wrong is not a subtle scale error — it is a
        // factor of ~57, which in-game reads as gyro aiming that barely
        // responds, and it would look like a sensitivity problem rather
        // than a unit bug.
        var data = Map(WithMotion(gyroPitch: MathF.PI));

        Assert.Equal(180f, data.GyroPitch, precision: 2);
    }

    [Fact]
    public void EachGyroAxisConvertsIndependentlyAndKeepsItsSign()
    {
        var data = Map(WithMotion(gyroPitch: 1f, gyroYaw: -2f, gyroRoll: 0.5f));

        Assert.Equal(57.2957795f, data.GyroPitch, precision: 3);
        Assert.Equal(-114.591559f, data.GyroYaw, precision: 3);
        Assert.Equal(28.6478897f, data.GyroRoll, precision: 3);
    }

    [Fact]
    public void AccelerometerIsConvertedFromMetresPerSecondSquaredToG()
    {
        // A pad sitting still reads the reaction to gravity, ≈9.81 m/s².
        // Clients expect that to arrive as ≈1.0 g and use it to work out
        // which way is down; forwarding 9.81 would tell them the pad is
        // under ten times gravity.
        var data = Map(WithMotion(accelY: 9.80665f));

        Assert.Equal(1f, data.AccelY, precision: 4);
    }

    [Fact]
    public void MotionIsZeroedOnAPadWithNoSensor()
    {
        // HasGyro exists precisely to separate "not moving" from "no
        // hardware". A pad without a sensor must not emit whatever
        // happens to be sitting in those fields.
        var data = Map(WithMotion(gyroYaw: 5f, accelZ: 9.8f, hasGyro: false));

        Assert.Equal(0f, data.GyroYaw);
        Assert.Equal(0f, data.AccelZ);
        Assert.Equal(DsuDeviceModel.PartialGyro, data.Device.Model);
    }

    [Fact]
    public void APadWithASensorAdvertisesFullGyro()
    {
        Assert.Equal(DsuDeviceModel.FullGyro, Map(WithMotion()).Device.Model);
    }

    [Fact]
    public void FastFlicksAreNotClamped()
    {
        // Same reasoning WebControllerProtocol applies to phone motion: a
        // real flick legitimately exceeds any bound we might pick, and
        // clamping would silently cap genuine input.
        var data = Map(WithMotion(gyroYaw: 40f)); // ~2292 deg/s

        Assert.True(data.GyroYaw > 2000f);
    }
}

public sealed class DsuSnapshotMapperStickTests
{
    [Fact]
    public void CentredStickIsExactly128()
    {
        // 128 and not 127: a neutral stick that does not land on a single
        // agreed value is drift the user cannot tune out.
        Assert.Equal(128, DsuSnapshotMapper.ToStickByte(0f));
    }

    [Theory]
    [InlineData(1f, 255)]
    [InlineData(-1f, 1)]
    [InlineData(0.5f, 192)]
    [InlineData(-0.5f, 64)]
    public void StickAxisScalesAcrossTheFullByteRange(float value, int expected)
    {
        Assert.Equal(expected, DsuSnapshotMapper.ToStickByte(value));
    }

    [Fact]
    public void OutOfRangeStickValuesAreClampedNotWrapped()
    {
        // A byte cast of an out-of-range value would wrap, turning a
        // hard-right push into a hard-left one. Out of range must land on
        // the same value as the corresponding extreme.
        Assert.Equal(DsuSnapshotMapper.ToStickByte(1f), DsuSnapshotMapper.ToStickByte(99f));
        Assert.Equal(DsuSnapshotMapper.ToStickByte(-1f), DsuSnapshotMapper.ToStickByte(-99f));
    }

    [Fact]
    public void FullDeflectionIsSymmetricAboutTheCentre()
    {
        // Scaling by 127 keeps 128 an exact centre, which costs the byte
        // value 0 — full-left is 1, not 0. That is the deliberate trade:
        // an off-centre neutral is drift the user cannot tune out, while
        // one unit at the extreme is imperceptible. Pinned here so the
        // asymmetry is a decision rather than a surprise.
        var left = DsuSnapshotMapper.ToStickByte(-1f);
        var right = DsuSnapshotMapper.ToStickByte(1f);
        var centre = DsuSnapshotMapper.ToStickByte(0f);

        Assert.Equal(1, left);
        Assert.Equal(255, right);
        Assert.Equal(centre - left, right - centre);
    }

    [Fact]
    public void NaNReadsAsCentredRatherThanGarbage()
    {
        Assert.Equal(128, DsuSnapshotMapper.ToStickByte(float.NaN));
        Assert.Equal(0, DsuSnapshotMapper.ToTriggerByte(float.NaN));
    }

    [Fact]
    public void StickYIsNotInverted()
    {
        // GameFlow's stick Y is already +up — SdlUnifiedInputSource
        // negates SDL's +down at the source — and DSU is +up too. An
        // inversion here would be a double negation, and "up looks down"
        // is the single most common bug in this kind of bridge.
        var snapshot = new ControllerSnapshot { LeftStick = new StickVector(0f, 1f) };
        var data = DsuSnapshotMapper.ToControllerData(snapshot, 0, 1, 0);

        Assert.Equal(255, data.LeftStickY);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    [InlineData(0.5f, 128)]
    public void TriggersScaleToFullByteRange(float value, int expected)
    {
        Assert.Equal(expected, DsuSnapshotMapper.ToTriggerByte(value));
    }

    [Fact]
    public void ADisconnectedSlotStillReportsCentredSticks()
    {
        // Zero here would be a stick slammed into the corner on a pad
        // that is not even present.
        var data = DsuSnapshotMapper.Disconnected(2);

        Assert.Equal(128, data.LeftStickX);
        Assert.Equal(128, data.LeftStickY);
        Assert.Equal(128, data.RightStickX);
        Assert.Equal(128, data.RightStickY);
        Assert.False(data.IsConnected);
        Assert.Equal(DsuSlotState.Disconnected, data.Device.State);
    }
}

public sealed class DsuSnapshotMapperButtonTests
{
    private static IReadOnlyDictionary<ButtonId, bool> Pressed(params ButtonId[] ids)
    {
        var map = ButtonState.Clone(ButtonState.CreateEmptyMap());
        foreach (var id in ids)
        {
            map[id] = true;
        }

        return map;
    }

    [Theory]
    [InlineData(ButtonId.South, DsuButtons.Cross)]
    [InlineData(ButtonId.East, DsuButtons.Circle)]
    [InlineData(ButtonId.West, DsuButtons.Square)]
    [InlineData(ButtonId.North, DsuButtons.Triangle)]
    [InlineData(ButtonId.LeftShoulder, DsuButtons.L1)]
    [InlineData(ButtonId.RightShoulder, DsuButtons.R1)]
    [InlineData(ButtonId.LeftTriggerButton, DsuButtons.L2)]
    [InlineData(ButtonId.RightTriggerButton, DsuButtons.R2)]
    [InlineData(ButtonId.Back, DsuButtons.Share)]
    [InlineData(ButtonId.Start, DsuButtons.Options)]
    [InlineData(ButtonId.LeftStick, DsuButtons.LeftStick)]
    [InlineData(ButtonId.RightStick, DsuButtons.RightStick)]
    [InlineData(ButtonId.DpadUp, DsuButtons.DpadUp)]
    [InlineData(ButtonId.DpadDown, DsuButtons.DpadDown)]
    [InlineData(ButtonId.DpadLeft, DsuButtons.DpadLeft)]
    [InlineData(ButtonId.DpadRight, DsuButtons.DpadRight)]
    public void EveryMappedButtonReachesItsDsuCounterpart(ButtonId source, DsuButtons expected)
    {
        // Correspondence is by POSITION, not by printed label: South is
        // the bottom face button, which DSU calls Cross. A player pressing
        // the bottom button expects the bottom button regardless of what
        // their pad has printed on it.
        Assert.Equal(expected, DsuSnapshotMapper.MapButtons(Pressed(source)));
    }

    [Fact]
    public void SeveralButtonsAtOnceAllRegister()
    {
        var result = DsuSnapshotMapper.MapButtons(
            Pressed(ButtonId.South, ButtonId.DpadUp, ButtonId.RightShoulder));

        Assert.True(result.HasFlag(DsuButtons.Cross));
        Assert.True(result.HasFlag(DsuButtons.DpadUp));
        Assert.True(result.HasFlag(DsuButtons.R1));
        Assert.False(result.HasFlag(DsuButtons.Triangle));
    }

    [Fact]
    public void GuideAndTouchpadAreNotInTheButtonWord()
    {
        // The protocol carries these as their own bytes. Folding them into
        // the word would set some unrelated face button.
        Assert.Equal(DsuButtons.None, DsuSnapshotMapper.MapButtons(Pressed(ButtonId.Guide, ButtonId.Touchpad)));

        var snapshot = new ControllerSnapshot { Buttons = Pressed(ButtonId.Guide, ButtonId.Touchpad) };
        var data = DsuSnapshotMapper.ToControllerData(snapshot, 0, 1, 0);

        Assert.True(data.PsButton);
        Assert.True(data.TouchButton);
    }

    [Fact]
    public void NothingPressedIsNoButtons()
    {
        Assert.Equal(DsuButtons.None, DsuSnapshotMapper.MapButtons(ButtonState.CreateEmptyMap()));
    }

    [Fact]
    public void ANullButtonMapReadsAsNeutralInsteadOfThrowing()
    {
        Assert.Equal(DsuButtons.None, DsuSnapshotMapper.MapButtons(null));
    }

    [Fact]
    public void DigitalButtonsReportBothAnalogExtremes()
    {
        // GameFlow tracks face buttons as digital, so the analog block
        // reports the extremes rather than inventing pressure the
        // hardware never gave us.
        var down = DsuSnapshotMapper.ToControllerData(
            new ControllerSnapshot { Buttons = Pressed(ButtonId.South) }, 0, 1, 0);
        var up = DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 0, 1, 0);

        Assert.Equal(255, down.AnalogCross);
        Assert.Equal(0, up.AnalogCross);
    }
}

public sealed class DsuSnapshotMapperIdentityTests
{
    [Fact]
    public void SlotIsClampedIntoTheProtocolsFourPadRange()
    {
        // DSU addresses four pads; GameFlow supports 16. An out-of-range
        // slot must not be written into a byte field unclamped.
        Assert.Equal(3, DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 99, 1, 0).Device.Slot);
        Assert.Equal(0, DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), -5, 1, 0).Device.Slot);
    }

    [Fact]
    public void EachSlotGetsADistinctStableMac()
    {
        // Clients key pads by MAC and expect it to survive a restart, so
        // this must be derived from the slot rather than randomised.
        var first = DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 0, 1, 0).Device.MacAddress;
        var second = DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 1, 1, 0).Device.MacAddress;
        var firstAgain = DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 0, 1, 0).Device.MacAddress;

        Assert.NotEqual(first, second);
        Assert.Equal(first, firstAgain);

        // Locally-administered bit set, so it can never collide with a
        // real network device.
        Assert.Equal(0x02u, (uint)((first >> 40) & 0xFF));
    }

    [Fact]
    public void BatteryReportsNotApplicableRatherThanGuessing()
    {
        // ControllerSnapshot carries no battery level. Reporting Full
        // would put a wrong charge reading in front of the user in every
        // emulator that surfaces it.
        Assert.Equal(
            DsuBatteryStatus.NotApplicable,
            DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 0, 1, 0).Device.Battery);
    }

    [Fact]
    public void PacketNumberAndTimestampPassStraightThrough()
    {
        var data = DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 0, 4321u, 99_000_000UL);

        Assert.Equal(4321u, data.PacketNumber);
        Assert.Equal(99_000_000UL, data.MotionTimestampMicroseconds);
    }

    [Fact]
    public void ANullSnapshotThrowsRatherThanEmittingASilentlyEmptyPad()
    {
        // Unlike network input, a null here is a programming error inside
        // the runtime, not hostile data — it should surface loudly.
        Assert.Throws<ArgumentNullException>(
            () => DsuSnapshotMapper.ToControllerData(null!, 0, 1, 0));
    }
}

public sealed class DsuSnapshotMapperTouchTests
{
    [Fact]
    public void TouchIsScaledToTheProtocolsSurfaceNotLeftNormalized()
    {
        // The snapshot is 0..1; DSU carries DS4 surface coordinates.
        // Passing 0..1 straight through would pin every touch to the
        // top-left corner.
        var snapshot = new ControllerSnapshot
        {
            TouchContacts = [new TouchContact(0, 0.5f, 0.5f, 1f)],
            TouchContactCount = 1
        };

        var touch = DsuSnapshotMapper.ToControllerData(snapshot, 0, 1, 0).FirstTouch;

        Assert.True(touch.IsActive);
        Assert.Equal(960, touch.X);
        Assert.Equal(471, touch.Y);
    }

    [Fact]
    public void APrimaryOnlySourceStillFillsTheFirstTouchSlot()
    {
        // Sources that report a primary contact without per-finger detail
        // populate TouchX/TouchY only; reporting no touch at all would
        // lose them entirely.
        var snapshot = new ControllerSnapshot { TouchDown = true, TouchX = 1f, TouchY = 0f };
        var touch = DsuSnapshotMapper.ToControllerData(snapshot, 0, 1, 0).FirstTouch;

        Assert.True(touch.IsActive);
        Assert.Equal(1920, touch.X);
        Assert.Equal(0, touch.Y);
    }

    [Fact]
    public void NoTouchLeavesBothSlotsInactive()
    {
        var data = DsuSnapshotMapper.ToControllerData(new ControllerSnapshot(), 0, 1, 0);

        Assert.False(data.FirstTouch.IsActive);
        Assert.False(data.SecondTouch.IsActive);
    }

    [Fact]
    public void TwoFingersFillBothSlots()
    {
        var snapshot = new ControllerSnapshot
        {
            TouchContacts = [new TouchContact(0, 0f, 0f, 1f), new TouchContact(1, 1f, 1f, 1f)],
            TouchContactCount = 2
        };

        var data = DsuSnapshotMapper.ToControllerData(snapshot, 0, 1, 0);

        Assert.True(data.FirstTouch.IsActive);
        Assert.True(data.SecondTouch.IsActive);
        Assert.Equal(1920, data.SecondTouch.X);
        Assert.Equal(942, data.SecondTouch.Y);
    }
}
