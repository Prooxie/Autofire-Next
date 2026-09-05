using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

/// <summary>
/// Covers hiding GameFlow's own emitted pads from the input device list.
/// </summary>
/// <remarks>
/// The filter answers "did this device appear at or after the sink that
/// owns its vendor/product pair activated?". Getting that wrong publishes
/// a virtual pad as physical hardware, which can then be assigned to
/// another slot as a feedback loop and is battery-polled for a number
/// that means nothing.
/// </remarks>
public sealed class InputDeviceCatalogVirtualFilterTests
{
    private const ushort Xbox360Vid = 0x045E;
    private const ushort Xbox360Pid = 0x028E;

    private static InputDeviceCatalog NewCatalog() =>
        new(new StubLocalization(), new DeviceCategoryOverrideStore(NullLogger<DeviceCategoryOverrideStore>.Instance));

    private static InputDeviceInfo Pad(string id, ushort vid = Xbox360Vid, ushort pid = Xbox360Pid) =>
        new(id, "Xbox 360 Controller", IsConnected: true, IsSelected: false,
            VendorId: vid, ProductId: pid, IsGamepad: true, Category: DeviceCategory.Gamepad);

    [Fact]
    public void APadThatAppearsAfterItsSinkActivatedIsHidden()
    {
        var catalog = NewCatalog();
        catalog.SetIgnoredHardwareSignatures(
            [(Xbox360Vid, Xbox360Pid, DateTimeOffset.UtcNow.AddSeconds(-1))]);

        catalog.ReplaceDevices("sdl", [Pad("sdl-gamepad-virtual")]);

        Assert.Empty(catalog.Devices);
    }

    /// <summary>
    /// A real pad of the same model that was already plugged in stays
    /// selectable. This is the reason the filter is time-based at all.
    /// </summary>
    [Fact]
    public void APadThatWasAlreadyPresentBeforeActivationStaysVisible()
    {
        var catalog = NewCatalog();
        catalog.ReplaceDevices("sdl", [Pad("sdl-gamepad-real")]);

        catalog.SetIgnoredHardwareSignatures(
            [(Xbox360Vid, Xbox360Pid, DateTimeOffset.UtcNow.AddSeconds(30))]);
        catalog.ReplaceDevices("sdl", [Pad("sdl-gamepad-real")]);

        Assert.Single(catalog.Devices);
    }

    /// <summary>
    /// The regression this was written for.
    /// </summary>
    /// <remarks>
    /// A device id is a hash of vendor, product and name, so a virtual pad
    /// that is removed and re-created hashes to exactly the same id. The
    /// catalog remembered when it first saw that id and never forgot, so
    /// the re-created pad inherited a timestamp from before this run's
    /// sink existed — and the filter, asking whether it appeared after
    /// activation, always answered no. One crashed run left an orphan
    /// behind, the orphan seeded the stale timestamp, and every launch
    /// afterwards published the virtual pad as physical hardware even
    /// once the orphan itself had been swept away.
    /// </remarks>
    [Fact]
    public void APadRecreatedUnderTheSameIdIsJudgedOnItsNewArrival()
    {
        var catalog = NewCatalog();

        // A leftover from a previous run, present before anything activates.
        catalog.ReplaceDevices("sdl", [Pad("sdl-gamepad-virtual")]);
        Assert.Single(catalog.Devices);

        // Swept away.
        catalog.ReplaceDevices("sdl", []);
        Assert.Empty(catalog.Devices);

        // This run's sink activates and creates its own pad, which hashes
        // to the same id as the one that just left.
        catalog.SetIgnoredHardwareSignatures(
            [(Xbox360Vid, Xbox360Pid, DateTimeOffset.UtcNow)]);
        catalog.ReplaceDevices("sdl", [Pad("sdl-gamepad-virtual")]);

        Assert.Empty(catalog.Devices);
    }

    [Fact]
    public void ADifferentModelIsNeverHidden()
    {
        var catalog = NewCatalog();
        catalog.SetIgnoredHardwareSignatures(
            [(Xbox360Vid, Xbox360Pid, DateTimeOffset.UtcNow.AddSeconds(-1))]);

        catalog.ReplaceDevices("sdl", [Pad("sdl-gamepad-dualsense", 0x054C, 0x0CE6)]);

        Assert.Single(catalog.Devices);
    }

    private sealed class StubLocalization : ILocalizationService
    {
        public event EventHandler? CultureChanged { add { } remove { } }

        public IReadOnlyList<LanguageOption> SupportedLanguages => [];

        public string CurrentCulture => "en";

        public string this[string key] => key;

        public string Translate(string key) => key;

        public void SetCulture(string cultureCode) { }
    }
}
