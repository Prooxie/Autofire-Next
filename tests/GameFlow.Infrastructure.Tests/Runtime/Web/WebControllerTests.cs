using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Web;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Web;

public sealed class WebControllerProtocolTests
{
    [Theory]
    [InlineData(0, ButtonId.South)]
    [InlineData(1, ButtonId.East)]
    [InlineData(2, ButtonId.West)]
    [InlineData(3, ButtonId.North)]
    [InlineData(4, ButtonId.LeftShoulder)]
    [InlineData(5, ButtonId.RightShoulder)]
    [InlineData(6, ButtonId.Back)]
    [InlineData(7, ButtonId.Start)]
    [InlineData(8, ButtonId.Guide)]
    [InlineData(9, ButtonId.LeftStick)]
    [InlineData(10, ButtonId.RightStick)]
    [InlineData(11, ButtonId.DpadUp)]
    [InlineData(12, ButtonId.DpadDown)]
    [InlineData(13, ButtonId.DpadLeft)]
    [InlineData(14, ButtonId.DpadRight)]
    [InlineData(15, ButtonId.Touchpad)]
    public void EveryWireBitMapsToItsDocumentedButton(int bit, ButtonId expected)
    {
        // This is the contract the browser's BIT table depends on. If
        // someone reorders ButtonId and the protocol silently follows,
        // these break — which is exactly the point.
        var snapshot = WebControllerProtocol.TryParseInput($"{{\"b\":{1 << bit}}}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsPressed(expected));
    }

    [Fact]
    public void MultipleButtonsInOneMaskAllRegister()
    {
        var mask = (1 << 0) | (1 << 4) | (1 << 11); // South + LeftShoulder + DpadUp
        var snapshot = WebControllerProtocol.TryParseInput($"{{\"b\":{mask}}}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsPressed(ButtonId.South));
        Assert.True(snapshot.IsPressed(ButtonId.LeftShoulder));
        Assert.True(snapshot.IsPressed(ButtonId.DpadUp));
        Assert.False(snapshot.IsPressed(ButtonId.North));
    }

    [Fact]
    public void AxesAndTriggersParse()
    {
        var snapshot = WebControllerProtocol.TryParseInput(
            "{\"b\":0,\"lx\":0.5,\"ly\":-0.25,\"rx\":-1,\"ry\":1,\"lt\":0.75,\"rt\":0.1}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(0.5f, snapshot!.LeftStick.X, precision: 3);
        Assert.Equal(-0.25f, snapshot.LeftStick.Y, precision: 3);
        Assert.Equal(-1f, snapshot.RightStick.X, precision: 3);
        Assert.Equal(1f, snapshot.RightStick.Y, precision: 3);
        Assert.Equal(0.75f, snapshot.LeftTrigger, precision: 3);
        Assert.Equal(0.1f, snapshot.RightTrigger, precision: 3);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]      // valid JSON, wrong shape
    [InlineData("\"a string\"")]
    [InlineData("")]
    public void MalformedInputReturnsNullInsteadOfThrowing(string json)
    {
        // These arrive from a phone on the network. A throw here would
        // kill the receive loop for that session.
        var snapshot = WebControllerProtocol.TryParseInput(json, padIndex: 0);
        Assert.Null(snapshot);
    }

    [Fact]
    public void OutOfRangeAxesAreClamped()
    {
        var snapshot = WebControllerProtocol.TryParseInput(
            "{\"b\":0,\"lx\":99,\"ly\":-99,\"lt\":5,\"rt\":-5}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(1f, snapshot!.LeftStick.X, precision: 3);
        Assert.Equal(-1f, snapshot.LeftStick.Y, precision: 3);
        Assert.Equal(1f, snapshot.LeftTrigger, precision: 3);
        Assert.Equal(0f, snapshot.RightTrigger, precision: 3);
    }

    [Fact]
    public void MissingFieldsDefaultToNeutral()
    {
        var snapshot = WebControllerProtocol.TryParseInput("{\"b\":0}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(0f, snapshot!.LeftStick.X, precision: 3);
        Assert.Equal(0f, snapshot.RightTrigger, precision: 3);
    }

    [Fact]
    public void PhoneMotionPopulatesGyroAndAccelerometer()
    {
        var snapshot = WebControllerProtocol.TryParseInput(
            "{\"b\":0,\"gyro\":1,\"gp\":0.5,\"gy\":-1.25,\"gr\":0.1,\"ax\":0.2,\"ay\":9.8,\"az\":-0.3}",
            padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.HasGyro);
        Assert.Equal(0.5f, snapshot.GyroPitch, precision: 3);
        Assert.Equal(-1.25f, snapshot.GyroYaw, precision: 3);
        Assert.Equal(0.1f, snapshot.GyroRoll, precision: 3);
        Assert.Equal(9.8f, snapshot.AccelY, precision: 3);
    }

    [Fact]
    public void PhoneWithoutMotionPermissionReportsNoGyro()
    {
        // A phone that never got sensor permission (or a desktop browser)
        // must read as "no gyro hardware", not "gyro sitting perfectly
        // still" — the pipeline treats those differently.
        var snapshot = WebControllerProtocol.TryParseInput("{\"b\":0}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.HasGyro);
    }

    [Fact]
    public void MotionValuesAreNotClampedToStickRange()
    {
        // Angular velocity legitimately exceeds 1.0 rad/s on a fast
        // flick; clamping it like a stick axis would cap real input.
        var snapshot = WebControllerProtocol.TryParseInput(
            "{\"b\":0,\"gyro\":1,\"gy\":8.5}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(8.5f, snapshot!.GyroYaw, precision: 3);
    }

    [Fact]
    public void HostileMotionValuesAreRejected()
    {
        var nan = WebControllerProtocol.TryParseInput("{\"b\":0,\"gyro\":1,\"gp\":\"NaN\"}", padIndex: 0);
        Assert.NotNull(nan);
        Assert.Equal(0f, nan!.GyroPitch, precision: 5);

        var huge = WebControllerProtocol.TryParseInput("{\"b\":0,\"gyro\":1,\"gy\":1e30}", padIndex: 0);
        Assert.NotNull(huge);
        Assert.InRange(huge!.GyroYaw, -1000f, 1000f);
    }

    [Fact]
    public void WrongTypedFieldsReadAsNeutralInsteadOfThrowing()
    {
        // Same hazard as the motion fields: JsonElement's TryGet* throw on a
        // wrong-typed element rather than returning false, so every numeric
        // field a phone can send needs to survive being a string.
        var snapshot = WebControllerProtocol.TryParseInput(
            "{\"b\":\"7\",\"lx\":\"0.5\",\"lt\":true,\"gyro\":\"1\"}", padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsPressed(ButtonId.South));
        Assert.Equal(0f, snapshot.LeftStick.X, precision: 3);
        Assert.Equal(0f, snapshot.LeftTrigger, precision: 3);
        Assert.False(snapshot.HasGyro);
    }

    [Fact]
    public void TouchpadContactsPopulateTheFullMultiTouchSnapshot()
    {
        var snapshot = WebControllerProtocol.TryParseInput(
            "{\"b\":32768,\"touch\":[{\"i\":7,\"x\":0.75,\"y\":0.25,\"p\":0.4},{\"i\":2,\"x\":0.2,\"y\":0.8}]}",
            padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.TouchDown);
        Assert.True(snapshot.IsPressed(ButtonId.Touchpad));
        Assert.Equal(2, snapshot.TouchContactCount);
        Assert.Equal([2, 7], snapshot.TouchContacts.Select(contact => contact.FingerIndex));
        Assert.Equal(0.2f, snapshot.TouchX, precision: 3);
        Assert.Equal(0.8f, snapshot.TouchY, precision: 3);
        Assert.Equal(1f, snapshot.TouchContacts[0].Pressure, precision: 3);
        Assert.Equal(0.4f, snapshot.TouchContacts[1].Pressure, precision: 3);
    }

    [Fact]
    public void TouchpadContactsAreBoundedAndMalformedEntriesAreIgnored()
    {
        var snapshot = WebControllerProtocol.TryParseInput(
            """
            {
              "touch": [
                {"i": 0, "x": -4, "y": 9, "p": 5},
                {"i": 0, "x": 0.5, "y": 0.5},
                {"i": -1, "x": 0.5, "y": 0.5},
                {"i": 1, "x": "bad", "y": 0.5},
                {"i": 2, "x": 0.2, "y": 0.2},
                {"i": 3, "x": 0.3, "y": 0.3},
                {"i": 4, "x": 0.4, "y": 0.4},
                {"i": 5, "x": 0.5, "y": 0.5},
                {"i": 6, "x": 0.6, "y": 0.6}
              ]
            }
            """,
            padIndex: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot!.TouchContactCount);
        Assert.Equal([0, 2, 3, 4, 5], snapshot.TouchContacts.Select(contact => contact.FingerIndex));
        Assert.Equal(0f, snapshot.TouchContacts[0].X);
        Assert.Equal(1f, snapshot.TouchContacts[0].Y);
        Assert.Equal(1f, snapshot.TouchContacts[0].Pressure);
    }
}

public sealed class WebControllerHubTests
{
    [Fact]
    public void PadsAreClaimedLowestFirstAndReleasedBack()
    {
        var hub = new WebControllerHub();

        Assert.Equal(0, hub.ClaimPad().PadIndex);
        var second = hub.ClaimPad();
        Assert.Equal(1, second.PadIndex);
        Assert.Equal(2, hub.ClaimPad().PadIndex);

        hub.ReleasePad(second);
        Assert.Equal(1, hub.ClaimPad().PadIndex); // anonymous freed slots remain immediately reusable
    }

    [Fact]
    public void ClaimingBeyondTheLimitReportsFull()
    {
        var hub = new WebControllerHub();
        for (var i = 0; i < WebControllerHub.MaxPads; i++)
        {
            Assert.True(hub.ClaimPad().IsValid);
        }

        Assert.False(hub.ClaimPad().IsValid); // 17th phone is turned away rather than overwriting someone
    }

    [Fact]
    public void DisconnectedPadReadsNeutral_NotItsLastInput()
    {
        // A phone that dies mid-press must not leave that button held
        // down in the game forever.
        var hub = new WebControllerHub();
        var lease = hub.ClaimPad();

        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());
        buttons[ButtonId.South] = true;
        Assert.True(hub.UpdatePad(lease, new ControllerSnapshot { Buttons = buttons }));
        Assert.True(hub.GetSnapshot(lease.PadIndex).IsPressed(ButtonId.South));

        hub.ReleasePad(lease);
        Assert.False(hub.GetSnapshot(lease.PadIndex).IsPressed(ButtonId.South));
    }

    [Fact]
    public void ConnectedPadListTracksClaims()
    {
        var hub = new WebControllerHub();
        Assert.Empty(hub.GetConnectedPads());

        var first = hub.ClaimPad();
        var second = hub.ClaimPad();
        Assert.Equal(2, hub.GetConnectedPads().Count);

        hub.ReleasePad(first);
        Assert.Single(hub.GetConnectedPads());
        Assert.Contains(second.PadIndex, hub.GetConnectedPads());
    }

    [Fact]
    public void RumbleQueueIsBoundedAndDropsOldestFirst()
    {
        var hub = new WebControllerHub();
        var lease = hub.ClaimPad();

        for (var i = 1; i <= 12; i++)
        {
            hub.QueueRumble(lease.PadIndex, new WebRumbleCommand(i / 12f, 0f, i));
        }

        var drained = new List<int>();
        while (hub.TryDequeueRumble(lease, out var command))
        {
            drained.Add(command.DurationMs);
        }

        Assert.Equal(8, drained.Count);          // capped
        Assert.Equal(12, drained[^1]);           // newest survived
        Assert.DoesNotContain(1, drained);       // oldest dropped
    }

    [Fact]
    public void OutOfRangePadIndicesAreIgnoredRatherThanThrowing()
    {
        var hub = new WebControllerHub();

        Assert.False(hub.UpdatePad(WebPadLease.Unavailable, new ControllerSnapshot()));
        hub.ReleasePad(WebPadLease.Unavailable);

        Assert.False(hub.IsPadConnected(-1));
        Assert.False(hub.IsPadConnected(999));
        Assert.False(hub.TryDequeueRumble(WebPadLease.Unavailable, out _));
    }

    [Fact]
    public void ReconnectingClientKeepsItsPadNumber()
    {
        var hub = new WebControllerHub();
        var phone = hub.ClaimPad("phone-a1b2c3");
        var otherPhone = hub.ClaimPad("phone-d4e5f6");

        hub.ReleasePad(phone);
        var reconnected = hub.ClaimPad("phone-a1b2c3");

        Assert.Equal(phone.PadIndex, reconnected.PadIndex);
        Assert.Equal(otherPhone.PadIndex, hub.ClaimPad("phone-d4e5f6").PadIndex);
    }

    [Fact]
    public void ReplacedSocketCannotOverwriteOrReleaseTheNewLease()
    {
        var hub = new WebControllerHub();
        var oldLease = hub.ClaimPad("phone-a1b2c3");
        var newLease = hub.ClaimPad("phone-a1b2c3");

        var pressed = ButtonState.Clone(ButtonState.CreateEmptyMap());
        pressed[ButtonId.South] = true;

        Assert.False(hub.UpdatePad(oldLease, new ControllerSnapshot { Buttons = pressed }));
        Assert.True(hub.UpdatePad(newLease, new ControllerSnapshot { Buttons = pressed }));

        hub.ReleasePad(oldLease);

        Assert.True(hub.IsPadConnected(newLease.PadIndex));
        Assert.True(hub.GetSnapshot(newLease.PadIndex).IsPressed(ButtonId.South));
        Assert.False(hub.IsLeaseCurrent(oldLease));
        Assert.True(hub.IsLeaseCurrent(newLease));
    }

    [Fact]
    public void ReleasedLeaseCannotReactivateItsPad()
    {
        var hub = new WebControllerHub();
        var lease = hub.ClaimPad("phone-a1b2c3");

        hub.ReleasePad(lease);

        Assert.False(hub.IsLeaseCurrent(lease));
        Assert.False(hub.UpdatePad(lease, new ControllerSnapshot()));
        Assert.False(hub.IsPadConnected(lease.PadIndex));
    }

    [Fact]
    public void OldSocketCannotDrainNewSocketsRumble()
    {
        var hub = new WebControllerHub();
        var oldLease = hub.ClaimPad("phone-a1b2c3");
        var newLease = hub.ClaimPad("phone-a1b2c3");
        hub.QueueRumble(newLease.PadIndex, new WebRumbleCommand(1f, 0.5f, 250));

        Assert.False(hub.TryDequeueRumble(oldLease, out _));
        Assert.True(hub.TryDequeueRumble(newLease, out var command));
        Assert.Equal(250, command.DurationMs);
    }

    [Fact]
    public void StaleConnectionCanBeReclaimedWithoutGivingItsOldLeaseAuthority()
    {
        var clock = new ManualTimeProvider();
        var hub = new WebControllerHub(clock);
        var first = hub.ClaimPad("phone-00-id");
        for (var i = 1; i < WebControllerHub.MaxPads; i++)
        {
            Assert.True(hub.ClaimPad($"phone-{i:D2}-id").IsValid);
        }

        clock.Advance(TimeSpan.FromSeconds(6));
        var replacement = hub.ClaimPad("replacement-id");

        Assert.Equal(first.PadIndex, replacement.PadIndex);
        Assert.False(hub.UpdatePad(first, new ControllerSnapshot()));
        Assert.True(hub.UpdatePad(replacement, new ControllerSnapshot()));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}

public sealed class WebControllerEffectBridgeTests
{
    [Fact]
    public void WebPadEffectIsClampedAndQueuedForTheOwningPhone()
    {
        var hub = new WebControllerHub();
        var lease = hub.ClaimPad("phone-a1b2c3");
        var state = new GameFlow.Infrastructure.Runtime.Effects.ControllerEffectState
        {
            LowFrequencyRumble = 1.5,
            HighFrequencyRumble = -0.25,
        };

        Assert.True(WebControllerEffectBridge.TryRoute(
            hub, WebControllerDeviceScanner.BuildDeviceId(lease.PadIndex), state));
        Assert.True(hub.TryDequeueRumble(lease, out var command));
        Assert.Equal(1f, command.LowFrequency);
        Assert.Equal(0f, command.HighFrequency);
        Assert.Equal(WebControllerEffectBridge.VibrationDurationMs, command.DurationMs);
    }

    [Fact]
    public void SilentEffectQueuesAnImmediateVibrationStop()
    {
        var hub = new WebControllerHub();
        var lease = hub.ClaimPad("phone-a1b2c3");

        Assert.True(WebControllerEffectBridge.TryRoute(
            hub,
            WebControllerDeviceScanner.BuildDeviceId(lease.PadIndex),
            GameFlow.Infrastructure.Runtime.Effects.ControllerEffectState.Silent));
        Assert.True(hub.TryDequeueRumble(lease, out var command));
        Assert.Equal(0, command.DurationMs);
    }

    [Fact]
    public void NonWebDeviceIsLeftForTheHardwareCollector()
    {
        var hub = new WebControllerHub();

        Assert.False(WebControllerEffectBridge.TryRoute(
            hub,
            "sdl-gamepad-1",
            GameFlow.Infrastructure.Runtime.Effects.ControllerEffectState.Silent));
    }
}

public sealed class WebControllerPageTests
{
    [Fact]
    public void PageKeepsAStablePerTabIdentityForReconnects()
    {
        Assert.Contains("sessionStorage.getItem(\"gameflow-client-id\")", WebControllerAssets.ControllerPage);
        Assert.Contains("/ws?client=", WebControllerAssets.ControllerPage);
    }

    [Fact]
    public void PageSustainsAndExplicitlyStopsVibration()
    {
        Assert.Contains("rumbleTimer = setInterval", WebControllerAssets.ControllerPage);
        Assert.Contains("navigator.vibrate(0)", WebControllerAssets.ControllerPage);
        Assert.Contains("stopRumble();", WebControllerAssets.ControllerPage);
    }
}

public sealed class WebControllerDeviceScannerTests
{
    [Fact]
    public void DeviceIdRoundTripsThroughTheParser()
    {
        for (var pad = 0; pad < WebControllerHub.MaxPads; pad++)
        {
            var id = WebControllerDeviceScanner.BuildDeviceId(pad);
            Assert.Equal(pad, WebControllerDeviceScanner.TryParsePadIndex(id));
        }
    }

    [Theory]
    [InlineData("sdl-gamepad-3")]
    [InlineData("evdev-keyboard-/dev/input/event2")]
    [InlineData("web-pad-")]
    [InlineData("web-pad-999")]   // beyond MaxPads
    [InlineData("web-pad-abc")]
    public void NonWebPadIdsAreRejected(string deviceId)
    {
        Assert.Equal(-1, WebControllerDeviceScanner.TryParsePadIndex(deviceId));
    }

    [Fact]
    public void ScanReportsOnlyConnectedPads()
    {
        var hub = new WebControllerHub();
        Assert.Empty(WebControllerDeviceScanner.Scan(hub));

        var pad = hub.ClaimPad().PadIndex;
        var devices = WebControllerDeviceScanner.Scan(hub);

        Assert.Single(devices);
        Assert.Equal(WebControllerDeviceScanner.BuildDeviceId(pad), devices[0].Id);
        Assert.True(devices[0].IsGamepad);
    }
}
