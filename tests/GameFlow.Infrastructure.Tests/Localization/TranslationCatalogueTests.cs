using System.Text.RegularExpressions;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Localization;

/// <summary>
/// Guards the translation catalogues against drift.
/// </summary>
/// <remarks>
/// <para>
/// These gaps are invisible in development and only ever show up to the
/// people least able to report them: an English-speaking developer adds a
/// key, ships, and a Polish user gets a raw <c>SidebarWalkthrough</c> in
/// their sidebar. Nothing failed, nothing logged.
/// </para>
/// <para>
/// At the time this was written the catalogues had drifted exactly that
/// way — six of the eight languages were missing six keys each, carried
/// two keys nobody used any more, and eight keys the code asked for were
/// in no catalogue at all. Each is cheap to fix and impossible to notice
/// by hand, which is what a test is for.
/// </para>
/// </remarks>
public sealed class TranslationCatalogueTests
{
    private static readonly Regex MsgId = new(@"^msgid\s+""([^""]*)""", RegexOptions.Multiline);
    private static readonly Regex MsgStr = new(@"^msgstr\s+""([^""]*)""", RegexOptions.Multiline);

    /// <summary>English is the reference catalogue; every other language mirrors it.</summary>
    private const string ReferenceLanguage = "en";

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(CatalogueDirectory, "*.po"))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(language, ReferenceLanguage, StringComparison.Ordinal))
            {
                data.Add(language);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_defines_exactly_the_english_keys(string language)
    {
        var english = KeysOf(ReferenceLanguage);
        var translated = KeysOf(language);

        var missing = english.Except(translated).Order().ToList();
        var stale = translated.Except(english).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"{language}.po is missing {missing.Count} key(s) present in en.po; those show as raw key names in the UI: " +
            string.Join(", ", missing));

        Assert.True(
            stale.Count == 0,
            $"{language}.po defines {stale.Count} key(s) that en.po does not — most likely renamed or deleted: " +
            string.Join(", ", stale));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void No_translation_is_left_empty(string language)
    {
        var empty = EntriesOf(language)
            .Where(entry => entry.Value.Length == 0)
            .Select(entry => entry.Key)
            .Order()
            .ToList();

        Assert.True(
            empty.Count == 0,
            $"{language}.po has {empty.Count} key(s) with an empty translation, which renders as blank text: " +
            string.Join(", ", empty));
    }

    [Fact]
    public void English_catalogue_is_not_empty()
    {
        // A sanity check on the file discovery itself: every assertion
        // above passes trivially against an empty reference.
        Assert.True(KeysOf(ReferenceLanguage).Count > 100);
    }

    /// <summary>
    /// Walks up from the test binary to the repository, so the test works
    /// from `dotnet test` at any directory and on any CI runner.
    /// </summary>
    private static string CatalogueDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName, "src", "GameFlow.App", "Localization");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate src/GameFlow.App/Localization from " + AppContext.BaseDirectory);
        }
    }

    private static IReadOnlyCollection<string> KeysOf(string language) =>
        EntriesOf(language).Select(e => e.Key).ToList();

    /// <summary>
    /// Reads a catalogue as key/translation pairs.
    /// </summary>
    /// <remarks>
    /// The empty msgid is the PO header, not a string, so it is skipped.
    /// </remarks>
    private static IReadOnlyList<KeyValuePair<string, string>> EntriesOf(string language)
    {
        var text = File.ReadAllText(Path.Combine(CatalogueDirectory, language + ".po"));
        var ids = MsgId.Matches(text);
        var values = MsgStr.Matches(text);

        var entries = new List<KeyValuePair<string, string>>(ids.Count);
        for (var i = 0; i < ids.Count && i < values.Count; i++)
        {
            var key = ids[i].Groups[1].Value;
            if (key.Length == 0)
            {
                continue;
            }

            entries.Add(new KeyValuePair<string, string>(key, values[i].Groups[1].Value));
        }

        return entries;
    }
}
