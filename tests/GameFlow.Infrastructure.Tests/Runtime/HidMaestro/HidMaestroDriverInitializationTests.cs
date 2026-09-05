using GameFlow.Infrastructure.Runtime.HidMaestro;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.HidMaestro;

public sealed class HidMaestroDriverInitializationTests
{
    [Fact]
    public void CatalogBindingDoesNotInstallAndMultipleOutputsInstallOnce()
    {
        var context = new FakeContext();
        var initialization = new HidMaestroDriverInitialization(context);
        Assert.Equal(0, context.InstallCount);
        Assert.True(initialization.EnsureInstalled(out var failure));
        Assert.Null(failure);
        Assert.True(initialization.EnsureInstalled(out failure));
        Assert.Equal(1, context.InstallCount);
        Assert.Null(failure);
    }

    [Fact]
    public void FailureIsActionableAndDoesNotRepeatGlobalSweep()
    {
        var context = new FakeContext { Fail = true };
        var initialization = new HidMaestroDriverInitialization(context);
        Assert.False(initialization.EnsureInstalled(out var failure));
        Assert.Contains("denied", failure);
        Assert.Contains("restart", failure);
        Assert.False(initialization.EnsureInstalled(out var secondFailure));
        Assert.Equal(failure, secondFailure);
        Assert.Equal(1, context.InstallCount);
    }

    [Fact]
    public void MissingInstallMethodDoesNotPretendSuccess()
    {
        var initialization = new HidMaestroDriverInitialization(new object());
        Assert.False(initialization.EnsureInstalled(out var failure));
        Assert.Contains("InstallDriver", failure);
    }

    public sealed class FakeContext
    {
        public int InstallCount { get; private set; }
        public bool Fail { get; init; }
        public void InstallDriver()
        {
            InstallCount++;
            if (Fail) throw new InvalidOperationException("denied");
        }
    }
}
