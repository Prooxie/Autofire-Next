using GameFlow.Infrastructure.Runtime;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class DemoInputSourceTests
{
    [Theory]
    [InlineData(8.5, 1)]
    [InlineData(9.5, 2)]
    [InlineData(10.0, 0)]
    public void Demo_generates_expected_touch_contacts(double elapsed, int expectedCount)
    {
        var snapshot = DemoInputSource.GenerateSnapshot(elapsed, "Demo");

        Assert.Equal(expectedCount, snapshot.TouchContacts.Count);
        Assert.Equal(expectedCount, snapshot.TouchContactCount);
        Assert.Equal(expectedCount > 0, snapshot.TouchDown);
        Assert.All(snapshot.TouchContacts, contact =>
        {
            Assert.InRange(contact.X, 0f, 1f);
            Assert.InRange(contact.Y, 0f, 1f);
        });
    }

    [Fact]
    public void Demo_touch_position_moves_during_contact_window()
    {
        var first = DemoInputSource.GenerateSnapshot(8.5, "Demo").TouchContacts[0];
        var later = DemoInputSource.GenerateSnapshot(8.8, "Demo").TouchContacts[0];

        Assert.NotEqual(first.X, later.X);
        Assert.NotEqual(first.Y, later.Y);
    }
}
