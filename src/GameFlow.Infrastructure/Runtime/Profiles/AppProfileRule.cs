namespace GameFlow.Infrastructure.Runtime.Profiles;

/// <summary>
/// "When this application is in front, use that profile."
/// </summary>
/// <param name="ProcessName">
/// Executable name without path or extension — <c>eldenring</c>, not
/// <c>C:\Games\ELDEN RING\eldenring.exe</c>. Matched case-insensitively.
/// </param>
/// <param name="ProfileId">Profile to activate. A rule naming a profile that no longer exists is skipped, not an error.</param>
/// <param name="Enabled">Lets a user park a rule without deleting it.</param>
public sealed record AppProfileRule(string ProcessName, string ProfileId, bool Enabled = true);

/// <summary>
/// Decides which profile a foreground application calls for.
///
/// <para>
/// Pure and separate from <see cref="ProfileAutoSwitchService"/> because
/// the interesting behaviour is all in the decision — first match wins,
/// an unknown app changes nothing, a disabled rule is invisible — and
/// none of it should need a running host, a real foreground window, or a
/// profile repository to test.
/// </para>
/// </summary>
public static class AppProfileMatcher
{
    /// <summary>
    /// The profile id for <paramref name="processName"/>, or
    /// <see langword="null"/> when no rule claims it.
    ///
    /// <para>
    /// <b>An unmatched app deliberately means "change nothing"</b>, not
    /// "go back to a default". Someone who alt-tabs from a game to a
    /// browser to read a wiki has not stopped playing, and swapping their
    /// mappings out from under them mid-session — then back on return —
    /// would be worse than useless. Leaving the last matched profile in
    /// place means the rules only ever fire on a deliberate switch INTO
    /// a known application.
    /// </para>
    ///
    /// <para>
    /// First match wins rather than most-specific-wins: names are exact,
    /// so two rules can only both match if the user entered the same
    /// process twice, and in that case list order is the only ordering
    /// they can see or control.
    /// </para>
    /// </summary>
    public static string? Match(IReadOnlyList<AppProfileRule>? rules, string? processName)
    {
        if (rules is null || rules.Count == 0 || string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.ProfileId))
            {
                continue;
            }

            if (string.Equals(Normalize(rule.ProcessName), Normalize(processName), StringComparison.OrdinalIgnoreCase))
            {
                return rule.ProfileId;
            }
        }

        return null;
    }

    /// <summary>
    /// Trims a rule's process name to the bare executable name.
    ///
    /// <para>
    /// Users paste what they have: a full path from Task Manager's "Open
    /// file location", or a name with <c>.exe</c> still on it. Both mean
    /// the same application, and rejecting them — or worse, silently
    /// never matching — would look like the feature is broken. Normalise
    /// on both sides of the comparison so the rule works however it was
    /// entered.
    /// </para>
    ///
    /// <para>
    /// Public because the settings UI normalises on the way IN as well,
    /// so the rule list shows the name that will actually be matched
    /// rather than the path the user pasted — being able to see the rule
    /// is wrong beats waiting for it to not fire.
    /// </para>
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().Trim('"');

        // GetFileNameWithoutExtension throws on characters the platform
        // rejects in a path, and a rule is free text the user typed.
        try
        {
            var name = Path.GetFileNameWithoutExtension(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
    }
}
