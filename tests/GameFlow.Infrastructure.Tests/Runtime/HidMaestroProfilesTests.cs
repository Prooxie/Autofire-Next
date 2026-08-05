using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Runtime.HidMaestro;
using GameFlow.Infrastructure.Runtime.Templates;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

/// <summary>
/// Contracts of the kind ↔ HIDMaestro-catalog mapping: the verified
/// catalog slugs come first in each candidate list (the old single-guess
/// "dualshock4" did not exist in the catalog, which is why DS4 slots
/// never created a device), the generic kind carries no slug at all
/// (it's built at runtime), and any catalog profile classifies into a
/// kind family that in turn resolves to the dashboard style — the
/// "theme follows the selected output controller" chain.
/// </summary>
public sealed class HidMaestroProfilesTests
{
    [Theory]
    [InlineData("dualsense-edge", "DualSense Edge", "Sony", true)]
    [InlineData("switch-pro", "Nintendo Switch Pro Controller", "Nintendo", true)]
    [InlineData("8bitdo-ultimate", "Ultimate Bluetooth Controller", "8BitDo", true)]
    [InlineData("arcade-fight-stick", "Arcade Fight Stick", "Hori", true)]
    [InlineData("logitech-g29", "G29 Racing Wheel", "Logitech", false)]
    [InlineData("thrustmaster-hotas", "T.16000M HOTAS", "Thrustmaster", false)]
    [InlineData("flight-yoke", "Flight Yoke System", "Logitech", false)]
    [InlineData("rudder-pedals", "Pro Flight Rudder Pedals", "Saitek", false)]
    public void Catalog_picker_keeps_game_controllers_and_hides_specialized_simulation_devices(
        string id,
        string name,
        string vendor,
        bool expected)
    {
        var profile = new HidMaestroCatalogProfile(id, name, vendor, 0, 0, 12, 6, true, "usb", true);

        Assert.Equal(expected, HidMaestroProfileCatalogService.IsGameControllerProfile(profile));
    }

    [Fact]
    public void Xbox360_first_candidate_is_the_verified_catalog_slug()
    {
        Assert.Equal("xbox-360-wired", HidMaestroProfiles.GetCandidateProfileIds(VirtualControllerKind.Xbox360)[0]);
    }

    [Fact]
    public void XboxSeries_first_candidate_is_the_verified_catalog_slug()
    {
        Assert.Equal("xbox-series-xs-bt", HidMaestroProfiles.GetCandidateProfileIds(VirtualControllerKind.XboxSeries)[0]);
    }

    [Fact]
    public void XboxOne_candidates_degrade_to_a_verified_family_member()
    {
        // The One-generation slugs are unverified against the shipped
        // catalog, so the list must end in a slug we KNOW exists —
        // resolution walks the list against the live catalog and takes
        // the first real one.
        var candidates = HidMaestroProfiles.GetCandidateProfileIds(VirtualControllerKind.XboxOne);
        Assert.NotEmpty(candidates);
        Assert.Contains("xbox-series-xs-bt", candidates);
    }

    [Fact]
    public void Every_selectable_kind_has_a_label()
    {
        foreach (var kind in HidMaestroProfiles.SelectableKinds)
        {
            Assert.False(string.IsNullOrWhiteSpace(HidMaestroProfiles.LabelFor(kind)));
        }
        Assert.Contains(VirtualControllerKind.XboxOne, HidMaestroProfiles.SelectableKinds);
        Assert.Contains(VirtualControllerKind.XboxSeries, HidMaestroProfiles.SelectableKinds);
    }

    [Fact]
    public void DualShock4_first_candidate_is_the_verified_catalog_slug_not_the_old_guess()
    {
        var candidates = HidMaestroProfiles.GetCandidateProfileIds(VirtualControllerKind.DualShock4);
        Assert.Equal("dualshock-4-v1-full", candidates[0]);
        // The old, nonexistent guess stays as a low-priority alias only.
        Assert.NotEqual("dualshock4", candidates[0]);
        Assert.Contains("dualshock4", candidates);
    }

    [Fact]
    public void DualSense_first_candidate_is_the_verified_catalog_slug()
    {
        Assert.Equal("dualsense", HidMaestroProfiles.GetCandidateProfileIds(VirtualControllerKind.DualSense)[0]);
    }

    [Fact]
    public void Generic_kind_has_no_catalog_candidates_because_it_is_built_at_runtime()
    {
        Assert.Empty(HidMaestroProfiles.GetCandidateProfileIds(VirtualControllerKind.GenericDirectInput));
    }

