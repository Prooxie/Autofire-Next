using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Input;
using GameFlow.Infrastructure.Runtime.Input.Mac;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input.Mac;

public sealed class MacHidKeyCodeMapTests
{
    [Theory]
    [InlineData(0x04, 0x41)] // HID Keyboard a -> VK_A (HID letter usages are alphabetical, unlike Carbon)
    [InlineData(0x1D, 0x5A)] // HID Keyboard z -> VK_Z
    [InlineData(0x2C, 0x20)] // HID Spacebar -> VK_SPACE
    [InlineData(0x28, 0x0D)] // HID Return -> VK_RETURN
    [InlineData(0x29, 0x1B)] // HID Escape -> VK_ESCAPE
    [InlineData(0x2A, 0x08)] // HID Backspace (Mac's "delete") -> VK_BACK
    [InlineData(0x4C, 0x2E)] // HID Delete Forward -> VK_DELETE
    [InlineData(0x52, 0x26)] // HID Up Arrow -> VK_UP
    [InlineData(0xE1, 0xA0)] // HID Left Shift -> VK_LSHIFT
    [InlineData(0xE3, 0x5B)] // HID Left GUI (Command) -> VK_LWIN
    [InlineData(0x2E, 0xBB)] // HID = and + -> VK_OEM_PLUS
    [InlineData(0x3A, 0x70)] // HID F1 -> VK_F1
    [InlineData(0x62, 0x60)] // HID Keypad 0 -> VK_NUMPAD0
    [InlineData(0x58, KeyboardVirtualKeys.NumpadEnter)]
    public void HidUsageToVirtualKey_MapsKnownUsagesCorrectly(int hidUsage, int expectedVirtualKey)
    {
        Assert.True(MacHidKeyCodeMap.HidUsageToVirtualKey.TryGetValue(hidUsage, out var vk));
        Assert.Equal(expectedVirtualKey, vk);
    }

    [Fact]
    public void HidUsageToVirtualKey_CoversEveryLetter()
    {
        for (var usage = 0x04; usage <= 0x1D; usage++)
        {
            Assert.True(MacHidKeyCodeMap.HidUsageToVirtualKey.ContainsKey(usage), $"HID usage {usage:X} has no VK mapping");
        }
    }

    [Fact]
    public void HidUsageToVirtualKey_CoversAllTenDigits()
    {
        for (var usage = 0x1E; usage <= 0x27; usage++)
        {
            Assert.True(MacHidKeyCodeMap.HidUsageToVirtualKey.ContainsKey(usage), $"HID usage {usage:X} has no VK mapping");
        }
    }

    [Fact]
    public void HidUsageToVirtualKey_HasNoDuplicateVirtualKeyTargets()
    {
        var values = MacHidKeyCodeMap.HidUsageToVirtualKey.Values.ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void HidUsageToVirtualKey_IgnoresTheKeyboardErrorCodes()
    {
        // 0x00 is reserved and 0x01-0x03 are ErrorRollOver / POSTFail /
        // ErrorUndefined — what a keyboard sends when it CANNOT report
        // the real state. Mapping any of them would turn "too many keys
        // held at once" into a phantom keypress that never releases.
        for (var usage = 0x00; usage < MacHidKeyCodeMap.FirstRealKeyUsage; usage++)
        {
            Assert.False(MacHidKeyCodeMap.HidUsageToVirtualKey.ContainsKey(usage), $"HID usage {usage:X} is an error code, not a key");
        }
    }

    [Fact]
    public void HidUsageToVirtualKey_DistinguishesPrintScreenFromF13()
    {
        // The Carbon table could not: it had no Print Screen keycode at
        // all, so a PC keyboard's Print Screen arrived as F13 and the map
        // guessed the legend from the physical position. HID reports both
        // separately, and conflating them again would be a regression.
        Assert.Equal(0x2C, MacHidKeyCodeMap.HidUsageToVirtualKey[0x46]); // Print Screen -> VK_SNAPSHOT
        Assert.Equal(0x7C, MacHidKeyCodeMap.HidUsageToVirtualKey[0x68]); // F13 -> VK_F13
    }
}

public sealed class MacEventInteropTests
{
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

public sealed class MacHidInteropTests
{
    [Fact]
    public void AccessTypeMatchesTheIOHIDCheckAccessContract()
    {
        // IOHIDCheckAccess returns kIOHIDAccessTypeGranted(0) /
        // Denied(1) / Unknown(2). The reader branches on these values to
        // decide whether a missing input stream is a consent decision or
        // a real failure, so the numbering is load-bearing: shifting it
        // would report a denial as "granted" and send a tester hunting
        // for a bug that is really an unticked checkbox.
        Assert.Equal(0, (int)MacHidInterop.AccessType.Granted);
        Assert.Equal(1, (int)MacHidInterop.AccessType.Denied);
        Assert.Equal(2, (int)MacHidInterop.AccessType.Unknown);
    }

    [Fact]
    public void TheElementUsagePagesAreTheOnesTheCallbackSwitchesOn()
    {
        // Keyboard values, mouse buttons and mouse axes are told apart
        // purely by their element's usage page. These three constants are
        // the whole routing table in MacRawInputReader.HandleValue.
        Assert.Equal(0x07u, MacHidInterop.UsagePageKeyboard);
        Assert.Equal(0x09u, MacHidInterop.UsagePageButton);
        Assert.Equal(0x01, MacHidInterop.UsagePageGenericDesktop);

        Assert.Equal(0x30u, MacHidInterop.UsageX);
        Assert.Equal(0x31u, MacHidInterop.UsageY);
        Assert.Equal(0x38u, MacHidInterop.UsageWheel);
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
