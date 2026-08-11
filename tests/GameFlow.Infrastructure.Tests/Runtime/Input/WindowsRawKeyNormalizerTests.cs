using GameFlow.Infrastructure.Runtime.Input;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input;

public sealed class WindowsRawKeyNormalizerTests
{
    [Theory]
    [InlineData(0x10, 0x2A, 0, 0xA0)] // generic Shift + left make code
    [InlineData(0x10, 0x36, 0, 0xA1)] // generic Shift + right make code
    [InlineData(0x11, 0x1D, 0, 0xA2)] // generic Control
    [InlineData(0x11, 0x1D, WindowsRawKeyNormalizer.KeyE0, 0xA3)]
    [InlineData(0x12, 0x38, 0, 0xA4)] // generic Alt
    [InlineData(0x12, 0x38, WindowsRawKeyNormalizer.KeyE0, 0xA5)]
    public void Normalize_PreservesModifierSide(
        ushort virtualKey,
        ushort makeCode,
        ushort flags,
        int expected)
    {
        Assert.Equal(expected, WindowsRawKeyNormalizer.Normalize(virtualKey, makeCode, flags));
    }

    [Fact]
    public void Normalize_DistinguishesMainAndNumpadEnter()
    {
        Assert.Equal(0x0D, WindowsRawKeyNormalizer.Normalize(0x0D, 0x1C, 0));
        Assert.Equal(
            KeyboardVirtualKeys.NumpadEnter,
            WindowsRawKeyNormalizer.Normalize(0x0D, 0x1C, WindowsRawKeyNormalizer.KeyE0));
    }

    [Theory]
    [InlineData(0x2D, 0x52, 0x60)] // Insert -> keypad 0
    [InlineData(0x23, 0x4F, 0x61)] // End -> keypad 1
    [InlineData(0x28, 0x50, 0x62)] // Down -> keypad 2
    [InlineData(0x22, 0x51, 0x63)] // Page Down -> keypad 3
    [InlineData(0x25, 0x4B, 0x64)] // Left -> keypad 4
    [InlineData(0x0C, 0x4C, 0x65)] // Clear -> keypad 5
    [InlineData(0x27, 0x4D, 0x66)] // Right -> keypad 6
    [InlineData(0x24, 0x47, 0x67)] // Home -> keypad 7
    [InlineData(0x26, 0x48, 0x68)] // Up -> keypad 8
    [InlineData(0x21, 0x49, 0x69)] // Page Up -> keypad 9
    [InlineData(0x2E, 0x53, 0x6E)] // Delete -> keypad decimal
    public void Normalize_KeepsNumpadIdentityWhenNumLockIsOff(
        ushort virtualKey,
        ushort makeCode,
        int expected)
    {
        Assert.Equal(expected, WindowsRawKeyNormalizer.Normalize(virtualKey, makeCode, 0));
    }

    [Fact]
    public void Normalize_DoesNotConvertExtendedNavigationCluster()
    {
        Assert.Equal(0x24, WindowsRawKeyNormalizer.Normalize(0x24, 0x47, WindowsRawKeyNormalizer.KeyE0));
        Assert.Equal(0x2E, WindowsRawKeyNormalizer.Normalize(0x2E, 0x53, WindowsRawKeyNormalizer.KeyE0));
    }

    [Theory]
    [InlineData(0x10, 0x2A, WindowsRawKeyNormalizer.KeyE0)] // Print Screen's synthetic Shift
    [InlineData(0x11, 0x1D, WindowsRawKeyNormalizer.KeyE1)] // Pause's synthetic Control
    public void Normalize_DropsSyntheticModifierPackets(ushort virtualKey, ushort makeCode, ushort flags)
    {
        Assert.Equal(0, WindowsRawKeyNormalizer.Normalize(virtualKey, makeCode, flags));
    }
}
