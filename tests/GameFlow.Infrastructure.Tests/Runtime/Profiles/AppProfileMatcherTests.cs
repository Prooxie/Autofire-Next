using GameFlow.Infrastructure.Runtime.Profiles;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Profiles;

/// <summary>
/// Which profile a foreground application calls for.
///
/// <para>
/// The decision is separated from the polling service precisely so it
/// can be tested like this — no host, no real foreground window, no
/// profile repository. What is being pinned is mostly what the matcher
/// declines to do: an app nobody wrote a rule for must not cause a
/// switch, because the alternative is a user's mappings changing every
/// time they alt-tab to read a wiki.
/// </para>
/// </summary>
public sealed class AppProfileMatcherTests
{
    private static readonly AppProfileRule[] Rules =
    [
        new("eldenring", "souls-profile"),
        new("factorio", "factory-profile")
    ];

    [Fact]
    public void AMatchingAppSelectsItsProfile() =>
        Assert.Equal("souls-profile", AppProfileMatcher.Match(Rules, "eldenring"));

    [Fact]
    public void MatchingIgnoresCase() =>
        Assert.Equal("souls-profile", AppProfileMatcher.Match(Rules, "EldenRing"));

    /// <summary>
    /// The behaviour the whole feature hinges on. Returning null means
    /// "leave the profile alone", so alt-tabbing to a browser mid-game
    /// does nothing — where falling back to a default would swap the
    /// user's mappings out and back on every glance at a wiki.
    /// </summary>
    [Fact]
    public void AnUnknownAppSelectsNothingRatherThanADefault() =>
        Assert.Null(AppProfileMatcher.Match(Rules, "chrome"));

    [Fact]
    public void ADisabledRuleIsInvisible()
    {
        AppProfileRule[] rules = [new("eldenring", "souls-profile", Enabled: false)];
        Assert.Null(AppProfileMatcher.Match(rules, "eldenring"));
    }

    [Fact]
    public void ARuleNamingNoProfileIsSkippedRatherThanMatchingEmpty()
    {
        // A half-written rule must not shadow a working one below it.
        AppProfileRule[] rules =
        [
            new("eldenring", ""),
            new("eldenring", "souls-profile")
        ];
        Assert.Equal("souls-profile", AppProfileMatcher.Match(rules, "eldenring"));
    }

    [Fact]
    public void TheFirstMatchWins()
    {
        AppProfileRule[] rules =
        [
            new("eldenring", "first"),
            new("eldenring", "second")
        ];
        Assert.Equal("first", AppProfileMatcher.Match(rules, "eldenring"));
    }

    /// <summary>
    /// Users paste what they have — a path from "Open file location", or
    /// a name with .exe still attached. Both name the same application,
    /// and a rule that silently never fires looks exactly like a broken
    /// feature.
    /// </summary>
    [Theory]
    [InlineData("eldenring.exe")]
    [InlineData("ELDENRING.EXE")]
    [InlineData("C:\\Games\\ELDEN RING\\Game\\eldenring.exe")]
    [InlineData("  eldenring  ")]
    [InlineData("\"eldenring.exe\"")]
    public void ARuleWrittenAnyReasonableWayStillMatches(string written)
    {
        AppProfileRule[] rules = [new(written, "souls-profile")];
        Assert.Equal("souls-profile", AppProfileMatcher.Match(rules, "eldenring"));
    }

    [Fact]
    public void NoRulesOrNoAppSelectsNothing()
    {
        Assert.Null(AppProfileMatcher.Match(null, "eldenring"));
        Assert.Null(AppProfileMatcher.Match([], "eldenring"));
        Assert.Null(AppProfileMatcher.Match(Rules, null));
        Assert.Null(AppProfileMatcher.Match(Rules, "   "));
    }

    /// <summary>
    /// A rule is free text. Path APIs throw on characters the platform
    /// rejects, and a typo must not take down the poll loop.
    /// </summary>
    [Fact]
    public void AnUnparseableRuleDoesNotThrow()
    {
        AppProfileRule[] rules = [new("bad|name<>", "some-profile")];
        Assert.Null(AppProfileMatcher.Match(rules, "eldenring"));
    }

    [Theory]
    [InlineData("eldenring.exe", "eldenring")]
    [InlineData("C:\\Games\\x\\eldenring.exe", "eldenring")]
    [InlineData("  spaced.exe ", "spaced")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeReducesToTheBareExecutableName(string? input, string expected) =>
        Assert.Equal(expected, AppProfileMatcher.Normalize(input));
}
