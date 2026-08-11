using GameFlow.Infrastructure.Runtime.Input;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input;

public sealed class KeyboardPhysicalLayoutTests
{
    [Fact]
    public void Ansi104_ContainsExactly104DistinctPhysicalKeys()
    {
        var keys = KeyboardPhysicalLayout.Ansi104;

        Assert.Equal(KeyboardPhysicalLayout.Ansi104KeyCount, keys.Count);
        Assert.Equal(keys.Count, keys.Select(key => key.VirtualKey).Distinct().Count());
    }

    [Fact]
    public void Ansi104_KeyRectanglesStayInsideSurfaceAndNeverOverlap()
    {
        var keys = KeyboardPhysicalLayout.Ansi104;
        foreach (var key in keys)
        {
            Assert.True(key.X >= 0 && key.Y >= 0, $"{key.Label} starts outside the surface");
            Assert.True(key.X + key.Width <= KeyboardPhysicalLayout.SurfaceWidth,
                $"{key.Label} extends past the surface width");
            Assert.True(key.Y + key.Height <= KeyboardPhysicalLayout.SurfaceHeight,
                $"{key.Label} extends past the surface height");
        }

        for (var leftIndex = 0; leftIndex < keys.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < keys.Count; rightIndex++)
            {
                var left = keys[leftIndex];
                var right = keys[rightIndex];
                var overlapsHorizontally = left.X < right.X + right.Width && right.X < left.X + left.Width;
                var overlapsVertically = left.Y < right.Y + right.Height && right.Y < left.Y + left.Height;
                Assert.False(overlapsHorizontally && overlapsVertically,
                    $"{left.Label} (VK {left.VirtualKey:X}) overlaps {right.Label} (VK {right.VirtualKey:X})");
            }
        }
    }

    [Fact]
    public void Ansi104_HasCompleteNavigationAndNumericClusters()
    {
        var keys = KeyboardPhysicalLayout.Ansi104;

        AssertKey(keys, 0x2C, 522, 0);  // Print Screen
        AssertKey(keys, 0x91, 556, 0);  // Scroll Lock
        AssertKey(keys, 0x13, 590, 0);  // Pause
        AssertKey(keys, 0x2D, 522, 42); // Insert
        AssertKey(keys, 0x24, 556, 42); // Home
        AssertKey(keys, 0x21, 590, 42); // Page Up
        AssertKey(keys, 0x2E, 522, 76); // Delete
        AssertKey(keys, 0x23, 556, 76); // End
        AssertKey(keys, 0x22, 590, 76); // Page Down

        var numpad = keys.Where(key => key.X >= 638).ToList();
        Assert.Equal(17, numpad.Count);
        Assert.Contains(numpad, key => key.VirtualKey == 0x90); // Num Lock
        Assert.Contains(numpad, key => key.VirtualKey == 0x60 && key.Width == 64); // wide zero
        Assert.Contains(numpad, key => key.VirtualKey == 0x6B && key.Height == 64); // tall plus
        Assert.Contains(numpad, key => key.VirtualKey == KeyboardVirtualKeys.NumpadEnter && key.Height == 64);
    }

    [Fact]
    public void NumpadEnter_HasAStableDistinctDiagnosticName()
    {
        Assert.Equal("Enter", VirtualKeyNames.GetName(0x0D));
        Assert.Equal("Num Enter", VirtualKeyNames.GetName(KeyboardVirtualKeys.NumpadEnter));
    }

    private static void AssertKey(
        IReadOnlyList<PhysicalKeyboardKey> keys,
        int virtualKey,
        double expectedX,
        double expectedY)
    {
        var key = Assert.Single(keys, key => key.VirtualKey == virtualKey);
        Assert.Equal(expectedX, key.X);
        Assert.Equal(expectedY, key.Y);
    }
}
