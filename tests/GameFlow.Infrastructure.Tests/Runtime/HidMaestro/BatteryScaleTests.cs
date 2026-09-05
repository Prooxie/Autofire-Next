using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.HidMaestro;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.HidMaestro;

/// <summary>
/// Pins the battery value written onto the emitted pad.
/// </summary>
/// <remarks>
/// The SDK documents the underlying report field as "0..10 (Sony
/// firmware convention)", and that wording was taken to describe the
/// SDK's own property too. It does not: the property takes a percentage
/// and the codec down-scales into the Sony field itself, so dividing
/// here divided the charge a second time.
///
/// <para>
/// Settled by measurement, not by the wording. With the division in
/// place a source pad at 25% was submitted as 3 and read back as 3%, and
/// one at 65% as 7 and read back as roughly 7% — two independent
/// readings that both match the raw number rather than a tenth of it.
/// </para>
/// </remarks>
public sealed class BatteryScaleTests
{
    private static ControllerSnapshot WithBattery(int? percent) =>
        ControllerSnapshot.Empty() with { BatteryPercent = percent };

    [Theory]
    [InlineData(100, 100)]
    [InlineData(95, 95)]
    [InlineData(75, 75)]
    [InlineData(65, 65)]
    [InlineData(25, 25)]
    [InlineData(5, 5)]
    [InlineData(0, 0)]
    public void APercentageTravelsThroughUnscaled(int percent, byte expected) =>
        Assert.Equal(expected, HidMaestroOutputSink.BatteryLevelFor(WithBattery(percent)));

    /// <summary>
    /// A wired pad, a keyboard, or nothing at all. Reporting full is what
    /// a wired controller says about itself; reporting zero is what the
    /// unwritten field used to say, and it reads as nearly flat.
    /// </summary>
    [Fact]
    public void ASourceWithNoBatteryReportsFull() =>
        Assert.Equal(100, HidMaestroOutputSink.BatteryLevelFor(WithBattery(null)));

    /// <summary>Nothing may exceed the scale, whatever a backend reports.</summary>
    [Theory]
    [InlineData(255)]
    [InlineData(1000)]
    [InlineData(-40)]
    public void OutOfRangeInputStaysInsideTheScale(int percent)
    {
        var level = HidMaestroOutputSink.BatteryLevelFor(WithBattery(percent));
        Assert.InRange(level, (byte)0, (byte)100);
    }
}
