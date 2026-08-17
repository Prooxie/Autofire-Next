using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Theming.Models;
using Serilog;

namespace GameFlow.Infrastructure.Theming;

/// <summary>
/// Catalog entry for one installed theme. Keeps the parsed
/// <see cref="ThemeDocument"/> alongside metadata used by the picker UI
/// and the style-to-theme resolver.
/// </summary>
public sealed record InstalledTheme(
    string Id,
    string DisplayName,
    string DirectoryPath,
    ThemeDocument Document,
    ControllerVisualStyle PreferredStyle);

/// <summary>
/// Discovers and caches every VSCView-compatible theme installed under
/// <see cref="AppPaths.ThemesDirectory"/>. A theme is "installed" when a
/// <c>theme.json</c> file exists inside its folder.
///
/// <para>
/// The registry is the single source of truth that the UI consults when
/// it needs to know "do we have a theme for this style?" — see
/// <see cref="GetThemeForStyle"/>. When no theme is found the UI falls
/// back to the existing programmatic XAML art, so a fresh install with
/// no themes still works.
/// </para>
///
/// <para>
/// Discovery rules:
/// </para>
/// <list type="bullet">
/// <item><b>Folder layout</b>: <c>{ThemesDirectory}/&lt;theme-id&gt;/theme.json</c>;</item>
/// <item><b>Theme id</b>: folder name, lowercased, hyphen-separated;</item>
/// <item><b>Style mapping</b>: deduced from the folder name — folders
///   containing <c>xbox</c> map to <see cref="ControllerVisualStyle.Xbox"/>,
///   <c>dualsense</c> / <c>ps5</c> map to PlayStation5, etc. Override by
///   adding a <c>"controllerStyle"</c> key at the root of theme.json
///   (extension to VSCView's schema; safely ignored by upstream tools).</item>
/// </list>
///
/// <para>
/// Registry contents are cached after the first <see cref="Refresh"/>
/// call. <see cref="Refresh"/> is cheap (a couple of directory listings
/// plus per-theme JSON parsing) and may be called whenever the user
/// installs new themes — typically from the settings dialog.
/// </para>
/// </summary>
public sealed class ThemeRegistry
{
    private readonly object syncRoot = new();
    private IReadOnlyList<InstalledTheme> themes = [];

    /// <summary>
    /// The process-wide registry, refreshed on first touch.
    ///
    /// <para>
    /// Scanning and parsing the themes folder is the expensive part, and
    /// there are now two unrelated consumers of it — the dashboard's
    /// surfaces and the OBS overlay server, which live in different
    /// assemblies and are constructed by different machinery (an Avalonia
    /// control with no constructor injection, and a DI singleton). This
    /// is what they share so the folder is walked once and both see the
    /// same theme ids.
    /// </para>
    ///
    /// <para>
    /// It is registered in DI as well, and injecting it is the preferred
    /// way to reach it. This property exists for the one caller that
    /// cannot.
    /// </para>
    /// </summary>
    public static ThemeRegistry Shared => LazyShared.Value;

    private static readonly Lazy<ThemeRegistry> LazyShared = new(() =>
    {
        var registry = new ThemeRegistry();
        try
        {
            registry.Refresh();
            Log.Information("Theme registry refreshed: {Count} theme(s) found.", registry.Themes.Count);
            foreach (var theme in registry.Themes)
            {
                Log.Information(
                    "  - id='{Id}' style={Style} name='{Name}' dir='{Dir}'",
                    theme.Id, theme.PreferredStyle, theme.DisplayName, theme.DirectoryPath);
            }
        }
        catch (Exception exception)
        {
            // A themes folder that cannot be read leaves an empty
            // registry, and every surface falls back to programmatic art
            // — which is the same path a fresh install with no themes
            // takes, so it is already exercised.
            Log.Warning(exception, "Initial theme registry refresh failed.");
        }
        return registry;
    }, isThreadSafe: true);

    /// <summary>Every theme discovered by the last <see cref="Refresh"/> call.</summary>
    public IReadOnlyList<InstalledTheme> Themes
    {
        get
        {
            lock (syncRoot)
            {
                return themes;
            }
        }
    }

