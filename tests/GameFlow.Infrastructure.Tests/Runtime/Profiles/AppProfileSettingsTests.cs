using System.Text.Json;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Runtime.Profiles;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Profiles;

/// <summary>
/// The per-app rules survive a trip through the settings file.
///
/// <para>
/// Worth its own tests because the failure is disproportionate: rules
/// live inside <see cref="AppSettings"/>, which is loaded once at
/// startup, so a shape System.Text.Json cannot round-trip does not lose
/// the rules — it takes the user's window size, culture, paths and
/// active profile with them. The rules are the first positional record
/// in a list to go into that file.
/// </para>
/// </summary>
public sealed class AppProfileSettingsTests
{
    private static AppSettings RoundTrip(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, ProfileJsonOptions.Default),
            ProfileJsonOptions.Default)!;

    [Fact]
    public void RulesSurviveSaveAndLoad()
    {
        var saved = RoundTrip(new AppSettings
        {
            AutoSwitchProfilesByApp = true,
            AppProfileRules =
            [
                new AppProfileRule("eldenring", "souls-profile"),
                new AppProfileRule("factorio", "factory-profile", Enabled: false)
            ]
        });

        Assert.True(saved.AutoSwitchProfilesByApp);
        Assert.Equal(2, saved.AppProfileRules.Count);
        Assert.Equal("eldenring", saved.AppProfileRules[0].ProcessName);
        Assert.Equal("souls-profile", saved.AppProfileRules[0].ProfileId);
        Assert.True(saved.AppProfileRules[0].Enabled);
        Assert.False(saved.AppProfileRules[1].Enabled);
    }

    [Fact]
    public void RuleOrderIsPreservedBecauseFirstMatchWins()
    {
        var saved = RoundTrip(new AppSettings
        {
            AppProfileRules =
            [
                new AppProfileRule("a", "first"),
                new AppProfileRule("b", "second"),
                new AppProfileRule("c", "third")
            ]
        });

        Assert.Equal(["first", "second", "third"], saved.AppProfileRules.Select(r => r.ProfileId));
    }

    /// <summary>
    /// A settings file written by a build before this feature existed
    /// has no such key at all, and it must load with the feature off
    /// rather than failing and resetting everything else to defaults.
    /// </summary>
    [Fact]
    public void ASettingsFileFromBeforeThisFeatureStillLoads()
    {
        var older = """
        { "activeProfileId": "speedrunner-default", "selectedCulture": "cs", "windowWidth": 1600 }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(older, ProfileJsonOptions.Default)!;

        Assert.False(loaded.AutoSwitchProfilesByApp);
        Assert.Empty(loaded.AppProfileRules);
        Assert.Equal("cs", loaded.SelectedCulture);
        Assert.Equal(1600, loaded.WindowWidth);
    }

    /// <summary>
    /// Enabled defaults to true, so a rule written by hand — or by a
    /// future build that drops the redundant key — is live rather than
    /// silently parked.
    /// </summary>
    [Fact]
    public void ARuleWithNoEnabledKeyIsEnabled()
    {
        var json = """
        { "appProfileRules": [ { "processName": "eldenring", "profileId": "souls" } ] }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(json, ProfileJsonOptions.Default)!;

        Assert.True(Assert.Single(loaded.AppProfileRules).Enabled);
    }
}
