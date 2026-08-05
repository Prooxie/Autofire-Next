using System.Diagnostics;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime;

public sealed class DemoInputSource : IInputSource
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly InputDeviceCatalog inputDeviceCatalog;
    private readonly string deviceName;

    public DemoInputSource(InputDeviceCatalog inputDeviceCatalog)
        : this(inputDeviceCatalog, null)
    {
    }

    public DemoInputSource(InputDeviceCatalog inputDeviceCatalog, string? displayName)
    {
        this.inputDeviceCatalog = inputDeviceCatalog;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? "DemoInput (preview)"
            : displayName.Trim();

        deviceName = DisplayName.Contains("fallback", StringComparison.OrdinalIgnoreCase)
            ? "Demo timeline (fallback)"
            : "Demo timeline";

        inputDeviceCatalog.ReplaceDevices(
        [
            new InputDeviceInfo(
                "demo-preview-device",
                "Demo preview controller",
                true,
                true)
        ]);
        inputDeviceCatalog.SetSelectedDevice("demo-preview-device");
        inputDeviceCatalog.SetProviderStatus("ProviderStatus_DemoActive");
    }

    public string DisplayName { get; }

    public ValueTask<ControllerSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        inputDeviceCatalog.SetProviderStatus("ProviderStatus_DemoActive");
        return ValueTask.FromResult(GenerateSnapshot(stopwatch.Elapsed.TotalSeconds, deviceName));
    }

    /// <summary>
    /// The demo waveform as a pure function of elapsed time — sticks
    /// sweep in Lissajous curves, triggers pulse, buttons fire on
    /// staggered cycles. Shared between the top-level "demo" input
    /// provider (above) and the per-slot demo preview
    /// (<see cref="Slots.SlotRuntime"/>, driven by
    /// <see cref="Templates.DeviceOutputTemplate.DemoPreview"/>), so a
    /// preview looks identical wherever it runs.
    /// </summary>
    public static ControllerSnapshot GenerateSnapshot(double elapsed, string deviceName)
    {
        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());

        var leftStick = new StickVector(
            (float)(Math.Sin(elapsed * 0.75d) * 0.42d),
            (float)(Math.Cos(elapsed * 0.95d) * 0.42d));

        var rightStick = new StickVector(
            (float)(Math.Sin(elapsed * 1.4d) * 0.85d),
            (float)(Math.Cos(elapsed * 1.2d) * 0.85d));

        var leftTrigger = (float)((Math.Sin(elapsed * 0.6d) + 1d) * 0.5d);
        var rightTrigger = (float)((Math.Cos(elapsed * 0.9d) + 1d) * 0.5d);

        buttons[ButtonId.LeftShoulder] = (elapsed % 8d) is > 5d and < 6.75d;
        buttons[ButtonId.RightShoulder] = (elapsed % 2d) < 1.1d;
        buttons[ButtonId.RightStick] = (elapsed % 7d) is > 2.5d and < 3.2d;
        buttons[ButtonId.LeftStick] = (elapsed % 9d) is > 1.5d and < 2.1d;
        buttons[ButtonId.South] = (elapsed % 3.4d) < 0.45d;
        buttons[ButtonId.East] = (elapsed % 4.3d) is > 2.1d and < 2.45d;
        buttons[ButtonId.West] = (elapsed % 5.2d) is > 3.3d and < 3.65d;
        buttons[ButtonId.North] = (elapsed % 6.1d) is > 4.1d and < 4.45d;
        buttons[ButtonId.Start] = (elapsed % 10d) is > 7.4d and < 7.9d;
        buttons[ButtonId.Back] = (elapsed % 11d) is > 6.6d and < 6.95d;
        buttons[ButtonId.DpadUp] = leftStick.Y > 0.30f;
        buttons[ButtonId.DpadDown] = leftStick.Y < -0.30f;
        buttons[ButtonId.DpadLeft] = leftStick.X < -0.30f;
        buttons[ButtonId.DpadRight] = leftStick.X > 0.30f;

        var touchContactCount = (elapsed % 12d) switch
        {
            > 8.4d and < 9.1d => 1,
            > 9.1d and < 9.8d => 2,
            _ => 0
        };

        // Move demo fingers through smooth, independent paths. A fresh
        // random point every frame reads as input noise; phase-shifted
        // curves exercise the whole touch surface while remaining easy to
        // follow visually and deterministic in tests.
        var primaryTouch = new TouchContact(
            0,
            (float)(0.5d + Math.Sin(elapsed * 1.37d) * 0.38d),
            (float)(0.5d + Math.Cos(elapsed * 0.91d) * 0.32d));
        var secondaryTouch = new TouchContact(
            1,
            (float)(0.5d + Math.Cos(elapsed * 1.11d + 1.7d) * 0.34d),
            (float)(0.5d + Math.Sin(elapsed * 1.53d + 0.8d) * 0.30d));
        IReadOnlyList<TouchContact> touchContacts = touchContactCount switch
        {
            1 => [primaryTouch],
            2 => [primaryTouch, secondaryTouch],
            _ => []
        };

        buttons[ButtonId.Touchpad] = touchContactCount > 0;
        buttons[ButtonId.LeftTriggerButton] = leftTrigger > 0.92f;
        buttons[ButtonId.RightTriggerButton] = rightTrigger > 0.92f;

        return new ControllerSnapshot
        {
            DeviceName = deviceName,
            LeftStick = leftStick,
            RightStick = rightStick,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
            Buttons = buttons,
            Timestamp = DateTimeOffset.UtcNow
        }.WithTouchContacts(touchContacts);
    }

    public ValueTask DisposeAsync()
    {
        inputDeviceCatalog.Clear("ProviderStatus_NoActiveProvider");
        return ValueTask.CompletedTask;
    }
}