    /// <summary>
    /// Re-scans the on-disk themes directory and reloads every theme.
    /// Idempotent: safe to call as often as you like; the heavy parsing
    /// only happens for themes whose <c>theme.json</c> exists.
    ///
    /// <para>
    /// The walk is recursive so the on-disk layout can mirror VSCView's
    /// own <c>&lt;controller-family&gt;/&lt;theme-name&gt;/theme.json</c>
    /// convention (e.g. <c>Xbox Wireless Controller/Default/theme.json</c>
    /// alongside the AL2009man asset pack's directory layout) or a
    /// simpler flat <c>&lt;theme-id&gt;/theme.json</c> arrangement.
    /// Every <c>theme.json</c> found in any descendant directory is
    /// loaded as a separate theme; the theme id becomes the directory
    /// path relative to the themes root with separators replaced by
    /// hyphens.
    /// </para>
    /// </summary>
    public void Refresh()
    {
        var dir = AppPaths.ThemesDirectory;

        var loaded = new List<InstalledTheme>();
        if (!Directory.Exists(dir))
        {
            // The directory is auto-created on AppPaths access, but
            // defend anyway against pathological filesystem state.
            Log.Debug("Themes directory does not exist: {Dir}", dir);
        }
        else
        {
            // Walk every theme.json file found under the themes root.
            // EnumerateFiles with AllDirectories is cheap because the
            // tree is shallow (1-3 levels at most) and the file count
            // is tiny relative to a normal source repository.
            foreach (var manifest in Directory.EnumerateFiles(
                         dir, "theme.json", SearchOption.AllDirectories))
            {
                var sub = Path.GetDirectoryName(manifest);
                if (string.IsNullOrEmpty(sub))
                {
                    continue;
                }

                try
                {
                    // Pass the registry's known themes-root explicitly.
                    // Without this the loader assumes "one directory
                    // above the theme.json" — which is wrong for our
                    // nested variant layout (themes/<style>/<variant>/
                    // theme.json or deeper).
                    var doc = ThemeJsonLoader.LoadFromFile(manifest, explicitThemesRoot: dir);

                    // Build an id from the path of the theme directory
                    // relative to the themes root, separators replaced
                    // with hyphens. Keeps ids unique even when two
                    // controllers happen to ship a folder called
                    // "default".
                    var relative = Path.GetRelativePath(dir, sub);
                    var id = relative
                        .Replace(Path.DirectorySeparatorChar, '-')
                        .Replace(Path.AltDirectorySeparatorChar, '-')
                        .ToLowerInvariant();

                    loaded.Add(new InstalledTheme(
                        Id: id,
                        DisplayName: string.IsNullOrWhiteSpace(doc.Name)
                            ? Path.GetFileName(sub) ?? id
                            : doc.Name,
                        DirectoryPath: sub,
                        Document: doc,
                        PreferredStyle: GuessStyleFromFolderName(relative)));
                    Log.Information("Loaded theme '{Name}' from {Path}.", doc.Name, manifest);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "Could not load theme manifest {Path}; skipping.",
                        manifest);
                }
            }
        }

