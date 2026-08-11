namespace GameFlow.Core.Models;

public sealed record ProfileDocument
{
    public string Id { get; init; } = "speedrunner-default";
    public string Name { get; init; } = "Speedrunner Default";
    public int Version { get; init; } = 5;
    /// <summary>
    /// Mapping ticks per second, 30–1000. Defaults to the maximum: the
    /// pipeline tick is cheap next to the input read it wraps, and the
    /// lower default only added latency between a press and the virtual
    /// controller reporting it. A pad that reports at 250 Hz does not
    /// report faster for being asked more often — this is a ceiling, and
    /// the cost of the ceiling being high is small.
    /// </summary>
    public int PollingRateHz { get; init; } = 1000;
    public string InputProvider { get; init; } = "sdl";

    /// <summary>
    /// The output backend id. Defaults to the platform's single real
    /// backend — <c>"hidmaestro"</c> on Windows — so a brand-new profile
    /// creates an actual virtual device out of the box. The historical
    /// default of <c>"preview"</c> meant a fresh install read input
    /// perfectly and yet never emitted a controller, which presented as
    /// "HIDMaestro doesn't create a virtual controller" when in fact
    /// HIDMaestro was never being asked to.
    /// </summary>
    public string OutputProvider { get; init; } = OperatingSystem.IsWindows() ? "hidmaestro" : "preview";
    public string PreferredInputDeviceId { get; init; } = string.Empty;
    public UiPreferences Ui { get; init; } = new();
    public IReadOnlyList<MappingRule> Rules { get; init; } = [];

    /// <summary>Layer definitions; membership is on each rule via <see cref="MappingRule.LayerId"/>. Empty by default — existing profiles load unaffected.</summary>
    public IReadOnlyList<ShiftLayer> ShiftLayers { get; init; } = [];
}