    [Theory]
    [InlineData("dualsense", null, VirtualControllerKind.DualSense)]
    [InlineData("dualsense-edge", null, VirtualControllerKind.DualSense)]
    [InlineData("dualshock-4-v1-full", null, VirtualControllerKind.DualShock4)]
    [InlineData("dualshock-3", null, VirtualControllerKind.DualShock4)]
    [InlineData("xbox-360-wired", null, VirtualControllerKind.Xbox360)]
    [InlineData("xbox-series-xs-bt", null, VirtualControllerKind.XboxSeries)]
    [InlineData("xbox-one-s-bt", null, VirtualControllerKind.XboxOne)]
    [InlineData("logitech-g29", "Logitech G29 Racing Wheel", VirtualControllerKind.GenericDirectInput)]
    // SwitchPro is a first-class output kind (VirtualControllerKind.SwitchPro),
    // so a Switch Pro classifies as itself rather than falling through to
    // the generic bucket the way it did before that kind existed.
    [InlineData("switch-pro", "Nintendo Switch Pro Controller", VirtualControllerKind.SwitchPro)]
    [InlineData("thrustmaster-t16000m", null, VirtualControllerKind.GenericDirectInput)]
    public void Catalog_profiles_classify_into_kind_families(string id, string? name, VirtualControllerKind expected)
    {
        Assert.Equal(expected, HidMaestroProfiles.ClassifyFamily(id, name));
    }

    [Fact]
    public void Classification_also_reads_the_display_name()
    {
        // An id that says nothing, rescued by the name.
        Assert.Equal(VirtualControllerKind.DualSense,
            HidMaestroProfiles.ClassifyFamily("sony-054c-0ce6", "DualSense Wireless Controller"));
    }

    [Theory]
    [InlineData(VirtualControllerKind.Xbox360, ControllerVisualStyle.Xbox360)]
    [InlineData(VirtualControllerKind.XboxOne, ControllerVisualStyle.XboxOne)]
    [InlineData(VirtualControllerKind.XboxSeries, ControllerVisualStyle.XboxSeries)]
    [InlineData(VirtualControllerKind.DualShock4, ControllerVisualStyle.PlayStation4)]
    [InlineData(VirtualControllerKind.DualSense, ControllerVisualStyle.PlayStation5)]
    // SimpleGamepad, NOT None: None means "render nothing", which is how
    // generic slots ended up as an empty panel with a no-theme message.
    [InlineData(VirtualControllerKind.GenericDirectInput, ControllerVisualStyle.SimpleGamepad)]
    public void Kind_families_resolve_to_dashboard_styles(VirtualControllerKind kind, ControllerVisualStyle expected)
    {
        Assert.Equal(expected, HidMaestroProfiles.ResolveVisualStyle(kind));
    }

    [Fact]
    public void Preactivation_signatures_use_the_real_catalog_identities()
    {
        Assert.Equal(((ushort)0x045E, (ushort)0x028E), HidMaestroProfiles.ResolveHardwareSignature(VirtualControllerKind.Xbox360));
        // DualSense advertises Sony 054C:0CE6 (the previous code carried
        // the DS4v2 PID here, so the emitted DualSense was never hidden
        // from the input list).
        Assert.Equal(((ushort)0x054C, (ushort)0x0CE6), HidMaestroProfiles.ResolveHardwareSignature(VirtualControllerKind.DualSense));
        Assert.Null(HidMaestroProfiles.ResolveHardwareSignature(VirtualControllerKind.GenericDirectInput));
    }

    [Fact]
    public void Template_defaults_deploy_the_generic_identity_convention()
    {
        var template = new DeviceOutputTemplate();
        Assert.Equal(0xBEEF, template.GenericVendorId);
        Assert.Equal(0xF001, template.GenericProductId);
        Assert.Equal(string.Empty, template.OutputProfileId);
    }

    [Fact]
    public void Template_clone_carries_the_new_fields()
    {
        var template = new DeviceOutputTemplate
        {
            OutputProfileId = "logitech-g29",
            GenericVendorId = 0x1234,
            GenericProductId = 0x5678,
            DemoPreview = true,
        };

        var clone = template.Clone();

        Assert.Equal("logitech-g29", clone.OutputProfileId);
        Assert.Equal(0x1234, clone.GenericVendorId);
        Assert.Equal(0x5678, clone.GenericProductId);
        Assert.True(clone.DemoPreview);
    }
}
