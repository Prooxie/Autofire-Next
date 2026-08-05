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

        Assert.Equal(0, hub.ClaimPad());
        Assert.Equal(1, hub.ClaimPad());
        Assert.Equal(2, hub.ClaimPad());

        hub.ReleasePad(1);
        Assert.Equal(1, hub.ClaimPad()); // the freed slot is reused, not appended after 2
    }

    [Fact]
    public void ClaimingBeyondTheLimitReportsFull()
    {
        var hub = new WebControllerHub();
        for (var i = 0; i < WebControllerHub.MaxPads; i++)
        {
            Assert.True(hub.ClaimPad() >= 0);
        }

        Assert.Equal(-1, hub.ClaimPad()); // 17th phone is turned away rather than overwriting someone
    }

    [Fact]
    public void DisconnectedPadReadsNeutral_NotItsLastInput()
    {
        // A phone that dies mid-press must not leave that button held
        // down in the game forever.
        var hub = new WebControllerHub();
        var pad = hub.ClaimPad();

        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());
        buttons[ButtonId.South] = true;
        hub.UpdatePad(pad, new ControllerSnapshot { Buttons = buttons });
        Assert.True(hub.GetSnapshot(pad).IsPressed(ButtonId.South));

        hub.ReleasePad(pad);
        Assert.False(hub.GetSnapshot(pad).IsPressed(ButtonId.South));
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
        Assert.Contains(second, hub.GetConnectedPads());
    }

    [Fact]
    public void RumbleQueueIsBoundedAndDropsOldestFirst()
    {
        var hub = new WebControllerHub();
        var pad = hub.ClaimPad();

        for (var i = 1; i <= 12; i++)
        {
            hub.QueueRumble(pad, new WebRumbleCommand(i / 12f, 0f, i));
        }

        var drained = new List<int>();
        while (hub.TryDequeueRumble(pad, out var command))
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

        hub.UpdatePad(-1, new ControllerSnapshot());
        hub.UpdatePad(999, new ControllerSnapshot());
        hub.ReleasePad(-5);

        Assert.False(hub.IsPadConnected(-1));
        Assert.False(hub.IsPadConnected(999));
        Assert.False(hub.TryDequeueRumble(-1, out _));
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

        var pad = hub.ClaimPad();
        var devices = WebControllerDeviceScanner.Scan(hub);

        Assert.Single(devices);
        Assert.Equal(WebControllerDeviceScanner.BuildDeviceId(pad), devices[0].Id);
        Assert.True(devices[0].IsGamepad);
    }
}
