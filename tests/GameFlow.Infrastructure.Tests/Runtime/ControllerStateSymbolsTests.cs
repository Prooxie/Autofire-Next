using GameFlow.Core.Models;
using GameFlow.Infrastructure.Theming;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class ControllerStateSymbolsTests
{
    [Fact]
    public void Resolves_touch_contacts_in_vscview_centered_coordinates()
    {
        var snapshot = ControllerSnapshot.Empty().WithTouchContacts(
        [
            new TouchContact(2, 0.25f, 0.75f),
            new TouchContact(7, 1f, 0f)
        ]);
        var symbols = new ControllerStateSymbols().UpdateSnapshot(snapshot);

        Assert.Equal(1, symbols.Resolve("touch_center:0:touch"));
        Assert.Equal(-0.5, symbols.Resolve("touch_center:0:x"), 5);
        Assert.Equal(0.5, symbols.Resolve("touch_center:0:y"), 5);
        Assert.Equal(1, symbols.Resolve("touch_center:1:x"), 5);
        Assert.Equal(-1, symbols.Resolve("touch_center:1:y"), 5);
    }

    [Fact]
    public void Does_not_duplicate_unified_touchpad_through_legacy_side_aliases()
    {
        var snapshot = ControllerSnapshot.Empty().WithTouchContacts([new TouchContact(0, 0.4f, 0.6f)]);
        var symbols = new ControllerStateSymbols().UpdateSnapshot(snapshot);

        Assert.Equal(0, symbols.Resolve("touch_left:0:touch"));
        Assert.Equal(0, symbols.Resolve("touch_right:click"));
    }

    [Fact]
    public void Uses_legacy_primary_touch_coordinates_when_contact_list_is_unavailable()
    {
        var snapshot = ControllerSnapshot.Empty()
            .WithTouch(true, 0.75f, 0.25f)
            .WithTouchContactCount(1);
        var symbols = new ControllerStateSymbols().UpdateSnapshot(snapshot);

        Assert.Equal(1, symbols.Resolve("touch_center:0:touch"));
        Assert.Equal(0.5, symbols.Resolve("touch_center:0:x"), 5);
        Assert.Equal(-0.5, symbols.Resolve("touch_center:0:y"), 5);
    }
}