        lock (syncRoot)
        {
            themes = loaded;
        }
    }

    /// <summary>
    /// Returns the installed themes to offer for <paramref name="style"/>,
    /// sorted by display name. Only exact controller-model matches are
    /// returned: a DualSense skin cannot be selected for a DualShock 4,
    /// and an Xbox 360 skin cannot be selected for an Xbox Series output.
    /// This keeps short skin names such as "Black" meaningful inside the
    /// model-specific picker and prevents misleading controller artwork.
    /// <see cref="ControllerVisualStyle.None"/> and
    /// <see cref="ControllerVisualStyle.Auto"/> still return an empty
    /// list — None means "render nothing" by contract.
    /// </summary>
    public IReadOnlyList<InstalledTheme> GetThemesForStyle(ControllerVisualStyle style)
    {
        if (style is ControllerVisualStyle.None or ControllerVisualStyle.Auto)
        {
            return [];
        }

        lock (syncRoot)
        {
            var result = new List<InstalledTheme>();
            foreach (var theme in themes)
            {
                if (theme.PreferredStyle == style)
                {
                    result.Add(theme);
                }
            }
            result.Sort(static (a, b) => string.Compare(
                a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }

    /// <summary>
    /// Resolves a theme by its registry id (the lower-cased,
    /// hyphen-separated relative path used as the dictionary key).
    /// Returns <see langword="null"/> when the id is unknown — e.g.
    /// when a profile referenced a theme that the user has since
    /// uninstalled. Callers should fall back to
    /// <see cref="GetThemeForStyle"/>.
    /// </summary>
    public InstalledTheme? GetThemeById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (syncRoot)
        {
            foreach (var t in themes)
            {
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Returns the first installed theme whose <see cref="InstalledTheme.PreferredStyle"/>
    /// matches <paramref name="style"/>, or <see langword="null"/> when no
    /// theme is installed for that family. The caller is expected to fall
    /// back to programmatic XAML art on a null return.
    /// </summary>
    public InstalledTheme? GetThemeForStyle(ControllerVisualStyle style)
    {
        var all = GetThemesForStyle(style);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// Maps a theme folder name to its preferred controller style.
    /// Matches are deliberately permissive — case-insensitive
    /// substring tests cover the common naming patterns (<c>xbox-series-x</c>,
    /// <c>ps5-default</c>, <c>dualshock-4-jet-black</c>, etc.).
    /// </summary>
    internal static ControllerVisualStyle GuessStyleFromFolderName(string folder)
    {
        var name = folder.ToLowerInvariant();
        // Composite Simple Gamepad skin names intentionally carry the
        // legend they render (for example simple-gamepad-xbox and
        // simple-gamepad-nintendo).  Resolve the family marker first so
        // those words do not incorrectly reclassify the skin as a full
        // Xbox or Switch Pro controller theme.
        if (name.Contains("simple-gamepad", StringComparison.Ordinal) ||
            name.Contains("simplegamepad", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.SimpleGamepad;
        }
        if (name.Contains("ps5", StringComparison.Ordinal) ||
            name.Contains("dualsense", StringComparison.Ordinal) ||
            name.Contains("playstation5", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.PlayStation5;
        }
        if (name.Contains("ps4", StringComparison.Ordinal) ||
            name.Contains("ds4", StringComparison.Ordinal) ||
            name.Contains("dualshock4", StringComparison.Ordinal) ||
            name.Contains("dualshock-4", StringComparison.Ordinal) ||
            name.Contains("playstation4", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.PlayStation4;
        }
        if (name.Contains("ps3", StringComparison.Ordinal) ||
            name.Contains("dualshock3", StringComparison.Ordinal) ||
            name.Contains("playstation3", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.PlayStation3;
        }
        // Xbox generations — keep specific matches above the generic
        // "xbox" fallback so e.g. "xbox-series-x" doesn't bucket into
        // the legacy Xbox style.
        if (name.Contains("series", StringComparison.Ordinal) ||
            name.Contains("xbsx", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.XboxSeries;
        }
        if (name.Contains("xbox-one", StringComparison.Ordinal) ||
            name.Contains("xboxone", StringComparison.Ordinal) ||
            name.Contains("xbone", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.XboxOne;
        }
        if (name.Contains("360", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.Xbox360;
        }
        if (name.Contains("xbox", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.Xbox;
        }
        if (name.Contains("switch", StringComparison.Ordinal) ||
            name.Contains("joycon", StringComparison.Ordinal) ||
            name.Contains("joy-con", StringComparison.Ordinal) ||
            name.Contains("nintendo", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.NintendoSwitch;
        }
        if (name.Contains("steam-deck", StringComparison.Ordinal) ||
            name.Contains("steamdeck", StringComparison.Ordinal) ||
            name.Contains("deck", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.SteamDeck;
        }
        if (name.Contains("steam", StringComparison.Ordinal) ||
            name.Contains("valve", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.SteamController;
        }
        if (name.Contains("arcade", StringComparison.Ordinal) ||
            name.Contains("hitbox", StringComparison.Ordinal) ||
            name.Contains("fightstick", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.Arcade;
        }
        if (name.Contains("keyboard", StringComparison.Ordinal) ||
            name.Contains("kbd", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.Keyboard;
        }
        if (name.Contains("mouse", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.Mouse;
        }
        if (name.Contains("simple", StringComparison.Ordinal) ||
            name.Contains("generic", StringComparison.Ordinal) ||
            name.Contains("gamepad", StringComparison.Ordinal))
        {
            return ControllerVisualStyle.SimpleGamepad;
        }
        return ControllerVisualStyle.Auto;
    }
}
