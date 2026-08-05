using GameFlow.Infrastructure.Runtime.Input.Linux;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input.Linux;

public sealed class EvdevInteropTests
{
    // Expected values hand-derived from this exact formula, verified
    // against /usr/include/asm-generic/ioctl.h and input.h on the build
    // machine (see EvdevInterop.cs's own comment for the derivation):
    // _IOC(dir,type,nr,size) = (dir<<30)|(type<<8)|(nr<<0)|(size<<16),
    // with dir=_IOC_READ=2, type='E'=0x45.
    //
    // EVIOCGBIT(EV_KEY=1, 36): nr=0x20+1=0x21, size=36=0x24
    //   0x80000000 | 0x00240000 | 0x00004500 | 0x00000021 = 0x80244521
    [Fact]
    public void EviocgbitEvKey_MatchesHandDerivedIoctlNumber()
    {
        Assert.Equal((nuint)0x80244521, EvdevInterop.EviocgbitEvKey(36));
    }

    // EVIOCGBIT(EV_REL=2, 4): nr=0x20+2=0x22, size=4
    //   0x80000000 | 0x00040000 | 0x00004500 | 0x00000022 = 0x80044522
    [Fact]
    public void EviocgbitEvRel_MatchesHandDerivedIoctlNumber()
    {
        Assert.Equal((nuint)0x80044522, EvdevInterop.EviocgbitEvRel(4));
    }

    // EVIOCGNAME(256): nr=0x06, size=256=0x100
    //   0x80000000 | 0x01000000 | 0x00004500 | 0x00000006 = 0x81004506
    [Fact]
    public void Eviocgname_MatchesHandDerivedIoctlNumber()
    {
        Assert.Equal((nuint)0x81004506, EvdevInterop.Eviocgname(256));
    }

    [Fact]
    public void InputEvent_MarshalsToExactlyTheVerified64BitEvdevWireSize()
    {
        // 8 (TvSec) + 8 (TvUsec) + 2 (Type) + 2 (Code) + 4 (Value) = 24,
        // already 8-byte aligned — no padding expected either way.
        var size = System.Runtime.InteropServices.Marshal.SizeOf<EvdevInterop.InputEvent>();
        Assert.Equal(24, size);
        Assert.Equal(EvdevInterop.InputEventSize, size);
    }
}

public sealed class EvdevKeyCodeMapTests
{
    [Theory]
    [InlineData(30, 0x41)]   // KEY_A -> VK_A
    [InlineData(44, 0x5A)]   // KEY_Z -> VK_Z (evdev's non-alphabetic scancode order — physical key layout, not letter order)
    [InlineData(57, 0x20)]   // KEY_SPACE -> VK_SPACE
    [InlineData(28, 0x0D)]   // KEY_ENTER -> VK_RETURN
    [InlineData(1, 0x1B)]    // KEY_ESC -> VK_ESCAPE
    [InlineData(42, 0xA0)]   // KEY_LEFTSHIFT -> VK_LSHIFT
    [InlineData(54, 0xA1)]   // KEY_RIGHTSHIFT -> VK_RSHIFT
    [InlineData(105, 0x25)]  // KEY_LEFT -> VK_LEFT
    [InlineData(59, 0x70)]   // KEY_F1 -> VK_F1
    [InlineData(11, 0x30)]   // KEY_0 -> VK_0
    public void EvdevToVirtualKey_MapsKnownCodesCorrectly(ushort evdevCode, int expectedVirtualKey)
    {
        Assert.True(EvdevKeyCodeMap.EvdevToVirtualKey.TryGetValue(evdevCode, out var vk));
        Assert.Equal(expectedVirtualKey, vk);
    }

    [Fact]
    public void EvdevToVirtualKey_CoversEveryLetter()
    {
        // KEY_A=30 .. not contiguous (evdev scancode order), so check by
        // the actual evdev codes rather than a numeric range.
        int[] letterCodes = [30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44];
        Assert.Equal(26, letterCodes.Length);
        foreach (var code in letterCodes)
        {
            Assert.True(EvdevKeyCodeMap.EvdevToVirtualKey.ContainsKey((ushort)code), $"evdev code {code} has no VK mapping");
        }
    }

    [Fact]
    public void EvdevToVirtualKey_HasNoUnintendedDuplicateVirtualKeyTargets()
    {
        // Two evdev codes mapping to the same VK makes them
        // indistinguishable downstream — a real bug, with ONE deliberate
        // exception: KEY_ENTER (28) and KEY_KPENTER (96) both map to
        // VK_RETURN because Windows itself reports both Enter keys that
        // way (they differ only by the extended-key flag, which this
        // contract doesn't carry). That collision is intentional and
        // documented in EvdevKeyCodeMap; anything else is not.
        const int VkReturn = 0x0D;

        var offenders = EvdevKeyCodeMap.EvdevToVirtualKey
            .Where(pair => pair.Value != VkReturn)
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"VK 0x{group.Key:X2} <- evdev {string.Join(", ", group.Select(p => p.Key))}")
            .ToList();

        Assert.True(offenders.Count == 0, "unintended duplicate VK targets: " + string.Join("; ", offenders));

        // And the intended pair is genuinely present, so this fails loudly
        // if either Enter mapping is ever silently dropped.
        var enterCodes = EvdevKeyCodeMap.EvdevToVirtualKey
            .Where(pair => pair.Value == VkReturn)
            .Select(pair => (int)pair.Key)
            .OrderBy(code => code)
            .ToList();
        Assert.Equal(new List<int> { 28, 96 }, enterCodes);
    }
}

public sealed class EvdevDeviceIdTests
{
    [Fact]
    public void BuildDeviceId_DiffersBetweenKeyboardAndMouseForTheSamePath()
    {
        var keyboardId = EvdevDeviceDiscovery.BuildDeviceId("keyboard", "/dev/input/event3");
        var mouseId = EvdevDeviceDiscovery.BuildDeviceId("mouse", "/dev/input/event3");

        Assert.NotEqual(keyboardId, mouseId);
    }

    [Fact]
    public void BuildDeviceId_IsDeterministic()
    {
        var first = EvdevDeviceDiscovery.BuildDeviceId("keyboard", "/dev/input/event3");
        var second = EvdevDeviceDiscovery.BuildDeviceId("keyboard", "/dev/input/event3");

        Assert.Equal(first, second);
    }
}
