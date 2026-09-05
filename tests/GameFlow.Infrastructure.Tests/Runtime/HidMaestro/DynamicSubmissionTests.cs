using GameFlow.Infrastructure.Runtime.HidMaestro;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace GameFlow.Infrastructure.Tests.Runtime.HidMaestro;

public sealed class DynamicSubmissionTests(ITestOutputHelper output)
{
    [Fact]
    public void MotionAndTouchAreClearedAfterSourceDisappears()
    {
        var sdk = new FakeController();
        using var bridge = Create(sdk);
        Assert.True(Submit(bridge, 1, new(true, (1, 2, 3), (4, 5, 6)), new(true, 100, 200, 1, false, 0, 0, 0)));
        Assert.Equal(4, sdk.Last.GyroDpsX);
        Assert.True(sdk.Last.TouchpadFinger0Active);
        Assert.True(Submit(bridge, 0, default, default));
        Assert.Equal(0, sdk.Last.GyroDpsX);
        Assert.Equal(0, sdk.Last.GyroDpsY);
        Assert.Equal(0, sdk.Last.GyroDpsZ);
        Assert.Equal(0, sdk.Last.AccelGX);
        Assert.False(sdk.Last.TouchpadFinger0Active);
    }

    [Theory]
    [InlineData(float.NaN, 0.5f)]
    [InlineData(float.PositiveInfinity, 0.5f)]
    [InlineData(float.NegativeInfinity, 0.5f)]
    [InlineData(float.MaxValue, 1f)]
    [InlineData(-2f, 0f)]
    public void AxesAreFiniteAndNormalized(float value, float expected)
    {
        var sdk = new FakeController();
        using var bridge = Create(sdk);
        Assert.True(Submit(bridge, value, default, default));
        Assert.Equal(expected, sdk.Last.Axes[Axis.X]);
        Assert.InRange(sdk.Last.Axes[Axis.Z], 0, 1);
    }

    [Fact]
    public void OpaqueProfilesUseTheSdkCanonicalAxisFallback()
    {
        var sdk = new FakeController(new Profile([], []));
        using var bridge = Create(sdk);

        Assert.True(bridge.Submit(1, -1, 0.5f, -0.5f, 0.25f, 0.75f,
            Array.Empty<(string, bool)>(), "None", 10, false, true, default, default));

        Assert.Equal(1f, sdk.Last.Axes[Axis.X]);
        Assert.Equal(0f, sdk.Last.Axes[Axis.Y]);
        Assert.Equal(0.75f, sdk.Last.Axes[Axis.Rx]);
        Assert.Equal(0.25f, sdk.Last.Axes[Axis.Ry]);
        Assert.Equal(0.25f, sdk.Last.Axes[Axis.Z]);
        Assert.Equal(0.75f, sdk.Last.Axes[Axis.Rz]);
    }

    [Fact]
    public void ReportsWarmSubmissionAllocations()
    {
        using var bridge = Create(new FakeController());
        for (var i = 0; i < 1000; i++) Submit(bridge, 0, default, default);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) Submit(bridge, 0, default, default);
        output.WriteLine($"Warm bridge allocations: {(GC.GetAllocatedBytesForCurrentThread() - before) / 10000d:F1} bytes/frame");
    }

    private static DynamicHidMaestroController Create(FakeController sdk) => new(
        sdk, null, typeof(State), typeof(Buttons), typeof(Hat),
        typeof(FakeController).GetMethod(nameof(FakeController.SubmitState))!, NullLogger.Instance);

    private static bool Submit(DynamicHidMaestroController bridge, float axis,
        DynamicHidMaestroController.MotionSubmission motion, DynamicHidMaestroController.TouchSubmission touch) =>
        bridge.Submit(axis, axis, axis, axis, axis, axis, Array.Empty<(string, bool)>(), "None", 10, false, true, motion, touch);

    public enum Axis { None, X, Y, Z, Rx, Ry, Rz }
    public enum Buttons { None }
    public enum Hat { None }
    public sealed record Stick(Axis XAxis, Axis YAxis);
    public sealed record Trigger(Axis Axis);
    public sealed class Profile(Stick[]? sticks = null, Trigger[]? triggers = null)
    {
        public Stick[] Sticks { get; } = sticks ?? [new(Axis.X, Axis.Y)];
        public Trigger[] Triggers { get; } = triggers ?? [new(Axis.Z)];
    }
    public sealed class FakeController : IDisposable
    {
        public FakeController(Profile? profile = null) => Profile = profile ?? new();
        public Profile Profile { get; }
        public string InstanceId => "ROOT\\HIDMAESTRO\\submission-test";
        public State Last;
        public void SubmitState(in State state) => Last = state;
        public void Dispose() { }
    }
    public struct State
    {
        public Dictionary<Axis, float> Axes;
        public Buttons Buttons;
        public Hat Hat;
        public byte BatteryLevel;
        public bool BatteryCharging, BatteryFull;
        public float AccelGX, AccelGY, AccelGZ, GyroDpsX, GyroDpsY, GyroDpsZ;
        public bool TouchpadFinger0Active, TouchpadFinger1Active;
        public ushort TouchpadFinger0X, TouchpadFinger0Y, TouchpadFinger1X, TouchpadFinger1Y;
        public byte TouchpadFinger0Id, TouchpadFinger1Id;
    }
}
