using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Input;
using GameFlow.Infrastructure.Runtime.Input.Mac;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input.Mac;

public sealed class MacKeyCodeMapTests
{
    [Theory]
    [InlineData(0x00, 0x41)] // kVK_ANSI_A -> VK_A
    [InlineData(0x06, 0x5A)] // kVK_ANSI_Z -> VK_Z (Mac's own non-alphabetic scancode order)
    [InlineData(0x31, 0x20)] // kVK_Space -> VK_SPACE
    [InlineData(0x24, 0x0D)] // kVK_Return -> VK_RETURN
    [InlineData(0x35, 0x1B)] // kVK_Escape -> VK_ESCAPE
    [InlineData(0x33, 0x08)] // kVK_Delete (Mac's Backspace) -> VK_BACK
    [InlineData(0x75, 0x2E)] // kVK_ForwardDelete (PC-style Delete) -> VK_DELETE
    [InlineData(0x7E, 0x26)] // kVK_UpArrow -> VK_UP
    [InlineData(0x38, 0xA0)] // kVK_Shift -> VK_LSHIFT
    [InlineData(0x37, 0x5B)] // kVK_Command -> VK_LWIN
    [InlineData(0x18, 0xBB)] // kVK_ANSI_Equal -> VK_OEM_PLUS
    [InlineData(0x7A, 0x70)] // kVK_F1 -> VK_F1
    [InlineData(0x52, 0x60)] // kVK_ANSI_Keypad0 -> VK_NUMPAD0
    [InlineData(0x4C, KeyboardVirtualKeys.NumpadEnter)]
    public void MacToVirtualKey_MapsKnownCodesCorrectly(int macKeycode, int expectedVirtualKey)
    {
        Assert.True(MacKeyCodeMap.MacToVirtualKey.TryGetValue(macKeycode, out var vk));
        Assert.Equal(expectedVirtualKey, vk);
    }

    [Fact]
    public void MacToVirtualKey_CoversEveryLetter()
    {
        int[] letterCodes = [0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25,
            0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06];
        Assert.Equal(26, letterCodes.Length);
        foreach (var code in letterCodes)
        {
            Assert.True(MacKeyCodeMap.MacToVirtualKey.ContainsKey(code), $"Mac keycode {code:X} has no VK mapping");
        }
    }

    [Fact]
    public void MacToVirtualKey_CoversAllTenDigits()
    {
        int[] digitCodes = [0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19, 0x1D];
        Assert.Equal(10, digitCodes.Length);
        foreach (var code in digitCodes)
        {
            Assert.True(MacKeyCodeMap.MacToVirtualKey.ContainsKey(code), $"Mac keycode {code:X} has no VK mapping");
        }
    }

    [Fact]
    public void MacToVirtualKey_HasNoDuplicateVirtualKeyTargets()
    {
        var values = MacKeyCodeMap.MacToVirtualKey.Values.ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}

public sealed class MacEventInteropTests
{
    [Fact]
    public void EventMask_SetsExactlyOneBitPerEventType()
    {
        var mask = MacEventInterop.EventMask(MacEventInterop.kCGEventKeyDown, MacEventInterop.kCGEventKeyUp);

        Assert.Equal((1UL << 10) | (1UL << 11), mask);
        Assert.NotEqual(0UL, mask & (1UL << (int)MacEventInterop.kCGEventKeyDown));
        Assert.NotEqual(0UL, mask & (1UL << (int)MacEventInterop.kCGEventKeyUp));
        Assert.Equal(0UL, mask & (1UL << (int)MacEventInterop.kCGEventMouseMoved)); // NOT requested — must stay clear
    }

    [Fact]
    public void EventMask_CombinesManyTypesWithoutCollision()
    {
        var mask = MacEventInterop.EventMask(
            MacEventInterop.kCGEventLeftMouseDown, MacEventInterop.kCGEventLeftMouseUp,
            MacEventInterop.kCGEventMouseMoved, MacEventInterop.kCGEventOtherMouseDown);

        var expected = (1UL << 1) | (1UL << 2) | (1UL << 5) | (1UL << 25);
        Assert.Equal(expected, mask);
    }

    [Fact]
    public void CGPoint_MarshalsAsTwoDoubles()
    {
        // CGPoint is publicly documented as { CGFloat x, y }, and CGFloat
        // is a double on 64-bit macOS — 16 bytes is the expected size,
        // though (like everything else in this file) unverified against
        // a real header.
        var size = System.Runtime.InteropServices.Marshal.SizeOf<MacEventInterop.CGPoint>();
        Assert.Equal(16, size);
    }
}

public sealed class MacInputDeviceScannerTests
{
    [Fact]
    public void ScanReportsNothingOffMacOS()
    {
        // The contract changed with the IOHIDManager rewrite. It used to
        // publish two fixed aggregate rows on EVERY platform, because
        // CGEventTap could not tell devices apart and there was nothing
        // real to enumerate. Now there is, so a non-macOS host — which
        // has no IOKit to ask — reports nothing rather than inventing
        // devices that do not exist.
        //
        // This is the only assertion about Scan that can run here. The
        // enumeration itself needs a Mac; see MacHidInterop's note.
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Empty(MacInputDeviceScanner.Scan());
    }

    [Fact]
    public void TheAggregateIdsAreKeptForProfilesSavedBeforePerDeviceEnumeration()
    {
        // A slot saved on the old build references one of these. Dropping
        // the constants would compile fine and silently lose that slot's
        // assignment on upgrade, which is the kind of break nobody
        // notices until their setup is gone.
        Assert.False(string.IsNullOrWhiteSpace(MacInputDeviceScanner.AggregateKeyboardId));
        Assert.False(string.IsNullOrWhiteSpace(MacInputDeviceScanner.AggregateMouseId));
    }
}
